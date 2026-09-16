# Unity's OpenXR provider

Drag'n Wash was built without VR, so its player has no XR assemblies, no native OpenXR plugin and no subsystem
manifest. The mod cannot invent those: they are parts of the Unity engine and have to come from a Unity build.
They live here so that installing the mod, packaging a release and building the code all work with nothing but this
repository and the .NET SDK.

The layout mirrors the game's `DragNWash_Data`, so installing is a plain copy:

| From here | Into the game |
|---|---|
| `Managed\*.dll` | `DragNWash_Data\Managed` |
| `Plugins\x86_64\*.dll` | `DragNWash_Data\Plugins\x86_64` |
| `UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json` | `DragNWash_Data\UnitySubsystems\UnityOpenXR` |

Building the mod copies them into the game next to the DLL, and every release zip carries the same files. The mod
also compiles against `Managed\`, so a fresh clone builds without a VR-patched game.

## What these files are

| File | Package | Version |
|---|---|---|
| `Unity.XR.OpenXR.dll`, `UnityOpenXR.dll` | com.unity.xr.openxr | 1.16.0 |
| `openxr_loader.dll` | OpenXR-SDK (Khronos), shipped inside com.unity.xr.openxr | 1.16.0 |
| `Unity.XR.Management.dll` | com.unity.xr.management | 4.5.3 |
| `Unity.XR.CoreUtils.dll` | com.unity.xr.core-utils | pulled in by the two above |
| `UnityEngine.SpatialTracking.dll`, `UnityEngine.XR.LegacyInputHelpers.dll` | com.unity.xr.legacyinputhelpers | pulled in by the two above |

`UnitySubsystemsManifest.json` is what tells the engine that an XR display and input subsystem exist at all.

They were taken, byte for byte, from a Mono Win64 player built by Unity **6000.3.21f1** with those packages and
managed stripping switched off - the same engine generation as the game (6000.3.14f1).

## Licences

`licenses\` holds the upstream texts as they ship with the packages.

- The Unity packages are covered by the Unity Companion License (source) and the **Unity Package Distribution
  License**, which is the one that allows these binaries to be redistributed, unmodified, as part of software made
  with Unity. That is what this mod is: the files are installed into a Unity game and do nothing on their own.
- `openxr_loader.dll` is the Khronos OpenXR loader under **Apache License 2.0** (see the third-party notices), which
  permits redistribution with the licence and notices kept - they are in `licenses\`.

Nothing here is modified in any way; if a file ever needs to change, it is replaced by a fresh Unity build, not
patched.

## Refreshing them

Only needed to move to another package version or another engine generation. Make an empty Unity project of the
game's engine generation, add `com.unity.xr.openxr` and `com.unity.xr.management` at the versions you want, build a
**Mono**, **Win64** player with **managed stripping disabled**, and copy the eight files out of its `*_Data` folder
into the folders above.
