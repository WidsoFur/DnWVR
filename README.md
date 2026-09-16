# DnWVR

**A VR mod for [Drag'n Wash](https://store.steampowered.com/app/4739660/).**

The game was made flat. This mod puts you inside it: stereo rendering through OpenXR, your own head, and the game's
paws on your hands. You wash the dragons with your arms, pick up the sponge by pointing at it, and the game's menus
and dialogue become panels in front of you. Nothing about the game itself is replaced.

![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3-5c8f2a?style=flat-square)
![OpenXR](https://img.shields.io/badge/OpenXR-SteamVR%20%7C%20Virtual%20Desktop-4a4a9c?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)
[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-widsofur-ffdd00?style=flat-square&logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/widsofur)

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

- **Other mod loaders** - the mod is MelonLoader-only today; the game hooks are plain Harmony patches, so a BepInEx
  entry point is mostly packaging work. Krazen's mod loader is planned too.
- **Full-body tracking** - hips and feet from extra trackers, so your body stands the way you do.
- **Haptics on touch** - a slap, a stroke or a scrub answered by the controller, not only by sound.

## Install

1. Install [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3) into the game folder
   (the one with `DragNWash.exe`), either with the installer or by unpacking `MelonLoader.x64.zip` there.
2. Download `DnWVR-<version>.zip` from [Releases](../../releases/latest) and extract it into the same folder,
   merging the folders it brings: `Mods\DnWVR.dll` plus Unity's OpenXR files in `DragNWash_Data`.
3. Start your VR runtime, then launch the game from Steam. VR starts on its own.

To uninstall, delete `Mods\DnWVR.dll`.

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

Every setting lives in `<game folder>\UserData\MelonPreferences.cfg`, each with a line saying what it does - turn
speed, hand offsets, body size, the pace of the sex scenes, the desktop view. Edit it while playing and press `F6`.

## Build

You need the [.NET SDK](https://dotnet.microsoft.com/download) 8.0 or newer and the game with MelonLoader; Unity is
not needed, as the OpenXR files ship in [`openxr/`](openxr/).

```bat
dotnet build -c Release
```

The DLL lands in `Mods` in the game folder, together with the OpenXR files if that game has none yet. If the game
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

DnWVR is MIT - see [LICENSE](LICENSE). It is an unofficial mod, not affiliated with the game's authors.

[`openxr/`](openxr/) carries Unity's OpenXR provider unmodified: Unity's own packages under the Unity Package
Distribution License and the Khronos OpenXR loader under Apache 2.0, with their licences in
[`openxr/licenses/`](openxr/licenses/).
