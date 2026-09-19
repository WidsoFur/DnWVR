using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace DnWVR.VR
{
    /// <summary>
    /// Stereo rendering tweaks: disables screen-space post effects that are wrong per eye or uncomfortable in VR, and uses
    /// MSAA instead of post-process anti-aliasing on the cameras.
    /// </summary>
    public static class RenderTweaks
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Type[] DisabledOverrides =
        {
            typeof(Vignette), typeof(LensDistortion), typeof(ChromaticAberration), typeof(FilmGrain),
            typeof(MotionBlur), typeof(DepthOfField), typeof(PaniniProjection), typeof(ScreenSpaceLensFlare),
        };

        /// <summary>
        /// Pref: the savings that cost nothing visible in the headset - a cheaper SSAO, no fluid passes while there is no
        /// fluid, no bloom where it is too faint to see, and no copy of each eye that nothing reads. One switch, so that all
        /// of it can be compared against the game.
        /// </summary>
        public static bool Optimize = true;

        // Bloom this faint is a full chain of passes for nothing anyone can see; the sex scenes' own glow is well above it.
        const float InvisibleBloom = 0.05f;

        static FieldInfo s_ssaoSettings, s_ssaoDownsample, s_ssaoBlur, s_fluidSystems;
        static readonly Dictionary<object, KeyValuePair<bool, object>> s_ssaoAuthored = new Dictionary<object, KeyValuePair<bool, object>>();
        static readonly List<VolumeComponent> s_disabled = new List<VolumeComponent>();
        static bool s_fluidPasses = true;
        static ICollection s_fluidList;
        static bool s_fluidGateInstalled;
        static readonly HashSet<string> s_fluidSized = new HashSet<string>();
        static UniversalRenderPipelineAsset s_opaqueAsset;
        static bool s_opaqueAuthored;

        // Both fluid passes take their buffers' size from the depth texture when there is no opaque copy.
        static bool FluidSizeRedirected => s_fluidSized.Count == 2;

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            PatchFluidGate(harmony);
            try
            {
                var original = AccessTools.Method(typeof(CameraSettingsListener), "OnAntiAliasingChanged");
                if (original != null)
                {
                    harmony.Patch(original, new HarmonyMethod(typeof(RenderTweaks).GetMethod(nameof(OnAntiAliasingChanged_Prefix), Any)));
                    Log.Msg("[RenderTweaks] patched CameraSettingsListener.OnAntiAliasingChanged");
                }
            }
            catch (Exception e)
            {
                Log.Error("[RenderTweaks] patch failed: " + e);
            }
        }

        static bool OnAntiAliasingChanged_Prefix(CameraSettingsListener __instance)
        {
            if (!XR.XRBootstrap.IsRunning) return true;
            ApplyCamera(__instance.GetComponent<Camera>());
            return false;
        }

        /// <summary>Pref: turn off the game's fluid renderer feature, a diagnostic switch for stereo issues.</summary>
        public static bool DisableFluidFeature = false;
        static bool s_fluidFeatureDisabled;
        static int s_eyeLogFrames;
        static int s_renderCallsThisFrame;
        static int s_lastFrame = -1;
        static bool s_renderHookInstalled;

        /// <summary>
        /// Logs stereo diagnostics after XR starts: the display's render passes, the eye texture size and, for a few frames,
        /// the camera renders per frame.
        /// </summary>
        public static void LogStereoState()
        {
            try
            {
                var display = XR.XRBootstrap.Loader != null ? XR.XRBootstrap.Loader.GetLoadedSubsystem<UnityEngine.XR.XRDisplaySubsystem>() : null;
                if (display == null) { Log.Msg("[RenderTweaks] no display subsystem"); return; }
                int passes = display.GetRenderPassCount();
                Log.Msg($"[RenderTweaks] display running={display.running} renderPasses={passes} eyeTex={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight} " +
                        $"scale={UnityEngine.XR.XRSettings.eyeTextureResolutionScale} textureLayout={display.textureLayout}");
                for (int i = 0; i < passes; i++)
                {
                    display.GetRenderPass(i, out var pass);
                    var desc = pass.renderTargetDesc;
                    Log.Msg($"[RenderTweaks]   pass {i}: params={pass.GetRenderParameterCount()} rt={desc.width}x{desc.height} slices={desc.volumeDepth} msaa={desc.msaaSamples} cullingPass={pass.cullingPassIndex}");
                }
                var cam = Camera.main;
                if (cam != null)
                    Log.Msg($"[RenderTweaks] Camera.main stereoEnabled={cam.stereoEnabled} targetEye={cam.stereoTargetEye} rt={(cam.targetTexture ? cam.targetTexture.name : "none")} allowMSAA={cam.allowMSAA}");
                if (!s_renderHookInstalled)
                {
                    s_renderHookInstalled = true;
                    s_eyeLogFrames = 5;
                    UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[RenderTweaks] LogStereoState failed: " + e.Message);
            }
        }

        static void OnBeginCameraRendering(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (s_eyeLogFrames <= 0) return;
            if (Time.frameCount != s_lastFrame)
            {
                if (s_lastFrame >= 0)
                {
                    Log.Msg($"[RenderTweaks] frame {s_lastFrame}: {s_renderCallsThisFrame} camera render(s)");
                    s_eyeLogFrames--;
                }
                s_lastFrame = Time.frameCount;
                s_renderCallsThisFrame = 0;
            }
            s_renderCallsThisFrame++;
            if (cam == Camera.main)
                Log.Msg($"[RenderTweaks]   {cam.name} stereoActiveEye={cam.stereoActiveEye} stereoEnabled={cam.stereoEnabled} pixel={cam.pixelWidth}x{cam.pixelHeight}");
            if (s_eyeLogFrames <= 0)
            {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                s_renderHookInstalled = false; // a later LogStereoState (F11 restart) can arm it again
                s_lastFrame = -1;
            }
        }

        /// <summary>Re-evaluate the fluid feature switch after a preference reload (F6).</summary>
        public static void ReapplyFluidSwitch()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            if (DisableFluidFeature)
            {
                s_fluidFeatureDisabled = false;
                ApplyFluidFeatureSwitch();
            }
            else if (s_fluidFeatureDisabled)
            {
                try
                {
                    int n = 0;
                    foreach (var feature in Resources.FindObjectsOfTypeAll<ScriptableRendererFeature>())
                    {
                        if (feature.GetType().Name != "FluidRenderingRendererFeature") continue;
                        feature.SetActive(true);
                        n++;
                    }
                    s_fluidFeatureDisabled = false;
                    Log.Msg($"[RenderTweaks] fluid renderer feature re-enabled on {n} renderer(s)");
                }
                catch (Exception e) { Log.Warning("[RenderTweaks] fluid re-enable failed: " + e.Message); }
            }
        }

        static void ApplyFluidFeatureSwitch()
        {
            if (!DisableFluidFeature || s_fluidFeatureDisabled) return;
            try
            {
                int n = 0;
                foreach (var feature in Resources.FindObjectsOfTypeAll<ScriptableRendererFeature>())
                {
                    if (feature.GetType().Name != "FluidRenderingRendererFeature") continue;
                    feature.SetActive(false);
                    n++;
                }
                s_fluidFeatureDisabled = true;
                Log.Msg($"[RenderTweaks] fluid renderer feature disabled on {n} renderer(s) (DisableFluidFeature pref)");
            }
            catch (Exception e)
            {
                Log.Warning("[RenderTweaks] fluid feature switch failed: " + e.Message);
            }
        }

        public static void ApplyToScene()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            ApplyFluidFeatureSwitch();
            ApplySsao();
            ApplyOpaqueCopy();
            int disabled = 0, faintBloom = 0;
            try
            {
                foreach (var volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var profile = volume.sharedProfile;
                    if (profile == null) continue;
                    foreach (var comp in profile.components)
                    {
                        if (comp == null) continue;
                        foreach (var t in DisabledOverrides)
                        {
                            if (t.IsInstanceOfType(comp) && comp.active)
                            {
                                comp.active = false;
                                s_disabled.Add(comp);
                                disabled++;
                            }
                        }
                        if (Optimize && comp is Bloom bloom && bloom.active && bloom.intensity.value < InvisibleBloom)
                        {
                            bloom.active = false;
                            s_disabled.Add(bloom);
                            faintBloom++;
                        }
                    }
                }
                foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    ApplyCamera(cam);
            }
            catch (Exception e)
            {
                Log.Warning("[RenderTweaks] ApplyToScene failed: " + e.Message);
            }
            if (disabled > 0) Log.Msg($"[RenderTweaks] disabled {disabled} post-processing overrides for VR");
            if (faintBloom > 0) Log.Msg($"[RenderTweaks] disabled {faintBloom} bloom overrides too faint to see");
        }

        /// <summary>
        /// Puts back what VR changed on assets the flat game shares - the post-processing overrides and the SSAO settings -
        /// so that stopping VR (F11) leaves the game looking as it did.
        /// </summary>
        public static void Restore()
        {
            foreach (var comp in s_disabled)
                if (comp != null) comp.active = true;
            s_disabled.Clear();
            try
            {
                foreach (var pair in s_ssaoAuthored)
                {
                    s_ssaoDownsample.SetValue(pair.Key, pair.Value.Key);
                    s_ssaoBlur.SetValue(pair.Key, pair.Value.Value);
                }
            }
            catch (Exception e) { Log.Warning("[RenderTweaks] SSAO settings not restored: " + e.Message); }
            s_ssaoAuthored.Clear();
            if (s_opaqueAsset != null) s_opaqueAsset.supportsCameraOpaqueTexture = s_opaqueAuthored;
            s_opaqueAsset = null;
        }

        /// <summary>After a preference reload (F6): puts the game's settings back and applies them again under the switch as it now is.</summary>
        public static void ReapplyOptimizations()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            Restore();
            ApplyToScene();
            DesktopView.Refresh();
        }

        /// <summary>
        /// URP copies every eye's colour into _CameraOpaqueTexture after the opaques, for shaders that show what is behind
        /// them. None of this game's shaders reads it; the fluid passes ask for it only to size their buffers, and they are
        /// sized from the depth texture instead (or, failing that, the copy is kept on the renders that draw fluid).
        /// </summary>
        static void ApplyOpaqueCopy()
        {
            if (!Optimize || s_opaqueAsset != null || !(FluidSizeRedirected || s_fluidGateInstalled)) return;
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null || !asset.supportsCameraOpaqueTexture) return;
            s_opaqueAsset = asset;
            s_opaqueAuthored = true;
            asset.supportsCameraOpaqueTexture = false;
            Log.Msg("[RenderTweaks] no opaque copy of each eye" + (FluidSizeRedirected ? "" : ", except on renders that draw fluid"));
        }

        /// <summary>
        /// Runs the game's SSAO at a quarter of the pixels with the one-pass blur. It is a full-resolution effect with a
        /// three-pass blur on every eye, and at the faint intensity this game uses the difference cannot be seen. The feature
        /// reads these settings every frame, so the change takes hold at once and is undone by <see cref="Restore"/>.
        /// </summary>
        static void ApplySsao()
        {
            if (!Optimize) return;
            try
            {
                int changed = 0;
                foreach (var feature in Resources.FindObjectsOfTypeAll<ScriptableRendererFeature>())
                {
                    if (feature.GetType().Name != "ScreenSpaceAmbientOcclusion") continue;
                    if (s_ssaoSettings == null)
                    {
                        s_ssaoSettings = AccessTools.Field(feature.GetType(), "m_Settings");
                        var settingsType = s_ssaoSettings.FieldType;
                        s_ssaoDownsample = AccessTools.Field(settingsType, "Downsample");
                        s_ssaoBlur = AccessTools.Field(settingsType, "BlurQuality");
                    }
                    var settings = s_ssaoSettings.GetValue(feature);
                    if (settings == null || s_ssaoAuthored.ContainsKey(settings)) continue;
                    s_ssaoAuthored[settings] = new KeyValuePair<bool, object>(
                        (bool)s_ssaoDownsample.GetValue(settings), s_ssaoBlur.GetValue(settings));
                    s_ssaoDownsample.SetValue(settings, true);
                    s_ssaoBlur.SetValue(settings, Enum.Parse(s_ssaoBlur.FieldType, "Low"));
                    changed++;
                }
                if (changed > 0) Log.Msg($"[RenderTweaks] SSAO downsampled with the one-pass blur on {changed} renderer(s)");
            }
            catch (Exception e)
            {
                Log.Warning("[RenderTweaks] SSAO left as the game has it: " + e.Message);
            }
        }

        /// <summary>
        /// The fluid renderer clears two full-size targets and composites a full-screen copy on every render of every eye,
        /// whether or not anything in the scene is liquid. Its systems register themselves while they are enabled, so an empty
        /// list is a frame with nothing to draw, and the passes are simply not queued then.
        /// </summary>
        static void PatchFluidGate(HarmonyLib.Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("FluidRenderingForGames.FluidRenderingRendererFeature");
                var original = type != null ? AccessTools.Method(type, "AddRenderPasses") : null;
                s_fluidSystems = type != null ? AccessTools.Field(type, "systems") : null;
                if (original == null || s_fluidSystems == null)
                {
                    Log.Warning("[RenderTweaks] fluid renderer not as expected; its passes run every frame as before");
                    return;
                }
                harmony.Patch(original, new HarmonyMethod(typeof(RenderTweaks).GetMethod(nameof(FluidAddRenderPasses_Prefix), Any)));
                s_fluidGateInstalled = true;
                Log.Msg("[RenderTweaks] patched FluidRenderingRendererFeature.AddRenderPasses");
                PatchFluidSize(harmony, type);
            }
            catch (Exception e)
            {
                Log.Warning("[RenderTweaks] fluid gate not installed: " + e.Message);
            }
        }

        static bool FluidAddRenderPasses_Prefix(object __instance, ref RenderingData renderingData)
        {
            bool run = true;
            if (Optimize && XR.XRBootstrap.IsRunning)
            {
                // The list is created once with the feature's type and only ever added to and removed from.
                var systems = s_fluidList;
                if (systems == null)
                {
                    systems = (s_fluidSystems.IsStatic ? s_fluidSystems.GetValue(null) : s_fluidSystems.GetValue(__instance)) as ICollection;
                    if (s_fluidSystems.IsStatic) s_fluidList = systems;
                }
                run = systems == null || systems.Count > 0;
                if (run != s_fluidPasses)
                {
                    s_fluidPasses = run;
                    Log.Msg($"[RenderTweaks] fluid passes {(run ? "on" : "off")}, {(systems != null ? systems.Count : 0)} fluid systems");
                }
            }
            // Without the size redirect the passes read the opaque texture's size, so wherever they run it has to exist.
            if (run && s_opaqueAsset != null && !FluidSizeRedirected) renderingData.cameraData.requiresOpaqueTexture = true;
            return run;
        }

        /// <summary>
        /// Each fluid pass reads the size of the camera's opaque texture and nothing else of it. Where that texture is not
        /// made, the depth texture the pass already draws against gives the same size, and the pass is pointed at it.
        /// </summary>
        static void PatchFluidSize(HarmonyLib.Harmony harmony, Type feature)
        {
            var transpiler = new HarmonyMethod(typeof(RenderTweaks).GetMethod(nameof(FluidSize_Transpiler), Any));
            foreach (var name in new[] { "FluidHeightPass", "FluidColorPass" })
            {
                try
                {
                    var pass = AccessTools.Inner(feature, name);
                    var record = pass != null ? AccessTools.Method(pass, "RecordRenderGraph") : null;
                    if (record == null) Log.Warning($"[RenderTweaks] fluid {name} not found");
                    else harmony.Patch(record, transpiler: transpiler);
                }
                catch (Exception e)
                {
                    Log.Warning($"[RenderTweaks] fluid {name} left sized from the opaque texture: " + e.Message);
                }
            }
            if (FluidSizeRedirected) Log.Msg("[RenderTweaks] fluid passes sized from the depth texture where there is no opaque copy");
        }

        static IEnumerable<CodeInstruction> FluidSize_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);
            var opaque = AccessTools.PropertyGetter(typeof(UniversalResourceData), nameof(UniversalResourceData.cameraOpaqueTexture));
            var desc = AccessTools.Method(typeof(RenderGraph), nameof(RenderGraph.GetTextureDesc), new[] { typeof(TextureHandle).MakeByRefType() });
            int getter = list.FindIndex(ci => ci.Calls(opaque));
            int read = getter < 0 ? -1 : list.FindIndex(getter, ci => ci.Calls(desc));
            // Exactly one read, handed through a local straight to GetTextureDesc with no other call on the way: anything
            // else is a build this was not made for.
            bool direct = getter >= 0 && read > getter && read - getter <= 4;
            for (int i = getter + 1; direct && i < read; i++)
                if (list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt) direct = false;
            if (!direct || list.FindIndex(getter + 1, ci => ci.Calls(opaque)) >= 0)
            {
                Log.Warning($"[RenderTweaks] {original.DeclaringType.Name} not as expected, it keeps the opaque texture");
                return list;
            }
            list[getter].opcode = OpCodes.Call;
            list[getter].operand = AccessTools.Method(typeof(RenderTweaks), nameof(FluidSizeSource));
            s_fluidSized.Add(original.DeclaringType.Name);
            return list;
        }

        // Same size, dimension and samples as the opaque texture, and Point-filtered like it with this game's settings;
        // the pass sets its own format.
        static TextureHandle FluidSizeSource(UniversalResourceData data)
        {
            var opaque = data.cameraOpaqueTexture;
            return opaque.IsValid() ? opaque : data.cameraDepthTexture;
        }

        static void ApplyCamera(Camera cam)
        {
            // The desktop view picks its own anti-aliasing.
            if (cam == null || DesktopView.Owns(cam)) return;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data != null)
            {
                data.antialiasing = AntialiasingMode.None;
            }
            cam.allowMSAA = true;
        }
    }
}
