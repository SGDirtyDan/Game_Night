$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root "config/games.json"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Missing config/games.json. Copy config/games.example.json or create a local manifest first."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$failures = New-Object System.Collections.Generic.List[string]

Write-Host "Game Night setup check"
Write-Host ""

foreach ($emulatorName in $manifest.emulators.PSObject.Properties.Name) {
    $emulator = $manifest.emulators.$emulatorName
    $exePath = Join-Path $root $emulator.executable
    $portablePath = Join-Path $root $emulator.portableMarker

    if (Test-Path -LiteralPath $exePath) {
        Write-Host "[OK] $($emulator.displayName) executable: $($emulator.executable)"
    } else {
        Write-Host "[FAIL] $($emulator.displayName) executable missing: $($emulator.executable)"
        $failures.Add("Missing emulator executable: $($emulator.executable)")
    }

    if (Test-Path -LiteralPath $portablePath) {
        Write-Host "[OK] $($emulator.displayName) portable marker: $($emulator.portableMarker)"
    } else {
        Write-Host "[FAIL] $($emulator.displayName) portable marker missing: $($emulator.portableMarker)"
        $failures.Add("Missing portable marker: $($emulator.portableMarker)")
    }
}

Write-Host ""

foreach ($game in $manifest.games) {
    $gamePath = Join-Path $root $game.relativePath

    if (-not (Test-Path -LiteralPath $gamePath)) {
        Write-Host "[FAIL] $($game.name): file missing at $($game.relativePath)"
        $failures.Add("Missing game file: $($game.relativePath)")
        continue
    }

    if ([string]::IsNullOrWhiteSpace($game.sha256)) {
        Write-Host "[OK] $($game.name): file present, SHA-256 not pinned"
    } else {
        $actualHash = (Get-FileHash -LiteralPath $gamePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -eq $game.sha256.ToLowerInvariant()) {
            Write-Host "[OK] $($game.name): verified SHA-256"
        } else {
            Write-Host "[FAIL] $($game.name): SHA-256 mismatch"
            Write-Host "       Expected: $($game.sha256)"
            Write-Host "       Found:    $actualHash"
            $failures.Add("Hash mismatch: $($game.name)")
        }
    }

    if ($game.compatibilityProfile) {
        $profilePath = Join-Path $root $game.compatibilityProfile
        if (Test-Path -LiteralPath $profilePath) {
            Write-Host "[OK] $($game.name): compatibility profile"
        } else {
            Write-Host "[FAIL] $($game.name): missing compatibility profile $($game.compatibilityProfile)"
            $failures.Add("Missing compatibility profile: $($game.compatibilityProfile)")
        }
    }
}

Write-Host ""

if ($failures.Count -gt 0) {
    Write-Host "Setup check failed with $($failures.Count) issue(s)."
    exit 1
}

Write-Host "Setup check passed."
