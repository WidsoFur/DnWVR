using System;
using FluidRenderingForGames;
using HarmonyLib;
using UnityEngine;

namespace DnWVR.VR
{
    // Contact detection shared by the paws and the sponge: what a probe overlaps, where it touches, and which of
    // those colliders belong to the dragon.
    public static partial class HandPatches
    {
        internal struct Contact
        {
            public Collider Collider;
            public Vector3 Point;
            public Vector3 Normal;
        }

        static readonly Collider[] s_overlap = new Collider[16];

        static bool Usable(Collider col, Transform ignoreRoot)
        {
            if (col == null) return false;
            if (ignoreRoot != null && col.transform.IsChildOf(ignoreRoot)) return false;
            if (col.attachedRigidbody != null && col.attachedRigidbody.GetComponent<PlayerController>() != null) return false;
            if (col.GetComponentInParent<PlayerController>() != null) return false;
            return !VRHands.IsHandCollider(col);
        }

        /// <summary>Find the surface the probe is touching; derive point/normal from a short cast along the motion.</summary>
        internal static bool FindContact(Vector3 pos, Vector3 velocity, float radius, Transform ignoreRoot, out Contact c)
        {
            c = default;
            int n = Physics.OverlapSphereNonAlloc(pos, radius, s_overlap, SurfaceMask, QueryTriggerInteraction.Ignore);
            Collider best = null;
            for (int i = 0; i < n; i++)
            {
                if (!Usable(s_overlap[i], ignoreRoot)) continue;
                best = s_overlap[i];
                break;
            }
            if (best == null) return false;
            c.Collider = best;
            RayPointNormal(pos, velocity, SurfaceMask, ref c);
            return true;
        }

        static void RayPointNormal(Vector3 pos, Vector3 motion, int mask, ref Contact c)
        {
            var dir = motion.sqrMagnitude > 1e-4f ? motion.normalized : Vector3.down;
            if (Physics.Raycast(pos - dir * 0.25f, dir, out var hit, 0.5f, mask, QueryTriggerInteraction.Ignore) && hit.collider == c.Collider)
            {
                c.Point = hit.point;
                c.Normal = hit.normal;
            }
            else
            {
                c.Point = pos;
                c.Normal = -dir;
            }
        }

