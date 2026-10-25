<#
.SYNOPSIS
    Builds MISAML on Windows and assembles a runnable dist folder.

.DESCRIPTION
    Runs every Windows step required to build.
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

# Stop script when PS errors occur
$ErrorActionPreference = "Stop"

# Change working dir to the folder where this script is located
Set-Location $PSScriptRoot

# literally just a cyan step msg for logging
function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# run an external program with the specified args and stop on fail
function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $File $($Arguments -join ' ')"
    }
}

# check whether a req cmd is available, display an install hint if missing
function Assert-Command([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' not found on PATH. $InstallHint"
    }
}

# check whether a req file exists at the specified path
function Assert-FileExists([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Expected file not found: $Path"
    }
}

# Test if Rust is installed
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

# Test if dotnet is installed
function Test-DotNet {
    Assert-Command "dotnet" "Install the .NET 8 SDK from https://dotnet.microsoft.com/en-us/download/dotnet/8.0"

    $sdks = dotnet --list-sdks
    $hasNet8 = $sdks | Where-Object { $_ -like "8.*" }
    if (-not $hasNet8) {
        throw ".NET 8 SDK not found. Installed SDKs:`n$($sdks -join "`n")"
    }

    Write-Host ".NET OK: $($hasNet8 | Select-Object -First 1)"
}

# Test if MSVC build tools are installed
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

# If $SkipChecks is not true, check deps
if (-not $SkipChecks) {
    Write-Step "Checking dependencies"
    Test-Rust
    Test-DotNet
    Test-MsvcBuildTools
}

# Log building rust
Write-Step "Building Rust (cargo build --release)"

# Actually build rust w/ Cargo, release
Invoke-Native "cargo" @("build", "--release")

# Ensure files exist
$rustOut = "target/release"
Assert-FileExists "$rustOut/misaml-cli.exe"
Assert-FileExists "$rustOut/misaml_shim.dll"

# removing stale output
Write-Step "Cleaning old C# output"
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue `
    "MISAML/Bootstrap/bin", "MISAML/Bootstrap/obj", "build/csharp"

# Log building C#
Write-Step "Building C# (dotnet publish -c Release)"

# Actually build C#
Invoke-Native "dotnet" @("publish", "-c", "Release")

# log to check locating ilrepack
Write-Step "Locating ILRepack"

# nuget dir for checking
$nugetRoot = $env:NUGET_PACKAGES
if (-not $nugetRoot) {
    $nugetRoot = Join-Path $env:USERPROFILE ".nuget\packages"
}

# check if ILRepack.exe exists
$ilRepack = Get-ChildItem (Join-Path $nugetRoot "ilrepack") -Recurse -Filter "ILRepack.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $ilRepack) {
    throw "ILRepack.exe not found under $nugetRoot\ilrepack. Did 'dotnet publish' restore it?"
}

Write-Host "Using $($ilRepack.FullName)"

# Set csharp build dir
$csharpOut = "build/csharp"
New-Item -ItemType Directory -Force -Path $csharpOut | Out-Null

# Everything that runs in the Default context goes into a single MISAML.dll
# MISAML.dll (core) must be listed first, it is the primary assembly, so
# the output keeps the name "MISAML" and its version attributes (mods and
# ModLoader both depend on that)

$bootstrapPublish = "MISAML/Bootstrap/bin/Release/net8.0/publish"
$mainPublish = "MISAML/bin/Release/net8.0/publish"

$inputs = @(
    "$mainPublish/MISAML.dll",
    "$bootstrapPublish/MISAML.Bootstrap.dll",
    "$mainPublish/0Harmony.dll"
)

$deps = @(
    "Tomlyn", 
    "Semver",
    "Microsoft.Extensions.DependencyInjection",
    "Microsoft.Extensions.DependencyInjection.Abstractions",
    "Microsoft.Extensions.Logging",
    "Microsoft.Extensions.Logging.Abstractions",
    "Microsoft.Extensions.Options",
    "Microsoft.Extensions.Primitives",
    "Serilog",
    "Serilog.Extensions.Logging",
    "Serilog.Sinks.Console",
    "Serilog.Sinks.File"  
)
foreach ($dep in $deps) {
    $path = "$bootstrapPublish/$dep.dll"
    Assert-FileExists $path
    $inputs += $path
}

$libs = @("/lib:$bootstrapPublish", "/lib:MISAML/vendor")
$f2 = Get-ChildItem (Join-Path $Dist "MISAML/runtime/shared/Microsoft.NETCore.App") -Directory -ErrorAction SilentlyContinue |
    Select-Object -Last 1
if ($fw) { $libs += "/lib:$($f2.FullName)"}

Invoke-Native "dotnet" (@($ilRepack.FullName, "/target:library") + $libs + @("/out:$csharpOut/MISAML.dll") + $inputs)

# Copy MISAML.GodotAPI.dll to dist
Write-Step "Copying MISAML.GodotAPI.dll"
$godotApiDll = "MISAML/GodotAPI/bin/Release/net8.0/publish/MISAML.GodotAPI.dll"
Assert-FileExists $godotApiDll
Copy-Item $godotApiDll $csharpOut -Force
 
Write-Step "Assembling $Dist"
 
# Dist directory
$distMisaml = Join-Path $Dist "MISAML"
New-Item -ItemType Directory -Force -Path $distMisaml | Out-Null
 
# Old loose dependency dlls from earlier builds
Remove-Item (Join-Path $distMisaml "*.dll") -Force -ErrorAction SilentlyContinue
 
# Copy all files into dist
Copy-Item "$rustOut/misaml-cli.exe" $Dist -Force
Copy-Item "$rustOut/misaml_shim.dll" $distMisaml -Force
Copy-Item "$csharpOut/MISAML.dll" $distMisaml -Force
Copy-Item "$csharpOut/MISAML.GodotAPI.dll" $distMisaml -Force
 
Write-Host ""
Write-Host "Done. Output in: $(Resolve-Path $Dist)" -ForegroundColor Green
 
# Optional run argument to start the game
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
