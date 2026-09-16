using System;
using System.Collections.Generic;
using com.gatordragongames.washnwalk.tools;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace DnWVR.VR
{
    /// <summary>
    /// Gameplay item laser: each controller targets the interactable nearest its aim ray and grip on that controller
    /// uses it. Touch-operated items (phone, water tap) stay with the hands unless IncludeHandTargets is set.
    /// One grip is one Interact: VRInput claims the press so it never becomes Player/Plap, and HandPatches skips
    /// Interacter.OnAttackStarted; either alone lets a single grip pick a tool up and put it straight back.
    /// </summary>
    public class ItemLaser : MonoBehaviour
    {
        public static ItemLaser Instance { get; private set; }
        public static bool Enabled = true;
        public static bool IncludeHandTargets = false;
        public static float ConeDeg = 8f;
        public static float Reach = 3f;

        const float CaptureRadius = 0.10f;   // m; an item this close to the ray always counts, whatever the angle
        const float WidthCapture = 0.25f;    // m; at most this much of an item's own width is added to that
        const float StickyDeg = 3f;          // the current target wins ties against its neighbours by this much
        const float GrabCooldown = 0.25f;    // s before the same controller can grab again
        const float CoarseMargin = 2f;       // m beyond Reach that an item's transform may be before its bounds are looked at
        const float MaxSpotSize = 3f;        // m, diagonal: bounds larger than this are a parent's, so aim at the transform

        static AccessTools.FieldRef<List<Interactable>> s_interactables;
        static Func<Interactable, Tool, bool> s_canInteract;
        static AccessTools.FieldRef<Interacter, bool> s_interactEnabled;
        static MelonLogger.Instance s_log;
        static Interactable s_prompt;
        static readonly Dictionary<Component, Renderer[]> s_renderers = new Dictionary<Component, Renderer[]>();

        sealed class Hand
        {
            public bool Right;
            public XRNode Node;
            public Interactable Target;
            public float Acquired;
            public float Cooldown;
            public Vector3 EndLocal;   // dot position in the target's space, so it rides a moving dragon at render rate
            public LineRenderer Line;
            public Transform Dot;
            public bool Visible;
        }

        struct ToolState
        {
            public Tool Current, Empty;
            public bool Holding;
        }

        readonly Hand[] _hands = new Hand[2];
        Material _mat;
        Interacter _interacter;
        Camera _interacterCam;
        bool _allowed;

        /// <summary>The laser runs (pref on and set up); HandPatches then leaves selection and interaction to it.</summary>
        public static bool Running => Enabled && Instance != null;

        /// <summary>The target the game should prompt for: the one acquired last by either hand, or null.</summary>
        public static Interactable PromptTarget => s_prompt != null ? s_prompt : null;

        public static void Ensure(MelonLogger.Instance log)
        {
            s_log = log;
            if (Instance != null) return;
            try
            {
                s_interactables = AccessTools.StaticFieldRefAccess<List<Interactable>>(AccessTools.Field(typeof(Interactable), "_interactables"));
                s_canInteract = AccessTools.MethodDelegate<Func<Interactable, Tool, bool>>(AccessTools.Method(typeof(Interactable), "CanInteract"), null, true);
            }
            catch (Exception e)
            {
                log.Error("[ItemLaser] reflection failed (grip interacts through the game's cone instead): " + e);
                return;
            }
            try { s_interactEnabled = AccessTools.FieldRefAccess<Interacter, bool>("interactEnabled"); }
            catch (Exception e) { log.Warning("[ItemLaser] Interacter.interactEnabled not bound (the laser ignores the game's interaction switch): " + e.Message); }
            var go = new GameObject("DnWVR_ItemLaser");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ItemLaser>();
        }

        /// <summary>Forget both targets, hide both rays and drop any claimed grip (XR stop, scene load); Update selects again.</summary>
        public static void ResetState()
        {
            s_prompt = null;
            VRInput.TakePendingGrab(false);
            VRInput.TakePendingGrab(true);
            if (Instance == null) return;
            foreach (var h in Instance._hands)
            {
                if (h == null) continue;
                h.Target = null;
                SetVisible(h, false);
            }
        }

        /// <summary>
        /// Called by VRInput on a grip press edge, before the gamepad state is queued: true if this controller's laser takes
        /// the press. Uses the target from the last Update, which re-checks it before acting.
        /// </summary>
        internal static bool WantsGrip(bool right)
        {
            // Re-check the gates: a press made right after the game takes control, before an Update sees it, must reach
            // the game as Plap (e.g. to advance the first dialogue line).
            if (!Running || !Instance._allowed || !Instance.ComputeAllowed()) return false;
            var h = Instance._hands[right ? 1 : 0];
            return h != null && h.Target != null && Time.unscaledTime >= h.Cooldown;
        }

        void Awake()
        {
            _mat = new Material(Canvas.GetDefaultCanvasMaterial());
            _hands[0] = BuildHand("Left", XRNode.LeftHand, false);
            _hands[1] = BuildHand("Right", XRNode.RightHand, true);
            VRRig.AfterCameraWrite += OnCameraWritten;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            VRRig.AfterCameraWrite -= OnCameraWritten;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => s_renderers.Clear();

        Hand BuildHand(string name, XRNode node, bool right)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.material = _mat;
            line.positionCount = 2;
            line.startWidth = 0.003f;
            line.endWidth = 0.0015f;
            line.startColor = new Color(1f, 0.85f, 0.45f, 0.25f);
            line.endColor = new Color(1f, 0.85f, 0.45f, 0.9f);
            line.useWorldSpace = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;

            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.name = "Dot";
            dot.transform.SetParent(go.transform, false);
            dot.transform.localScale = Vector3.one * 0.02f;
            var mr = dot.GetComponent<MeshRenderer>();
            mr.material = _mat;
            mr.material.color = new Color(1f, 0.8f, 0.3f, 1f);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            dot.SetActive(false);

            return new Hand { Right = right, Node = node, Line = line, Dot = dot.transform };
        }

        // ---------------------------------------------------------------------------------------------------
        // Selection and grabs
        // ---------------------------------------------------------------------------------------------------

        void Update()
        {
            float now = Time.unscaledTime;
            bool allowed = ComputeAllowed();
            if (allowed != _allowed)
            {
                _allowed = allowed;
                LogI($"[ItemLaser] allowed -> {allowed}");
            }
            var tools = allowed ? ReadTools() : default;
            bool grabbed = false;

            foreach (var h in _hands)
            {
                // Always drain the press, so a grab claimed just before the laser switches off never fires later.
                bool grab = VRInput.TakePendingGrab(h.Right);
                if (!allowed || !VRLaser.TryGetAimRay(h.Right, out var ray))
                {
                    if (grab) LogI($"[ItemLaser] {Side(h)} grab dropped ({(allowed ? "no aim ray" : "laser not allowed")})");
                    SetTarget(h, null, 0f, now);
                    SetVisible(h, false);
                    continue;
                }
                if (grab)
                {
                    if (!grabbed && Usable(h.Target, h.Right, tools))
                    {
                        Grab(h, tools, now);
                        grabbed = true;
                        tools = ReadTools(); // the grab may have changed the held tool and which hand has it
                        continue;
                    }
                    LogI($"[ItemLaser] {Side(h)} grab dropped ({(grabbed ? "the other hand grabbed this frame" : "target no longer usable")})");
                }

                var next = Select(h, ray, tools, out var end, out float dist);
                SetTarget(h, next, dist, now);
                if (next != null) h.EndLocal = next.transform.InverseTransformPoint(end);
                else SetVisible(h, false);
            }

            Interactable prompt = null;
            float latest = float.MinValue;
            foreach (var h in _hands)
            {
                if (h.Target != null && h.Acquired > latest) { latest = h.Acquired; prompt = h.Target; }
            }
            s_prompt = prompt;
        }

        // Reads state only (no logging; the Interacter lookup is a cache), so WantsGrip can call it from the input update.
        bool ComputeAllowed()
        {
            if (!Enabled || !VRRig.Active || !VRHands.Attached || !ToolManager.IsValid()) return false;
            // The radial menu owns the controllers; cutscenes have no item interaction.
            if (VRInput.SuppressGameInput || CutsceneState.Active) return false;
            return GameGateOpen();
        }

        // Conditions the game can change at any moment. Its Player input is off in menus, dialogue and the intermission.
        bool GameGateOpen()
        {
            if (VRLaser.PointerActive || MenuHands.PokeActive) return false;
            var gsm = GameStateManager.Instance;
            if (gsm == null || gsm.IsPaused) return false;
            if (!VRInput.GamePlayerInputEnabled) return false;
            return InteracterEnabled();
        }

        bool InteracterEnabled()
        {
            var cam = Camera.main;
            if (cam == null) return false;
            if (cam != _interacterCam)
            {
                _interacterCam = cam;
                _interacter = cam.GetComponent<Interacter>();
            }
            if (_interacter == null || !_interacter.isActiveAndEnabled) return false;
            return s_interactEnabled == null || s_interactEnabled(_interacter);
        }

        static ToolState ReadTools()
        {
            var ts = new ToolState();
            try
            {
                ts.Current = ToolManager.GetCurrentTool();
                ts.Empty = ToolManager.GetEmptyTool();
            }
            catch { }
            ts.Holding = ts.Current != null && ts.Empty != null && ts.Current.GetName() != ts.Empty.GetName();
            return ts;
        }

        void Grab(Hand h, ToolState tools, float now)
        {
            var t = h.Target;
            string what = $"{t.GetType().Name} '{t.name}'";
            LogI($"[ItemLaser] {Side(h)} grab {what} with {(tools.Current != null ? tools.Current.GetName() : "null")}");
            foreach (var o in _hands)
            {
                if (!ReferenceEquals(o.Target, t)) continue;
                o.Target = null;
                SetVisible(o, false);
            }
            h.Cooldown = now + GrabCooldown;
            // A tool picked up here belongs to this controller, even if the other one gripped later in the same input update.
            VRHands.NotifyGrip(h.Right);
            try { t.Interact(tools.Current); }
            catch (Exception e) { s_log?.Warning($"[ItemLaser] Interact on {what} failed: {e.Message}"); }
            VRWidgets.Haptic(h.Node, 0.5f, 0.06f);
        }

        void SetTarget(Hand h, Interactable next, float dist, float now)
        {
            if (ReferenceEquals(next, h.Target)) return;
            bool had = h.Target != null;
            h.Target = next;
            if (next != null)
            {
                h.Acquired = now;
                VRWidgets.Haptic(h.Node, 0.15f, 0.02f);
                LogI($"[ItemLaser] {Side(h)} target -> {next.GetType().Name} '{next.name}' {dist:0.0} m");
            }
            else if (had)
            {
                LogI($"[ItemLaser] {Side(h)} target -> none");
            }
        }

        // Score: angle off the ray, less the angle the item's own size covers at that distance (AimPoint), less StickyDeg
        // for the current target; the lowest under ConeDeg wins. CanUse walks the station's children, so it only runs for
        // a would-be winner.
        Interactable Select(Hand h, Ray ray, ToolState tools, out Vector3 end, out float dist)
        {
            end = default;
            dist = 0f;
            var list = s_interactables();
            if (list == null) return null;
            Interactable best = null;
            float bestScore = ConeDeg;
            Vector3 bestAim = default;
            float coarse = (Reach + CoarseMargin) * (Reach + CoarseMargin);
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                if (!IncludeHandTargets && it.interactsWithHand) continue;
                if ((it.transform.position - ray.origin).sqrMagnitude > coarse) continue;
                var aim = AimPoint(ray, it, out float capture);
                var v = aim - ray.origin;
                float d = v.magnitude;
                if (d > Reach) continue;
                float score = Vector3.Angle(ray.direction, v) - Mathf.Atan2(capture, d) * Mathf.Rad2Deg - (ReferenceEquals(it, h.Target) ? StickyDeg : 0f);
                if (score >= bestScore || !CanUse(it, h.Right, tools)) continue;
                best = it;
                bestScore = score;
                bestAim = aim;
                dist = d;
            }
            if (best != null) end = EndPoint(ray, best, bestAim);
            return best;
        }

        // Re-checks a target chosen in an earlier frame right before use: flags, dragon state and cooldowns change.
        static bool Usable(Interactable it, bool right, ToolState tools)
        {
            if (it == null || !it.gameObject.activeInHierarchy) return false;
            if (!IncludeHandTargets && it.interactsWithHand) return false;
            return CanUse(it, right, tools);
        }

        // While the other controller holds the tool, this one only gets what the empty hand could use as well
        // (DragonTalk, window, jukebox; not a station or crate that needs the tool).
        static bool CanUse(Interactable it, bool right, ToolState tools)
        {
            if (!CanInteract(it, tools.Current)) return false;
            return !tools.Holding || right == VRHands.ToolHandIsRight || CanInteract(it, tools.Empty);
        }

        static bool CanInteract(Interactable it, Tool tool)
        {
            if (tool == null) return false;
            try { return s_canInteract(it, tool); }
            catch { return false; }
        }

        // Where the laser aims on an item: its middle, and anywhere up or down its height, so a crate, a ladder or a
        // bucket is pointed at as a whole instead of at the one spot it stands on.
        static Vector3 AimPoint(Ray ray, Interactable it, out float capture)
        {
            var spot = Spot(it);
            float half = spot.extents.y;
            var middle = spot.center;
            // A wide item is met anywhere across it too, up to WidthCapture; a thin one stays as exact as before.
            capture = CaptureRadius + Mathf.Min(WidthCapture, Mathf.Min(spot.extents.x, spot.extents.z));
            if (half < 0.01f) return middle;
            // Closest point between the ray and the upright line through the tool; a ray along it keeps the middle.
            float upDotRay = ray.direction.y;
            float denominator = 1f - upDotRay * upDotRay;
            if (denominator < 1e-4f) return middle;
            var toMiddle = middle - ray.origin;
            float along = (Vector3.Dot(toMiddle, ray.direction) * upDotRay - toMiddle.y) / denominator;
            return middle + Vector3.up * Mathf.Clamp(along, -half, half);
        }

        // What the item takes up: a stand offers the whole of the tool that belongs there, at whatever size and whether
        // the tool is standing on it now or in your hands - the mount frame is 2.2 m across, and having to pick out its
        // foot is what made it awkward to take. Anything else is measured too, but bounds larger than MaxSpotSize belong
        // to a parent (the dragon behind a talk point), not to the item, and those keep aiming at the transform.
        static Bounds Spot(Interactable it)
        {
            if (it is InteractableToolEquip station)
            {
                var placed = station.GetPlacedModel();
                if (placed != null && MeshBounds(placed, out var tool)) return tool;
            }
            if (MeshBounds(it, out var own) && own.size.magnitude <= MaxSpotSize) return own;
            return new Bounds(it.transform.position, Vector3.zero);
        }

        // Drawn bounds of a model that may be switched off: a disabled renderer reports none, so its mesh is placed by
        // hand. Meshes that follow bones (the ladder) give their rest pose, which is where the model stands anyway.
        static bool MeshBounds(Component root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            if (!s_renderers.TryGetValue(root, out var renderers))
            {
                renderers = root.GetComponentsInChildren<Renderer>(true);
                s_renderers[root] = renderers;
            }
            foreach (var r in renderers)
            {
                if (r == null) continue;
                Bounds b;
                if (r is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) continue;
                    b = Placed(skinned.rootBone != null ? skinned.rootBone : r.transform, skinned.localBounds);
                }
                else if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    b = Placed(r.transform, filter.sharedMesh.bounds);
                else continue;
                if (any) bounds.Encapsulate(b);
                else { bounds = b; any = true; }
            }
            return any;
        }

        // A local mesh box in world space (Transform.TransformBounds, which Unity does not expose).
        static Bounds Placed(Transform t, Bounds local)
        {
            var x = t.TransformVector(local.extents.x, 0f, 0f);
            var y = t.TransformVector(0f, local.extents.y, 0f);
            var z = t.TransformVector(0f, 0f, local.extents.z);
            var extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                                      Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                                      Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(t.TransformPoint(local.center), extents * 2f);
        }

        // The dot: the ray's hit on the item, else a hit on a ray toward the aim point, else the aim point. A parent hit
        // near the aim point also counts (the dragon under DragonTalk). The hand layer and triggers (DragonTalk's talk
        // sphere, hitboxes) are skipped so the dot lands on solid geometry.
        static Vector3 EndPoint(Ray ray, Interactable it, Vector3 aim)
        {
            int mask = VRHands.HandLayer != 0 ? ~(1 << VRHands.HandLayer) : Physics.DefaultRaycastLayers;
            var t = it.transform;
            if (Physics.Raycast(ray, out var hit, Reach + 0.5f, mask, QueryTriggerInteraction.Ignore) && Related(hit, t, aim)) return hit.point;
            var v = aim - ray.origin;
            float d = v.magnitude;
            if (d > 1e-3f && Physics.Raycast(ray.origin, v / d, out hit, d + 0.05f, mask, QueryTriggerInteraction.Ignore) && Related(hit, t, aim)) return hit.point;
            return aim;
        }

        static bool Related(RaycastHit hit, Transform target, Vector3 aim)
        {
            var ht = hit.collider.transform;
            if (ht.IsChildOf(target)) return true;
            return target.IsChildOf(ht) && (hit.point - aim).sqrMagnitude < 0.5f * 0.5f;
        }

        // ---------------------------------------------------------------------------------------------------
        // Visuals
        // ---------------------------------------------------------------------------------------------------

        // Runs after every camera write (late latch included), so ray and dot follow the controller at render rate.
        void OnCameraWritten()
        {
            foreach (var h in _hands)
            {
                var target = h.Target;
                if (_allowed && target != null && VRLaser.TryGetAimRay(h.Right, out var ray))
                {
                    var end = target.transform.TransformPoint(h.EndLocal);
                    h.Line.SetPosition(0, ray.origin);
                    h.Line.SetPosition(1, end);
                    h.Dot.position = end;
                    h.Dot.localScale = Vector3.one * (0.012f + 0.006f * Vector3.Distance(ray.origin, end));
                    SetVisible(h, true);
                }
                else
                {
                    SetVisible(h, false);
                }
            }
        }

        static void SetVisible(Hand h, bool v)
        {
            if (h.Visible == v) return;
            h.Visible = v;
            h.Line.enabled = v;
            h.Dot.gameObject.SetActive(v);
        }

        static string Side(Hand h) => h.Right ? "R" : "L";

        static void LogI(string msg)
        {
            if (DnWVRMod.DebugInteractionLog) s_log?.Msg(msg);
        }
    }
}
