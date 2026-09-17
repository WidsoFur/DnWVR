using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace DnWVR.VR
{
    /// <summary>
    /// AutoInputSwitcher patches that keep XR devices from pausing the game or flipping the button glyphs.
    /// </summary>
    public static class InputPatches
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static MethodInfo s_trySetGlyphType;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                s_trySetGlyphType = AccessTools.Method(typeof(AutoInputSwitcher), "TrySetGlyphType");
                var original = AccessTools.Method(typeof(AutoInputSwitcher), "OnDeviceChanged");
                if (original == null)
                    Log.Warning("[InputPatches] AutoInputSwitcher.OnDeviceChanged not found; skipping");
                else
                {
                    harmony.Patch(original, new HarmonyMethod(typeof(InputPatches).GetMethod(nameof(OnDeviceChanged_Prefix), Any)));
                    Log.Msg("[InputPatches] patched AutoInputSwitcher.OnDeviceChanged");
                }
                var update = AccessTools.Method(typeof(AutoInputSwitcher), "Update");
                if (update == null)
                    Log.Warning("[InputPatches] AutoInputSwitcher.Update not found; skipping");
                else
                {
                    harmony.Patch(update, new HarmonyMethod(typeof(InputPatches).GetMethod(nameof(Update_Prefix), Any)));
                    Log.Msg("[InputPatches] patched AutoInputSwitcher.Update");
                }
            }
            catch (Exception e)
            {
                Log.Error("[InputPatches] failed: " + e);
            }

            // Diagnostic only: its own try, so a Steamworks load or patch failure never takes the patches above with it.
            try
            {
                var overlay = AccessTools.Method(typeof(AutoInputSwitcher), "OnGameOverlayActivated");
                if (overlay == null)
                    Log.Warning("[InputPatches] AutoInputSwitcher.OnGameOverlayActivated not found; skipping");
                else
                {
                    harmony.Patch(overlay, postfix: new HarmonyMethod(typeof(InputPatches).GetMethod(nameof(OnGameOverlayActivated_Postfix), Any)));
                    Log.Msg("[InputPatches] patched AutoInputSwitcher.OnGameOverlayActivated");
                }
            }
            catch (Exception e)
            {
                Log.Warning("[InputPatches] Steam overlay diagnostic not patched: " + e.Message);
            }
        }

        // The game fires a "Pause" intent on every device change (and throws before a menu exists), while XR devices and
        // the virtual gamepad appear at arbitrary moments, so they are hidden from it.
        static bool OnDeviceChanged_Prefix(InputDevice device, InputDeviceChange change)
        {
            if (device == null) return true;
            if (device is UnityEngine.InputSystem.XR.XRHMD || device is UnityEngine.InputSystem.XR.XRController || device is TrackedDevice)
                return false;
            var iface = device.description.interfaceName ?? string.Empty;
            if (iface == VRInput.InterfaceName || iface == "OpenXR" || iface == "XRInput" || iface == "XRInputV1")
                return false;
            return true;
        }

        // Laser clicks look like mouse presses and the virtual gamepad like an Xbox pad, so re-detecting the control type
        // would flicker the hints; while XR runs it stays pinned to controller (ForceControllerGlyphs).
        static bool Update_Prefix() => !XR.XRBootstrap.IsRunning;

        // Diagnostic: fires on Steam overlay / SteamVR dashboard open and close; the game sends its own "Pause" on open.
        static void OnGameOverlayActivated_Postfix()
        {
            Log.Msg("[InputPatches] Steam overlay activated -> game sends Pause");
        }

        /// <summary>Pin the game to controller mode with Xbox glyphs (matches Touch controllers).</summary>
        public static void ForceControllerGlyphs()
        {
            try
            {
                var inst = AccessTools.Field(typeof(AutoInputSwitcher), "instance")?.GetValue(null) as AutoInputSwitcher;
                if (inst == null || s_trySetGlyphType == null) return;
                s_trySetGlyphType.Invoke(inst, new object[] { ActionHintDatabase.GlyphType.Xbox });
                Log.Msg("[InputPatches] control type pinned to controller (Xbox glyphs)");
            }
            catch (Exception e)
            {
                Log.Warning("[InputPatches] ForceControllerGlyphs failed: " + e.Message);
            }
        }
    }
}
