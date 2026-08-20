param(
    [ValidateSet("Friend", "Host")]
    [string] $PackageMode = "Friend",

    [string] $Tag = "",

    [switch] $Draft
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $root "version.json") -Raw | ConvertFrom-Json
$version = $versionInfo.version

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "version.json does not include a version."
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$version"
}

$packageName = if ($PackageMode -eq "Friend") { "GameNight-Friend" } else { "GameNight" }
$zipPath = Join-Path $root "dist/releases/$packageName-$version.zip"
$feedPath = Join-Path $root "update.json"
$notesPath = Join-Path $root "dist/releases/release-notes-$version.md"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is not installed or is not available in PATH. Install it with: winget install GitHub.cli"
}

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Missing release zip: $zipPath. Run scripts/New-GameNightRelease.ps1 first."
}

if (-not (Test-Path -LiteralPath $feedPath)) {
    throw "Missing update feed: $feedPath. Run scripts/New-GameNightRelease.ps1 first."
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $notesPath) | Out-Null

$releaseNoteLines = @($versionInfo.releaseNotes | ForEach-Object { "- $_" })
$notesLines = @(
    "# Game Night $version",
    "",
    "## Release Notes",
    ""
)
$notesLines += $releaseNoteLines
$notesLines += @(
    "",
    "## Update Feed",
    "",
    "The update feed is tracked at ``update.json`` and should be pushed to ``main`` after this release is prepared."
)

$notesLines | Set-Content -LiteralPath $notesPath -Encoding UTF8

$args = @(
    "release",
    "create",
    $Tag,
    $zipPath,
    "--title",
    "Game Night $version",
    "--notes-file",
    $notesPath
)

if ($Draft) {
    $args += "--draft"
}

& gh @args
if ($LASTEXITCODE -ne 0) {
    throw "gh release create failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Published GitHub release:"
Write-Host $Tag
Write-Host ""
Write-Host "Next:"
Write-Host "  git add version.json update.json docs scripts"
Write-Host "  git commit -m `"Prepare Game Night $version release feed`""
Write-Host "  git push"
