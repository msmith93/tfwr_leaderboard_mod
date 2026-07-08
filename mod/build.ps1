<#
.SYNOPSIS
    Builds TFWRRemoteAPI and installs the plugin into the game's BepInEx/plugins folder (Windows).

.DESCRIPTION
    Windows equivalent of build.sh. Resolves the game install directory, compiles the
    plugin with the .NET SDK (net46 target), and copies TFWRRemoteAPI.dll into
    <GameDir>\BepInEx\plugins\. Restart the game afterwards to load the mod.

    NOTE: This script and the Windows build path were written by Claude and have NOT
    been tested on a real Windows machine. Verify the game path and report issues.

.PARAMETER GameDir
    Path to "The Farmer Was Replaced" install directory. If omitted, uses the
    TFWR_GAME_DIR environment variable, then the default Steam location
    "C:\Program Files (x86)\Steam\steamapps\common\The Farmer Was Replaced".

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\The Farmer Was Replaced"
#>
[CmdletBinding()]
param(
    [string]$GameDir
)

$ErrorActionPreference = "Stop"

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $ScriptDir "TFWRRemoteAPI"

# Resolve game dir: -GameDir param > TFWR_GAME_DIR env var > default Steam path.
if (-not $GameDir) {
    if ($env:TFWR_GAME_DIR) {
        $GameDir = $env:TFWR_GAME_DIR
    } else {
        $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\The Farmer Was Replaced"
    }
}

$PluginDir = Join-Path $GameDir "BepInEx\plugins"

if (-not (Test-Path -LiteralPath $GameDir)) {
    Write-Error "Game directory not found: $GameDir`nPass the correct path with -GameDir `"<path>`" (or set `$env:TFWR_GAME_DIR)."
    exit 1
}
if (-not (Test-Path -LiteralPath $PluginDir)) {
    Write-Error "BepInEx plugins folder not found: $PluginDir`nMake sure BepInEx 5.4.23 is installed in the game directory and has been run once."
    exit 1
}

Write-Host "Building TFWRRemoteAPI..."
Push-Location $ProjectDir
try {
    # Build with .NET SDK targeting net46. Pass GameDir so reference DLLs resolve.
    dotnet build -c Release -p:GameDir="$GameDir"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet build failed with exit code $LASTEXITCODE"
        exit 1
    }

    $DllPath = Join-Path $ProjectDir "bin\Release\TFWRRemoteAPI.dll"
    if (-not (Test-Path -LiteralPath $DllPath)) {
        Write-Error "Build output not found at $DllPath"
        Write-Host "Checking for DLL in other locations..."
        Get-ChildItem -Path (Join-Path $ProjectDir "bin") -Recurse -Filter "TFWRRemoteAPI.dll" -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Host $_.FullName }
        exit 1
    }

    # Copy to BepInEx plugins.
    Copy-Item -LiteralPath $DllPath -Destination (Join-Path $PluginDir "TFWRRemoteAPI.dll") -Force
    Write-Host "Installed to $(Join-Path $PluginDir 'TFWRRemoteAPI.dll')"
    Write-Host "Done! Restart the game to load the mod."
}
finally {
    Pop-Location
}
