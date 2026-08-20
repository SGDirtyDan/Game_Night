param(
    [Parameter(Mandatory = $true)]
    [string] $GameId
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root "config/games.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$game = $manifest.games | Where-Object { $_.id -eq $GameId } | Select-Object -First 1

if (-not $game) {
    throw "Unknown game id: $GameId"
}

$emulator = $manifest.emulators.$($game.emulator)
if (-not $emulator) {
    throw "Unknown emulator '$($game.emulator)' for game '$($game.name)'."
}

$exePath = Join-Path $root $emulator.executable
$gamePath = Join-Path $root $game.relativePath

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Missing emulator executable: $($emulator.executable)"
}

if (-not (Test-Path -LiteralPath $gamePath)) {
    throw "Missing game file: $($game.relativePath)"
}

$actualHash = (Get-FileHash -LiteralPath $gamePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $game.sha256.ToLowerInvariant()) {
    throw "Refusing to launch '$($game.name)' because the SHA-256 hash does not match config/games.json."
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $exePath
$startInfo.WorkingDirectory = Split-Path -Parent $exePath
$escapedGamePath = $gamePath.Replace('"', '\"')
$startInfo.Arguments = "-e `"$escapedGamePath`""

[System.Diagnostics.Process]::Start($startInfo) | Out-Null
