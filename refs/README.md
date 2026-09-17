# refs

Assemblies the mod is **compiled against** and never ships. The game's own assemblies come from the installed game, and
MelonLoader's come from the installed loader; BepInEx is not on NuGet, so its two assemblies live here instead, the same
way [`openxr/`](../openxr/) carries Unity's OpenXR provider.

| File | From |
|---|---|
| `bepinex5\BepInEx.dll` | `BepInEx_win_x64_5.4.23.5.zip`, `BepInEx\core\` |
| `bepinex5\0Harmony.dll` | the same zip, the HarmonyX that BepInEx loads |

Both are referenced with `<Private>false</Private>`: the player's BepInEx supplies them at runtime.

To refresh them, download the release from [BepInEx](https://github.com/BepInEx/BepInEx/releases), copy the two files out
of `BepInEx\core\`, and say here which build they came from. A build with BepInEx actually installed prefers that
installation, so a newer loader can be tried without touching this folder.

BepInEx is LGPL-2.1; the licence text is in [`licenses/BepInEx-LICENSE.txt`](licenses/BepInEx-LICENSE.txt).
