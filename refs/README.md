# refs

Assemblies the mod is **compiled against** and never ships. The game's own come from the installed game; a mod loader's
come from here, so that the mod builds for **both** loaders with only one of them installed - or with neither. It is the
same arrangement as [`openxr/`](../openxr/), which carries Unity's OpenXR provider.

| File | From |
|---|---|
| `bepinex5\BepInEx.dll` | `BepInEx_win_x64_5.4.23.5.zip`, `BepInEx\core\` |
| `bepinex5\0Harmony.dll` | the same zip, the HarmonyX that BepInEx loads |
| `melonloader\MelonLoader.dll` | MelonLoader 0.7.3, `MelonLoader\net35\` |
| `melonloader\0Harmony.dll` | the same install, the HarmonyX that MelonLoader loads |

All are referenced with `<Private>false</Private>`: the player's loader supplies them at runtime. A build with a loader
actually installed in the game prefers that installation, so a newer loader can be tried without touching this folder.

To refresh them, take the files out of a released [BepInEx](https://github.com/BepInEx/BepInEx/releases) zip or a
[MelonLoader](https://github.com/LavaGang/MelonLoader/releases) install, and say here which build they came from.

BepInEx is LGPL-2.1 and MelonLoader is Apache-2.0; their licence texts are in
[`licenses/`](licenses/).
