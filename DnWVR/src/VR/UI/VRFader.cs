using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace DnWVR.VR
{
    /// <summary>
    /// Head-locked black overlay over the whole field of view, driven by camera flights (CutsceneState.InTransition),
    /// the game's IntermissionFade and BlackoutFader, and short mod fades. Going black is instant, clearing is smooth.
    /// It covers the world but not the VR panels or the laser: the game plays dialogue and asks questions over its own
    /// blackout (every sex scene starts black until its intro is answered).
    /// </summary>
    public class VRFader : MonoBehaviour
    {
        public static VRFader Instance { get; private set; }
        public static float FadeOutSpeed = 4f;    // back to clear, per second
        public static float Distance = 0.5f;
        public static float SizeMeters = 8f;

        static AccessTools.FieldRef<BlackoutFader> s_blackout;
        static AccessTools.FieldRef<BlackoutFader, CanvasGroup> s_blackoutGroup;

        Canvas _canvas;
        Image _image;
        float _alpha;
        float _requestUntil;
        float _requestAlpha;
        int _lastFrame = -1;

        public static void Ensure()
        {
            if (Instance != null) return;
            try
            {
                s_blackout = AccessTools.StaticFieldRefAccess<BlackoutFader>(AccessTools.Field(typeof(BlackoutFader), "_instance"));
                s_blackoutGroup = AccessTools.FieldRefAccess<BlackoutFader, CanvasGroup>("canvasGroup");
            }
            catch (Exception e)
            {
                Log.Warning("[VRFader] BlackoutFader unavailable: " + e.Message);
            }
            var go = new GameObject("DnWVR_Fader");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<VRFader>();
            Application.onBeforeRender += Instance.OnBeforeRender;
        }

        void Awake()
        {
            var cgo = new GameObject("Canvas", typeof(RectTransform));
            cgo.transform.SetParent(transform, false);
            cgo.layer = LayerMask.NameToLayer("UI");
            _canvas = cgo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            // Just above the world (its transparents sort at 0) and below every converted panel, which VRUI lifts to
            // VRUI.SortingShift and above: the fader hides the world, never the game's own cards, HUD or dialogue.
            _canvas.sortingOrder = 1;
            var rt = (RectTransform)cgo.transform;
            float px = 4000f;
            rt.sizeDelta = new Vector2(px, px);
            rt.localScale = Vector3.one * (SizeMeters / px);
            rt.localPosition = new Vector3(0f, 0f, Distance);

            var igo = new GameObject("Black", typeof(RectTransform));
            igo.transform.SetParent(rt, false);
            igo.layer = cgo.layer;
            _image = igo.AddComponent<Image>();
            _image.color = new Color(0f, 0f, 0f, 0f);
            _image.raycastTarget = false;
            // Depth-independent: never occluded by walls/props between the eyes and the quad.
            var mat = new Material(Canvas.GetDefaultCanvasMaterial()) { renderQueue = 3999 };
            mat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            _image.material = mat;
            var irt = (RectTransform)igo.transform;
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
            _canvas.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        /// <summary>Ask for black for a short time (e.g. around a teleport or a camera cut).</summary>
        public static void Flash(float seconds, float alpha = 1f)
        {
            if (Instance == null) return;
            // Keep a still-running stronger request; an expired one must not leak into this one.
            bool previousStillRunning = Time.unscaledTime < Instance._requestUntil;
            Instance._requestAlpha = previousStillRunning ? Mathf.Max(alpha, Instance._requestAlpha) : alpha;
            Instance._requestUntil = Mathf.Max(Instance._requestUntil, Time.unscaledTime + seconds);
            Instance._alpha = Mathf.Max(Instance._alpha, alpha);
            Instance.Present();
        }

        /// <summary>
        /// Cover the view immediately. Call from LateUpdate when a camera flight is detected: a canvas enabled in
        /// onBeforeRender is drawn one frame late, which would show the first step of the flight.
        /// </summary>
        public static void ForceBlackNow()
        {
            if (Instance == null) return;
            Instance._alpha = 1f;
            Instance.Present();
        }

        float TargetAlpha()
        {
            float a = 0f;
            if (Time.unscaledTime < _requestUntil) a = Mathf.Max(a, _requestAlpha);
            try
            {
                // A player walking a scene never rides its camera flights.
                if (CutsceneState.InTransition && !SceneWalk.Walking) a = 1f;
                // Between-level intermission: the game's full-screen transition card is only a HUD panel in VR.
                if (IntermissionFade.isRunning) a = 1f;
                if (s_blackout != null)
                {
                    var bf = s_blackout();
                    if (bf != null)
                    {
                        var g = s_blackoutGroup(bf);
                        if (g != null) a = Mathf.Max(a, g.alpha);
                    }
                }
            }
            catch { }
            return Mathf.Clamp01(a);
        }

        void Update()
        {
            if (!XR.XRBootstrap.IsRunning && _canvas.gameObject.activeSelf)
            {
                _alpha = 0f;
                _canvas.gameObject.SetActive(false);
            }
        }

        void OnBeforeRender()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            if (Time.frameCount != _lastFrame)
            {
                _lastFrame = Time.frameCount;
                CutsceneState.Refresh();
                float target = TargetAlpha();
                if (target > _alpha) _alpha = target;
                else _alpha = Mathf.MoveTowards(_alpha, target, FadeOutSpeed * Time.unscaledDeltaTime);
            }
            Present();
            var cam = Camera.main;
            if (cam != null) transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
        }

        void Present()
        {
            bool visible = _alpha > 0.005f;
            if (_canvas.gameObject.activeSelf != visible) _canvas.gameObject.SetActive(visible);
            if (visible) _image.color = new Color(0f, 0f, 0f, _alpha);
        }
    }
}
