using System.Collections;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace DnWVR
{
    /// <summary>
    /// The mod's log. Every module writes here instead of being handed the loader's own logger, which is what keeps the
    /// rest of the source from naming a mod loader at all; the loader binds the sink it wants on the way in.
    /// </summary>
    public static class Log
    {
        static ManualLogSource s_sink;

        internal static void Bind(ManualLogSource sink) => s_sink = sink;

        public static void Msg(string message) => s_sink?.LogInfo(message);

        public static void Warning(string message) => s_sink?.LogWarning(message);

        public static void Error(string message) => s_sink?.LogError(message);
    }

    /// <summary>One line in the settings file, whichever loader wrote it.</summary>
    public sealed class Pref<T>
    {
        readonly ConfigEntry<T> _entry;

        internal Pref(ConfigEntry<T> entry) => _entry = entry;

        public T Value
        {
            get => _entry.Value;
            set => _entry.Value = value;
        }
    }

    /// <summary>Where the settings live while the game runs: BepInEx\config\com.widsofur.dnwvr.cfg, under [DnWVR].</summary>
    static class PrefStore
    {
        static ConfigFile s_config;

        internal static void Bind(ConfigFile config) => s_config = config;

        // BepInEx writes the whole file every time a setting is set, which would fight a player editing it while the game
        // runs - the very thing F6 is for. The mod saves when it means to instead.
        internal static void Open() => s_config.SaveOnConfigSet = false;

        internal static Pref<T> Create<T>(string name, T fallback, string help) =>
            new Pref<T>(s_config.Bind("DnWVR", name, fallback, help));

        internal static void Reload() => s_config.Reload();

        internal static void Save() => s_config.Save();
    }

    /// <summary>What the mod asks of its loader beyond a log and a settings file.</summary>
    public static class Host
    {
        static MonoBehaviour s_runner;

        public static HarmonyLib.Harmony Harmony { get; private set; }

        internal static void Bind(HarmonyLib.Harmony harmony, MonoBehaviour runner)
        {
            Harmony = harmony;
            s_runner = runner;
        }

        // BepInEx plugins are MonoBehaviours, so the plugin itself runs the mod's coroutines.
        public static void StartCoroutine(IEnumerator routine) => s_runner.StartCoroutine(routine);

        /// <summary>Where the F9 diagnostics dumps go.</summary>
        public static string DataDir => Path.Combine(Paths.BepInExRootPath, "DnWVR");
    }
}
