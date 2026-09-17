using MelonLoader;

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
}
