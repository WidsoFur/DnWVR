using System;
using System.Collections.Generic;
using System.Reflection;
using DnWVR.Diag;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DnWVR.VR
{
    /// <summary>
    /// The VR tab's Performance row: four steps from the game's own picture to the cheapest one that still reads well in a
    /// headset. Each step gives up first what costs the most for the least that shows: the monitor's copy of the view,
    /// then the shadows, then the eyes' resolution and anti-aliasing. The pipeline asset and the lights it changes are the
    /// flat game's too, so all of it goes back as it was when VR stops.
    /// </summary>
    public static class VRPerformance
    {
        public enum Level { Quality, Balanced, Fast, Fastest }

        public static Level Current = Level.Quality;

        static readonly string[] s_names = { "Quality", "Balanced", "Fast", "Fastest" };
        // Per level: the eyes' resolution scale (URP takes anything within 0.05 of 1 as 1) and their MSAA (0 = the game's).
        static readonly float[] s_renderScale = { 1f, 0.9f, 0.8f, 0.7f };
        static readonly int[] s_msaa = { 0, 0, 2, 2 };

        // The lighter shadows: two cascades over a shorter reach, the first ending where the game's own first does so that
        // shadows within reach keep their sharpness, and the 9-tap soft filter instead of 16.
        const int LightCascades = 2;
        const float LightShadowDistance = 35f;
        // The monitor's view from Balanced on; Fastest shows the left eye there instead of rendering a view of its own.
        const int LightDesktopHeight = 720;

        public static string Name => s_names[(int)Current];

        static UniversalRenderPipelineAsset s_asset;
        static float s_scale, s_split2, s_distance, s_firstCascade;
        static UpscalingFilterSelection s_filter;
        static int s_msaaAuthored, s_cascades;
        static readonly Dictionary<UniversalAdditionalLightData, SoftShadowQuality> s_lights =
            new Dictionary<UniversalAdditionalLightData, SoftShadowQuality>();
        static PropertyInfo s_assetSoftQuality;
        static FieldInfo s_xrMsaa;
        static bool s_xrMsaaLooked;
        static int s_logged = -1;

        /// <summary>The level named in the cfg; anything unknown is Quality, the game's own.</summary>
        public static Level Parse(string text)
        {
            for (int i = 0; i < s_names.Length; i++)
                if (string.Equals(text?.Trim(), s_names[i], StringComparison.OrdinalIgnoreCase)) return (Level)i;
            Log.Warning($"[Performance] \"{text}\" is not one of {string.Join(", ", s_names)}; using Quality");
            return Level.Quality;
        }

        /// <summary>
        /// Brings the pipeline, the scene's lights and the desktop view in line with Current: on XR start, after each scene
        /// load, on F6 and from the settings row. What already matches is left alone, as a new resolution or sample count
        /// reallocates the eyes' textures.
        /// </summary>
        public static void Apply()
        {
            bool lighter = Current >= Level.Balanced;
            DesktopView.MaxHeight = lighter ? LightDesktopHeight : 0;
            DesktopView.Shadows = !lighter;
            DesktopView.UseEye = Current == Level.Fastest;
            DesktopView.Refresh();
            if (!XR.XRBootstrap.IsRunning) return;
            try
            {
                ApplyAsset(lighter);
                ApplyLights(lighter);
            }
            catch (Exception e)
            {
                Log.Warning("[Performance] not all of it applied: " + e.Message);
            }
            if (s_logged != (int)Current)
            {
                s_logged = (int)Current;
                PerfLog.Restart();
                var asset = s_asset;
                Log.Msg($"[Performance] {Name}: " + (asset == null ? "no URP asset" :
                        $"render scale {asset.renderScale:0.00}, MSAA {asset.msaaSampleCount}x, shadows {asset.shadowCascadeCount} " +
                        $"cascades to {asset.shadowDistance:0} m, {s_lights.Count} lights at medium soft shadows") +
                        $", desktop view {(DesktopView.UseEye ? "showing the left eye" : lighter ? "720p without shadows" : "as set")}");
            }
        }

        /// <summary>Puts the pipeline asset and the lights back as the game had them (XR stopping).</summary>
        public static void Restore()
        {
            RestoreAsset();
            RestoreLights();
            s_logged = -1;
        }

        static void ApplyAsset(bool lighter)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) return;
            if (asset != s_asset)
            {
                RestoreAsset();
                s_asset = asset;
                s_scale = asset.renderScale;
                s_filter = asset.upscalingFilter;
                s_msaaAuthored = asset.msaaSampleCount;
                s_cascades = asset.shadowCascadeCount;
                s_split2 = asset.cascade2Split;
                s_distance = asset.shadowDistance;
                float first = s_cascades >= 4 ? asset.cascade4Split.x : s_cascades == 3 ? asset.cascade3Split.x : s_split2;
                s_firstCascade = first * s_distance;
            }
            int level = (int)Current;
            // Below 1 URP upscales, and its automatic filter picks the point filter, with a pass of its own, at even ratios.
            bool scaled = s_renderScale[level] < 0.95f;
            float distance = lighter ? Mathf.Min(LightShadowDistance, s_distance) : s_distance;
            Set(asset, scaled ? s_renderScale[level] : s_scale, scaled ? UpscalingFilterSelection.Linear : s_filter,
                s_msaa[level] > 0 && XrMsaa != null ? Mathf.Min(s_msaa[level], s_msaaAuthored) : s_msaaAuthored,
                lighter ? Mathf.Min(LightCascades, s_cascades) : s_cascades,
                lighter && s_cascades > LightCascades ? Mathf.Clamp(s_firstCascade / distance, 0.05f, 0.5f) : s_split2,
                distance);
        }

        static void RestoreAsset()
        {
            if (s_asset != null) Set(s_asset, s_scale, s_filter, s_msaaAuthored, s_cascades, s_split2, s_distance);
            s_asset = null;
        }

        static void Set(UniversalRenderPipelineAsset asset, float scale, UpscalingFilterSelection filter, int msaa, int cascades,
                        float split2, float distance)
        {
            if (asset.renderScale != scale) asset.renderScale = scale;
            if (asset.upscalingFilter != filter) asset.upscalingFilter = filter;
            if (asset.msaaSampleCount != msaa)
            {
                // URP hands the asset's count to the headset's display once it differs from the one it last handed on,
                // and that was the game's 4 before any display existed: the eye textures themselves have one sample and
                // the samples live in URP's own buffer, resolved into them. Telling URP the new count is the one it gave
                // keeps it that way, so the display is never asked for multisampled eyes.
                asset.msaaSampleCount = msaa;
                XrMsaa?.SetValue(null, (MSAASamples)msaa);
            }
            if (asset.shadowCascadeCount != cascades) asset.shadowCascadeCount = cascades;
            if (asset.cascade2Split != split2) asset.cascade2Split = split2;
            if (asset.shadowDistance != distance) asset.shadowDistance = distance;
        }

        // Soft shadow quality is set per light; a light left on the pipeline's setting gets the asset's, which is High here.
        static void ApplyLights(bool lighter)
        {
            if (!lighter)
            {
                RestoreLights();
                return;
            }
            var assetQuality = AssetSoftShadowQuality();
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light.shadows != LightShadows.Soft || !light.TryGetComponent<UniversalAdditionalLightData>(out var data)) continue;
                if (s_lights.ContainsKey(data)) continue;
                var quality = data.softShadowQuality;
                var effective = quality == SoftShadowQuality.UsePipelineSettings ? assetQuality : quality;
                if (effective != SoftShadowQuality.High) continue;
                s_lights[data] = quality;
                data.softShadowQuality = SoftShadowQuality.Medium;
            }
        }

        static void RestoreLights()
        {
            foreach (var pair in s_lights)
                if (pair.Key != null) pair.Key.softShadowQuality = pair.Value;
            s_lights.Clear();
        }

        static FieldInfo XrMsaa
        {
            get
            {
                if (s_xrMsaaLooked) return s_xrMsaa;
                s_xrMsaaLooked = true;
                s_xrMsaa = AccessTools.Field(typeof(XRSystem), "s_MSAASamples");
                if (s_xrMsaa == null || s_xrMsaa.FieldType != typeof(MSAASamples))
                {
                    s_xrMsaa = null;
                    Log.Warning("[Performance] URP's XR sample count is not where it was; the eyes keep the game's MSAA");
                }
                return s_xrMsaa;
            }
        }

        static SoftShadowQuality AssetSoftShadowQuality()
        {
            try
            {
                if (s_assetSoftQuality == null)
                    s_assetSoftQuality = AccessTools.Property(typeof(UniversalRenderPipelineAsset), "softShadowQuality");
                if (s_asset != null && s_assetSoftQuality != null) return (SoftShadowQuality)s_assetSoftQuality.GetValue(s_asset, null);
            }
            catch (Exception e) { Log.Warning("[Performance] the pipeline's soft shadow quality is unreadable: " + e.Message); }
            return SoftShadowQuality.High;
        }
    }
}
