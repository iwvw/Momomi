param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ArtifactsDir,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$TemplatePath = "$PSScriptRoot/../release-notes-template.md",
    [string]$ChangesDir = "$PSScriptRoot/../release-notes",
    [string]$PreviousTag = ""
)

$ErrorActionPreference = 'Stop'

function Format-Size([string]$fileName) {
    $path = Join-Path $ArtifactsDir $fileName
    if (-not (Test-Path -LiteralPath $path)) { return '—' }
    $mb = (Get-Item -LiteralPath $path).Length / 1MB
    return ('{0:N1} MB' -f $mb)
}

$changesFile = Join-Path $ChangesDir "$Version.md"
if (Test-Path -LiteralPath $changesFile) {
    $changes = (Get-Content -LiteralPath $changesFile -Raw).Trim()
}
elseif (-not [string]::IsNullOrWhiteSpace($PreviousTag)) {
    $changes = "**Full Changelog**: https://github.com/iwvw/Momomi/compare/$PreviousTag...v$Version"
}
else {
    $changes = "**Full Changelog**: https://github.com/iwvw/Momomi/releases/tag/v$Version"
}

$template = Get-Content -LiteralPath $TemplatePath -Raw

$sizeMap = [ordered]@{
    '{{SIZE_X64_SETUP}}'           = Format-Size "Momomi-$Version-x64-setup.exe"
    '{{SIZE_X64_PORTABLE}}'        = Format-Size "Momomi-$Version-x64-portable.zip"
    '{{SIZE_X64_FULL_SETUP}}'      = Format-Size "Momomi-$Version-x64-full-setup.exe"
    '{{SIZE_X64_FULL}}'            = Format-Size "Momomi-$Version-x64-full.zip"
    '{{SIZE_X64_SEP_SETUP}}'       = Format-Size "Momomi-$Version-x64-sep-setup.exe"
    '{{SIZE_X64_SEP}}'             = Format-Size "Momomi-$Version-x64-sep.zip"
    '{{SIZE_X64_FULL_SEP_SETUP}}'  = Format-Size "Momomi-$Version-x64-full-sep-setup.exe"
    '{{SIZE_X64_FULL_SEP}}'        = Format-Size "Momomi-$Version-x64-full-sep.zip"
    '{{SIZE_ARM64_PORTABLE}}'      = Format-Size "Momomi-$Version-arm64-portable.zip"
    '{{SIZE_ARM64_FULL}}'          = Format-Size "Momomi-$Version-arm64-full.zip"
}

$body = $template.Replace('{{CHANGES}}', $changes).Replace('{{VERSION}}', $Version)
foreach ($kv in $sizeMap.GetEnumerator()) {
    $body = $body.Replace($kv.Key, $kv.Value)
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $body, $utf8)
Write-Host "Release notes written to $OutputPath"
