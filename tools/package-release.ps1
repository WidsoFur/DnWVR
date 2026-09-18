<#
.SYNOPSIS
    Builds DnWVR and packs a release zip that extracts straight into the game folder.

.DESCRIPTION
    One zip per mod loader, each extracting straight into the game folder: the loader's own DLL - Mods\DnWVR.dll for
    MelonLoader, BepInEx\plugins\DnWVR\DnWVR.BepInEx.dll for BepInEx - plus Unity's OpenXR provider
    (DragNWash_Data\Managed, Plugins\x86_64 and UnitySubsystems), taken from openxr\ in this repository.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package-release.ps1
#>
param(
    # A folder holding the OpenXR provider, laid out like DragNWash_Data (default: openxr\ in this repository)
    [string]$ProviderDir,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist"),
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "DnWVR\DnWVR.csproj"
$props = Join-Path $root "Directory.Build.props"

[xml]$build = Get-Content -LiteralPath $props
$version = $build.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $props" }

if (-not $NoBuild) {
    dotnet build $root -c Release -p:SkipDeploy=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$dll = Join-Path $root "DnWVR\bin\Release\DnWVR.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Mod not built: $dll" }
$bepinex = Join-Path $root "DnWVR.BepInEx\bin\Release\DnWVR.BepInEx.dll"
if (-not (Test-Path -LiteralPath $bepinex)) { throw "BepInEx build missing: $bepinex" }

if (-not $ProviderDir) { $ProviderDir = Join-Path $root "openxr" }
if (-not (Test-Path -LiteralPath $ProviderDir)) { throw "OpenXR provider folder not found: $ProviderDir" }
$ProviderDir = (Resolve-Path -LiteralPath $ProviderDir).Path

# The OpenXR provider goes into both zips; a player needs it whichever loader they run.
# Archive path (always with forward slashes, as the zip format requires) -> source file
$provider = [ordered]@{}
foreach ($f in "Unity.XR.OpenXR.dll", "Unity.XR.Management.dll", "Unity.XR.CoreUtils.dll", "UnityEngine.SpatialTracking.dll", "UnityEngine.XR.LegacyInputHelpers.dll") {
    $provider["DragNWash_Data/Managed/$f"] = Join-Path $ProviderDir "Managed\$f"
}
foreach ($f in "UnityOpenXR.dll", "openxr_loader.dll") {
    $provider["DragNWash_Data/Plugins/x86_64/$f"] = Join-Path $ProviderDir "Plugins\x86_64\$f"
}
$provider["DragNWash_Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json"] = Join-Path $ProviderDir "UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json"

$packages = [ordered]@{
    "MelonLoader" = @{ "Mods/DnWVR.dll" = $dll }
    "BepInEx" = @{ "BepInEx/plugins/DnWVR/DnWVR.BepInEx.dll" = $bepinex }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

foreach ($loader in $packages.Keys) {
    $files = [ordered]@{}
    foreach ($entry in $packages[$loader].Keys) { $files[$entry] = $packages[$loader][$entry] }
    foreach ($entry in $provider.Keys) { $files[$entry] = $provider[$entry] }
    foreach ($source in $files.Values) {
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing file: $source" }
    }

    $zip = Join-Path $OutDir "DnWVR-$version-$loader.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.Keys) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $files[$entry], $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "Release package: $zip ($($files.Count) files)"
}

Write-Host "OpenXR provider taken from $ProviderDir"
