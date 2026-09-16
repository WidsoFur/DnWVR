<#
.SYNOPSIS
    Builds DnWVR and packs a release zip that extracts straight into the game folder.

.DESCRIPTION
    The zip holds Mods\DnWVR.dll and Unity's OpenXR provider (DragNWash_Data\Managed, Plugins\x86_64 and
    UnitySubsystems), taken from openxr\ in this repository.

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

[xml]$csproj = Get-Content -LiteralPath $project
$version = $csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }

if (-not $NoBuild) {
    dotnet build $project -c Release -p:SkipDeploy=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$dll = Join-Path $root "DnWVR\bin\Release\DnWVR.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Mod not built: $dll" }

if (-not $ProviderDir) { $ProviderDir = Join-Path $root "openxr" }
if (-not (Test-Path -LiteralPath $ProviderDir)) { throw "OpenXR provider folder not found: $ProviderDir" }
$ProviderDir = (Resolve-Path -LiteralPath $ProviderDir).Path

# Archive path (always with forward slashes, as the zip format requires) -> source file
$files = [ordered]@{ "Mods/DnWVR.dll" = $dll }
foreach ($f in "Unity.XR.OpenXR.dll", "Unity.XR.Management.dll", "Unity.XR.CoreUtils.dll", "UnityEngine.SpatialTracking.dll", "UnityEngine.XR.LegacyInputHelpers.dll") {
    $files["DragNWash_Data/Managed/$f"] = Join-Path $ProviderDir "Managed\$f"
}
foreach ($f in "UnityOpenXR.dll", "openxr_loader.dll") {
    $files["DragNWash_Data/Plugins/x86_64/$f"] = Join-Path $ProviderDir "Plugins\x86_64\$f"
}
$files["DragNWash_Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json"] = Join-Path $ProviderDir "UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json"

foreach ($source in $files.Values) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing file: $source" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path (Resolve-Path -LiteralPath $OutDir).Path "DnWVR-$version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $files.Keys) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $files[$entry], $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Release package: $zip (provider from $ProviderDir)"
