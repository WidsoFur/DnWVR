using System;
using DnWVR.XR;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Poke hands for the main menu and credits (menu within arm's reach): a fingertip near a button hovers, pushing onto
    /// the panel presses, pulling back clicks, all through the system mouse the laser feeds. A press only counts when the
    /// fingertip approached from the front at the panel's current placement.
    /// </summary>
    public class MenuHands : MonoBehaviour
    {
        public static MenuHands Instance { get; private set; }
        /// <summary>A fingertip currently owns the UI pointer (the laser must not feed the mouse).</summary>
        public static bool PokeActive { get; private set; }

        public static float PokeMenuDistance = 0.5f;
        public static float PokeMenuWidth = 0.8f;
        public static float PokeMenuHeight = -0.2f;
        public static float HoverDistance = 0.10f;   // fingertip up to this far in front of the panel hovers
        public static float PressDepth = 0.004f;     // fingertip this close to (or behind) the panel presses
        public static float ReleaseDistance = 0.02f; // pull back this far in front of the panel to release
        public static float MaxBehind = 0.12f;       // deeper than this behind the panel does not count
        public static float OwnerBias = 0.02f;       // the hovering owner keeps the pointer unless another tip is this much closer
        /// <summary>Index fingertip relative to the controller grip pose (metres, controller space).</summary>
        public static Vector3 TipOffset = new Vector3(0f, -0.02f, 0.11f);


        struct Touch
        {
            public Canvas Canvas;
            public float Depth;      // metres; negative = in front of the panel, positive = pushed through
            public Vector3 Point;    // fingertip projected onto the panel
        }

        class Hand
        {
            public XRNode Node;
            public bool Right;
            public Transform Root;
            public Transform Tip;
            public Vector3 TipPos;
            public bool Tracked;
            public bool Pressed;
            public bool HasTouch;
            public Touch Contact;
            // Armed = the fingertip has been in front of this panel at this placement since it last lost it.
            public bool Armed;
            public Canvas ArmCanvas;
            public Vector3 ArmPos;
            public Quaternion ArmRot;
        }

        readonly Hand[] _hands = new Hand[2];
        int _owner = -1;
        int _pokeFrame = -1;
        bool _pointerWasOnScreen;
        Vector2 _lastScreen = VirtualMouse.OffScreen;

        public static bool IsPokeScene
        {
            get
            {
                var n = SceneManager.GetActiveScene().name;
                return n == "StartScene" || n == "EndScene";
            }
        }

        /// <summary>Pref MenuHands: poke hands in the main menu and credits, with the menu within arm's reach.</summary>
        public static bool Enabled = false;

        public static float MenuDistance(float normal) => Enabled && XRBootstrap.IsRunning && IsPokeScene ? PokeMenuDistance : normal;
        public static float MenuWidth(float normal) => Enabled && XRBootstrap.IsRunning && IsPokeScene ? PokeMenuWidth : normal;
        public static float MenuHeightOffset(float normal) => Enabled && XRBootstrap.IsRunning && IsPokeScene ? PokeMenuHeight : normal;

        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("DnWVR_MenuHands");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MenuHands>();
        }

        void Awake()
        {
            var mat = MakeMaterial(new Color(0.93f, 0.78f, 0.68f, 1f));
            var tipMat = MakeMaterial(new Color(1f, 0.9f, 0.55f, 1f));
            _hands[0] = BuildHand("Left", XRNode.LeftHand, false, mat, tipMat);
            _hands[1] = BuildHand("Right", XRNode.RightHand, true, mat, tipMat);
            VRRig.AfterCameraWrite += OnCameraWritten;
        }

        void OnDestroy()
        {
            VRRig.AfterCameraWrite -= OnCameraWritten;
        }

        // ---------------------------------------------------------------------------------------------------
        // Visuals
        // ---------------------------------------------------------------------------------------------------

        static Material MakeMaterial(Color color)
        {
            foreach (var name in new[] { "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default" })
            {
                var sh = Shader.Find(name);
                if (sh == null) continue;
                var m = new Material(sh);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                if (m.HasProperty("_Color")) m.SetColor("_Color", color);
                return m;
            }
            var fallback = new Material(Canvas.GetDefaultCanvasMaterial());
            fallback.color = color;
            return fallback;
        }

        Hand BuildHand(string side, XRNode node, bool right, Material mat, Material tipMat)
        {
            var root = new GameObject("DnWVR_MenuHand" + side).transform;
            root.SetParent(transform, false);
            float mirror = right ? 1f : -1f;

            Part(root, PrimitiveType.Sphere, mat, new Vector3(0f, -0.005f, 0.02f), Quaternion.identity, new Vector3(0.075f, 0.03f, 0.09f));
            // index finger, pointing forward to the tip
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0f, -0.012f, 0.078f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.018f, 0.032f, 0.018f));
            // curled middle / ring / pinky
            for (int i = 0; i < 3; i++)
                Part(root, PrimitiveType.Sphere, mat, new Vector3(-mirror * (0.018f + i * 0.017f), -0.02f, 0.06f), Quaternion.identity, new Vector3(0.02f, 0.022f, 0.028f));
            // thumb
            Part(root, PrimitiveType.Capsule, mat, new Vector3(mirror * 0.035f, -0.005f, 0.04f), Quaternion.Euler(80f, mirror * -35f, 0f), new Vector3(0.018f, 0.022f, 0.018f));

            var tip = Part(root, PrimitiveType.Sphere, tipMat, TipOffset, Quaternion.identity, Vector3.one * 0.012f);
            tip.name = "Tip";
            root.gameObject.SetActive(false);
            return new Hand { Node = node, Right = right, Root = root, Tip = tip };
        }

        static Transform Part(Transform parent, PrimitiveType type, Material mat, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        // ---------------------------------------------------------------------------------------------------
        // Per frame
        // ---------------------------------------------------------------------------------------------------

        // Runs right after the rig writes the camera (LateUpdate and again before render).
        void OnCameraWritten()
        {
            // With the pref off: no hands, no poke, the pointer is released and the laser handles the menus.
            bool xr = VRRig.Active && Enabled;
            bool showHands = xr && !VRHands.Attached;
            foreach (var h in _hands)
            {
                Vector3 lp = Vector3.zero;
                Quaternion lr = Quaternion.identity;
                h.Tracked = xr && HeadPose.TryGetController(h.Node, out lp, out lr);
                if (h.Tracked)
                {
                    VRRig.TrackingToWorld(lp, lr, out var wp, out var wr);
                    h.Root.SetPositionAndRotation(wp, wr);
                    h.TipPos = wp + wr * TipOffset;
                }
                bool visible = showHands && h.Tracked;
                if (h.Root.gameObject.activeSelf != visible) h.Root.gameObject.SetActive(visible);
            }

            if (Time.frameCount == _pokeFrame) return;
            _pokeFrame = Time.frameCount;
            UpdatePoke(xr && IsPokeScene && !VRHands.Attached);
        }

        void Update()
        {
            // Nothing wrote the camera this frame (XR stopped, no pose): let go of the pointer.
            if (!VRRig.Active)
            {
                if (PokeActive) ReleasePointer();
                foreach (var h in _hands)
                {
                    h.Pressed = h.Armed = h.HasTouch = false;
                    if (h.Root.gameObject.activeSelf) h.Root.gameObject.SetActive(false);
                }
            }
        }

        static bool IsMenuCanvas(Canvas c) => c.name == "Menu" || c.name.StartsWith("GameMenu", StringComparison.Ordinal);

        static bool FindTouch(Hand h, out Touch touch)
        {
            touch = default;
            bool found = false;
            float best = float.MaxValue;
            foreach (var c in VRUI.Converted)
            {
                if (c == null || !c.gameObject.activeInHierarchy || c.renderMode != RenderMode.WorldSpace) continue;
                if (!IsMenuCanvas(c)) continue;
                var eventCam = VRUI.EventCamera(c);
                if (eventCam == null) continue;
                var rt = (RectTransform)c.transform;
                // The panel's front faces the viewer; its forward axis points away from them.
                float depth = Vector3.Dot(h.TipPos - rt.position, rt.forward);
                if (depth < -HoverDistance || depth > MaxBehind) continue;
                var point = h.TipPos - rt.forward * depth;
                var local = rt.InverseTransformPoint(point);
                if (!rt.rect.Contains(new Vector2(local.x, local.y))) continue;
                // The UI raycaster resolves the pointer through the event camera: a point outside its view
                // cannot hit anything, so it must not take the pointer (the laser stays usable).
                var vp = eventCam.WorldToViewportPoint(point);
                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                float score = Mathf.Abs(depth);
                if (score < best)
                {
                    best = score;
                    touch = new Touch { Canvas = c, Depth = depth, Point = point };
                    found = true;
                }
            }
            return found;
        }

        void TrackTouch(int index)
        {
            var h = _hands[index];
            h.HasTouch = h.Tracked && FindTouch(h, out h.Contact);
            if (!h.HasTouch)
            {
                h.Armed = false;
                return;
            }
            var t = h.Contact.Canvas.transform;
            if (h.ArmCanvas != h.Contact.Canvas || h.ArmPos != t.position || h.ArmRot != t.rotation)
            {
                // Another panel, or the panel moved under the fingertip: it must be approached from the front again.
                h.ArmCanvas = h.Contact.Canvas;
                h.ArmPos = t.position;
                h.ArmRot = t.rotation;
                h.Armed = false;
                if (h.Pressed)
                {
                    // Let go without a click (button up off screen hits nothing).
                    h.Pressed = false;
                    if (_owner == index) VirtualMouse.Release();
                }
            }
            if (h.Contact.Depth < -ReleaseDistance) h.Armed = true;
        }

        void UpdatePoke(bool allowed)
        {
            if (!allowed)
            {
                if (PokeActive) ReleasePointer();
                foreach (var h in _hands) h.Pressed = h.Armed = h.HasTouch = false;
                return;
            }

            for (int i = 0; i < _hands.Length; i++) TrackTouch(i);

            // A pressing hand keeps the pointer; otherwise the fingertip closest to a panel takes it.
            int chosen = -1;
            if (_owner >= 0 && _hands[_owner].Pressed && _hands[_owner].HasTouch)
            {
                chosen = _owner;
            }
            else
            {
                float best = float.MaxValue;
                for (int i = 0; i < _hands.Length; i++)
                {
                    if (!_hands[i].HasTouch) continue;
                    float score = Mathf.Abs(_hands[i].Contact.Depth) - (i == _owner ? OwnerBias : 0f);
                    if (score < best) { best = score; chosen = i; }
                }
            }

            if (chosen != _owner && _owner >= 0 && _hands[_owner].Pressed)
            {
                // The pressing hand left its panel: release before anyone else takes the pointer.
                _hands[_owner].Pressed = false;
                VirtualMouse.Feed(_lastScreen, false);
            }
            for (int i = 0; i < _hands.Length; i++)
                if (i != chosen) _hands[i].Pressed = false;

            if (chosen < 0)
            {
                if (PokeActive) ReleasePointer();
                return;
            }

            // A hand that just took the pointer shows its position for a frame before it may press,
            // so a hand-off never lands a release and a press in the same input update.
            bool handoff = chosen != _owner;
            _owner = chosen;
            var hand = _hands[chosen];
            var touch = hand.Contact;
            if (!hand.Pressed && hand.Armed && !handoff && touch.Depth >= -PressDepth)
            {
                hand.Pressed = true;
                VRWidgets.Haptic(hand.Node, 0.35f, 0.03f);
            }
            else if (hand.Pressed && touch.Depth < -ReleaseDistance)
            {
                hand.Pressed = false;
                VRWidgets.Haptic(hand.Node, 0.15f, 0.02f);
            }

            var sp = VRUI.EventCamera(touch.Canvas).WorldToScreenPoint(touch.Point); // non-null: FindTouch checked it this frame
            _lastScreen = new Vector2(sp.x, sp.y);
            VirtualMouse.Feed(_lastScreen, hand.Pressed);
            PokeActive = true;
            _pointerWasOnScreen = true;
        }

        void ReleasePointer()
        {
            if (_pointerWasOnScreen) VirtualMouse.Release();
            _pointerWasOnScreen = false;
            PokeActive = false;
            _owner = -1;
        }
    }
}
