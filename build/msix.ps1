param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$SourceDir,
  [Parameter(Mandatory = $true)][string]$OutputDir,
  [string]$Publisher = "",
  [string]$CertPath = "",
  [string]$CertPassword = "",
  [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"

# 以项目内的 Package.appxmanifest 为单一数据源，避免与打包脚本各维护一份导致漂移。
# 仅就地替换 Identity 的 Publisher / Version / ProcessorArchitecture，其余（能力、虚拟化设置、
# 视觉资产、入口）全部沿用清单定义。
$projectDir = Resolve-Path (Join-Path $PSScriptRoot "..\src\Momomi.App")
$manifestSource = Join-Path $projectDir "Package.appxmanifest"
if (-not (Test-Path $manifestSource)) { throw "未找到 Package.appxmanifest：$manifestSource" }
$manifest = Get-Content -LiteralPath $manifestSource -Raw

if ([string]::IsNullOrWhiteSpace($Publisher)) {
  $m = [regex]::Match($manifest, '<Identity[^>]*Publisher="([^"]*)"')
  if ($m.Success) { $Publisher = $m.Groups[1].Value } else { $Publisher = "CN=DSUK" }
}

$manifest = [regex]::Replace($manifest, '<Identity[^>]*/>',
  "<Identity Name=`"Momomi.App`" Publisher=`"$Publisher`" Version=`"$Version`" ProcessorArchitecture=`"$Arch`" />")

# 分离版（框架依赖）需声明 WindowsAppRuntime 包依赖，否则启动时找不到 WinUI 运行时（CLR20r3 崩溃）。
# 自包含版已内置运行时（存在 Microsoft.ui.xaml.dll），无需依赖声明。
if (-not (Test-Path (Join-Path $SourceDir "Microsoft.ui.xaml.dll"))) {
  $depXml = '    <PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="2.6.4.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />'
  $manifest = [regex]::Replace($manifest,
    '(<TargetDeviceFamily[^>]*/>)',
    "`$1`r`n$depXml")
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
# MakeAppx 从源目录打包，清单必须位于源目录根部。
$manifestPath = Join-Path $SourceDir "AppxManifest.xml"
# MakeAppx 要求 UTF-8（带 BOM）。
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($true)))

# 定位 MakeAppx / SignTool。
$sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory |
  Where-Object { $_.Name -match '^10\.' } |
  Sort-Object { [version]$_.Name } -Descending |
  Select-Object -First 1
$makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"
$signtool = Join-Path $sdkBin.FullName "x64\signtool.exe"
if (-not (Test-Path $makeappx)) { throw "未找到 makeappx.exe：$makeappx" }

$msixName = "Momomi-$Version-$Arch.msix"
$msixPath = Join-Path $OutputDir $msixName

# 打包（/nv 跳过文件引用校验：外部路径无需解析）。
& $makeappx pack /o /nv /d $SourceDir /p $msixPath
if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败，退出码 $LASTEXITCODE" }

# 签名（提供证书时）。
if ($CertPath -and (Test-Path $CertPath)) {
  if (-not (Test-Path $signtool)) { throw "未找到 signtool.exe：$signtool" }
  $signArgs = @("sign", "/fd", "SHA256", "/a", "/f", $CertPath)
  if ($CertPassword) { $signArgs += @("/p", $CertPassword) }
  $signArgs += $msixPath
  & $signtool @signArgs
  if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败，退出码 $LASTEXITCODE" }
  Write-Host "已签名：$msixPath"
} else {
  Write-Host "::warning::未提供签名证书，生成的 MSIX 未签名，仅用于本地测试。"
}

Write-Host "MSIX 产物：$msixPath"
Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
