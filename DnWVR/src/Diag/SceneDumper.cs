using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using com.gatordragongames.washnwalk.tools;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

namespace DnWVR.Diag
{
    /// <summary>
    /// Diagnostics: writes cameras, canvases, player and hand hierarchies, the tool anchor, input devices
    /// and XR subsystem state to a text file under UserData/DnWVR.
    /// </summary>
    public static class SceneDumper
    {
        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static string DumpToFile(string tag)
        {
            var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "DnWVR");
            Directory.CreateDirectory(dir);
            var scene = SceneManager.GetActiveScene().name;
            var path = Path.Combine(dir, $"dump_{scene}_{tag}_{DateTime.Now:HHmmss}.txt");
            File.WriteAllText(path, Dump());
            return path;
        }

        public static string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== DnWVR scene dump {DateTime.Now} ===");
            Section(sb, "Environment", DumpEnvironment);
            Section(sb, "Cameras", DumpCameras);
            Section(sb, "Canvases", DumpCanvases);
            Section(sb, "EventSystem", DumpEventSystem);
            Section(sb, "Player", DumpPlayer);
            Section(sb, "OrbitCamera", DumpOrbitCamera);
            Section(sb, "PlapperHand", DumpPlapperHands);
            Section(sb, "ToolManager", DumpToolManager);
            Section(sb, "Input devices", DumpInputDevices);
            Section(sb, "XR", DumpXR);
            Section(sb, "Root objects", DumpRoots);
            return sb.ToString();
        }

        static void Section(StringBuilder sb, string title, Action<StringBuilder> body)
        {
            sb.AppendLine();
            sb.AppendLine($"--- {title} ---");
            try { body(sb); }
            catch (Exception e) { sb.AppendLine($"!! {title} failed: {e}"); }
        }

        static void DumpEnvironment(StringBuilder sb)
        {
            sb.AppendLine($"Unity {Application.unityVersion}; gfx {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceName})");
            sb.AppendLine($"Screen {Screen.width}x{Screen.height} fullscreen={Screen.fullScreen} mode={Screen.fullScreenMode}");
            sb.AppendLine($"Scene: {SceneManager.GetActiveScene().name}; loaded scenes: {SceneManager.sceneCount}");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.AppendLine($"  [{i}] {s.name} loaded={s.isLoaded} roots={s.rootCount}");
            }
            var rp = GraphicsSettings.currentRenderPipeline;
            sb.AppendLine($"RenderPipeline: {(rp ? rp.name + " (" + rp.GetType().Name + ")" : "built-in")}");
            if (rp is UniversalRenderPipelineAsset urp)
            {
                sb.AppendLine($"  URP msaa={urp.msaaSampleCount} renderScale={urp.renderScale} hdr={urp.supportsHDR} " +
                              $"upscaling={urp.upscalingFilter}");
            }
            sb.AppendLine($"Quality level: {QualitySettings.GetQualityLevel()} ({QualitySettings.names[QualitySettings.GetQualityLevel()]}) vsync={QualitySettings.vSyncCount}");
            sb.AppendLine($"XRSettings.enabled={XRSettings.enabled} loadedDevice='{XRSettings.loadedDeviceName}' supported=[{string.Join(",", XRSettings.supportedDevices)}]");
        }

        static void DumpCameras(StringBuilder sb)
        {
            var main = Camera.main;
            sb.AppendLine($"Camera.main = {(main ? TPath(main.transform) : "null")}");
            foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"* {TPath(cam.transform)} enabled={cam.enabled} active={cam.gameObject.activeInHierarchy} depth={cam.depth} fov={cam.fieldOfView} " +
                              $"near={cam.nearClipPlane} far={cam.farClipPlane} mask={cam.cullingMask:X} clear={cam.clearFlags} rt={(cam.targetTexture ? cam.targetTexture.name : "none")} " +
                              $"stereoEye={cam.stereoTargetEye} tag={cam.tag} msaa={cam.allowMSAA} hdr={cam.allowHDR}");
                var extra = cam.GetComponent<UniversalAdditionalCameraData>();
                if (extra != null)
                {
                    sb.AppendLine($"    URP: renderType={extra.renderType} stack={extra.cameraStack?.Count} postFX={extra.renderPostProcessing} aa={extra.antialiasing}/{extra.antialiasingQuality} " +
                                  $"renderShadows={extra.renderShadows} requiresDepth={extra.requiresDepthTexture} requiresColor={extra.requiresColorTexture} volumeMask={extra.volumeLayerMask.value:X}");
                    if (extra.cameraStack != null)
                        foreach (var c in extra.cameraStack) sb.AppendLine($"      stacked: {(c ? TPath(c.transform) : "null")}");
                }
                DumpComponents(sb, cam.gameObject, "    ");
            }
        }

        static void DumpCanvases(StringBuilder sb)
        {
            sb.AppendLine($"VRUI.OnTop={DnWVR.VR.VRUI.OnTop}");
            foreach (var cv in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!cv.isRootCanvas) continue;
                var scaler = cv.GetComponent<CanvasScaler>();
                var rt = cv.transform as RectTransform;
                sb.AppendLine($"* {TPath(cv.transform)} active={cv.gameObject.activeInHierarchy} mode={cv.renderMode} cam={(cv.worldCamera ? cv.worldCamera.name : "null")} " +
                              $"order={cv.sortingOrder} layer={LayerMask.LayerToName(cv.gameObject.layer)} size={rt.rect.width}x{rt.rect.height} scale={cv.transform.localScale} " +
                              $"scaler={(scaler ? scaler.uiScaleMode + " ref=" + scaler.referenceResolution : "none")}");
                int children = 0;
                foreach (Transform t in cv.transform) { if (children++ < 12) sb.AppendLine($"    - {t.name} active={t.gameObject.activeSelf} [{ComponentNames(t.gameObject)}]"); }
                if (children > 12) sb.AppendLine($"    ... {children - 12} more");
            }
        }

        static void DumpEventSystem(StringBuilder sb)
        {
            var es = EventSystem.current;
            sb.AppendLine($"EventSystem.current = {(es ? TPath(es.transform) : "null")}");
            if (es)
            {
                sb.AppendLine($"  selected={(es.currentSelectedGameObject ? TPath(es.currentSelectedGameObject.transform) : "null")} inputModule={(es.currentInputModule ? es.currentInputModule.GetType().FullName : "null")}");
                DumpComponents(sb, es.gameObject, "  ");
            }
            foreach (var pi in UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"PlayerInput: {TPath(pi.transform)} actions={(pi.actions ? pi.actions.name : "null")} map={pi.currentActionMap?.name} scheme={pi.currentControlScheme} " +
                              $"behavior={pi.notificationBehavior} devices=[{string.Join(",", pi.devices)}] uiModule={(pi.uiInputModule ? pi.uiInputModule.name : "null")}");
            }
        }

        static void DumpPlayer(StringBuilder sb)
        {
            foreach (var pc in UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"PlayerController at {TPath(pc.transform)} pos={pc.transform.position} rot={pc.transform.eulerAngles}");
                var capsule = pc.GetComponent<CapsuleCollider>();
                var body = pc.GetComponent<Rigidbody>();
                if (capsule != null && body != null)
                    sb.AppendLine($"  capsule radius={capsule.radius:0.###} height={capsule.height:0.###} center={capsule.center} layer={LayerMask.LayerToName(pc.gameObject.layer)}; " +
                                  $"body interpolation={body.interpolation} maxDepenetration={body.maxDepenetrationVelocity} velocity={body.linearVelocity}; VR body offset={DnWVR.VR.PlayerBody.Offset}");
                var root = pc.transform;
                DumpHierarchy(sb, root, "  ", 6, 400);
            }
            var look = typeof(LookController).GetField("instance", AnyStatic)?.GetValue(null) as LookController;
            if (look)
            {
                sb.AppendLine($"LookController instance: {TPath(look.transform)} yaw={look.LookYaw} pitch={look.LookPitch} lookPos={LookController.GetLookPosition()} lookRot={LookController.GetLookRotation().eulerAngles}");
            }
        }

        static void DumpOrbitCamera(StringBuilder sb)
        {
            var oc = WalkNWashOrbitCamera.GetInstance();
            sb.AppendLine($"WalkNWashOrbitCamera = {(oc ? TPath(oc.transform) : "null")}");
            if (!oc) return;
            var cfg = oc.GetConfiguration();
            sb.AppendLine($"  configuration={(cfg ? cfg.name : "null")} data={oc.GetCurrentCameraData()}");
            if (cfg)
            {
                sb.AppendLine($"  nodes ({cfg.nodes.Count}):");
                foreach (var n in cfg.nodes) sb.AppendLine($"    - {n.GetType().Name} '{n.name}' ");
                try
                {
                    var prms = cfg.exposedParameters;
                    sb.AppendLine($"  exposedParameters ({prms.Count}):");
                    foreach (var p in prms) sb.AppendLine($"    - {p.name} : {p.GetValueType()?.Name} = {p.value}");
                }
                catch (Exception e) { sb.AppendLine("  exposedParameters unavailable: " + e.Message); }
            }
        }

        static void DumpPlapperHands(StringBuilder sb)
        {
            foreach (var ph in UnityEngine.Object.FindObjectsByType<PlapperHand>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"PlapperHand at {TPath(ph.transform)} active={ph.gameObject.activeInHierarchy}");
                var hand = typeof(PlapperHand).GetField("hand", AnyInstance)?.GetValue(ph) as Transform;
                var anim = typeof(PlapperHand).GetField("animator", AnyInstance)?.GetValue(ph) as Animator;
                sb.AppendLine($"  hand={(hand ? TPath(hand) : "null")} localPos={hand?.localPosition} animator={(anim ? TPath(anim.transform) : "null")}");
                if (anim)
                {
                    sb.AppendLine($"  animator controller={(anim.runtimeAnimatorController ? anim.runtimeAnimatorController.name : "null")} params:");
                    foreach (var p in anim.parameters) sb.AppendLine($"    - {p.name} ({p.type})");
                }
                var root = ph.transform;
                DumpHierarchy(sb, root, "  ", 8, 400);
            }

            sb.AppendLine($"VRHands attached={DnWVR.VR.VRHands.Attached} toolHandIsRight={DnWVR.VR.VRHands.ToolHandIsRight} holdingTool={DnWVR.VR.VRHands.HoldingTool} " +
                          $"secondHand={DnWVR.VR.VRHands.SecondHand} " +
                          $"gripToAimL={DnWVR.VR.VRHands.GripToAim(false).eulerAngles} " +
                          $"gripToAimR={DnWVR.VR.VRHands.GripToAim(true).eulerAngles} " +
                          $"twinActive={DnWVR.VR.VRHands.TwinActive} " +
                          $"plapperAuthoredActive={DnWVR.VR.VRHands.PlapperAuthoredActive}");
            // Grip axes relative to the head: the paw's base pose assumes +Y points back at the user and +Z up the handle.
            var cam = Camera.main;
            if (cam != null)
            {
                var inv = Quaternion.Inverse(cam.transform.rotation);
                if (DnWVR.VR.VRHands.Left != null)
                    sb.AppendLine($"  grip L (head-local) forward={(inv * DnWVR.VR.VRHands.Left.forward).ToString("F2")} up={(inv * DnWVR.VR.VRHands.Left.up).ToString("F2")} tracked={DnWVR.VR.VRHands.LeftTracked}");
                if (DnWVR.VR.VRHands.Right != null)
                    sb.AppendLine($"  grip R (head-local) forward={(inv * DnWVR.VR.VRHands.Right.forward).ToString("F2")} up={(inv * DnWVR.VR.VRHands.Right.up).ToString("F2")} tracked={DnWVR.VR.VRHands.RightTracked}");
            }
            // The twin has no PlapperHand; find it by name so a leaked copy shows up as well.
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name != "DnWVR_PlapperTwin") continue;
                sb.AppendLine($"Twin hand at {TPath(t)} active={t.gameObject.activeInHierarchy}");
                DumpHierarchy(sb, t, "  ", 8, 400);
            }
        }

        static void DumpToolManager(StringBuilder sb)
        {
            var inst = typeof(ToolManager).GetField("_instance", AnyStatic)?.GetValue(null) as ToolManager;
            sb.AppendLine($"ToolManager._instance = {(inst != null ? "set" : "null")}");
            if (inst == null) return;
            var anchor = typeof(ToolManager).GetField("_toolModelAnchor", AnyInstance)?.GetValue(inst) as Transform;
            sb.AppendLine($"  anchor={(anchor ? TPath(anchor) : "null")} localPos={anchor?.localPosition} localRot={anchor?.localEulerAngles}");
            var tool = ToolManager.GetCurrentTool();
            sb.AppendLine($"  currentTool={(tool ? tool.name + " (" + tool.GetType().Name + ")" : "null")} model={(tool && tool.GetModel() ? TPath(tool.GetModel().transform) : "null")}");
            var empty = ToolManager.GetEmptyTool();
            sb.AppendLine($"  emptyTool={(empty ? empty.name : "null")}");
            if (anchor) DumpHierarchy(sb, anchor, "  ", 6, 200);
            foreach (var t in Resources.FindObjectsOfTypeAll<Tool>())
                sb.AppendLine($"  Tool asset: {t.name} ({t.GetType().Name})");
        }

        static void DumpInputDevices(StringBuilder sb)
        {
            foreach (var d in InputSystem.devices)
                sb.AppendLine($"* {d.displayName} layout={d.layout} id={d.deviceId} enabled={d.enabled} usages=[{string.Join(",", d.usages)}] class={d.description.deviceClass} interface={d.description.interfaceName}");
            sb.AppendLine($"Cursor lock={Cursor.lockState} visible={Cursor.visible}");
        }

        static void DumpXR(StringBuilder sb)
        {
            LogXRDescriptorsTo(sb);
            var t = Type.GetType("UnityEngine.XR.Management.XRGeneralSettings, Unity.XR.Management");
            sb.AppendLine($"Unity.XR.Management loaded: {(t != null)}");
            if (t != null)
            {
                var inst = t.GetProperty("Instance", AnyStatic)?.GetValue(null);
                sb.AppendLine($"  XRGeneralSettings.Instance = {(inst != null ? "set" : "null")}");
            }
            var openxr = Type.GetType("UnityEngine.XR.OpenXR.OpenXRSettings, Unity.XR.OpenXR");
            sb.AppendLine($"Unity.XR.OpenXR loaded: {(openxr != null)}");
            var subsystemsDir = Path.Combine(Application.dataPath, "UnitySubsystems");
            sb.AppendLine($"UnitySubsystems dir: {subsystemsDir} exists={Directory.Exists(subsystemsDir)}");
            if (Directory.Exists(subsystemsDir))
                foreach (var d in Directory.GetDirectories(subsystemsDir)) sb.AppendLine($"  - {System.IO.Path.GetFileName(d)}");
        }

        public static void LogXRDescriptors(MelonLogger.Instance log)
        {
            var sb = new StringBuilder();
            LogXRDescriptorsTo(sb);
            log.Msg(sb.ToString());
        }

        static void LogXRDescriptorsTo(StringBuilder sb)
        {
            var disp = new List<XRDisplaySubsystemDescriptor>();
            SubsystemManager.GetSubsystemDescriptors(disp);
            sb.AppendLine($"XRDisplaySubsystemDescriptors: {disp.Count}");
            foreach (var d in disp) sb.AppendLine($"  - {d.id}");
            var inp = new List<XRInputSubsystemDescriptor>();
            SubsystemManager.GetSubsystemDescriptors(inp);
            sb.AppendLine($"XRInputSubsystemDescriptors: {inp.Count}");
            foreach (var d in inp) sb.AppendLine($"  - {d.id}");
            var running = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(running);
            sb.AppendLine($"Running display subsystems: {running.Count}");
            foreach (var r in running) sb.AppendLine($"  - {r.subsystemDescriptor.id} running={r.running}");
        }

        static void DumpRoots(StringBuilder sb)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.AppendLine($"Scene {s.name}:");
                foreach (var go in s.GetRootGameObjects())
                    sb.AppendLine($"  - {go.name} active={go.activeSelf} [{ComponentNames(go)}]");
            }
            var dontDestroy = new List<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.parent == null && t.gameObject.scene.name == "DontDestroyOnLoad") dontDestroy.Add(t.gameObject);
            sb.AppendLine("DontDestroyOnLoad:");
            foreach (var go in dontDestroy) sb.AppendLine($"  - {go.name} active={go.activeSelf} [{ComponentNames(go)}]");
        }

        public static string TPath(Transform t)
        {
            if (!t) return "null";
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string ComponentNames(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                names.Add(c == null ? "<missing>" : c.GetType().Name);
            return string.Join(", ", names);
        }

        static void DumpComponents(StringBuilder sb, GameObject go, string indent)
        {
            foreach (var c in go.GetComponents<Component>())
                sb.AppendLine($"{indent}+ {(c == null ? "<missing script>" : c.GetType().FullName)}");
        }

        static void DumpHierarchy(StringBuilder sb, Transform root, string indent, int maxDepth, int maxLines)
        {
            int lines = 0;
            void Rec(Transform t, int depth)
            {
                if (lines++ > maxLines) return;
                sb.AppendLine($"{indent}{new string(' ', depth * 2)}{t.name} active={t.gameObject.activeSelf} lp={t.localPosition} lr={t.localEulerAngles} [{ComponentNames(t.gameObject)}]");
                if (depth >= maxDepth) return;
                foreach (Transform c in t) Rec(c, depth + 1);
            }
            Rec(root, 0);
            if (lines > maxLines) sb.AppendLine($"{indent}... truncated at {maxLines} lines");
        }
    }
}
