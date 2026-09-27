# 下载内核与地理数据库到指定目录，供"内置版"打包使用。
# 用法: pwsh -File build/fetch-bundle.ps1 -DestDir <目录> [-Arch amd64]
param(
    [Parameter(Mandatory = $true)][string]$DestDir,
    [string]$Arch = "amd64"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

$proxies = @(
    "https://gh-proxy.org",
    "https://ghproxy.net",
    "https://ghfast.top",
    ""
)  # 末尾空串表示直连

function Fetch-Url([string]$Url, [string]$OutFile) {
    foreach ($p in $proxies) {
        $target = if ([string]::IsNullOrEmpty($p)) { $Url } else { "$p/$Url" }
        try {
            Write-Host "  GET $target"
            Invoke-WebRequest -Uri $target -OutFile $OutFile -TimeoutSec 120 -UseBasicParsing
            if ((Get-Item $OutFile).Length -gt 0) { return $true }
        } catch {
            Write-Host "  FAIL: $($_.Exception.Message.Split("`n")[0])"
        }
    }
    return $false
}

# 1. 内核
Write-Host "== 获取 mihomo 最新版本 =="
$apiUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest"
$release = $null
foreach ($p in $proxies) {
    $target = if ([string]::IsNullOrEmpty($p)) { $apiUrl } else { "$p/$apiUrl" }
    try {
        $r = Invoke-WebRequest -Uri $target -TimeoutSec 60 -UseBasicParsing -Headers @{ "User-Agent" = "Momomi" }
        $release = $r.Content | ConvertFrom-Json
        if ($release) { break }
    } catch {
        Write-Host "  API FAIL via '$p': $($_.Exception.Message.Split("`n")[0])"
    }
}
if (-not $release) { throw "无法获取 mihomo 版本信息" }

$tag = $release.tag_name
Write-Host "版本: $tag"

$asset = $release.assets |
    Where-Object { $_.name -match "windows-$Arch" -and $_.name -match "\.zip$" -and $_.name -match "compatible" } |
    Select-Object -First 1
if (-not $asset) {
    $asset = $release.assets | Where-Object { $_.name -match "windows-$Arch" -and $_.name -match "\.zip$" } | Select-Object -First 1
}
if (-not $asset) { throw "未找到 windows-$Arch 的内核压缩包" }

$zipPath = Join-Path $DestDir "mihomo.zip"
if (-not (Fetch-Url $asset.browser_download_url $zipPath)) { throw "内核下载失败" }

Write-Host "== 解压内核 =="
Expand-Archive -Path $zipPath -DestinationPath $DestDir -Force
Remove-Item $zipPath -Force
# 压缩包内的 exe 名为 mihomo-windows-amd64-compatible.exe 等，统一重命名为 mihomo.exe
$exe = Get-ChildItem $DestDir -Recurse -Filter "*.exe" |
    Where-Object { $_.Name -match "mihomo" } | Select-Object -First 1
if (-not $exe) { throw "压缩包内未找到 mihomo 可执行文件" }
Move-Item $exe.FullName (Join-Path $DestDir "mihomo.exe") -Force
Set-Content -Path (Join-Path $DestDir "version.txt") -Value $tag -NoNewline -Encoding UTF8

# 2. wintun（TUN 需要）
Write-Host "== 获取 wintun =="
$wintunZip = Join-Path $DestDir "wintun.zip"
if (Fetch-Url "https://wintun.net/builds/wintun-0.14.1.zip" $wintunZip) {
    $tmp = Join-Path $DestDir "wintun_extract"
    Expand-Archive -Path $wintunZip -DestinationPath $tmp -Force
    $dll = Get-ChildItem $tmp -Recurse -Filter "wintun.dll" |
        Where-Object { $_.DirectoryName -match "\\$Arch\\" } | Select-Object -First 1
    if (-not $dll) { $dll = Get-ChildItem $tmp -Recurse -Filter "wintun.dll" | Select-Object -First 1 }
    if ($dll) { Copy-Item $dll.FullName (Join-Path $DestDir "wintun.dll") -Force }
    Remove-Item $wintunZip, $tmp -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "::warning::wintun 下载失败，内置包将不含 wintun.dll（TUN 首次使用时会再下载）"
}

# 3. 地理数据库
Write-Host "== 获取地理数据库 =="
$geodata = @(
    @{ Name = "geoip.metadb"; Url = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.metadb" },
    @{ Name = "geosite.dat";  Url = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geosite.dat" },
    @{ Name = "geoip.dat";    Url = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.dat" },
    @{ Name = "ASN.mmdb";     Url = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/GeoLite2-ASN.mmdb" },
    @{ Name = "country.mmdb"; Url = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/country.mmdb" }
)
foreach ($g in $geodata) {
    $out = Join-Path $DestDir $g.Name
    if (-not (Fetch-Url $g.Url $out)) {
        Write-Host "::warning::$($g.Name) 下载失败，内置包将不含该文件"
    }
}

Write-Host "== 内置资源就绪 =="
Get-ChildItem $DestDir | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
