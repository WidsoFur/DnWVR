using System;
using System.Collections.Generic;
using com.gatordragongames.washnwalk.tools;
using DnWVR.XR;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Controller anchors (Left/Right) and surface hands (PhysLeft/PhysRight) that carry the game's single bare hand
    /// (Plapper, a left-hand mesh mirrored on the right) and tool socket (ToolAnchor) on either controller; the tool
    /// controller shows a copy of the paw (the twin) while it holds nothing. Everything lives in the active scene, never
    /// DontDestroyOnLoad, so the re-parented game objects die with their scene.
    /// </summary>
    public static class VRHands
    {
        public static Transform Left { get; private set; }
        public static Transform Right { get; private set; }
        public static bool LeftTracked { get; private set; }
        public static bool RightTracked { get; private set; }
        /// <summary>Seconds of controller speed history behind <see cref="PeakSpeed"/>.</summary>
        public const float PeakWindow = 0.1f;

        public static SurfaceHand PhysLeft { get; private set; }
        public static SurfaceHand PhysRight { get; private set; }

        /// <summary>Hands stop at solid surfaces and paws lie on them (off: hands go wherever the controllers are).</summary>
        public static bool PhysicalHands = true;
        /// <summary>Radius (m) of the palm sphere that is kept out of surfaces and probes for touch.</summary>
        public static float PalmRadius = 0.045f;
        /// <summary>Radius (m) of the paw's touch capsule, from the palm centre to mid-finger.</summary>
        public static float FingerRadius = 0.028f;
        /// <summary>A controller sample implying more than this (m/s, tracking space) is a tracking jump, not hand motion.</summary>
        public static float MaxTrackSpeed = 10f;
        /// <summary>Shows the hand copy on the tool controller while that controller holds nothing.</summary>
        public static bool SecondHand = true;
        /// <summary>Hands rest on the dragon's penis (its tube, see <see cref="PenisTube"/>) instead of passing through it.</summary>
        public static bool RestOnPenis = true;

        // Paw contact capsule in Plapper space (bone plane z = -0.04, fingers +Y): the palm centre, which the base pose puts
        // on the grip origin, and mid-finger (knuckles near y 0.11, tips near 0.2).
        static readonly Vector3 PawPalm = new Vector3(0.01f, 0.05f, -0.04f);
        static readonly Vector3 PawFingers = new Vector3(0f, 0.15f, -0.04f);

        // Built-in paw pose on the grip. Mesh: fingers +Y, palm +Z, thumb +X. Grip: +X right, +Y back toward the user,
        // +Z up the handle, aim ray 60 degrees below +Z. Pitch 180 points the fingers forward with the palm down,
        // 30 degrees below the aim ray; z -12 turns the left paw's fingers slightly right; PlapperBaseRoll stands the paw up
        // around the controller (thumb up, palm toward the other hand). Mirrored on the right.
        public static readonly Vector3 PlapperBaseEuler = new Vector3(180f, 0f, -12f);
        /// <summary>Roll of the left paw about its own fingers (degrees; + turns it to the left, mirrored on the right paw).</summary>
        public const float PlapperBaseRoll = 90f;
        /// <summary>Puts the paw's palm centre on the grip origin (the palm centroid of the real hand).</summary>
        public static readonly Vector3 PlapperBasePos = -(Quaternion.Euler(PlapperBaseEuler) * Quaternion.AngleAxis(PlapperBaseRoll, Vector3.up) * PawPalm);

        // Grip offsets added to the built-in pose, in controller axes (authored for the LEFT side; mirrored on the right).
        public static Vector3 PlapperOffsetPos = Vector3.zero;
        public static Vector3 PlapperOffsetEuler = Vector3.zero;
        // Tool socket offsets, in the tool controller's aim frame (authored for the RIGHT side; mirrored on the left).
        public static Vector3 ToolOffsetPos = Vector3.zero;
        public static Vector3 ToolOffsetEuler = Vector3.zero;

        // Tools are authored in the flat game's camera frame (+Z forward, +Y up), which is a controller's aim pose, so the
        // tool socket is turned by the measured grip -> aim rotation; this pitch estimate stands in until it is measured.
        public static readonly Quaternion DefaultGripToAim = Quaternion.Euler(40f, 0f, 0f);
        static readonly Quaternion[] s_gripToAim = { DefaultGripToAim, DefaultGripToAim };
        static readonly bool[] s_gripToAimMeasured = new bool[2];
        /// <summary>Rotation from a controller's grip pose to its aim pose (measured once the runtime reports both).</summary>
        public static Quaternion GripToAim(bool right) => s_gripToAim[right ? 1 : 0];

        public static int HandLayer { get; private set; } = 0;

        public static bool ToolHandIsRight { get; private set; } = true;
        /// <summary>The controller that last pressed grip; tool pick-ups and interaction rays come from it.</summary>
        public static bool InteractHandIsRight { get; private set; } = true;
        public static Transform InteractHand => InteractHandIsRight ? Right : Left;
        public static bool InteractHandTracked => InteractHandIsRight ? RightTracked : LeftTracked;

        static Transform s_plapper, s_toolAnchor;
        static Transform s_plapperParent, s_toolParent;
        static Vector3 s_plapperLP, s_toolLP;
        static Quaternion s_plapperLR, s_toolLR;
        static Vector3 s_plapperScale = Vector3.one;
        static bool s_attached;
        static MelonLogger.Instance s_log;
        static float s_lastSampleTime = -1f;
        // Hand speed is sampled in tracking space, so it is body-relative: walking, riding the dragon skin or a moving
        // cutscene spot add nothing to a slap.
        static Vector3 s_lastLpL, s_lastLpR, s_trackVelL, s_trackVelR;
        static bool s_hasLpL, s_hasLpR;
        static readonly TimedSamples s_speedL = new TimedSamples(64), s_speedR = new TimedSamples(64);
        static bool s_toolEventHooked;
        static bool s_holdingTool;
        // The held tool model and its own solid colliders, switched off while it is held in VR.
        static ToolModel s_toolCollidersModel;
        static readonly List<Collider> s_toolGameColliders = new List<Collider>();
        // The player's own colliders: surface hands and their queries never stop at them.
        static readonly HashSet<Collider> s_playerColliders = new HashSet<Collider>();
        static Transform s_plapperSlap;
        // Whether the scene ships the Plapper active (sex scenes do not); Reparent shows it regardless.
        static bool s_plapperAuthoredActive;
        static Transform s_twin, s_twinSource, s_twinHand, s_twinSlap;
        static Animator s_twinAnimator;
        static AudioSource s_twinAudio;

        public static bool Attached => s_attached;
        public static bool HoldingTool => s_holdingTool;
        public static bool PlapperAuthoredActive => s_plapperAuthoredActive;
        /// <summary>A controller shows a bare paw when it is not the tool hand or the tool hand holds nothing.</summary>
        public static bool IsBareHand(bool right) => right != ToolHandIsRight || !s_holdingTool;
        /// <summary>The game's ToolTest on ToolAnchor (its interactEnabled is the game's interaction switch).</summary>
        public static ToolTest ToolTest { get; private set; }

        public static Animator TwinAnimator => s_twinAnimator;
        public static AudioSource TwinAudio => s_twinAudio;
        public static bool TwinActive => s_twin != null && s_twin.gameObject.activeInHierarchy;

        /// <summary>Controller velocity relative to the body (tracking space turned by the rig yaw), once per frame.</summary>
        public static Vector3 TrackVel(bool right) => right ? s_trackVelR : s_trackVelL;
        /// <summary>Largest <see cref="TrackVel"/> magnitude over the last <see cref="PeakWindow"/> seconds.</summary>
        public static float PeakSpeed(bool right) => (right ? s_speedR : s_speedL).Max(Time.unscaledTime, PeakWindow);

        public static void Initialize(MelonLogger.Instance log)
        {
            s_log = log;
            HandLayer = PickHandLayer();
            PenisTube.Initialize(log);
            VRRig.AfterCameraWrite += UpdateFromTracking;
            if (!s_toolEventHooked)
            {
                ToolManager.ToolChangedEvent += OnToolChanged;
                s_toolEventHooked = true;
            }
        }

        // An unnamed layer has an empty row in the game's collision matrix: hand objects on it never collide, and the game's
        // masked raycasts never see them.
        static int PickHandLayer()
        {
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return 0;
        }

        /// <summary>Creates the anchors and surface hands in the active scene; call after every scene load.</summary>
        public static void EnsureAnchors()
        {
            if (Left != null && Right != null && PhysLeft != null && PhysRight != null) return;
            Left = new GameObject("DnWVR_HandL").transform;
            Right = new GameObject("DnWVR_HandR").transform;
            PhysLeft = SurfaceHand.Create("DnWVR_PhysHandL", Left, HandLayer);
            PhysRight = SurfaceHand.Create("DnWVR_PhysHandR", Right, HandLayer);
            s_lastSampleTime = -1f;
        }

        /// <summary>Places the anchors from the latest controller samples; runs right after each camera write.</summary>
        public static void UpdateFromTracking()
        {
            if (Left == null || Right == null) return;
            LeftTracked = Place(XRNode.LeftHand, Left, out var lpL, out bool validL);
            RightTracked = Place(XRNode.RightHand, Right, out var lpR, out bool validR);

            float now = Time.unscaledTime;
            float dt = now - s_lastSampleTime;
            if (VRRig.ConsumeRigChanged())
            {
                // Tracking space jumped (snap turn, recenter, cut) without the hands moving: place them afresh.
                // Body-relative (tracking-space) speed keeps sampling, so the jump adds nothing to it.
                PhysLeft?.ResetSolve();
                PhysRight?.ResetSolve();
            }
            // The late latch calls this again in the same frame (dt = 0): sample once per frame so LateUpdate and late-latch
            // samples never mix into one velocity.
            if (s_lastSampleTime >= 0f && dt > 1e-3f)
            {
                s_trackVelL = SampleTrackVel(s_speedL, validL && s_hasLpL, lpL - s_lastLpL, dt, now, "L", out _);
                s_trackVelR = SampleTrackVel(s_speedR, validR && s_hasLpR, lpR - s_lastLpR, dt, now, "R", out _);
            }
            if (dt > 1e-3f || s_lastSampleTime < 0f)
            {
                // A position the runtime does not track gives no sample, and neither does the first frame after it returns.
                s_lastLpL = lpL; s_hasLpL = validL;
                s_lastLpR = lpR; s_hasLpR = validR;
                s_lastSampleTime = now;
                MeasureGripToAim(false);
                MeasureGripToAim(true);
            }
            if (PhysicalHands)
            {
                SolveHand(false);
                SolveHand(true);
            }
            // HandPatches parks them from ToolTest, which does not tick where ToolAnchor is inactive (sex scenes).
            if (s_attached) ParkSlapColliders();
            // There nothing ticks the paws' touch either.
            if (s_attached && s_plapper != null && s_plapper.gameObject.activeInHierarchy)
                HandPatches.TickWithoutToolTest(s_plapper.GetComponent<PlapperHand>());
        }

        // A hand without a paw (holding a tool) is kept out at its grip only; the tool itself can still reach into things.
        static void SolveHand(bool right)
        {
            var hand = Phys(right);
            if (hand == null || !hand.gameObject.activeInHierarchy) return;
            bool paw = right == ToolHandIsRight ? TwinActive : s_plapper != null && s_plapper.gameObject.activeInHierarchy;
            if (paw) hand.Solve(PawPoint(right, PawPalm), PawLocalRotation(right), PalmRadius);
            else hand.Solve(Vector3.zero, null, PalmRadius);
        }

        /// <summary>
        /// Moves each paw's parked SlapCollider from plapper_L z = -5, which the base pose puts in front of the hand among
        /// the dragon's jiggle bones, to z = +5 behind it; a collider mid-slap (z = -0.13) is left alone. Call after the
        /// animators run and before JiggleUpdateExample (execution order 10200) reads its colliders.
        /// </summary>
        public static void ParkSlapColliders()
        {
            ParkSlapCollider(s_plapperSlap);
            ParkSlapCollider(s_twinSlap);
        }

        static void ParkSlapCollider(Transform slap)
        {
            if (slap != null && slap.localPosition.z < -1f) slap.localPosition = new Vector3(0f, 0.06f, 5f);
        }

        /// <summary>The palm centre of a bare paw (see <see cref="ContactPaw"/>).</summary>
        public static Vector3 ContactProbe(bool right)
        {
            ContactPaw(right, out var palm, out _);
            return palm;
        }

        /// <summary>
        /// A bare paw's touch capsule in world space, from the palm centre to about mid-finger, placed from the surface hand
        /// (which stays on surfaces) or, without surface hands, from the controller; same pose math as the drawn paw.
        /// </summary>
        public static void ContactPaw(bool right, out Vector3 palm, out Vector3 fingers)
        {
            Transform frame;
            var p = Phys(right);
            if (PhysicalHands && p != null && p.gameObject.activeInHierarchy) frame = p.transform;
            else
            {
                frame = right ? Right : Left;
                if (frame == null) { palm = fingers = Vector3.zero; return; }
            }
            var pos = frame.position;
            var rot = frame.rotation;
            palm = pos + rot * PawPoint(right, PawPalm);
            fingers = pos + rot * PawPoint(right, PawFingers);
        }

        static bool Place(XRNode node, Transform anchor, out Vector3 localPos, out bool positionValid)
        {
            if (!HeadPose.TryGetController(node, out localPos, out var lr, out positionValid)) return false;
            VRRig.TrackingToWorld(localPos, lr, out var wp, out var wr);
            anchor.SetPositionAndRotation(wp, wr);
            return true;
        }

        static Vector3 SampleTrackVel(TimedSamples speed, bool valid, Vector3 localDelta, float dt, float now, string side, out bool jump)
        {
            jump = false;
            if (!valid) return Vector3.zero;
            var v = VRRig.RigRotation * (localDelta / dt);
            if (v.sqrMagnitude > MaxTrackSpeed * MaxTrackSpeed)
            {
                jump = true;
                if (DnWVRMod.DebugInteractionLog) s_log?.Msg($"[VRHands] {side} controller jumped {localDelta.magnitude:0.000} m in one frame; no speed sample");
                return Vector3.zero;
            }
            speed.Add(now, v.magnitude);
            return v;
        }

        /// <summary>Called by input when a grip press starts on a controller.</summary>
        public static void NotifyGrip(bool right)
        {
            InteractHandIsRight = right;
        }

        static Transform Host(bool right)
        {
            if (PhysicalHands)
            {
                var ph = right ? PhysRight : PhysLeft;
                if (ph != null) { ph.SetActive(true); return ph.Visual; }
            }
            return right ? Right : Left;
        }

        static SurfaceHand Phys(bool right) => right ? PhysRight : PhysLeft;

        /// <summary>Moves the game's hand objects under the controllers. Safe to call repeatedly.</summary>
        public static void AttachGameHands()
        {
            if (!XRBootstrap.IsRunning) return;
            EnsureAnchors();
            var cam = Camera.main;
            if (cam == null) return;
            var plapper = s_plapper != null ? s_plapper : cam.transform.Find("Plapper");
            var tool = s_toolAnchor != null ? s_toolAnchor : cam.transform.Find("ToolAnchor");
            if (plapper == null && tool == null) return; // not a gameplay scene
            PhysLeft.SetActive(PhysicalHands);
            PhysRight.SetActive(PhysicalHands);
            CollectPlayerColliders();

            if (plapper != null && s_plapper != plapper)
            {
                s_plapper = plapper; s_plapperParent = plapper.parent; s_plapperLP = plapper.localPosition; s_plapperLR = plapper.localRotation; s_plapperScale = plapper.localScale;
                s_plapperAuthoredActive = plapper.gameObject.activeSelf;
                s_plapperSlap = plapper.Find("Hand/plapper_L/SlapCollider");
                DestroyTwin();
            }
            if (tool != null && s_toolAnchor != tool)
            {
                s_toolAnchor = tool; s_toolParent = tool.parent; s_toolLP = tool.localPosition; s_toolLR = tool.localRotation;
                ToolTest = tool.GetComponent<ToolTest>();
            }
            s_holdingTool = IsHoldingTool();
            EnsureTwin();
            Reparent();
            s_attached = s_plapper != null || s_toolAnchor != null;
        }

        static bool IsHoldingTool()
        {
            try
            {
                if (!ToolManager.IsValid()) return false;
                var cur = ToolManager.GetCurrentTool();
                var empty = ToolManager.GetEmptyTool();
                return cur != null && empty != null && cur.GetName() != empty.GetName();
            }
            catch { return false; }
        }

        static void Reparent()
        {
            bool bareRight = !ToolHandIsRight;
            var toolHost = Host(ToolHandIsRight);
            var bareHost = Host(bareRight);
            if (s_toolAnchor != null && toolHost != null)
            {
                if (s_toolAnchor.parent != toolHost)
                {
                    s_toolAnchor.SetParent(toolHost, false);
                    s_log?.Msg($"VRHands: ToolAnchor on {(ToolHandIsRight ? "right" : "left")} hand");
                }
                PoseToolAnchor();
                SyncHeldToolColliders();
            }
            if (s_plapper != null && bareHost != null)
            {
                if (s_plapper.parent != bareHost)
                {
                    s_plapper.SetParent(bareHost, false);
                    s_log?.Msg($"VRHands: bare hand on {(bareRight ? "right (mirrored)" : "left")} hand");
                }
                PoseHandMesh(s_plapper, bareRight);
                s_plapper.gameObject.SetActive(true);
            }
            if (s_twin != null)
            {
                var twinHost = Host(ToolHandIsRight);
                if (twinHost != null && s_twin.parent != twinHost)
                {
                    s_twin.SetParent(twinHost, false);
                    s_log?.Msg($"VRHands: twin hand on {(ToolHandIsRight ? "right (mirrored)" : "left")} hand");
                }
                PoseHandMesh(s_twin, ToolHandIsRight);
                // Shown exactly when the real paw is (so both hands or neither), and never over a held tool.
                bool visible = SecondHand && s_plapper != null && s_plapper.gameObject.activeSelf && !s_holdingTool;
                if (s_twin.gameObject.activeSelf != visible) s_twin.gameObject.SetActive(visible);
            }
        }

        static void PoseToolAnchor()
        {
            if (s_toolAnchor == null) return;
            bool mirror = !ToolHandIsRight;
            var aim = GripToAim(ToolHandIsRight);
            s_toolAnchor.localPosition = aim * Mirror(ToolOffsetPos + HeldTool.AnchorOffset(HeldToolModel()), mirror);
            s_toolAnchor.localRotation = aim * Quaternion.Euler(MirrorEuler(ToolOffsetEuler, mirror));
            s_toolAnchor.localScale = Vector3.one;
        }

        // Grip and aim are poses of one rigid device, so the rotation between them is fixed; it is re-applied only when it
        // moves by 3 degrees or more.
        static void MeasureGripToAim(bool right)
        {
            try
            {
                var dev = UnityEngine.InputSystem.InputSystem.GetDevice<UnityEngine.InputSystem.XR.XRController>(
                    right ? UnityEngine.InputSystem.CommonUsages.RightHand : UnityEngine.InputSystem.CommonUsages.LeftHand);
                if (dev == null) return;
                var gripControl = dev.TryGetChildControl<UnityEngine.InputSystem.Controls.QuaternionControl>("deviceRotation");
                var aimControl = dev.TryGetChildControl<UnityEngine.InputSystem.Controls.QuaternionControl>("pointerRotation")
                                 ?? dev.TryGetChildControl<UnityEngine.InputSystem.Controls.QuaternionControl>("pointerOrientation");
                if (gripControl == null || aimControl == null) return;
                var grip = gripControl.ReadValue();
                var aim = aimControl.ReadValue();
                // An untracked controller reports zero or identity rotations: nothing to measure.
                if (!IsUnitRotation(grip) || !IsUnitRotation(aim) || grip == Quaternion.identity || aim == Quaternion.identity) return;
                var offset = Quaternion.Inverse(grip.normalized) * aim.normalized;
                int i = right ? 1 : 0;
                if (s_gripToAimMeasured[i] && Quaternion.Angle(s_gripToAim[i], offset) < 3f) return;
                s_gripToAim[i] = offset;
                s_gripToAimMeasured[i] = true;
                var e = offset.eulerAngles;
                s_log?.Msg($"VRHands: {(right ? "R" : "L")} controller aim pose is ({e.x:0.0}, {e.y:0.0}, {e.z:0.0}) from the grip");
                if (s_attached && right == ToolHandIsRight) PoseToolAnchor();
            }
            catch (Exception e)
            {
                if (s_gripToAimFailed) return;
                s_gripToAimFailed = true;
                s_log?.Warning("VRHands: grip -> aim measurement failed (tools keep the estimate): " + e.Message);
            }
        }

        static bool s_gripToAimFailed;

        static bool IsUnitRotation(Quaternion q)
        {
            float lengthSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return lengthSq > 0.9f && lengthSq < 1.1f;
        }

        static void PoseHandMesh(Transform t, bool right)
        {
            t.localPosition = PawLocalPosition(right);
            t.localRotation = PawLocalRotation(right);
            t.localScale = PawLocalScale(right);
        }

        static Vector3 PawLocalPosition(bool right) => Mirror(PlapperBasePos + PlapperOffsetPos, right);

        // The roll turns about the paw's own fingers (its +Y); mirroring the paw across X reverses the sense of that turn.
        static Quaternion PawLocalRotation(bool right)
            => Quaternion.Euler(MirrorEuler(PlapperOffsetEuler, right)) * Quaternion.Euler(MirrorEuler(PlapperBaseEuler, right))
               * Quaternion.AngleAxis(right ? -PlapperBaseRoll : PlapperBaseRoll, Vector3.up);

        static Vector3 PawLocalScale(bool right)
        {
            var s = s_plapperScale;
            return right ? new Vector3(-Mathf.Abs(s.x), s.y, s.z) : new Vector3(Mathf.Abs(s.x), s.y, s.z);
        }

        // A point in the posed Plapper's space, in its host's space (the controller anchor or the surface hand).
        static Vector3 PawPoint(bool right, Vector3 plapperPoint)
            => PawLocalPosition(right) + PawLocalRotation(right) * Vector3.Scale(PawLocalScale(right), plapperPoint);

        // The twin is the game's hand minus PlapperHand, so it raises no game events. It is cloned under an inactive
        // holder so no OnEnable runs first: a never-enabled PlapperHand also skips OnDisable, which would destroy the
        // copied AudioSource. Instantiate points the kept JiggleColliderExample at the clone's own SlapCollider.
        static void EnsureTwin()
        {
            if (s_plapper == null || (s_twin != null && s_twinSource == s_plapper)) return;
            DestroyTwin();
            var host = Host(ToolHandIsRight);
            if (host == null) return;
            GameObject holder = null;
            try
            {
                holder = new GameObject("DnWVR_TwinHolder");
                holder.SetActive(false);
                // Never "Plapper": AttachGameHands finds the real hand by that name.
                var go = UnityEngine.Object.Instantiate(s_plapper.gameObject, holder.transform, false);
                go.name = "DnWVR_PlapperTwin";
                foreach (var p in go.GetComponentsInChildren<PlapperHand>(true)) UnityEngine.Object.DestroyImmediate(p);
                s_twin = go.transform;
                s_twinSource = s_plapper;
                s_twinAnimator = go.GetComponent<Animator>();
                s_twinHand = s_twin.Find("Hand/plapper_L");
                // Flat mode can leave the source mid-reach; the copy starts at rest, posed only by PoseHandMesh.
                ResetLocalPose(s_twin.Find("Hand"));
                ResetLocalPose(s_twinHand);
                s_twinSlap = s_twin.Find("Hand/plapper_L/SlapCollider");
                s_twinAudio = s_twinHand != null ? s_twinHand.GetComponent<AudioSource>() : null;
                if (s_twinAudio != null) s_twinAudio.loop = true;
                go.SetActive(false);
                s_twin.SetParent(host, false);
                s_log?.Msg($"VRHands: twin hand created (animator={s_twinAnimator != null}, audio={s_twinAudio != null}, slap collider={s_twinSlap != null})");
            }
            catch (Exception e)
            {
                s_log?.Warning("VRHands: twin hand setup failed: " + e.Message);
                DestroyTwin();
            }
            finally
            {
                if (holder != null) UnityEngine.Object.Destroy(holder);
            }
        }

        static void DestroyTwin()
        {
            if (s_twin != null) UnityEngine.Object.Destroy(s_twin.gameObject);
            ForgetTwin();
        }

        static void ForgetTwin()
        {
            s_twin = s_twinSource = s_twinHand = s_twinSlap = null;
            s_twinAnimator = null;
            s_twinAudio = null;
        }

        static void ResetLocalPose(Transform t)
        {
            if (t != null) t.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        /// <summary>Holds the twin's plapper_L at rest, as the touch code does with PlapperHand.hand (the animator never drives it).</summary>
        public static void PinTwinHand() => ResetLocalPose(s_twinHand);

        static Vector3 Mirror(Vector3 v, bool mirror) => mirror ? new Vector3(-v.x, v.y, v.z) : v;
        static Vector3 MirrorEuler(Vector3 e, bool mirror) => mirror ? new Vector3(e.x, -e.y, -e.z) : e;

        static void CollectPlayerColliders()
        {
            s_playerColliders.Clear();
            foreach (var pc in UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var col in pc.GetComponentsInChildren<Collider>(true))
                    s_playerColliders.Add(col);
        }

        static void OnToolChanged(Tool newTool)
        {
            if (!s_attached) return;
            s_log?.Msg($"VRHands: tool -> {(newTool != null ? newTool.GetName() : "null")}");
            bool holding = IsHoldingTool();
            if (holding && !s_holdingTool)
            {
                // A tool was just picked up: it belongs to the hand that reached for it.
                if (ToolHandIsRight != InteractHandIsRight)
                    ToolHandIsRight = InteractHandIsRight;
            }
            s_holdingTool = holding;
            Reparent();
        }

        // A held tool's solid colliders (Default, World) would stop the surface hand's queries and the player's movement at
        // the hand, so they are off while held; triggers and Hitbox/SprayOnly volumes stay on for the game's queries.
        static void SyncHeldToolColliders()
        {
            try
            {
                var model = HeldToolModel();
                if (ReferenceEquals(model, s_toolCollidersModel)) return;
                // A new model: ToolManager destroyed the previous one with its tool, so there is nothing to restore.
                s_toolCollidersModel = model;
                s_toolGameColliders.Clear();
                if (model != null) DisableGameToolColliders(model);
            }
            catch (Exception e)
            {
                s_log?.Warning("VRHands: held tool collider setup failed: " + e.Message);
            }
        }

        static ToolModel HeldToolModel()
        {
            if (!ToolManager.IsValid()) return null;
            var tool = ToolManager.GetCurrentTool();
            if (tool == null) return null;
            var empty = ToolManager.GetEmptyTool();
            if (empty != null && tool.GetName() == empty.GetName()) return null;
            var model = tool.GetModel();
            return model != null ? model : null;
        }

        static void DisableGameToolColliders(ToolModel model)
        {
            foreach (var c in model.GetComponentsInChildren<Collider>(true))
            {
                // Parts the prefab keeps hidden never collide in the game either.
                if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                int layer = c.gameObject.layer;
                if (layer != 0 && layer != WalkNWashPhysics.WorldLayer) continue;
                c.enabled = false;
                s_toolGameColliders.Add(c);
            }
            if (s_toolGameColliders.Count > 0)
                s_log?.Msg($"VRHands: {model.name}: {s_toolGameColliders.Count} solid collider(s) switched off while held");
        }

        static void RestoreGameToolColliders()
        {
            foreach (var c in s_toolGameColliders)
                if (c != null) c.enabled = true;
            s_toolGameColliders.Clear();
            s_toolCollidersModel = null;
        }

        /// <summary>Gives the game's objects back to their original parents (XR stop). Safe if they were destroyed.</summary>
        public static void DetachGameHands()
        {
            try
            {
                DestroyTwin();
                RestoreGameToolColliders();
                if (s_plapper != null && s_plapperParent != null)
                {
                    s_plapper.SetParent(s_plapperParent, false);
                    s_plapper.localPosition = s_plapperLP; s_plapper.localRotation = s_plapperLR; s_plapper.localScale = s_plapperScale;
                }
                if (s_toolAnchor != null && s_toolParent != null)
                {
                    s_toolAnchor.SetParent(s_toolParent, false);
                    s_toolAnchor.localPosition = s_toolLP; s_toolAnchor.localRotation = s_toolLR;
                }
            }
            catch (Exception e) { s_log?.Warning("VRHands: detach failed: " + e.Message); }
            s_plapper = s_toolAnchor = null;
            s_plapperSlap = null;
            ToolTest = null;
            ForgetTwin();
            s_attached = false;
            PhysLeft?.SetActive(false);
            PhysRight?.SetActive(false);
        }

        /// <summary>Re-applies the offsets and the surface-hands switch after a pref change.</summary>
        public static void ApplyOffsets()
        {
            if (!s_attached) return;
            if (!PhysicalHands)
            {
                PhysLeft?.SetActive(false);
                PhysRight?.SetActive(false);
            }
            AttachGameHands();
        }

        /// <summary>Forgets all references once the scene, and everything created in it, is gone.</summary>
        public static void OnSceneChanged()
        {
            s_plapper = s_toolAnchor = null;
            s_plapperParent = s_toolParent = null;
            s_plapperSlap = null;
            ToolTest = null;
            // The twin died with this scene's hand objects.
            ForgetTwin();
            s_attached = false;
            s_holdingTool = false;
            // The tool model died with the scene.
            s_toolCollidersModel = null;
            s_toolGameColliders.Clear();
            s_playerColliders.Clear();
            ToolHandIsRight = true;
            InteractHandIsRight = true;
            Left = Right = null;
            PhysLeft = PhysRight = null;
            LeftTracked = RightTracked = false;
            s_lastSampleTime = -1f;
            s_trackVelL = s_trackVelR = Vector3.zero;
            s_hasLpL = s_hasLpR = false;
            s_speedL.Clear();
            s_speedR.Clear();
        }

        /// <summary>Is this collider one of ours (hands, held tool)?</summary>
        public static bool IsHandCollider(Collider c)
        {
            if (c == null) return false;
            if (c.gameObject.layer == HandLayer && HandLayer != 0) return true;
            return c.GetComponentInParent<SurfaceHand>() != null;
        }

        public static bool IsPlayerCollider(Collider c) => c != null && s_playerColliders.Contains(c);
    }
}