        internal static void HitHitboxes(Vector3 pos, float radius, Collider[] buffer, FluidParticleSystemSettings fluid, HitboxTrigger.HitType type)
        {
            int n = Physics.OverlapSphereNonAlloc(pos, radius, buffer, HitboxMask);
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != null && buffer[i].TryGetComponent<HitboxTrigger>(out var hb))
                    hb.Hit(fluid, type);
            }
        }

        // ------------------------------------------------------------------------------------
        // What a bare paw may touch
        // ------------------------------------------------------------------------------------

        enum TouchKind { None, Surface, Game }

        static AccessTools.FieldRef<WalkNWashPenetratorPhysicsApprox, GameObject[]> s_penSegments;
        static int s_dragonFrame = -1;
        static bool s_hasDragon;
        static WalkNWashSceneState.DragonDescription s_dragon;
        static GameObject s_penDragon;
        static WalkNWashPenetratorPhysicsApprox s_pen;
        static float s_penLookup = -10f;
        static GameObject[] s_penCollidersOf;
        static readonly System.Collections.Generic.HashSet<Collider> s_penColliders = new System.Collections.Generic.HashSet<Collider>();
        static readonly Collider[] s_hitboxProbe = new Collider[16];
        static readonly Collider[] s_overlapFingers = new Collider[16];
        static SphereCollider s_probeSphere;
        static CapsuleCollider s_probeCapsule;
        static bool s_loggedPenetrationError;
        static bool s_loggedNoPenetration;

        // Game: the active dragon or a surface next to a hand hitbox (buttons, phone, water tap, bucket). Surface: anything
        // else, only with TouchWorldSurfaces. DecalableCollider does not identify the dragon: any decalable collider can carry it.
        static TouchKind Classify(Collider col, Vector3 palm, Vector3 fingers, ref int nearHitbox)
        {
            if (IsDragonCollider(col)) return TouchKind.Game;
            if (nearHitbox < 0) nearHitbox = NearHandHitbox(palm, fingers, 0.1f) ? 1 : 0;
            if (nearHitbox == 1) return TouchKind.Game;
            return TouchWorldSurfaces ? TouchKind.Surface : TouchKind.None;
        }

        /// <summary>The active dragon's skin, anything under it, or its penis.</summary>
        internal static bool IsDragonCollider(Collider col)
        {
            if (col == null) return false;
            RefreshDragon();
            if (!s_hasDragon) return false;
            if (col == s_dragon.collider) return true;
            if (s_dragon.gameObject != null && col.transform.IsChildOf(s_dragon.gameObject.transform)) return true;
            return IsPenetratorSegment(col);
        }

        /// <summary>One of the active dragon's penis colliders: the game's segments, or the mod's along a bone penis.</summary>
        internal static bool IsPenisCollider(Collider col)
        {
            if (col == null) return false;
            RefreshDragon();
            return s_hasDragon && (IsPenetratorSegment(col) || BonePenis.Owns(col));
        }

        static void RefreshDragon()
        {
            if (s_dragonFrame == Time.frameCount) return;
            s_dragonFrame = Time.frameCount;
            s_hasDragon = WalkNWashSceneState.TryGetActiveDragon(out s_dragon);
        }

        /// <summary>The active dragon's penis collider driver, or null.</summary>
        internal static WalkNWashPenetratorPhysicsApprox ActivePenis()
        {
            RefreshDragon();
            return s_hasDragon ? FindPenis() : null;
        }

        static WalkNWashPenetratorPhysicsApprox FindPenis()
        {
            float now = Time.unscaledTime;
            if (s_penDragon != s_dragon.gameObject || (s_pen == null && now - s_penLookup > 1f))
            {
                s_penDragon = s_dragon.gameObject;
                s_penLookup = now;
                // The boner is optional on a dragon.
                s_pen = s_dragon.boner != null ? s_dragon.boner.GetComponentInChildren<WalkNWashPenetratorPhysicsApprox>() : null;
                if (s_pen == null && s_dragon.gameObject != null) s_pen = s_dragon.gameObject.GetComponentInChildren<WalkNWashPenetratorPhysicsApprox>();
            }
            return s_pen;
        }

        // The penis colliders are instantiated without a parent, so the dragon hierarchy test misses them.
        static bool IsPenetratorSegment(Collider col)
        {
            if (s_penSegments == null) return false;
            var pen = FindPenis();
            if (pen == null) return false;
            var segments = s_penSegments(pen);
            if (segments == null) return false;
            // The game re-creates the segments (a new array) whenever the penis is re-enabled; their colliders never change.
            if (segments != s_penCollidersOf)
            {
                s_penCollidersOf = segments;
                s_penColliders.Clear();
                foreach (var seg in segments)
                    if (seg != null)
                        foreach (var c in seg.GetComponentsInChildren<Collider>(true)) s_penColliders.Add(c);
            }
            return s_penColliders.Contains(col);
        }

        // Only hitboxes a hand operates count, not any layer-6 volume: the boner trigger alone is a 0.4 m sphere that
        // would make the floor around the penis touchable.
        static bool NearHandHitbox(Vector3 palm, Vector3 fingers, float radius)
        {
            int n = Physics.OverlapCapsuleNonAlloc(palm, fingers, radius, s_hitboxProbe, HitboxMask);
            for (int i = 0; i < n; i++)
            {
                var col = s_hitboxProbe[i];
                if (col != null && (col.TryGetComponent<HitboxPlapInteractable>(out _) || col.TryGetComponent<BucketSpongeHitbox>(out _))) return true;
            }
            return false;
        }

        static HitboxPlapInteractable FindPressHitbox(Vector3 pos, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(pos, radius, s_hitboxProbe, HitboxMask);
            for (int i = 0; i < n; i++)
            {
                if (s_hitboxProbe[i] != null && s_hitboxProbe[i].TryGetComponent<HitboxPlapInteractable>(out var hb)) return hb;
            }
            return null;
        }

        // Probe sphere at a (a == b) or capsule from a to b. The cached probe colliders stay disabled: ComputePenetration
        // takes their pose from the arguments.
        static bool Penetration(Collider col, Vector3 a, Vector3 b, float radius, out Vector3 normal, out float depth)
        {
            normal = Vector3.zero;
            depth = 0f;
            try
            {
                if (s_probeSphere == null || s_probeCapsule == null)
                {
                    var go = s_probeSphere != null ? s_probeSphere.gameObject : new GameObject("DnWVR_TouchProbe");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    if (s_probeSphere == null) { s_probeSphere = go.AddComponent<SphereCollider>(); s_probeSphere.enabled = false; }
                    if (s_probeCapsule == null) { s_probeCapsule = go.AddComponent<CapsuleCollider>(); s_probeCapsule.direction = 1; s_probeCapsule.enabled = false; }
                }
                var t = col.transform;
                var axis = b - a;
                float len = axis.magnitude;
                if (len < 1e-4f)
                {
                    s_probeSphere.radius = radius;
                    return Physics.ComputePenetration(s_probeSphere, a, Quaternion.identity, col, t.position, t.rotation, out normal, out depth) && depth > 0f;
                }
                s_probeCapsule.radius = radius;
                s_probeCapsule.height = len + 2f * radius;
                var rot = Quaternion.FromToRotation(Vector3.up, axis / len);
                return Physics.ComputePenetration(s_probeCapsule, (a + b) * 0.5f, rot, col, t.position, t.rotation, out normal, out depth) && depth > 0f;
            }
            catch (Exception e)
            {
                if (!s_loggedPenetrationError) { s_loggedPenetrationError = true; s_log?.Warning("[Touch] ComputePenetration failed; using the ray fallback: " + e.Message); }
                return false;
            }
        }

        // Fills s_overlap with the colliders in the palm sphere or finger capsule, each once (palm first); returns the count.
        static int OverlapPaw(Vector3 palm, Vector3 fingers, float palmRadius, float fingerRadius, int mask)
        {
            int n = Physics.OverlapSphereNonAlloc(palm, palmRadius, s_overlap, mask, QueryTriggerInteraction.Ignore);
            int m = Physics.OverlapCapsuleNonAlloc(palm, fingers, fingerRadius, s_overlapFingers, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < m && n < s_overlap.Length; i++)
                if (Array.IndexOf(s_overlap, s_overlapFingers[i], 0, n) < 0) s_overlap[n++] = s_overlapFingers[i];
            return n;
        }

        // Uses the deeper of the palm sphere and finger capsule. Penetration can point out through the far side once the
        // probe's centre line is under a mesh surface, so the face a ray from outside meets decides point and normal.
        static bool PawContact(Collider col, Vector3 palm, Vector3 fingers, float palmRadius, float fingerRadius, ref Contact c)
        {
            bool palmHit = Penetration(col, palm, palm, palmRadius, out var n, out float depth);
            bool viaFingers = false;
            if (Penetration(col, palm, fingers, fingerRadius, out var fn, out float fd) && (!palmHit || fd > depth))
            {
                n = fn; depth = fd; viaFingers = true;
            }
            if (!palmHit && !viaFingers) return false;
            float r = viaFingers ? fingerRadius : palmRadius;
            var axis = viaFingers ? DeeperEnd(palm, fingers, n) : palm;
            c.Normal = n;
            c.Point = axis - n * (r - depth);
            if (SurfaceAlong(col, axis, n, out var face))
            {
                if (viaFingers && Vector3.Dot(face.normal, n) < 0f)
                {
                    // That is the far side's normal: the other end of the capsule may be the one that touches.
                    var end = DeeperEnd(palm, fingers, face.normal);
                    if (end != axis && SurfaceAlong(col, end, face.normal, out var face2)) face = face2;
                }
                c.Point = face.point;
                c.Normal = face.normal;
            }
            return true;
        }

        static Vector3 DeeperEnd(Vector3 a, Vector3 b, Vector3 normal) => Vector3.Dot(a, normal) <= Vector3.Dot(b, normal) ? a : b;

        // The face of col nearest to p on the line through p along n. Casts both ways because mesh queries only meet front
        // faces, so the result holds whichever way n points.
        static bool SurfaceAlong(Collider col, Vector3 p, Vector3 n, out RaycastHit hit)
        {
            const float reach = 0.5f;
            bool down = col.Raycast(new Ray(p + n * reach, -n), out var hd, reach + 0.1f);
            bool up = col.Raycast(new Ray(p - n * reach, n), out var hu, reach + 0.1f);
            if (down && up) hit = (hd.point - p).sqrMagnitude <= (hu.point - p).sqrMagnitude ? hd : hu;
            else hit = down ? hd : hu;
            return down || up;
        }

        const float HoldReach = 0.5f;

        // A touching paw the overlap misses may be pushed under the surface (no physical hands, tunnelling, a snap). The touch
        // holds if a ray in along the last normal meets the last collider, unless the paw passed through a thin part of it.
        static bool HoldUnderSurface(TouchState st, Vector3 palm, Vector3 fingers, out Contact c)
        {
            c = default;
            var col = st.LastCollider;
            if (col == null || !col.enabled || !col.gameObject.activeInHierarchy || st.LastNormal.sqrMagnitude < 0.5f) return false;
            if (!UnderFace(col, palm, st.LastNormal, out var face) && !UnderFace(col, fingers, st.LastNormal, out face)) return false;
            c.Collider = col;
            c.Point = face.point;
            c.Normal = face.normal;
            return true;
        }

        static bool UnderFace(Collider col, Vector3 p, Vector3 n, out RaycastHit face)
        {
            if (!col.Raycast(new Ray(p + n * HoldReach, -n), out face, HoldReach)) return false;
            float depth = HoldReach - face.distance;
            return depth < 0.01f || !col.Raycast(new Ray(p, n), out _, depth - 0.005f);
        }
    }
}
