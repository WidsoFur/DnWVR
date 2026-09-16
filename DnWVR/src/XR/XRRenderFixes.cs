using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DnWVR.XR
{
    /// <summary>
    /// The game was built without XR, so URP stripped its XR-only shader passes. Turns off the XRSystem visibility and
    /// occlusion meshes that need them, and draws a full-screen pass 0 instead of a visible-mesh draw with a missing pass.
    /// </summary>
    public static class XRRenderFixes
    {
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static MelonLogger.Instance s_log;
        static bool s_patched;
        static object s_lastLoader;
        static FieldInfo s_wrappedCommandBuffer;
        static int s_fallbackDraws;
        static bool s_loggedFallback;

        /// <summary>Call after XR starts and after every scene load (URP may re-create the occlusion material).</summary>
        public static void Apply(MelonLogger.Instance log, bool singlePassInstanced)
        {
            s_log = log;
            if (!s_patched) Patch(DnWVRMod.Instance.HarmonyInstance, log);
            try
            {
                var t = typeof(XRSystem);
                SetStatic(t, "s_UseVisibilityMesh", false);
                SetStatic(t, "s_OcclusionMeshScaling", 0f);
                var matField = t.GetField("s_OcclusionMeshMaterial", AnyStatic);
                if (matField != null && matField.GetValue(null) is Material occ && occ != null)
                {
                    UnityEngine.Object.Destroy(occ);
                    matField.SetValue(null, null);
                }
                // Multipass rendering must never switch a pass into single-pass (stripped stereo keywords).
                if (!singlePassInstanced) XRSystem.singlePassAllowed = false;
                XRSystem.foveatedRenderingCaps = FoveatedRenderingCaps.None;

                bool firstForThisSession = !ReferenceEquals(s_lastLoader, XRBootstrap.Loader);
                if (firstForThisSession)
                {
                    s_lastLoader = XRBootstrap.Loader;
                    LogShaderDiagnostics(log);
                    log.Msg($"[XRRenderFixes] visibilityMesh={InvokeStatic(t, "GetUseVisibilityMesh")} occlusionScale={InvokeStatic(t, "GetOcclusionMeshScale")} " +
                            $"singlePassAllowed={XRSystem.singlePassAllowed} mirrorMode={InvokeStatic(t, "GetMirrorViewMode")}");
                    MelonCoroutines.Start(DumpLayoutForFrames(5));
                }
            }
            catch (Exception e)
            {
                log.Error("[XRRenderFixes] apply failed: " + e);
            }
        }

        static void SetStatic(Type t, string name, object value)
        {
            var f = t.GetField(name, AnyStatic);
            if (f == null) { s_log?.Warning($"[XRRenderFixes] {t.Name}.{name} not found"); return; }
            f.SetValue(null, value);
        }

        static object InvokeStatic(Type t, string name)
        {
            try { return t.GetMethod(name, AnyStatic)?.Invoke(null, null); }
            catch { return "?"; }
        }

        static void Patch(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            s_patched = true;
            try
            {
                s_wrappedCommandBuffer = typeof(BaseCommandBuffer).GetField("m_WrappedCommandBuffer", AnyInstance);
                int n = 0;
                foreach (var m in typeof(XRPass).GetMethods(AnyInstance))
                {
                    if (m.Name != "RenderVisibleMeshCustomMaterial") continue;
                    var p = m.GetParameters();
                    if (p.Length == 0) continue;
                    string prefix = p[0].ParameterType == typeof(CommandBuffer) ? nameof(VisibleMesh_Prefix)
                                  : p[0].ParameterType == typeof(RasterCommandBuffer) ? nameof(VisibleMeshRaster_Prefix) : null;
                    if (prefix == null) continue;
                    harmony.Patch(m, new HarmonyMethod(typeof(XRRenderFixes).GetMethod(prefix, AnyStatic)));
                    n++;
                }
                log.Msg($"[XRRenderFixes] guarded {n} XRPass.RenderVisibleMeshCustomMaterial overload(s)");
            }
            catch (Exception e)
            {
                log.Error("[XRRenderFixes] visible-mesh guard failed: " + e);
            }
        }

        static bool VisibleMesh_Prefix(CommandBuffer cmd, Material material, MaterialPropertyBlock materialBlock, int shaderPass)
        {
            if (material != null && shaderPass < material.passCount) return true;
            DrawFullscreenFallback(cmd, material, materialBlock, shaderPass);
            return false;
        }

        static bool VisibleMeshRaster_Prefix(RasterCommandBuffer cmd, Material material, MaterialPropertyBlock materialBlock, int shaderPass)
        {
            if (material != null && shaderPass < material.passCount) return true;
            var wrapped = s_wrappedCommandBuffer != null ? s_wrappedCommandBuffer.GetValue(cmd) as CommandBuffer : null;
            DrawFullscreenFallback(wrapped, material, materialBlock, shaderPass);
            return false;
        }

        static void DrawFullscreenFallback(CommandBuffer cmd, Material material, MaterialPropertyBlock block, int wantedPass)
        {
            s_fallbackDraws++;
            if (!s_loggedFallback)
            {
                s_loggedFallback = true;
                s_log?.Warning($"[XRRenderFixes] visible-mesh draw with missing pass {wantedPass} on {(material != null ? material.shader.name : "null")} " +
                               $"(passCount {(material != null ? material.passCount : 0)}): drawing a full-screen triangle with pass 0 instead");
            }
            if (cmd == null || material == null || material.passCount == 0) return;
            // Procedural full-screen triangle with pass 0, the same draw Blitter.BlitTexture does.
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, block);
        }

        static void LogShaderDiagnostics(MelonLogger.Instance log)
        {
            void Report(string shaderName, string xrPassName)
            {
                var sh = Shader.Find(shaderName);
                if (sh == null) { log.Msg($"[XRRenderFixes] shader '{shaderName}': not in build"); return; }
                int xrPass = -1;
                if (!string.IsNullOrEmpty(xrPassName))
                {
                    var mat = new Material(sh);
                    xrPass = mat.FindPass(xrPassName);
                    UnityEngine.Object.Destroy(mat);
                }
                log.Msg($"[XRRenderFixes] shader '{shaderName}': supported={sh.isSupported} passCount={sh.passCount}" +
                        (string.IsNullOrEmpty(xrPassName) ? "" : $" '{xrPassName}' index={xrPass}"));
            }
            Report("Hidden/Universal Render Pipeline/UberPost", "UberPostXR");
            Report("Hidden/Universal Render Pipeline/FinalPost", "FinalPostXR");
            Report("Hidden/Universal Render Pipeline/XR/XROcclusionMesh", null);
            Report("Hidden/Universal Render Pipeline/XR/XRMirrorView", null);
        }

        static IEnumerator DumpLayoutForFrames(int frames)
        {
            // The display starts a few seconds after the loader; until then every camera only gets an empty pass.
            float waited = 0f;
            while (!XRSystem.displayActive && waited < 30f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!XRSystem.displayActive)
            {
                s_log?.Msg("[XRRenderFixes] display not active after 30 s; no layout dump");
                yield break;
            }
            yield return null;
            // While dumpDebugInfo is on, Core RP logs the XR pass layout (pass, cull, view, slice, viewport) every frame.
            XRSystem.dumpDebugInfo = true;
            for (int i = 0; i < frames; i++) yield return null;
            XRSystem.dumpDebugInfo = false;
            s_log?.Msg($"[XRRenderFixes] layout dump done; visible-mesh fallback draws so far: {s_fallbackDraws}");
        }
    }
}
