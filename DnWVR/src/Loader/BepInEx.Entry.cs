using BepInEx;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace DnWVR
{
    /// <summary>BepInEx's door into the mod: it binds the log, the settings and Harmony, then steps aside.</summary>
    [BepInPlugin(Id, "DnWVR", "0.1.2")]
    [BepInProcess("DragNWash.exe")]
    public class BepInExEntry : BaseUnityPlugin
    {
        public const string Id = "com.widsofur.dnwvr";

        void Awake()
        {
            Log.Bind(Logger);
            PrefStore.Bind(Config);
            Host.Bind(new Harmony(Id), this);
            DnWVRMod.Initialize();

            SceneManager.sceneLoaded += (scene, mode) => DnWVRMod.SceneLoaded(scene.buildIndex, scene.name);
            // BepInEx wakes its plugins before the first scene loads, so that event covers every scene. If a loader ever
            // wakes us later, the scene already up would otherwise never be set up at all.
            var active = SceneManager.GetActiveScene();
            if (active.isLoaded) DnWVRMod.SceneLoaded(active.buildIndex, active.name);
        }

        void Update() => DnWVRMod.Tick();

        void OnApplicationQuit() => DnWVRMod.Quit();
    }
}
