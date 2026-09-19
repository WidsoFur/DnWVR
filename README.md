# DnWVR - A VR mod for [Drag'n Wash](https://store.steampowered.com/app/4739660/).

The game was made flat. This mod puts you inside it: stereo rendering through OpenXR, your own head, and the game's
paws on your hands. You wash the dragons with your arms, pick up the sponge by pointing at it, and the game's menus
and dialogue become panels in front of you. Nothing about the game itself is replaced.

![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3-5c8f2a?style=flat-square)
![OpenXR](https://img.shields.io/badge/OpenXR-SteamVR%20%7C%20Virtual%20Desktop-4a4a9c?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)
[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-widsofur-ffdd00?style=flat-square&logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/widsofur)

## Disclaimer

DnWVR is an independent mod. It is not affiliated with Gator Dragon Games, the makers of Drag'n Wash, nor with any other
mod for the game. It is distributed only here on GitHub; a copy from anywhere else did not come from its author.

## Features

- **VR** - stereo rendering and head tracking through OpenXR, tested with SteamVR and Virtual Desktop.
- **Two hands** - the game's paw on both controllers. Touch the dragon to stroke it, hit it to slap, push buttons and
  the phone.
- **Item laser** - point at a tool or a stand and grip to pick it up, put it back or use it.
- **A camera that stays yours** - dialogue never moves your view, cutscenes leave your head free, camera cuts hide
  behind a short fade. Snap or smooth turning.
- **VR interface** - menus, HUD and dialogue drawn over the world, with a pointer on the hand you last gripped with.
  Dialogue advances on its own; grip or trigger skips a line.
- **Interactive scenes** - your hands set the pace and build the dragon's pleasure, which brings its lines and
  its climax. Hold **A** to finish sooner, and walk around while you do.

## Plans

- **Other mod loaders** - MelonLoader and BepInEx are supported; Krazen's mod loader is planned too.
- **Full-body tracking** - hips and feet from extra trackers, so your body stands the way you do.
- **Haptics on touch** - a slap, a stroke or a scrub answered by the controller, not only by sound.

## Install

The mod comes as one download per mod loader. Install **one** loader, never both: each hooks the game on its own, so
with two in the folder the mod loads twice and patches the game twice.

<details open>
<summary><b>BepInEx</b> - recommended</summary>

1. Download [`BepInEx_win_x64_5.4.23.5.zip`](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip),
   the **x64** one: the game is 64-bit, and the x86 build does nothing at all, without a word. Extract it into the game
   folder so that `winhttp.dll` ends up right next to `DragNWash.exe`, not in a folder of its own.
2. Extract [`DnWVR-0.1.1-BepInEx.zip`](../../releases/download/v0.1.1/DnWVR-0.1.1-BepInEx.zip) into the same folder,
   merging the folders it brings: `BepInEx\plugins\DnWVR` plus Unity's OpenXR files in `DragNWash_Data`.
3. Start your VR runtime, then launch the game from Steam. VR starts on its own.

If the game starts flat, look in `BepInEx`: after the first launch it holds `LogOutput.log` and `config`. If they are
not there, BepInEx never ran - check that `winhttp.dll` sits next to `DragNWash.exe` and is 26 KB, not 22 KB.

Settings live in `BepInEx\config\com.widsofur.dnwvr.cfg`, each with a line saying what it does. To uninstall, delete
`BepInEx\plugins\DnWVR`.

</details>

<details>
<summary><b>MelonLoader</b></summary>

1. Install MelonLoader 0.7.3 into the game folder - the one with `DragNWash.exe` - in **one** of these two ways, not
   both: they put the same MelonLoader in the same place, so the second only gets in the way of the first.
   * **either** [`MelonLoader.Installer.exe`](https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.Installer.exe):
     run it, pick `DragNWash.exe` and version 0.7.3;
   * **or** [`MelonLoader.x64.zip`](https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip):
     unpack it into the game folder so that `version.dll` ends up right next to `DragNWash.exe`.
2. Extract [`DnWVR-0.1.1-MelonLoader.zip`](../../releases/download/v0.1.1/DnWVR-0.1.1-MelonLoader.zip) into the same
   folder, merging the folders it brings: `Mods\DnWVR.dll` plus Unity's OpenXR files in `DragNWash_Data`.
3. Start your VR runtime, then launch the game from Steam. VR starts on its own.

Settings live in `UserData\MelonPreferences.cfg`, each with a line saying what it does. To uninstall, delete
`Mods\DnWVR.dll`.

</details>

Both downloads are on the [releases page](../../releases/latest), and `F6` re-reads the settings file while you play.

## Controls

| Input | Action |
|---|---|
| Sticks | Move and turn |
| Bare paws | Stroke, slap, push buttons and the phone |
| Point + **Grip** | Pick up an item, put it back, use it - and skip the dialogue line |
| **Trigger** | Use the held tool |
| Laser + **Trigger** | Menus and dialogue answers; the laser moves to whichever hand pulls the trigger |
| **A** / **B** | Jump / Crouch, and hold **A** in a sex scene to finish it |
| **X** | Pause |

Keyboard: `F5` head tracking on/off, `F6` reload settings, `F8` recenter, `F11` stop/start VR.

Every setting lives in the config file named in the install steps above, each with a line saying what it does - turn
speed, hand offsets, body size, the pace of the sex scenes, the desktop view. Edit it while playing and press `F6`.

The **Performance** row in the VR section of the game's options starts at Balanced: no ambient occlusion, lighter
shadows and desktop window's view, and the eyes at 90%. If the game stutters in the headset, step it to Fast (80% with
2x MSAA) or Fastest (70%, with the window showing the left eye). Quality is the game's own picture, whose ambient
occlusion shimmers in a headset.
The log has a `[Perf]` line every 5 s with the frame times and the GPU and compositor times the VR runtime reports;
attach it when you report it. `VRRenderOptimizations` turns off the mod's own rendering savings, to compare against
the game as it ships.

## Build

You need the [.NET SDK](https://dotnet.microsoft.com/download) 8.0 or newer and the game. Unity is not needed, as
the OpenXR files ship in [`openxr/`](openxr/), and neither is BepInEx, as the assemblies it is compiled against ship
in [`refs/`](refs/).

```bat
dotnet build -c Release
```

That builds the mod for both loaders from one source tree: `DnWVR.dll` for MelonLoader and `DnWVR.BepInEx.dll` for
BepInEx, differing only in the four files under [`DnWVR/src/Loader`](DnWVR/src/Loader). Each is deployed into the game
if its loader is installed there, so having one of them is enough; `-p:SkipDeploy=true` deploys neither. The OpenXR
files are laid down with them if that game has none yet. If the game
is not in the default Steam path, put your own in `Directory.Build.user.props`:

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:\SteamLibrary\steamapps\common\Drag'n Wash</GameDir>
  </PropertyGroup>
</Project>
```

`tools\package-release.ps1` builds the release zip.

## Licence

DnWVR is MIT - see [LICENSE](LICENSE).

[`openxr/`](openxr/) carries Unity's OpenXR provider unmodified: Unity's own packages under the Unity Package
Distribution License and the Khronos OpenXR loader under Apache 2.0, with their licences in
[`openxr/licenses/`](openxr/licenses/).
