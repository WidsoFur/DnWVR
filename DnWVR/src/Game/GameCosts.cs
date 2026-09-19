using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityScriptableSettings;

namespace DnWVR.Game
{
    /// <summary>
    /// Work the game repeats every frame for the same answer, flat or in VR. Each patch returns exactly what the game would
    /// have, and does without the repeat.
    /// </summary>
    public static class GameCosts
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Dictionary<string, Setting> s_settings = new Dictionary<string, Setting>();
        static int s_jiggleFrame = -1;
        static bool s_jiggleLogged;

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            PatchSettings(harmony);
            PatchJiggle(harmony);
        }

        // SettingsManager.GetSetting walks the list comparing each setting's name, and every read of a Unity object's name
        // makes a new string: PlayerController asks for its crouch and mouse settings each frame, some 0.7 KB of garbage a
        // frame for the collector. The names are asset names and never change, so the first answer is kept.
        static void PatchSettings(HarmonyLib.Harmony harmony)
        {
            try
            {
                var type = typeof(SettingsManager);
                harmony.Patch(AccessTools.Method(type, nameof(SettingsManager.GetSetting)),
                    new HarmonyMethod(typeof(GameCosts).GetMethod(nameof(GetSetting_Prefix), Any)),
                    new HarmonyMethod(typeof(GameCosts).GetMethod(nameof(GetSetting_Postfix), Any)));
                var forget = new HarmonyMethod(typeof(GameCosts).GetMethod(nameof(ForgetSettings), Any));
                harmony.Patch(AccessTools.Method(type, nameof(SettingsManager.AddSetting)), postfix: forget);
                harmony.Patch(AccessTools.Method(type, nameof(SettingsManager.RemoveSetting)), postfix: forget);
                Log.Msg("[GameCosts] settings looked up once");
            }
            catch (Exception e)
            {
                Log.Warning("[GameCosts] settings looked up as the game does: " + e.Message);
            }
        }

        static bool GetSetting_Prefix(string name, ref Setting __result)
        {
            if (name == null || !s_settings.TryGetValue(name, out var setting) || setting == null) return true;
            __result = setting;
            return false;
        }

        static void GetSetting_Postfix(string name, Setting __result)
        {
            if (name != null && __result != null) s_settings[name] = __result;
        }

        static void ForgetSettings() => s_settings.Clear();

        // The start scene's jiggle updater survives into the washing scene, which brings its own, and each runs the whole
        // pose pass and waits for its jobs; the second finds the simulation done and writes the same pose again.
        static void PatchJiggle(HarmonyLib.Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("GatorDragonGames.JigglePhysics.JiggleUpdateExample");
                var lateUpdate = type != null ? AccessTools.Method(type, "LateUpdate") : null;
                if (lateUpdate == null)
                {
                    Log.Warning("[GameCosts] jiggle updater not found, left as it is");
                    return;
                }
                harmony.Patch(lateUpdate, new HarmonyMethod(typeof(GameCosts).GetMethod(nameof(JiggleLateUpdate_Prefix), Any)));
                Log.Msg("[GameCosts] jiggle pose done once a frame");
            }
            catch (Exception e)
            {
                Log.Warning("[GameCosts] jiggle updater left as it is: " + e.Message);
            }
        }

        static bool JiggleLateUpdate_Prefix()
        {
            int frame = Time.frameCount;
            if (frame != s_jiggleFrame)
            {
                s_jiggleFrame = frame;
                return true;
            }
            if (!s_jiggleLogged)
            {
                s_jiggleLogged = true;
                Log.Msg("[GameCosts] a second jiggle updater is running; its repeat of the pose is skipped");
            }
            return false;
        }
    }
}
