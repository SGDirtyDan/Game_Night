param(
    [ValidateSet("Friend", "Host")]
    [string] $PackageMode = "Friend",

    [string] $GitHubOwner = "SGDirtyDan",

    [string] $GitHubRepository = "Game_Night",

    [string] $Tag = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$versionPath = Join-Path $root "version.json"

if (-not (Test-Path -LiteralPath $versionPath)) {
    throw "Missing version.json."
}

$versionInfo = Get-Content -LiteralPath $versionPath -Raw | ConvertFrom-Json
$version = $versionInfo.version

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "version.json does not include a version."
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$version"
}

& (Join-Path $PSScriptRoot "Publish-GameNight.ps1") -PackageMode $PackageMode
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$packageName = if ($PackageMode -eq "Friend") { "GameNight-Friend" } else { "GameNight" }
$packageRoot = Join-Path $root "dist/$packageName"
$releaseRoot = Join-Path $root "dist/releases"
$zipName = "$packageName-$version.zip"
$zipPath = Join-Path $releaseRoot $zipName

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$packageUrl = "https://github.com/$GitHubOwner/$GitHubRepository/releases/download/$Tag/$zipName"
$feedPath = Join-Path $releaseRoot "update.json"
$repositoryFeedPath = Join-Path $root "update.json"

$feed = [ordered]@{
    latestVersion = $version
    channel = $PackageMode.ToLowerInvariant()
    packageUrl = $packageUrl
    sha256 = $hash
    releaseNotes = @($versionInfo.releaseNotes)
}

$feedJson = $feed | ConvertTo-Json -Depth 4
$feedJson | Set-Content -LiteralPath $feedPath -Encoding UTF8
$feedJson | Set-Content -LiteralPath $repositoryFeedPath -Encoding UTF8

Write-Host ""
Write-Host "Prepared Game Night release:"
Write-Host "Package: $zipPath"
Write-Host "SHA-256: $hash"
Write-Host "Feed:    $feedPath"
Write-Host "Repo feed:"
Write-Host $repositoryFeedPath
Write-Host ""
Write-Host "GitHub release tag:"
Write-Host $Tag
Write-Host ""
Write-Host "GitHub asset URL:"
Write-Host $packageUrl
