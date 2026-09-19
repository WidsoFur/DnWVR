using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace DnWVR.VR
{
    /// <summary>
    /// Converts the game's Screen Space Overlay canvases, which the headset never sees, into world-space panels: menus are
    /// placed in front of the head each time one is shown, everything else (HUD, dialogue, fades) follows the head smoothly.
    /// </summary>
    public static class VRUI
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static float MenuDistance = 1.8f;
        public static float MenuWidthMeters = 1.7f;
        public static float HudDistance = 1.3f;
        public static float HudWidthMeters = 2.4f;
        public static float FollowSharpness = 8f;

        /// <summary>
        /// Converted canvases keep their order among themselves but all sort above the head-locked fader (1), so a
        /// game card drawn under a fade - the day-change intermission (-1), the credits - stays visible in VR.
        /// </summary>
        public const int SortingShift = 100;

        /// <summary>Converted canvases draw over the world (no depth test, overlay queue) instead of hiding behind geometry.</summary>
        public static bool OnTop = true;
        const int OnTopQueue = 4000;
        static readonly Dictionary<Material, Material> s_onTop = new Dictionary<Material, Material>();
        static readonly HashSet<Material> s_onTopCopies = new HashSet<Material>();
        static readonly List<Graphic> s_graphics = new List<Graphic>();

        static readonly HashSet<Canvas> s_converted = new HashSet<Canvas>();
        static readonly List<RectTransform> s_hudCanvases = new List<RectTransform>();
        public static IEnumerable<Canvas> Converted => s_converted;

        /// <summary>
        /// Re-scales the menu panels already standing in the room, for when the menu size changes while the game runs.
        /// A panel keeps the pixel size it was converted with, so a new world width is only a new scale.
        /// </summary>
        public static void ApplyMenuSize()
        {
            foreach (var c in s_converted)
            {
                if (c == null || !IsMenuCanvas(c)) continue;
                var rt = (RectTransform)c.transform;
                if (rt.sizeDelta.x <= 0f) continue;
                rt.localScale = Vector3.one * (MenuHands.MenuWidth(MenuWidthMeters) / rt.sizeDelta.x);
            }
        }

        static HudFollower s_hudFollower;
        static AccessTools.FieldRef<UiPrompt, RectTransform> s_promptRect;
        static AccessTools.FieldRef<UiProgressBar, Material> s_barMaterial;

        /// <summary>Adds a world-space panel the mod builds itself: the laser points at it and it draws over the world.</summary>
        public static void AddPanel(Canvas c)
        {
            if (c == null || !s_converted.Add(c)) return;
            if (OnTop) DrawOnTop(c);
        }

        public static void Initialize()
        {
        }

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            try { s_barMaterial = AccessTools.FieldRefAccess<UiProgressBar, Material>("_material"); }
            catch (Exception e) { Log.Warning("[VRUI] progress bar material not found, the bars may not fill in VR: " + e.Message); }
            try
            {
                s_promptRect = AccessTools.FieldRefAccess<UiPrompt, RectTransform>("promptRectTransform");
                var show = AccessTools.Method(typeof(Menu), "Show");
                if (show != null)
                {
                    harmony.Patch(show, postfix: new HarmonyMethod(typeof(VRUI).GetMethod(nameof(MenuShow_Postfix), Any)));
                    Log.Msg("[VRUI] patched Menu.Show");
                }
                var prompt = AccessTools.Method(typeof(UiPrompt), "_ShowPrompt");
                if (prompt != null)
                {
                    harmony.Patch(prompt, new HarmonyMethod(typeof(VRUI).GetMethod(nameof(ShowPrompt_Prefix), Any)));
                    Log.Msg("[VRUI] patched UiPrompt._ShowPrompt");
                }
                // The UI input module treats a locked cursor as "pointer off screen"; in VR the lock is meaningless.
                var lateUpdate = AccessTools.Method(typeof(GameStateManager), "LateUpdate");
                if (lateUpdate != null)
                {
                    harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(VRUI).GetMethod(nameof(CursorLock_Postfix), Any)));
                    Log.Msg("[VRUI] patched GameStateManager.LateUpdate");
                }
            }
            catch (Exception e)
            {
                Log.Error("[VRUI] patch failed: " + e);
            }
        }

        static bool IsMenuCanvas(Canvas c) => c.name == "Menu" || c.name.StartsWith("GameMenu");

        /// <summary>Convert all screen-space canvases in the loaded scenes. Safe to call repeatedly.</summary>
        public static void ConvertAll()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            var cam = Camera.main;
            if (cam == null) return;
            EnsureFollower();
            int n = 0;
            foreach (var c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!c.isRootCanvas || s_converted.Contains(c)) continue;
                if (c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                if (c.name.Contains("DebugUI") || c.name.StartsWith("DnWVR", StringComparison.Ordinal)) continue;
                try
                {
                    Convert(c);
                    s_converted.Add(c);
                    n++;
                }
                catch (Exception e)
                {
                    Log.Warning($"[VRUI] failed to convert canvas {c.name}: {e.Message}");
                }
            }
            if (n > 0) Log.Msg($"[VRUI] converted {n} canvases to world space");
        }

        static void Convert(Canvas c)
        {
            var scaler = c.GetComponent<CanvasScaler>();
            Vector2 pixelSize = new Vector2(Screen.width, Screen.height);
            if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                // Layouts authored for the monitor's aspect: keep that aspect for the reference resolution too.
                pixelSize = scaler.referenceResolution;
                float aspect = (float)Screen.width / Screen.height;
                if (scaler.matchWidthOrHeight >= 0.5f) pixelSize = new Vector2(pixelSize.y * aspect, pixelSize.y);
                else pixelSize = new Vector2(pixelSize.x, pixelSize.x / aspect);
            }

            c.renderMode = RenderMode.WorldSpace;
            c.sortingOrder += SortingShift;
            // No event camera of its own: the raycaster and the mod resolve a panel through Camera.main (see EventCamera).
            c.worldCamera = null;
            if (scaler != null) scaler.dynamicPixelsPerUnit = 2f;
            var rt = (RectTransform)c.transform;
            rt.sizeDelta = pixelSize;
            rt.pivot = new Vector2(0.5f, 0.5f);

            bool menu = IsMenuCanvas(c);
            float width = menu ? MenuHands.MenuWidth(MenuWidthMeters) : HudWidthMeters;
            float scale = width / pixelSize.x;
            if (menu)
            {
                rt.SetParent(null, false);
                rt.localScale = Vector3.one * scale;
                PlaceInFrontOfHead(rt, MenuHands.MenuDistance(MenuDistance));
            }
            else
            {
                // Scene canvases must stay in their own scene (re-parenting under a DontDestroyOnLoad object
                // would leak them across level loads), so the follower drives their world pose instead.
                rt.localScale = Vector3.one * scale;
                if (!s_hudCanvases.Contains(rt)) s_hudCanvases.Add(rt);
                s_hudFollower.SnapTo(rt);
            }
            if (OnTop) DrawOnTop(c);
            Log.Msg($"[VRUI] {c.name}: {pixelSize.x}x{pixelSize.y} px -> {width:0.00} m ({(menu ? "menu" : "head-locked")})");
        }

        // Gives every graphic under a converted canvas a copy of its material that skips the depth test and draws after the
        // world. Graphics created later (dialogue choices, dropdowns) are picked up by the follower's sweep.
        static void DrawOnTop(Canvas c)
        {
            c.GetComponentsInChildren(true, s_graphics);
            foreach (var g in s_graphics)
            {
                if (g is TMP_Text text)
                {
                    var shared = text.fontSharedMaterial;
                    var top = OnTopMaterial(shared);
                    if (top != shared) text.fontSharedMaterial = top;
                }
                else
                {
                    var material = g.material;
                    if (OwnBarMaterial(g, material))
                    {
                        // The bar writes its fill to this instance every frame; a copy would stop the bar where it stood.
                        material.renderQueue = OnTopQueue;
                        material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                        s_onTopCopies.Add(material);
                        continue;
                    }
                    var top = OnTopMaterial(material);
                    if (top != material) g.material = top;
                }
            }
            s_graphics.Clear();
        }

        // Whether material is the instance a progress bar made for this graphic alone, and never a shared asset.
        static bool OwnBarMaterial(Graphic g, Material material)
        {
            if (s_barMaterial == null || material == null || s_onTopCopies.Contains(material)) return false;
            var bar = g.GetComponentInParent<UiProgressBar>(true);
            return bar != null && s_barMaterial(bar) == material;
        }

        static Material OnTopMaterial(Material source)
        {
            if (source == null || s_onTopCopies.Contains(source)) return source;
            if (!s_onTop.TryGetValue(source, out var copy) || copy == null)
            {
                copy = new Material(source) { name = source.name + " (DnWVR on top)", renderQueue = OnTopQueue };
                copy.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
                s_onTop[source] = copy;
                s_onTopCopies.Add(copy);
            }
            return copy;
        }

        /// <summary>
        /// The camera the UI raycaster resolves a panel through. No converted canvas keeps a world camera of its own,
        /// so GraphicRaycaster.eventCamera is Camera.main; use this wherever the mod turns a point on a panel into a
        /// pointer position.
        /// </summary>
        public static Camera EventCamera(Canvas c) => Camera.main;

        static void EnsureFollower()
        {
            if (s_hudFollower != null) return;
            var go = new GameObject("DnWVR_HudFollower");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_hudFollower = go.AddComponent<HudFollower>();
        }

        /// <summary>Yaw-only placement in front of the current head pose, facing the head.</summary>
        public static void PlaceInFrontOfHead(Transform t, float distance)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var pos = cam.transform.position + fwd * distance;
            pos.y = cam.transform.position.y + MenuHands.MenuHeightOffset(-0.1f);
            t.position = pos;
            t.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }

        static void MenuShow_Postfix(Menu __instance)
        {
            if (!XR.XRBootstrap.IsRunning) return;
            try
            {
                ConvertAll();
                var canvas = __instance.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                var root = canvas.rootCanvas;
                if (root == null || !IsMenuCanvas(root) || !s_converted.Contains(root)) return;
                // Within arm's reach a submenu opened by a poke must stay where the finger is, not jump onto it.
                if (MenuHands.IsPokeScene && MenuHands.PokeActive) return;
                PlaceInFrontOfHead(root.transform, MenuHands.MenuDistance(MenuDistance));
            }
            catch (Exception e)
            {
                Log.Warning("[VRUI] MenuShow failed: " + e.Message);
            }
        }

        // Projects the interact prompt onto the head-locked HUD panel instead of screen pixels.
        static bool ShowPrompt_Prefix(UiPrompt __instance, Vector3 worldPosition)
        {
            if (!XR.XRBootstrap.IsRunning) return true;
            var rect = s_promptRect(__instance);
            if (rect == null) return true;
            var canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return true;
            var cam = EventCamera(canvas);
            if (cam == null) return true;
            var target = worldPosition + Vector3.up * 0.3f;
            var toTarget = target - cam.transform.position;
            if (Vector3.Dot(toTarget, cam.transform.forward) <= 0.05f)
            {
                rect.anchoredPosition = Vector2.down * 10000f;
                return false;
            }
            var canvasRt = (RectTransform)canvas.transform;
            var plane = new Plane(canvasRt.forward, canvasRt.position);
            var ray = new Ray(cam.transform.position, toTarget.normalized);
            if (!plane.Raycast(ray, out float enter))
            {
                rect.anchoredPosition = Vector2.down * 10000f;
                return false;
            }
            var hit = ray.GetPoint(enter);
            var local = canvasRt.InverseTransformPoint(hit);
            // Prompt rect is anchored bottom-left in the original layout; convert centre-based local to that.
            var size = canvasRt.rect.size;
            rect.anchoredPosition = new Vector2(local.x + size.x * 0.5f, local.y + size.y * 0.5f);
            return false;
        }

        static void CursorLock_Postfix()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        }

        public static void OnSceneChanged()
        {
            s_converted.RemoveWhere(c => c == null);
            s_hudCanvases.RemoveAll(rt => rt == null);
        }

        /// <summary>Smoothly keeps the head-locked canvases in front of the eyes (without re-parenting them).</summary>
        public class HudFollower : MonoBehaviour
        {
            bool _initialized;

            public void SnapTo(RectTransform rt)
            {
                var cam = Camera.main;
                if (cam == null) return;
                if (!_initialized) { Target(cam, out var p, out var r); transform.SetPositionAndRotation(p, r); _initialized = true; }
                rt.SetPositionAndRotation(transform.position, transform.rotation);
            }

            static void Target(Camera cam, out Vector3 pos, out Quaternion rot)
            {
                pos = cam.transform.position + cam.transform.forward * HudDistance;
                rot = cam.transform.rotation;
            }

            int _sweep;

            void LateUpdate()
            {
                var cam = Camera.main;
                if (cam == null || !XR.XRBootstrap.IsRunning) return;
                // Canvases created after the scene loads (CumEffect prefab, dropdown lists) are picked up by this sweep, and
                // graphics added to converted canvases (dialogue choices) get their on-top materials.
                if (++_sweep % 15 == 0 && OnTop)
                    foreach (var c in s_converted)
                        if (c != null) DrawOnTop(c);
                if (_sweep >= 60) { _sweep = 0; ConvertAll(); }
                Target(cam, out var targetPos, out var targetRot);
                float k = _initialized ? 1f - Mathf.Exp(-FollowSharpness * Time.unscaledDeltaTime) : 1f;
                _initialized = true;
                transform.position = Vector3.Lerp(transform.position, targetPos, k);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, k);
                for (int i = s_hudCanvases.Count - 1; i >= 0; i--)
                {
                    var rt = s_hudCanvases[i];
                    if (rt == null) { s_hudCanvases.RemoveAt(i); continue; }
                    rt.SetPositionAndRotation(transform.position, transform.rotation);
                }
            }
        }
    }
}
