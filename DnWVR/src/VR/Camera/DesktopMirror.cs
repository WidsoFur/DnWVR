using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Preview in the desktop window. Replaces URP's XRMirrorView, whose shader the non-XR game build ships unsupported: puts
    /// the DesktopView camera's texture onto the back buffer, else the runtime's native mirror blit or a copy of the left eye.
    /// </summary>
    public static class DesktopMirror
    {
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const int LeftEyeMode = XRMirrorViewBlitMode.LeftEye;

        static Pref<bool> s_prefEnabled;
        static Pref<bool> s_prefFlipX;
        static Pref<bool> s_prefFlipY;

        static RenderTexture s_rt;
        static string s_path = "";
        static bool s_copyUnsupported;
        static bool s_loggedError;
        static bool s_loggedDesc;

        public static bool Enabled => s_prefEnabled == null || s_prefEnabled.Value;
        // Where UVs start at the top (D3D), URP renders the XR eye target top-down and Blit reads it bottom-up, so the
        // copy is flipped vertically there by default; each pref flips its axis once more.
        static bool FlipX => s_prefFlipX != null && s_prefFlipX.Value;
        static bool FlipY => SystemInfo.graphicsUVStartsAtTop != (s_prefFlipY != null && s_prefFlipY.Value);

        public static void Install(HarmonyLib.Harmony harmony)
        {
            s_prefEnabled = PrefStore.Create("DesktopMirror", true, "Show the VR view in the desktop window");
            s_prefFlipX = PrefStore.Create("DesktopMirrorFlipHorizontal", false,
                "Mirror the desktop preview left to right (if it reads mirrored)");
            s_prefFlipY = PrefStore.Create("DesktopMirrorFlipY", false,
                "Flip the desktop preview vertically (if it shows upside down)");
            var type = AccessTools.TypeByName("UnityEngine.Experimental.Rendering.XRMirrorView");
            var method = type != null ? AccessTools.Method(type, "RenderMirrorView") : null;
            if (method == null)
            {
                Log.Warning("[DesktopMirror] XRMirrorView.RenderMirrorView not found; no desktop preview");
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(DesktopMirror).GetMethod(nameof(RenderMirrorView_Prefix), AnyStatic)));
            Log.Msg("[DesktopMirror] installed");
        }

        // Runs on the render loop after every XR base camera's passes, with the desktop back buffer as CameraTarget.
        static bool RenderMirrorView_Prefix(CommandBuffer cmd, Camera camera, XRDisplaySubsystem display)
        {
            if (!Enabled) return true;
            try
            {
                if (cmd == null || camera == null || display == null || !display.running) return false;
                Mirror(cmd, camera, display);
            }
            catch (Exception e)
            {
                if (!s_loggedError)
                {
                    s_loggedError = true;
                    Log.Warning("[DesktopMirror] mirror failed (will keep trying): " + e);
                }
            }
            return false;
        }

        static void Mirror(CommandBuffer cmd, Camera camera, XRDisplaySubsystem display)
        {
            var backBuffer = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            var view = DesktopView.Texture;
            if (view != null)
            {
                // The view already has the window's shape, and a camera texture is stored the Unity way up: only the prefs flip it.
                var r = new Rect(0f, 0f, 1f, 1f);
                if (FlipX) r = new Rect(1f, 0f, -1f, 1f);
                if (s_prefFlipY != null && s_prefFlipY.Value) r = new Rect(r.x, 1f, r.width, -1f);
                cmd.SetRenderTarget(backBuffer);
                cmd.Blit(view, backBuffer, new Vector2(r.width, r.height), new Vector2(r.x, r.y));
                SetPath("the desktop view camera between the eyes");
                return;
            }

            if (display.GetPreferredMirrorBlitMode() != LeftEyeMode) display.SetPreferredMirrorBlitMode(LeftEyeMode);
            bool hasDesc = display.GetMirrorViewBlitDesc(null, out var desc, LeftEyeMode);
            if (hasDesc && desc.nativeBlitAvailable)
            {
                cmd.SetRenderTarget(backBuffer);
                display.AddGraphicsThreadMirrorViewBlit(cmd, desc.nativeBlitInvalidStates, LeftEyeMode);
                SetPath("the left eye (native blit)");
                return;
            }

            if (s_copyUnsupported || display.GetRenderPassCount() < 1) return;
            // Display pass 0 is the left eye (the left-eye camera renders it); the runtime's description only gives the crop.
            display.GetRenderPass(0, out var pass);
            if (pass.GetRenderParameterCount() < 1) return;
            pass.GetRenderParameter(camera, 0, out var param);

            int width = pass.renderTargetDesc.width;
            int height = pass.renderTargetDesc.height;
            GraphicsFormat format = pass.renderTargetDesc.graphicsFormat;
            int msaa = pass.renderTargetDesc.msaaSamples;
            Rect srcRect = new Rect(0f, 0f, 1f, 1f);
            if (hasDesc && desc.blitParamsCount > 0)
            {
                desc.GetBlitParameter(0, out var bp);
                srcRect = bp.srcRect;
                if (!s_loggedDesc)
                {
                    s_loggedDesc = true;
                    string source = bp.srcTex != null
                        ? $"{bp.srcTex.name} {bp.srcTex.width}x{bp.srcTex.height} {bp.srcTex.graphicsFormat} {bp.srcTex.dimension}"
                        : "null";
                    Log.Msg($"[DesktopMirror] runtime blit: params={desc.blitParamsCount} srcTex={source} " +
                            $"slice={bp.srcTexArraySlice} srcRect={bp.srcRect} destRect={bp.destRect}; " +
                               $"left eye pass 0 {width}x{height} {format}");
                }
            }

            if (msaa > 1 || (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) == 0 || width <= 0 || height <= 0)
            {
                s_copyUnsupported = true;
                Log.Warning($"[DesktopMirror] cannot copy the eye texture (msaa={msaa}, copyTextureSupport={SystemInfo.copyTextureSupport}); no desktop preview");
                return;
            }

            EnsureTexture(width, height, format);
            cmd.CopyTexture(pass.renderTarget, param.textureArraySlice, 0, s_rt, 0, 0);
            var uv = ComputeUV(srcRect, param.projection, width, height);
            cmd.SetRenderTarget(backBuffer);
            cmd.Blit(s_rt, backBuffer, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
            SetPath("the left eye (copy + blit to the back buffer)");
        }

        // Crops the eye image to the window aspect around where the eye looks (asymmetric projection), unless the
        // runtime gave a cropped source rect; then applies the flips (a negative size reads backwards).
        static Rect ComputeUV(Rect src, Matrix4x4 projection, int texWidth, int texHeight)
        {
            bool flipX = FlipX;
            bool flipY = FlipY;
            Rect r = src;
            bool full = Mathf.Abs(src.x) < 1e-4f && Mathf.Abs(src.y) < 1e-4f && Mathf.Abs(src.width - 1f) < 1e-4f && Mathf.Abs(src.height - 1f) < 1e-4f;
            if (full)
            {
                float screenAspect = Screen.width / (float)Mathf.Max(1, Screen.height);
                float srcAspect = texWidth / (float)Mathf.Max(1, texHeight);
                float w = 1f, h = 1f;
                if (srcAspect > screenAspect) w = screenAspect / srcAspect;
                else h = srcAspect / screenAspect;
                // The view direction (0,0,-1) lands at ndc (-m02, -m12); a flipped image stores it mirrored.
                float gazeU = flipX ? 0.5f + 0.5f * projection.m02 : 0.5f - 0.5f * projection.m02;
                float gazeV = flipY ? 0.5f + 0.5f * projection.m12 : 0.5f - 0.5f * projection.m12;
                float x = Mathf.Clamp(gazeU - w * 0.5f, 0f, 1f - w);
                float y = Mathf.Clamp(gazeV - h * 0.5f, 0f, 1f - h);
                r = new Rect(x, y, w, h);
            }
            if (flipX) r = new Rect(r.x + r.width, r.y, -r.width, r.height);
            if (flipY) r = new Rect(r.x, r.y + r.height, r.width, -r.height);
            return r;
        }

        static void EnsureTexture(int width, int height, GraphicsFormat format)
        {
            if (s_rt != null && s_rt.width == width && s_rt.height == height && s_rt.graphicsFormat == format && s_rt.IsCreated()) return;
            if (s_rt != null)
            {
                s_rt.Release();
                UnityEngine.Object.Destroy(s_rt);
            }
            var d = new RenderTextureDescriptor(width, height, format, GraphicsFormat.None)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };
            s_rt = new RenderTexture(d) { name = "DnWVR_DesktopMirror" };
            s_rt.Create();
        }

        static void SetPath(string path)
        {
            if (s_path == path) return;
            s_path = path;
            Log.Msg("[DesktopMirror] the desktop window shows " + path);
        }
    }
}
