param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $ZipPath,

    [Parameter(Mandatory = $true)]
    [int] $AppProcessId,

    [Parameter(Mandatory = $true)]
    [string] $RelaunchPath
)

$ErrorActionPreference = "Stop"

$PackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
$ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
$RelaunchPath = [System.IO.Path]::GetFullPath($RelaunchPath)

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
    $process = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    if ($process) {
        Wait-Process -Id $AppProcessId -Timeout 60 -ErrorAction SilentlyContinue
    }

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
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }

    Get-ChildItem -LiteralPath $PackageRoot -Force | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }

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
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }

    if (Test-Path -LiteralPath $RelaunchPath) {
        Start-Process -FilePath $RelaunchPath -WorkingDirectory (Split-Path -Parent $RelaunchPath)
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
