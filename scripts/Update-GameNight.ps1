param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $ZipPath,

    [Parameter(Mandatory = $true)]
    [int] $AppProcessId,

    [Parameter(Mandatory = $true)]
    [string] $RelaunchPath,

    [string] $LogPath = ""
)

$ErrorActionPreference = "Stop"

$PackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
$ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
$RelaunchPath = [System.IO.Path]::GetFullPath($RelaunchPath)

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logRoot = Join-Path $env:LOCALAPPDATA "GameNight\Logs"
    $LogPath = Join-Path $logRoot "last-update.log"
}

$LogPath = [System.IO.Path]::GetFullPath($LogPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null

function Write-UpdateLog {
    param([string] $Message)

    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

Set-Content -LiteralPath $LogPath -Value "Game Night updater started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8
Write-UpdateLog "PackageRoot: $PackageRoot"
Write-UpdateLog "ZipPath: $ZipPath"
Write-UpdateLog "RelaunchPath: $RelaunchPath"

if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container)) {
    throw "Package root does not exist: $PackageRoot"
}

if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Update zip does not exist: $ZipPath"
}

$packageRootInfo = [System.IO.DirectoryInfo]::new($PackageRoot)
if ($packageRootInfo.Parent -eq $null) {
    throw "Refusing to update a filesystem root: $PackageRoot"
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GameNightUpdate-" + [System.Guid]::NewGuid().ToString("N"))
$extractRoot = Join-Path $workRoot "extract"
$preserveRoot = Join-Path $workRoot "preserve"

New-Item -ItemType Directory -Force -Path $extractRoot, $preserveRoot | Out-Null

try {
    $process = if ($AppProcessId -gt 0) { Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue } else { $null }
    if ($process) {
        Write-UpdateLog "Waiting for app process $AppProcessId to exit."
        Wait-Process -Id $AppProcessId -Timeout 60 -ErrorAction SilentlyContinue
        $process = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
        if ($process) {
            throw "App process $AppProcessId did not exit within 60 seconds."
        }
    }

    Write-UpdateLog "Extracting update zip."
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractRoot -Force

    $sourceRoot = Get-ChildItem -LiteralPath $extractRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "app/GameNight.exe") } |
        Select-Object -First 1

    if ($null -eq $sourceRoot) {
        if (Test-Path -LiteralPath (Join-Path $extractRoot "app/GameNight.exe")) {
            $sourceRoot = [System.IO.DirectoryInfo]::new($extractRoot)
        } else {
            throw "Could not find app/GameNight.exe inside the update zip."
        }
    }
    Write-UpdateLog "Update source root: $($sourceRoot.FullName)"

    $preservePaths = @(
        "games",
        "config/controller-profile.json",
        "emulators/dolphin/Dolphin-x64/User"
    )

    foreach ($relativePath in $preservePaths) {
        $source = Join-Path $PackageRoot $relativePath
        if (-not (Test-Path -LiteralPath $source)) {
            continue
        }

        $target = Join-Path $preserveRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Write-UpdateLog "Preserving $relativePath"
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }

    Write-UpdateLog "Removing old package files."
    Get-ChildItem -LiteralPath $PackageRoot -Force | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }

    Write-UpdateLog "Copying new package files."
    Get-ChildItem -LiteralPath $sourceRoot.FullName -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $PackageRoot -Recurse -Force
    }

    foreach ($relativePath in $preservePaths) {
        $source = Join-Path $preserveRoot $relativePath
        if (-not (Test-Path -LiteralPath $source)) {
            continue
        }

        $target = Join-Path $PackageRoot $relativePath
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Write-UpdateLog "Restoring $relativePath"
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }

    if (Test-Path -LiteralPath $RelaunchPath) {
        Write-UpdateLog "Relaunching Game Night."
        Start-Process -FilePath $RelaunchPath -WorkingDirectory (Split-Path -Parent $RelaunchPath)
    } else {
        Write-UpdateLog "Relaunch path missing after update: $RelaunchPath"
    }
    Write-UpdateLog "Update completed."
}
catch {
    Write-UpdateLog "Update failed: $($_.Exception.Message)"
    throw
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
