<#
.SYNOPSIS
    Builds MISAML on Windows and assembles a runnable dist folder.

.DESCRIPTION
    Runs every Windows step from the README:
      1. Checks Rust, the .NET 8 SDK and the MSVC build tools
      2. cargo build --release
      3. dotnet publish -c Release
      4. Merges the C# dependencies with ILRepack into build/csharp
      5. Assembles the dist folder

    Place this script in the repository root.
    Existing files in the dist folder (misaml.toml, MISAML/runtime) are kept.

.EXAMPLE
    .\build-windows.ps1
    .\build-windows.ps1 -Dist C:\Games\MISAML -Run
#>

param(
    [string]$Dist = "dist",
    [switch]$SkipChecks,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $File $($Arguments -join ' ')"
    }
}

function Assert-Command([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' not found on PATH. $InstallHint"
    }
}

function Assert-FileExists([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Expected file not found: $Path"
    }
}

# ---------------------------------------------------------------------------
# 1. Dependency checks
# ---------------------------------------------------------------------------

function Test-Rust {
    Assert-Command "rustc" "Install Rust from https://rustup.rs (default MSVC option)."
    Assert-Command "cargo" "Install Rust from https://rustup.rs (default MSVC option)."

    $versionText = rustc --version
    if ($versionText -notmatch "rustc (\d+)\.(\d+)") {
        throw "Could not parse rustc version: $versionText"
    }

    $rustVersion = [version]"$($Matches[1]).$($Matches[2])"
    if ($rustVersion -lt [version]"1.85") {
        throw "rustc $rustVersion found, 1.85 or higher required. Run 'rustup update'."
    }

    Write-Host "Rust OK: $versionText"
}

function Test-DotNet {
    Assert-Command "dotnet" "Install the .NET 8 SDK from https://dotnet.microsoft.com/en-us/download/dotnet/8.0"

    $sdks = dotnet --list-sdks
    $hasNet8 = $sdks | Where-Object { $_ -like "8.*" }
    if (-not $hasNet8) {
        throw ".NET 8 SDK not found. Installed SDKs:`n$($sdks -join "`n")"
    }

    Write-Host ".NET OK: $($hasNet8 | Select-Object -First 1)"
}

function Test-MsvcBuildTools {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        Write-Warning "vswhere.exe not found, cannot verify Visual Studio Build Tools. Continuing anyway."
        return
    }

    $installPath = & $vswhere -products * -latest `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath

    if (-not $installPath) {
        throw "MSVC C++ tools not found. Install Visual Studio Build Tools with the 'Desktop development with C++' workload."
    }

    Write-Host "MSVC OK: $installPath"
}

if (-not $SkipChecks) {
    Write-Step "Checking dependencies"
    Test-Rust
    Test-DotNet
    Test-MsvcBuildTools
}

# ---------------------------------------------------------------------------
# 2. Rust build
# ---------------------------------------------------------------------------

Write-Step "Building Rust (cargo build --release)"
Invoke-Native "cargo" @("build", "--release")

$rustOut = "target/release"
Assert-FileExists "$rustOut/misaml-cli.exe"
Assert-FileExists "$rustOut/misaml_shim.dll"

# ---------------------------------------------------------------------------
# 3. C# build
# ---------------------------------------------------------------------------

Write-Step "Building C# (dotnet publish -c Release)"
Invoke-Native "dotnet" @("publish", "-c", "Release")

# ---------------------------------------------------------------------------
# 4. ILRepack merge
# ---------------------------------------------------------------------------

Write-Step "Locating ILRepack"

$nugetRoot = $env:NUGET_PACKAGES
if (-not $nugetRoot) {
    $nugetRoot = Join-Path $env:USERPROFILE ".nuget\packages"
}

$ilRepack = Get-ChildItem (Join-Path $nugetRoot "ilrepack") -Recurse -Filter "ILRepack.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $ilRepack) {
    throw "ILRepack.exe not found under $nugetRoot\ilrepack. Did 'dotnet publish' restore it?"
}

Write-Host "Using $($ilRepack.FullName)"

$csharpOut = "build/csharp"
New-Item -ItemType Directory -Force -Path $csharpOut | Out-Null

Write-Step "Merging MISAML.Bootstrap.dll"
$bootstrapPublish = "MISAML/Bootstrap/bin/Release/net8.0/publish"
Invoke-Native "dotnet" @(
    $ilRepack.FullName,
    "/target:library",
    "/lib:$bootstrapPublish",
    "/out:$csharpOut/MISAML.Bootstrap.dll",
    "$bootstrapPublish/MISAML.Bootstrap.dll",
    "$bootstrapPublish/Tomlyn.dll",
    "$bootstrapPublish/Semver.dll",
    "$bootstrapPublish/Microsoft.Extensions.Primitives.dll"
)

Write-Step "Merging MISAML.dll"
$mainPublish = "MISAML/bin/Release/net8.0/publish"
Invoke-Native "dotnet" @(
    $ilRepack.FullName,
    "/target:library",
    "/lib:MISAML/vendor",
    "/out:$csharpOut/MISAML.dll",
    "$mainPublish/MISAML.dll",
    "$mainPublish/0Harmony.dll"
)

Write-Step "Copying MISAML.GodotAPI.dll"
$godotApiDll = "MISAML/GodotAPI/bin/Release/net8.0/publish/MISAML.GodotAPI.dll"
Assert-FileExists $godotApiDll
Copy-Item $godotApiDll $csharpOut -Force

# ---------------------------------------------------------------------------
# 5. Assemble dist
# ---------------------------------------------------------------------------

Write-Step "Assembling $Dist"

$distMisaml = Join-Path $Dist "MISAML"
New-Item -ItemType Directory -Force -Path $distMisaml | Out-Null

Copy-Item "$rustOut/misaml-cli.exe" $Dist -Force
Copy-Item "$rustOut/misaml_shim.dll" $distMisaml -Force
Copy-Item "$csharpOut/MISAML.dll" $distMisaml -Force
Copy-Item "$csharpOut/MISAML.Bootstrap.dll" $distMisaml -Force
Copy-Item "$csharpOut/MISAML.GodotAPI.dll" $distMisaml -Force

Write-Host ""
Write-Host "Done. Output in: $(Resolve-Path $Dist)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Optional: run
# ---------------------------------------------------------------------------

if ($Run) {
    Write-Step "Starting misaml-cli"
    Push-Location $Dist
    try {
        & .\misaml-cli.exe
    }
    finally {
        Pop-Location
    }
}
