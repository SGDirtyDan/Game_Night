param(
    [ValidateSet("Host", "Friend")]
    [string] $PackageMode = "Host"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/GameNight.Launcher/GameNight.Launcher.csproj"
$distRoot = Join-Path $root "dist"
$packageName = if ($PackageMode -eq "Friend") { "GameNight-Friend" } else { "GameNight" }
$packageRoot = Join-Path $distRoot $packageName
$publishRoot = Join-Path $packageRoot "app"

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "games") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "artwork/banners") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "artwork/covers") | Out-Null

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishRoot `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $root "config") -Destination (Join-Path $packageRoot "config") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root "shared") -Destination (Join-Path $packageRoot "shared") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root "docs") -Destination (Join-Path $packageRoot "docs") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root "version.json") -Destination (Join-Path $packageRoot "version.json") -Force
Copy-Item -LiteralPath (Join-Path $root "scripts/Get-GameHash.ps1") -Destination (Join-Path $packageRoot "Get-GameHash.ps1") -Force

if (Test-Path -LiteralPath (Join-Path $root "update.example.json")) {
    Copy-Item -LiteralPath (Join-Path $root "update.example.json") -Destination (Join-Path $packageRoot "update.example.json") -Force
}

if (Test-Path -LiteralPath (Join-Path $root "artwork")) {
    $sourceArtwork = Join-Path $root "artwork"
    $targetArtwork = Join-Path $packageRoot "artwork"
    Get-ChildItem -LiteralPath $sourceArtwork -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $targetArtwork -Recurse -Force
    }
}

if ($PackageMode -eq "Host") {
    $sourceGames = Join-Path $root "games"
    $targetGames = Join-Path $packageRoot "games"
    if (Test-Path -LiteralPath $sourceGames) {
        Get-ChildItem -LiteralPath $sourceGames -Force | Where-Object { $_.Name -ne ".gitkeep" } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $targetGames -Recurse -Force
        }
    }
}

if (Test-Path -LiteralPath (Join-Path $root "emulators/dolphin/Dolphin-x64/Dolphin.exe")) {
    New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "emulators/dolphin") | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "emulators/dolphin/Dolphin-x64") -Destination (Join-Path $packageRoot "emulators/dolphin/Dolphin-x64") -Recurse -Force
}

if ($PackageMode -eq "Friend") {
    $dolphinRoot = Join-Path $packageRoot "emulators/dolphin/Dolphin-x64"
    $dolphinUser = Join-Path $dolphinRoot "User"
    $dolphinConfig = Join-Path $dolphinUser "Config"

    New-Item -ItemType Directory -Force -Path $dolphinConfig | Out-Null

    foreach ($path in @(
        "GCPadNew.ini",
        "GCKeyNew.ini",
        "WiimoteNew.ini",
        "DSUClient.ini",
        "TimePlayed.ini",
        "RetroAchievements.ini"
    )) {
        $target = Join-Path $dolphinConfig $path
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }

    $controllerProfile = Join-Path $packageRoot "config/controller-profile.json"
    if (Test-Path -LiteralPath $controllerProfile) {
        Remove-Item -LiteralPath $controllerProfile -Force
    }

    foreach ($path in @(
        "Cache",
        "Dump",
        "GameConfig",
        "Logs",
        "ScreenShots",
        "StateSaves"
    )) {
        $target = Join-Path $dolphinUser $path
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }

    @"
[General]
ISOPaths = 1
ISOPath0 = $(Join-Path $packageRoot "games")

[Core]
GFXBackend = D3D

[Display]
Fullscreen = False

[NetPlay]
TraversalChoice = direct
ChunkedUploadLimit = 0x00000bb8
ConnectPort = 0x0a42
EnableChunkedUploadLimit = False
HostCode =
HostPort = 0x0a42
IndexName =
IndexPassword =
IndexRegion =
ListenPort = 0x0a42
Nickname =
UseIndex = False
UseUPNP = False

[SDL_Hints]
SDL_JOYSTICK_DIRECTINPUT = 1
SDL_JOYSTICK_ENHANCED_REPORTS = 1
SDL_JOYSTICK_HIDAPI_COMBINE_JOY_CONS = 1
SDL_JOYSTICK_HIDAPI_PS5_PLAYER_LED = 0
SDL_JOYSTICK_HIDAPI_VERTICAL_JOY_CONS = 0
SDL_JOYSTICK_WGI = 0
"@ | Set-Content -LiteralPath (Join-Path $dolphinConfig "Dolphin.ini") -Encoding UTF8

    @"
[Settings]
AspectRatio = 0
InternalResolution = 3

[Hardware]
Adapter = Auto
VSync = False

[Enhancements]
MSAA = 1
SSAA = False
MaxAnisotropy = 0
OutputResampling = 0
ColorCorrection = False
PostProcessingShader =
"@ | Set-Content -LiteralPath (Join-Path $dolphinConfig "GFX.ini") -Encoding UTF8
}

@"
Game Night

1. Put your own local game files in this folder.
2. Supported Dolphin files are detected automatically.
3. Run Get-GameHash.ps1 if you need to check a file hash.

Friend packages do not bundle commercial ROM/ISO/RVZ files.
"@ | Set-Content -LiteralPath (Join-Path $packageRoot "games/README.txt") -Encoding UTF8

@"
Game Night

Package mode: $PackageMode

Run:

  app\GameNight.exe

Before playing:

  - Start with app\GameNight.exe
  - Put your own local game files in games\
  - Or use Locate Game in the launcher to import a matching file
  - Confirm config\games.json points at those files
  - Use the in-app NetPlay fields to set nickname, mode, and port
  - Use Apply Profile for known detected controllers
  - Open Dolphin only if your controller does not have a built-in Game Night profile yet

Friend packages intentionally start without a controller mapping or NetPlay nickname.

The launcher verifies emulator setup, game hashes, controller status, and Dolphin NetPlay settings before launch.
"@ | Set-Content -LiteralPath (Join-Path $packageRoot "README-FIRST.txt") -Encoding UTF8

Write-Host ""
Write-Host "Published Game Night $PackageMode package:"
Write-Host $packageRoot
Write-Host ""
Write-Host "Run:"
Write-Host (Join-Path $publishRoot "GameNight.exe")
