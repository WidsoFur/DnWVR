using MelonLoader;

[assembly: MelonInfo(typeof(DnWVR.MelonEntry), "DnWVR", "0.1.0", "WidsoFur")]
[assembly: MelonGame("Gator Dragon Games", "DragNWash")]
// Every patch in this mod is applied by hand, so the loader's sweep for [HarmonyPatch] classes has nothing to find.
[assembly: HarmonyDontPatchAll]

namespace DnWVR
{
    /// <summary>MelonLoader's door into the mod: it binds the log and Harmony, then steps aside.</summary>
    public class MelonEntry : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Log.Bind(LoggerInstance);
            Host.Bind(HarmonyInstance);
            DnWVRMod.Initialize();
        }

        public override void OnUpdate() => DnWVRMod.Tick();

        public override void OnSceneWasLoaded(int buildIndex, string sceneName) => DnWVRMod.SceneLoaded(buildIndex, sceneName);

        public override void OnApplicationQuit() => DnWVRMod.Quit();
    }
}
