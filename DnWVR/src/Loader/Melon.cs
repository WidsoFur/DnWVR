using System.Collections;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace DnWVR
{
    /// <summary>
    /// The mod's log. Every module writes here instead of being handed the loader's own logger, which is what keeps the
    /// rest of the source from naming a mod loader at all; the loader binds the sink it wants on the way in.
    /// </summary>
    public static class Log
    {
        static MelonLogger.Instance s_sink;

        internal static void Bind(MelonLogger.Instance sink) => s_sink = sink;

        public static void Msg(string message) => s_sink?.Msg(message);

        public static void Warning(string message) => s_sink?.Warning(message);

        public static void Error(string message) => s_sink?.Error(message);
    }

    /// <summary>One line in the settings file, whichever loader wrote it.</summary>
    public sealed class Pref<T>
    {
        readonly MelonPreferences_Entry<T> _entry;

        internal Pref(MelonPreferences_Entry<T> entry) => _entry = entry;

        public T Value
        {
            get => _entry.Value;
            set => _entry.Value = value;
        }
    }

    /// <summary>Where the settings live while the game runs: UserData\MelonPreferences.cfg, under [DnWVR].</summary>
    static class PrefStore
    {
        static MelonPreferences_Category s_category;

        internal static void Open() => s_category = MelonPreferences.CreateCategory("DnWVR", "Drag'n Wash VR");

        internal static Pref<T> Create<T>(string name, T fallback, string help) =>
            new Pref<T>(s_category.CreateEntry(name, fallback, help));

        internal static void Reload() => MelonPreferences.Load();

        internal static void Save() => MelonPreferences.Save();
    }

    /// <summary>What the mod asks of its loader beyond a log and a settings file.</summary>
    public static class Host
    {
        // HarmonyLib is spelled out because `using MelonLoader;` brings a MelonLoader.Harmony namespace into scope here,
        // which would shadow the type.
        public static HarmonyLib.Harmony Harmony { get; private set; }

        internal static void Bind(HarmonyLib.Harmony harmony) => Harmony = harmony;

        public static void StartCoroutine(IEnumerator routine) => MelonCoroutines.Start(routine);

        /// <summary>Where the F9 diagnostics dumps go.</summary>
        public static string DataDir => Path.Combine(MelonEnvironment.UserDataDirectory, "DnWVR");
    }
}
