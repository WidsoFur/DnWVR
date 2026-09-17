using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Holding A in a sex scene finishes it: while A is held the A button shows below the view with a ring that fills over
    /// <see cref="HoldSeconds"/>, and a full ring asks the scene for its climax (SexScene.RequestFinish). Letting go early
    /// empties the ring.
    /// </summary>
    public class FinishHold : MonoBehaviour
    {
        /// <summary>Seconds A must be held.</summary>
        public static float HoldSeconds = 3f;

        const float Distance = 0.7f;
        const float Drop = 0.18f;
        const float SizeMeters = 0.09f;
        const float Pixels = 256f;

        public static FinishHold Instance { get; private set; }

        Canvas _canvas;
        RectTransform _root;
        Image _ring;
        float _progress;
        bool _consumed;

        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("DnWVR_FinishHold");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<FinishHold>();
            Application.onBeforeRender += Instance.OnBeforeRender;
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        void Awake()
        {
            var cgo = new GameObject("Canvas", typeof(RectTransform));
            cgo.transform.SetParent(transform, false);
            cgo.layer = LayerMask.NameToLayer("UI");
            _canvas = cgo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 32000;
            _root = (RectTransform)cgo.transform;
            _root.sizeDelta = new Vector2(Pixels, Pixels);
            _root.localScale = Vector3.one * (SizeMeters / Pixels);

            var ring = VRWidgets.MakeDisc(256, 0.8f);
            MakeImage("Track", ring, new Color(1f, 1f, 1f, 0.25f), Pixels);
            _ring = MakeImage("Fill", ring, new Color(1f, 0.55f, 0.75f, 1f), Pixels);
            _ring.type = Image.Type.Filled;
            _ring.fillMethod = Image.FillMethod.Radial360;
            _ring.fillOrigin = (int)Image.Origin360.Top;
            _ring.fillClockwise = true;
            _ring.fillAmount = 0f;
            MakeImage("Button", VRWidgets.MakeDisc(256), new Color(0.08f, 0.08f, 0.1f, 0.85f), Pixels * 0.72f);

            var label = VRWidgets.MakeText("A", _root, 120f);
            label.text = "A";
            label.fontStyle = FontStyles.Bold;
            label.rectTransform.sizeDelta = new Vector2(Pixels, Pixels);

            _canvas.gameObject.SetActive(false);
        }

        Image MakeImage(string name, Sprite sprite, Color color, float size)
        {
            var image = VRWidgets.MakeImage(name, _root, color, sprite);
            image.raycastTarget = false;
            image.rectTransform.sizeDelta = new Vector2(size, size);
            return image;
        }

        void Update()
        {
            bool button = VRInput.Right.Valid && VRInput.Right.Primary;
            if (!button) _consumed = false;
            if (!button || _consumed || !SexScene.CanFinish)
            {
                _progress = 0f;
                Show(false);
                return;
            }
            if (!_canvas.gameObject.activeSelf)
            {
                Show(true);
                VRUI.AddPanel(_canvas);
            }
            _progress += Time.unscaledDeltaTime / Mathf.Max(0.1f, HoldSeconds);
            _ring.fillAmount = Mathf.Clamp01(_progress);
            if (_progress < 1f) return;
            _consumed = true;
            _progress = 0f;
            Show(false);
            VRWidgets.Haptic(XRNode.RightHand, 0.6f, 0.15f);
            SexScene.RequestFinish();
        }

        void Show(bool visible)
        {
            if (_canvas.gameObject.activeSelf != visible) _canvas.gameObject.SetActive(visible);
            if (!visible) _ring.fillAmount = 0f;
        }

        // Head-locked below the view, placed with the late-latched head.
        void OnBeforeRender()
        {
            if (_canvas == null || !_canvas.gameObject.activeSelf) return;
            var cam = Camera.main;
            if (cam == null) return;
            var t = cam.transform;
            var pos = t.position + t.forward * Distance - t.up * Drop;
            _root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - t.position, t.up));
        }

    }
}
