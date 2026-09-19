using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DnWVR.Diag
{
    /// <summary>
    /// Looks for the install mistakes that leave a player with a flat game and nothing in the log to say why. The OpenXR
    /// files that come in the zip have to sit inside the game's data folder, where Unity looks for them; unpacked anywhere
    /// else the mod loads fine and VR simply never starts, and the only message used to be "is the headset connected?".
    /// </summary>
    public static class InstallCheck
    {
        // Relative to DragNWash_Data. The managed assemblies are not listed: without them the mod would not load at all.
        static readonly string[][] s_xrFiles =
        {
            new[] { "UnitySubsystems", "UnityOpenXR", "UnitySubsystemsManifest.json" },
            new[] { "Plugins", "x86_64", "UnityOpenXR.dll" },
            new[] { "Plugins", "x86_64", "openxr_loader.dll" },
        };

        /// <summary>Says what is wrong with the install, if anything, and returns whether VR has a chance to start.</summary>
        public static bool Run()
        {
            string data = Application.dataPath;
            string root = Path.GetDirectoryName(data);

            var missing = new List<string>();
            foreach (var parts in s_xrFiles)
            {
                string relative = Path.Combine(parts);
                if (!File.Exists(Path.Combine(data, relative))) missing.Add(relative);
            }
            if (missing.Count > 0)
            {
                Log.Error($"[Install] Unity's OpenXR files are missing from {data}: {string.Join(", ", missing)}. VR cannot " +
                          "start without them. Extract the DnWVR zip into the game folder itself - the one with DragNWash.exe - " +
                          "merging the folders it brings, not into a folder of its own.");
            }

            // Each loader proxies a system DLL next to the game; with both in place the mod loads twice and patches twice.
            bool melon = File.Exists(Path.Combine(root, "version.dll")) && Directory.Exists(Path.Combine(root, "MelonLoader"));
            bool bepinex = File.Exists(Path.Combine(root, "winhttp.dll")) && Directory.Exists(Path.Combine(root, "BepInEx", "core"));
            if (melon && bepinex)
            {
                Log.Warning("[Install] MelonLoader and BepInEx are both installed, so this mod is loaded twice and patches the " +
                            "game twice. Keep one: remove version.dll and the MelonLoader folder, or winhttp.dll and the " +
                            "BepInEx folder.");
            }
            return missing.Count == 0;
        }
    }
}
