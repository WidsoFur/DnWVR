using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DnWVR.VR
{
    /// <summary>
    /// The desktop window's own view from between the eyes: a non-XR camera that follows the headset (lightly smoothed for
    /// recordings) and renders, shaped like the window, into a texture that DesktopMirror puts on the back buffer instead of
    /// the cropped left eye. It copies the game camera's rendering settings and renders before the eyes, so the texture is
    /// current when the window is drawn.
    /// </summary>
    public static class DesktopView
    {
        /// <summary>The window shows this camera (off: the cropped left eye).</summary>
        public static bool Enabled = true;
        /// <summary>Vertical field of view (degrees).</summary>
        public static float FieldOfView = 60f;
        /// <summary>Height (pixels) of the extra render, at most the window's; 0 = the window's height.</summary>
        public static int Height = 1080;
        /// <summary>Seconds over which the view follows the head; 0 = exactly.</summary>
        public static float Smoothing = 0.1f;

        // More than a head moves in one frame: a cut, teleport or snap turn.
        const float JumpDistance = 0.5f;
        const float JumpAngle = 20f;

        static AccessTools.FieldRef<UniversalAdditionalCameraData, int> s_rendererIndex;
        static Camera s_camera;
        static RenderTexture s_texture;
        static Camera s_source;
        static Vector3 s_position, s_fromPosition, s_target, s_lastTarget;
        static Quaternion s_rotation = Quaternion.identity, s_fromRotation = Quaternion.identity;
        static Quaternion s_targetRotation = Quaternion.identity, s_lastTargetRotation = Quaternion.identity;
        static bool s_hasPose;
        static int s_frame = -1;
        static int s_lastWriteFrame = -100;
        static bool s_failed;

        /// <summary>The texture to show in the window, or null to fall back to the eye.</summary>
        public static RenderTexture Texture => Enabled && s_camera != null && s_camera.enabled && s_texture != null && s_texture.IsCreated() ? s_texture : null;

        public static void Install()
        {
            try { s_rendererIndex = AccessTools.FieldRefAccess<UniversalAdditionalCameraData, int>("m_RendererIndex"); }
            catch (Exception e) { Log.Warning("[DesktopView] renderer index unavailable, the view uses the default renderer: " + e.Message); }
            VRRig.AfterCameraWrite += Follow;
            Log.Msg("[DesktopView] installed");
        }

        /// <summary>Every frame (Update): stops the extra render once the rig no longer writes the camera (loading, XR off).</summary>
        public static void Tick()
        {
            if (s_camera != null && s_camera.enabled && Time.frameCount - s_lastWriteFrame > 2) Disable();
        }

        /// <summary>Stops rendering the view and frees its texture until it is needed again.</summary>
        public static void Disable()
        {
            s_hasPose = false;
            if (s_camera != null && s_camera.enabled) s_camera.enabled = false;
            if (s_texture != null && s_texture.IsCreated()) s_texture.Release();
        }

        // After each camera write: the view follows the headset's centre eye (the camera the rig writes renders both eyes).
        static void Follow()
        {
            var source = VRRig.Camera;
            if (!Enabled || s_failed || !DesktopMirror.Enabled || !VRRig.Active || source == null || !source.isActiveAndEnabled)
            {
                Disable();
                return;
            }
            if (!Ensure(source)) return;
            s_lastWriteFrame = Time.frameCount;

            var t = source.transform;
            if (s_frame != Time.frameCount)
            {
                // Each frame eases from the view shown last frame; a later write in the same frame only refines the target.
                s_frame = Time.frameCount;
                s_fromPosition = s_position;
                s_fromRotation = s_rotation;
                s_lastTarget = s_target;
                s_lastTargetRotation = s_targetRotation;
            }
            s_target = t.position;
            s_targetRotation = t.rotation;
            bool jump = !s_hasPose || (s_target - s_lastTarget).sqrMagnitude > JumpDistance * JumpDistance
                        || Quaternion.Angle(s_targetRotation, s_lastTargetRotation) > JumpAngle;
            if (jump || Smoothing <= 0f)
            {
                s_position = s_target;
                s_rotation = s_targetRotation;
            }
            else
            {
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Smoothing);
                s_position = Vector3.Lerp(s_fromPosition, s_target, k);
                s_rotation = Quaternion.Slerp(s_fromRotation, s_targetRotation, k);
            }
            s_hasPose = true;
            s_camera.transform.SetPositionAndRotation(s_position, s_rotation);
        }

        static bool Ensure(Camera source)
        {
            try
            {
                int windowWidth = Mathf.Max(1, Screen.width), windowHeight = Mathf.Max(1, Screen.height);
                int height = Mathf.Clamp(Height > 0 ? Mathf.Min(Height, windowHeight) : windowHeight, 64, 2160);
                int width = Mathf.Clamp(Mathf.RoundToInt(height * (float)windowWidth / windowHeight), 64, 4096);
                int msaa = Msaa(source);
                if (s_texture == null || s_texture.width != width || s_texture.height != height || s_texture.antiAliasing != msaa)
                {
                    if (s_texture != null)
                    {
                        if (s_camera != null) s_camera.targetTexture = null;
                        s_texture.Release();
                        UnityEngine.Object.Destroy(s_texture);
                    }
                    s_texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "DnWVR_DesktopView", antiAliasing = msaa };
                    Log.Msg($"[DesktopView] the window's view renders at {width}x{height}, MSAA {msaa}x (window {windowWidth}x{windowHeight})");
                }
                if (!s_texture.IsCreated()) s_texture.Create();
                if (s_camera == null)
                {
                    var go = new GameObject("DnWVR_DesktopView");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_camera = go.AddComponent<Camera>();
                    s_source = null;
                }
                if (s_source != source)
                {
                    CopySettings(source);
                    s_source = source;
                }
                Sync(source);
                return true;
            }
            catch (Exception e)
            {
                s_failed = true;
                Disable();
                Log.Error("[DesktopView] disabled, the window shows the left eye: " + e);
                return false;
            }
        }

        // With a target texture URP takes the sample count from the texture. The view is for the monitor, where FXAA reads
        // the same and costs a fraction of four samples on a whole extra render; only without the savings does it match the eyes.
        static int Msaa(Camera source)
        {
            if (RenderTweaks.Optimize) return 1;
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int samples = asset != null && source.allowMSAA ? asset.msaaSampleCount : 1;
            return samples == 2 || samples == 4 || samples == 8 ? samples : 1;
        }

        // Per write: what the game or the rig may change on the source camera, plus the view's own shape.
        static void Sync(Camera source)
        {
            if (!s_camera.enabled) s_camera.enabled = true;
            if (s_camera.targetTexture != s_texture) s_camera.targetTexture = s_texture;
            s_camera.fieldOfView = Mathf.Clamp(FieldOfView, 20f, 120f);
            s_camera.aspect = s_texture.width / (float)s_texture.height;
            // URP sorts by depth: render before the eyes, whose mirror step shows this texture.
            s_camera.depth = Mathf.Floor(source.depth) - 2f;
            s_camera.cullingMask = source.cullingMask;
            s_camera.clearFlags = source.clearFlags;
            s_camera.backgroundColor = source.backgroundColor;
            s_camera.nearClipPlane = source.nearClipPlane;
            s_camera.farClipPlane = source.farClipPlane;
            s_camera.allowHDR = source.allowHDR;
            s_camera.allowMSAA = source.allowMSAA;
            s_camera.useOcclusionCulling = source.useOcclusionCulling;
            s_camera.rect = new Rect(0f, 0f, 1f, 1f);
            s_camera.eventMask = 0;
        }

        // Once per game camera: copies the URP renderer and its settings from the source camera, minus XR.
        static void CopySettings(Camera source)
        {
            s_camera.CopyFrom(source);
            // The copy may carry explicit headset matrices: the view builds its own from its pose, FOV and aspect.
            s_camera.ResetWorldToCameraMatrix();
            s_camera.ResetProjectionMatrix();
            s_camera.ResetCullingMatrix();
            s_camera.ResetStereoViewMatrices();
            s_camera.ResetStereoProjectionMatrices();
            s_camera.usePhysicalProperties = false;
            s_camera.gameObject.layer = source.gameObject.layer;
            var sd = source.GetUniversalAdditionalCameraData();
            var dd = s_camera.GetUniversalAdditionalCameraData();
            if (sd == null || dd == null) return;
            dd.renderType = CameraRenderType.Base;
            if (s_rendererIndex != null)
            {
                int index = s_rendererIndex(sd);
                if (s_rendererIndex(dd) != index) dd.SetRenderer(index);
            }
            dd.renderPostProcessing = sd.renderPostProcessing;
            dd.antialiasing = RenderTweaks.Optimize ? AntialiasingMode.FastApproximateAntialiasing : sd.antialiasing;
            dd.antialiasingQuality = sd.antialiasingQuality;
            dd.stopNaN = sd.stopNaN;
            dd.dithering = sd.dithering;
            dd.renderShadows = sd.renderShadows;
            dd.requiresDepthOption = sd.requiresDepthOption;
            dd.requiresColorOption = sd.requiresColorOption;
            dd.volumeLayerMask = sd.volumeLayerMask;
            dd.volumeTrigger = sd.volumeTrigger != null ? sd.volumeTrigger : source.transform;
            var volumeMode = source.GetVolumeFrameworkUpdateMode();
            if (s_camera.GetVolumeFrameworkUpdateMode() != volumeMode) s_camera.SetVolumeFrameworkUpdateMode(volumeMode);
            dd.allowXRRendering = false;
            dd.allowHDROutput = false;
            dd.useScreenCoordOverride = false;
        }
    }
}
