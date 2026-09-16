using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// Where a hand is drawn: at its controller, but never inside solid geometry. A controller inside a wall, a prop or the
    /// dragon leaves the hand on that surface however deep it goes, riding along with it, and the deeper it is, the flatter
    /// a paw lies. Kinematic (no rigidbody or colliders of its own) and solved after every camera write, so it never lags.
    /// </summary>
    public class SurfaceHand : MonoBehaviour
    {
        public Transform Target;
        public Transform Visual { get; private set; }

        /// <summary>Depth (m) of the controller behind a surface at which a paw lies fully flat on it.</summary>
        public static float AlignDepth = 0.03f;
        /// <summary>Rate at which a paw turns onto a surface and back (the game's hand eases at 10/s).</summary>
        public static float AlignRate = 10f;
        /// <summary>Gap kept between the palm sphere and a surface, along the surface normal (m).</summary>
        public static float Skin = 0.01f;
        /// <summary>Time constant (s) with which a hand on the moving dragon follows its surface; 0 = every step at once.</summary>
        public static float SoftTime = 0.04f;
        /// <summary>Deepest (m) an eased palm may stay behind where the surface pushed it.</summary>
        const float SoftDepth = 0.01f;
        /// <summary>Gap (m) between the palm sphere and the penis, small enough for touch detection.</summary>
        const float PenisSkin = 0.004f;
        /// <summary>Fastest (degrees/s) a hand resting on the penis moves around it to the controller's side.</summary>
        const float PenisTurnRate = 540f;

        const int SolidMask = 1 | (1 << WalkNWashPhysics.WorldLayer);
        static readonly RaycastHit[] s_hits = new RaycastHit[16];
        static readonly Collider[] s_overlaps = new Collider[16];
        static SphereCollider s_probe;
        static int s_syncFrame = -1;

        Vector3 _palm;              // where the palm centre was drawn last (outside geometry)
        bool _hasPalm;
        Collider _surface;          // what the palm rests on, and where the palm is in its space
        Vector3 _palmOnSurface;
        float _align;               // 0 = the controller's rotation, 1 = palm flat on the surface
        Vector3 _alignNormal = Vector3.up;
        int _frame = -1;
        Vector3 _correction;        // drawn palm minus the controller's palm, eased on the dragon
        float _softUntil = -1f;
        bool _onPenis;              // resting on the penis tube, on the side _penisSide (radial, carried along _penisAxis)
        bool _hasPenisSide;
        Vector3 _penisSide, _penisAxis;

        /// <summary>The controller is behind a surface and the hand is held on it.</summary>
        public bool InContact { get; private set; }

        public static SurfaceHand Create(string name, Transform target, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            var h = go.AddComponent<SurfaceHand>();
            h.Target = target;
            var visual = new GameObject(name + "_Visual");
            visual.layer = layer;
            visual.transform.SetParent(go.transform, false);
            h.Visual = visual.transform;
            go.SetActive(false);
            return h;
        }

        public void SetActive(bool on)
        {
            if (gameObject.activeSelf == on) return;
            if (on) ResetSolve();
            gameObject.SetActive(on);
        }

        /// <summary>Places the hand afresh on the next solve (activation, scene load, snap turn, recenter, cut).</summary>
        public void ResetSolve()
        {
            _hasPalm = false;
            _surface = null;
            _align = 0f;
            _correction = Vector3.zero;
            _softUntil = -1f;
            _onPenis = false;
            _hasPenisSide = false;
            InContact = false;
            if (Target != null) transform.SetPositionAndRotation(Target.position, Target.rotation);
        }

        /// <summary>
        /// Places the hand for this frame. palmLocal: the palm centre in hand space; pawLocalRotation: the drawn paw's
        /// rotation in hand space, to lay its palm onto a surface (null for a hand holding a tool: position only).
        /// </summary>
        public void Solve(Vector3 palmLocal, Quaternion? pawLocalRotation, float radius)
        {
            if (Target == null || !gameObject.activeInHierarchy) return;
            // Once per frame: the late latch solves again with a fresher controller pose but must not ease twice.
            bool newFrame = _frame != Time.frameCount;
            _frame = Time.frameCount;
            float dt = newFrame ? Time.unscaledDeltaTime : 0f;
            // The dragon's skin collider moves and re-bakes in LateUpdate: sync so the queries see where it is now.
            if (s_syncFrame != Time.frameCount)
            {
                s_syncFrame = Time.frameCount;
                Physics.SyncTransforms();
            }

            var targetRot = Target.rotation;
            var targetPalm = Target.position + targetRot * palmLocal;
            Vector3 start;
            if (!_hasPalm) start = HeadPosition(targetPalm);
            else if (_surface != null && _surface.enabled && _surface.gameObject.activeInHierarchy)
            {
                // Position and rotation only: the penis segments change their scale every frame.
                var st = _surface.transform;
                start = st.position + st.rotation * _palmOnSurface;
            }
            else start = _palm;
            bool contact = Resolve(start, targetPalm, radius, out var palm, out var normal, out var surface);
            // The penis is left out of the physics queries: its tube is solved on its own.
            bool onPenis = SolveOnPenis(ref palm, radius, newFrame, dt, out var penisNormal);
            if (onPenis)
            {
                contact = true;
                normal = penisNormal;
                surface = null;
            }

            float depth = contact ? Vector3.Dot(targetPalm - palm, -normal) : 0f;
            InContact = contact && depth > 0.002f;
            palm = Ease(palm, targetPalm, contact, onPenis || (contact && HandPatches.IsDragonCollider(surface)), normal, newFrame, dt);

            var hostRot = targetRot;
            if (pawLocalRotation.HasValue)
            {
                float want = InContact ? Mathf.Clamp01(depth / AlignDepth) : 0f;
                if (InContact) _alignNormal = _align < 0.01f ? normal : Vector3.Slerp(_alignNormal, normal, 1f - Mathf.Exp(-2f * AlignRate * dt));
                _align = Mathf.Lerp(_align, want, 1f - Mathf.Exp(-AlignRate * dt));
                if (_align > 1e-3f)
                {
                    var pawLocal = pawLocalRotation.Value;
                    // Palm (+Z) into the surface, fingers (+Y) where the controller points them, laid along the surface.
                    var fingers = Vector3.ProjectOnPlane(targetRot * pawLocal * Vector3.up, _alignNormal);
                    if (fingers.sqrMagnitude < 1e-6f) fingers = Vector3.ProjectOnPlane(targetRot * Vector3.forward, _alignNormal);
                    if (fingers.sqrMagnitude > 1e-6f)
                    {
                        var flat = Quaternion.LookRotation(-_alignNormal, fingers) * Quaternion.Inverse(pawLocal);
                        hostRot = Quaternion.Slerp(targetRot, flat, _align);
                    }
                }
            }
            else _align = 0f;

            // The palm centre stays where it was resolved, whichever way the hand turns.
            transform.SetPositionAndRotation(palm - hostRot * palmLocal, hostRot);
            _palm = palm;
            _hasPalm = true;
            _surface = InContact ? surface : null;
            if (_surface != null) _palmOnSurface = Quaternion.Inverse(_surface.transform.rotation) * (palm - _surface.transform.position);
        }

        // A fresh solve sweeps from the head, which is outside geometry (from the controller itself without a camera).
        static Vector3 HeadPosition(Vector3 fallback)
        {
            var cam = VRRig.Camera != null ? VRRig.Camera : Camera.main;
            return cam != null ? cam.transform.position : fallback;
        }

        // The dragon's re-baked skin and its penis move under a resting hand: on them (soft) the correction (drawn palm minus
        // the controller's) eases instead of copying each step, but never leaves the palm more than SoftDepth inside.
        Vector3 Ease(Vector3 palm, Vector3 targetPalm, bool contact, bool soft, Vector3 normal, bool newFrame, float dt)
        {
            var correction = palm - targetPalm;
            if (soft && SoftTime > 0f) _softUntil = Time.unscaledTime + 0.1f;
            if (SoftTime <= 0f || !_hasPalm || Time.unscaledTime >= _softUntil)
            {
                _correction = correction;
                return palm;
            }
            if (newFrame) _correction = Vector3.Lerp(_correction, correction, 1f - Mathf.Exp(-dt / SoftTime));
            var eased = targetPalm + _correction;
            if (contact)
            {
                float behind = Vector3.Dot(palm - eased, normal);
                if (behind > SoftDepth) eased += normal * (behind - SoftDepth);
            }
            return eased;
        }

        // The palm rests on the penis tube on the side it touched and slides freely along the shaft. A controller deep
        // inside the thin shaft, or past its axis, keeps that side instead of flipping around it, and moving to the
        // controller's side goes around the surface at a limited rate rather than jumping through.
        bool SolveOnPenis(ref Vector3 palm, float radius, bool newFrame, float dt, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!VRHands.RestOnPenis || !PenisTube.Closest(palm, out var center, out float tubeRadius, out var axis))
            {
                _onPenis = false;
                _hasPenisSide = false;
                return false;
            }
            float clearance = tubeRadius + radius + PenisSkin;
            var offset = palm - center;
            float distance = offset.magnitude;
            // The remembered side bends with the shaft.
            var side = _hasPenisSide ? (Quaternion.FromToRotation(_penisAxis, axis) * _penisSide).normalized : Vector3.zero;
            var raw = distance > 1e-5f ? offset / distance : (_hasPenisSide ? side : Vector3.up);
            _penisAxis = axis;
            if (!_onPenis)
            {
                if (distance >= clearance)
                {
                    _penisSide = raw;
                    _hasPenisSide = true;
                    return false;
                }
                _onPenis = true;
                if (!_hasPenisSide) side = raw;
            }
            // Near the surface the controller's direction is reliable; deep inside, or past the axis, the side is kept.
            float trust = Vector3.Dot(raw, side) < 0f ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f * clearance, 0.9f * clearance, distance));
            var want = Vector3.Slerp(side, raw, trust);
            var dir = (newFrame ? Vector3.RotateTowards(side, want, PenisTurnRate * Mathf.Deg2Rad * dt, 0f) : side).normalized;
            if (distance >= clearance && Vector3.Dot(raw, dir) > 0.7f)
            {
                // Clear of the shaft on the side the hand rests on: let go.
                _onPenis = false;
                _penisSide = raw;
                _hasPenisSide = true;
                return false;
            }
            _penisSide = dir;
            _hasPenisSide = true;
            palm = center + dir * clearance;
            normal = dir;
            return true;
        }

        // A non-convex mesh (the dragon's skin) cannot push out a sphere whose centre is behind it, so the palm must never
        // get there: push out before every sweep and never sweep through a surface already touched.
        static bool Resolve(Vector3 from, Vector3 to, float radius, out Vector3 end, out Vector3 normal, out Collider surface)
        {
            surface = null;
            normal = Vector3.up;
            bool contact = Depenetrate(ref from, radius, ref normal, ref surface);
            if (Sweep(from, to, radius, out end, ref normal, ref surface)) contact = true;
            if (Depenetrate(ref end, radius, ref normal, ref surface)) contact = true;
            return contact;
        }

        static bool Sweep(Vector3 from, Vector3 to, float radius, out Vector3 end, ref Vector3 normal, ref Collider surface)
        {
            end = from;
            bool any = false;
            var remaining = to - from;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                float distance = remaining.magnitude;
                if (distance < 1e-5f) break;
                var dir = remaining / distance;
                if (!NearestHit(end, radius, dir, distance + Skin, out var hit, out bool startsInside))
                {
                    end += remaining;
                    break;
                }
                any = true;
                surface = hit.collider;
                if (startsInside)
                {
                    // Already overlapping it: never travel through it. Push out, then slide the rest of the way along it.
                    if (!Depenetrate(ref end, radius, ref normal, ref surface)) normal = -dir;
                    remaining = Vector3.ProjectOnPlane(remaining, normal);
                    continue;
                }
                // Stop at the contact, then a skin's width off the surface along its normal (backing off along a glancing
                // sweep's own direction would leave far less, and the moving skin would close it within a frame).
                float travel = Mathf.Min(hit.distance, distance);
                end += dir * travel + hit.normal * Skin;
                normal = hit.normal;
                remaining = Vector3.ProjectOnPlane(dir * (distance - travel), hit.normal);
            }
            return any;
        }

        // The nearest usable hit of a sphere sweep; a collider the sphere already overlaps at the start wins.
        static bool NearestHit(Vector3 origin, float radius, Vector3 dir, float distance, out RaycastHit best, out bool startsInside)
        {
            best = default;
            startsInside = false;
            bool found = false;
            int n = Physics.SphereCastNonAlloc(origin, radius, dir, s_hits, distance, SolidMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = s_hits[i];
                if (h.collider == null || !Usable(h.collider)) continue;
                // Unity reports a collider the sweep starts inside with distance 0 and no point.
                if (h.distance <= 0f && h.point == Vector3.zero)
                {
                    if (!startsInside) { best = h; startsInside = true; found = true; }
                    continue;
                }
                if (!startsInside && (!found || h.distance < best.distance)) { best = h; found = true; }
            }
            return found;
        }

        static bool Depenetrate(ref Vector3 position, float radius, ref Vector3 normal, ref Collider surface)
        {
            bool any = false;
            var probe = Probe(radius);
            if (probe == null) return false;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                bool moved = false;
                int n = Physics.OverlapSphereNonAlloc(position, radius, s_overlaps, SolidMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = s_overlaps[i];
                    if (!Usable(c)) continue;
                    var t = c.transform;
                    if (!Physics.ComputePenetration(probe, position, Quaternion.identity, c, t.position, t.rotation, out var dir, out float depth) || depth <= 1e-4f) continue;
                    position += dir * (depth + Skin);
                    normal = dir;
                    surface = c;
                    any = moved = true;
                }
                if (!moved) break;
            }
            return any;
        }

        // The penis colliders are left out: SolveOnPenis handles its tube.
        static bool Usable(Collider c) => c != null && c.enabled && !VRHands.IsHandCollider(c) && !VRHands.IsPlayerCollider(c)
            && !HandPatches.IsPenisCollider(c);

        // The query shape for ComputePenetration: an enabled trigger on the hand layer, parked far below the world.
        static SphereCollider Probe(float radius)
        {
            if (s_probe == null)
            {
                var go = new GameObject("DnWVR_SurfaceProbe");
                DontDestroyOnLoad(go);
                go.layer = VRHands.HandLayer;
                go.transform.position = new Vector3(0f, -10000f, 0f);
                s_probe = go.AddComponent<SphereCollider>();
                s_probe.isTrigger = true;
            }
            if (s_probe.radius != radius) s_probe.radius = radius;
            return s_probe;
        }
    }
}
