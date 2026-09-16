using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Laser pointer for menus and dialogue choices, shown while the game wants a free cursor. The right controller's
    /// hit on a converted canvas is fed to the system Mouse as a screen position (trigger = left button), so the game's
    /// InputSystemUIInputModule sees an ordinary mouse and every widget works unchanged.
    /// </summary>
    public class VRLaser : MonoBehaviour
    {
        public static VRLaser Instance { get; private set; }
        public static float MaxDistance = 6f;

        /// <summary>Above every panel the mod draws (32000) and every converted canvas (VRUI.SortingShift and above).</summary>
        const int SortingOrder = 32500;

        static MelonLogger.Instance s_log;

        LineRenderer _line;
        Transform _dot;
        Material _mat;
        bool _visible;
        bool _wasHit;

        public static bool PointerActive { get; private set; }

        /// <summary>Which controller the pointer is on: the one that last reached out with it.</summary>
        public static bool PointerHandIsRight { get; private set; } = true;

        static bool s_leftTriggerWas, s_rightTriggerWas;

        public static void Ensure(MelonLogger.Instance log)
        {
            s_log = log;
            if (Instance != null) return;
            var go = new GameObject("DnWVR_Laser");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<VRLaser>();
        }

        void Awake()
        {
            _mat = new Material(Canvas.GetDefaultCanvasMaterial());
            // Drawn after the on-top UI panels (VRUI) so the ray and its dot stay visible on them.
            _mat.renderQueue = 4001;
            // Sorting order comes first, and the panels sit at VRUI.SortingShift and above: the pointer goes over all of them.
            _mat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _line = gameObject.AddComponent<LineRenderer>();
            _line.material = _mat;
            _line.positionCount = 2;
            _line.startWidth = 0.004f;
            _line.endWidth = 0.002f;
            _line.startColor = new Color(0.4f, 0.9f, 1f, 0.9f);
            _line.endColor = new Color(0.4f, 0.9f, 1f, 0.2f);
            _line.useWorldSpace = true;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.sortingOrder = SortingOrder;
            _line.enabled = false;

            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.name = "Dot";
            dot.transform.SetParent(transform, false);
            dot.transform.localScale = Vector3.one * 0.012f;
            var mr = dot.GetComponent<MeshRenderer>();
            mr.material = _mat;
            mr.material.color = new Color(1f, 0.95f, 0.5f, 1f);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.sortingOrder = SortingOrder;
            _dot = dot.transform;
            _dot.gameObject.SetActive(false);
        }

        // Menus and dialogue choices; plain dialogue lines unlock the game's cursor too but need no pointer.
        static bool GameWantsCursor()
        {
            if (DebugSceneMenu.IsOpen) return true;
            try { return DialogueVR.PointerWanted(); }
            catch { return false; }
        }

        /// <summary>World-space aim ray of a controller: the OpenXR aim pose, else the grip pose.</summary>
        internal static bool TryGetAimRay(bool right, out Ray ray)
        {
            ray = default;
            Vector3 lp; Quaternion lr;
            var dev = InputSystem.GetDevice<XRController>(right ? UnityEngine.InputSystem.CommonUsages.RightHand : UnityEngine.InputSystem.CommonUsages.LeftHand);
            if (dev != null)
            {
                var pc = dev.TryGetChildControl<Vector3Control>("pointerPosition");
                var rc = dev.TryGetChildControl<QuaternionControl>("pointerRotation") ?? dev.TryGetChildControl<QuaternionControl>("pointerOrientation");
                if (pc != null && rc != null)
                {
                    lp = pc.ReadValue();
                    lr = rc.ReadValue();
                    if (lp != Vector3.zero || lr != Quaternion.identity)
                    {
                        VRRig.TrackingToWorld(lp, lr, out var wp, out var wr);
                        ray = new Ray(wp, wr * Vector3.forward);
                        return true;
                    }
                }
            }
            var grip = right ? VRHands.Right : VRHands.Left;
            if (grip != null && (right ? VRHands.RightTracked : VRHands.LeftTracked))
            {
                // Grip pose turned to the aim pose (measured from the runtime once it reports both, an estimate before that).
                var rot = grip.rotation * VRHands.GripToAim(right);
                ray = new Ray(grip.position, rot * Vector3.forward);
                return true;
            }
            return false;
        }

        // The pointer sits on the hand you last reached with - the one that closed its grip. While it is up, in menus and
        // over dialogue answers where grip does nothing, a trigger press claims it for that controller instead, so you
        // answer with whichever hand you raise. The press that claims it lands wherever that hand is already pointing.
        static bool ClaimPointer()
        {
            bool left = VRInput.Left.Valid && VRInput.Left.TriggerPressed;
            bool right = VRInput.Right.Valid && VRInput.Right.TriggerPressed;
            // Both at once claims nothing: there would be no telling which hand meant it.
            if (PointerActive && left != right)
            {
                if (left && !s_leftTriggerWas) VRHands.NotifyGrip(false);
                else if (right && !s_rightTriggerWas) VRHands.NotifyGrip(true);
            }
            s_leftTriggerWas = left;
            s_rightTriggerWas = right;
            PointerHandIsRight = VRHands.InteractHandIsRight;
            return PointerHandIsRight;
        }

        void Update()
        {
            bool right = ClaimPointer();
            DialogueVR.Tick();
            Ray ray = default;
            // A fingertip poking a menu owns the pointer; the laser waits until it leaves.
            bool on = VRRig.Active && !MenuHands.PokeActive && GameWantsCursor() && TryGetAimRay(right, out ray);
            if (!on)
            {
                SetVisible(false);
                if (PointerActive) { PointerActive = false; VirtualMouse.Release(); }
                return;
            }
            PointerActive = true;

            Canvas hitCanvas = null;
            float bestDist = MaxDistance;
            Vector3 hitPoint = ray.GetPoint(MaxDistance);
            foreach (var c in VRUI.Converted)
            {
                if (c == null || !c.gameObject.activeInHierarchy || c.renderMode != RenderMode.WorldSpace) continue;
                var rt = (RectTransform)c.transform;
                var plane = new Plane(rt.forward, rt.position);
                if (!plane.Raycast(ray, out float enter) || enter <= 0.05f || enter >= bestDist) continue;
                var p = ray.GetPoint(enter);
                var local = rt.InverseTransformPoint(p);
                if (!rt.rect.Contains(new Vector2(local.x, local.y))) continue;
                bestDist = enter;
                hitPoint = p;
                hitCanvas = c;
            }

            var hand = right ? VRInput.Right : VRInput.Left;
            bool click = hand.Valid && hand.TriggerPressed;
            var eventCam = hitCanvas != null ? VRUI.EventCamera(hitCanvas) : null;
            if (eventCam != null)
            {
                var sp = eventCam.WorldToScreenPoint(hitPoint);
                VirtualMouse.Feed(new Vector2(sp.x, sp.y), click);
                _wasHit = true;
            }
            else if (_wasHit)
            {
                VirtualMouse.Release();
                _wasHit = false;
            }

            SetVisible(true);
            _line.SetPosition(0, ray.origin);
            _line.SetPosition(1, hitPoint);
            _dot.position = hitPoint;
            _dot.gameObject.SetActive(hitCanvas != null);
        }

        void SetVisible(bool v)
        {
            if (_visible == v) return;
            _visible = v;
            _line.enabled = v;
            if (!v) _dot.gameObject.SetActive(false);
        }
    }
}
