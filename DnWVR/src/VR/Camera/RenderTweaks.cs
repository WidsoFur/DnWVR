using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
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

        public static void ApplyPatches(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            try
            {
                var original = AccessTools.Method(typeof(CameraSettingsListener), "OnAntiAliasingChanged");
                if (original != null)
                {
                    harmony.Patch(original, new HarmonyMethod(typeof(RenderTweaks).GetMethod(nameof(OnAntiAliasingChanged_Prefix), Any)));
                    log.Msg("[RenderTweaks] patched CameraSettingsListener.OnAntiAliasingChanged");
                }
            }
            catch (Exception e)
            {
                log.Error("[RenderTweaks] patch failed: " + e);
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
        public static void LogStereoState(MelonLogger.Instance log)
        {
            try
            {
                var display = XR.XRBootstrap.Loader != null ? XR.XRBootstrap.Loader.GetLoadedSubsystem<UnityEngine.XR.XRDisplaySubsystem>() : null;
                if (display == null) { log.Msg("[RenderTweaks] no display subsystem"); return; }
                int passes = display.GetRenderPassCount();
                log.Msg($"[RenderTweaks] display running={display.running} renderPasses={passes} eyeTex={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight} " +
                        $"scale={UnityEngine.XR.XRSettings.eyeTextureResolutionScale} textureLayout={display.textureLayout}");
                for (int i = 0; i < passes; i++)
                {
                    display.GetRenderPass(i, out var pass);
                    var desc = pass.renderTargetDesc;
                    log.Msg($"[RenderTweaks]   pass {i}: params={pass.GetRenderParameterCount()} rt={desc.width}x{desc.height} slices={desc.volumeDepth} msaa={desc.msaaSamples} cullingPass={pass.cullingPassIndex}");
                }
                var cam = Camera.main;
                if (cam != null)
                    log.Msg($"[RenderTweaks] Camera.main stereoEnabled={cam.stereoEnabled} targetEye={cam.stereoTargetEye} rt={(cam.targetTexture ? cam.targetTexture.name : "none")} allowMSAA={cam.allowMSAA}");
                if (!s_renderHookInstalled)
                {
                    s_renderHookInstalled = true;
                    s_eyeLogFrames = 5;
                    UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                }
            }
            catch (Exception e)
            {
                log.Warning("[RenderTweaks] LogStereoState failed: " + e.Message);
            }
        }

        static void OnBeginCameraRendering(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (s_eyeLogFrames <= 0) return;
            if (Time.frameCount != s_lastFrame)
            {
                if (s_lastFrame >= 0)
                {
                    DnWVRMod.Log.Msg($"[RenderTweaks] frame {s_lastFrame}: {s_renderCallsThisFrame} camera render(s)");
                    s_eyeLogFrames--;
                }
                s_lastFrame = Time.frameCount;
                s_renderCallsThisFrame = 0;
            }
            s_renderCallsThisFrame++;
            if (cam == Camera.main)
                DnWVRMod.Log.Msg($"[RenderTweaks]   {cam.name} stereoActiveEye={cam.stereoActiveEye} stereoEnabled={cam.stereoEnabled} pixel={cam.pixelWidth}x{cam.pixelHeight}");
            if (s_eyeLogFrames <= 0)
            {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                s_renderHookInstalled = false; // a later LogStereoState (F11 restart) can arm it again
                s_lastFrame = -1;
            }
        }

        /// <summary>Re-evaluate the fluid feature switch after a preference reload (F6).</summary>
        public static void ReapplyFluidSwitch(MelonLogger.Instance log)
        {
            if (!XR.XRBootstrap.IsRunning) return;
            if (DisableFluidFeature)
            {
                s_fluidFeatureDisabled = false;
                ApplyFluidFeatureSwitch(log);
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
                    log.Msg($"[RenderTweaks] fluid renderer feature re-enabled on {n} renderer(s)");
                }
                catch (Exception e) { log.Warning("[RenderTweaks] fluid re-enable failed: " + e.Message); }
            }
        }

        static void ApplyFluidFeatureSwitch(MelonLogger.Instance log)
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
                log.Msg($"[RenderTweaks] fluid renderer feature disabled on {n} renderer(s) (DisableFluidFeature pref)");
            }
            catch (Exception e)
            {
                log.Warning("[RenderTweaks] fluid feature switch failed: " + e.Message);
            }
        }

        public static void ApplyToScene(MelonLogger.Instance log)
        {
            if (!XR.XRBootstrap.IsRunning) return;
            ApplyFluidFeatureSwitch(log);
            int disabled = 0;
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
                                disabled++;
                            }
                        }
                    }
                }
                foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    ApplyCamera(cam);
            }
            catch (Exception e)
            {
                log.Warning("[RenderTweaks] ApplyToScene failed: " + e.Message);
            }
            if (disabled > 0) log.Msg($"[RenderTweaks] disabled {disabled} post-processing overrides for VR");
        }

        static void ApplyCamera(Camera cam)
        {
            if (cam == null) return;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data != null)
            {
                data.antialiasing = AntialiasingMode.None;
            }
            cam.allowMSAA = true;
        }
    }
}
