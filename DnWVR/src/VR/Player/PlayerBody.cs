using System;
using System.Collections.Generic;
using System.Reflection;
using DnWVR.XR;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// The player's body in VR. The game's floating capsule (MassSpringController, radius 0.5 m) stays where the stick
    /// leaves it, so walking around the room left the collider and the feet behind the head. Their offset follows the
    /// headset instead: the capsule's centre, the feet (Base) and the body's jiggle collider move under it, swept so they
    /// never enter geometry. The rigidbody itself is not moved, so locomotion, its interpolation and the camera are
    /// untouched. The capsule is also slimmer (with the game's top and bottom), the jiggle collider shrinks with it so a
    /// player standing close does not shove the dragon's jiggling parts, and pushes out of moving geometry are capped.
    /// Reaching into the dragon with the head never moves the body: the offset backs off before it touches the dragon or
    /// sinks into anything, and only the ground under the body can be climbed onto, dropped from or mounted.
    /// </summary>
    public static class PlayerBody
    {
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const float Skin = 0.01f;
        const float MinRadius = 0.05f;
        // The dragon breathes and moves: the capsule settles this far from it, and sweeps stop a skin further out.
        const float DragonGap = 0.03f;
        // Ground under the head this much higher or lower than under the body is not stood on.
        const float MaxStep = 0.25f;

        /// <summary>The capsule and the feet follow the headset around the room (off: they stay where the stick puts them).</summary>
        public static bool RoomScale = true;
        /// <summary>Capsule radius (m) in VR; 0 = the game's.</summary>
        public static float Radius = 0.2f;
        /// <summary>Fastest (m/s) the body is pushed out of geometry that moved into it; 0 = the game's.</summary>
        public static float PushSpeed = 2f;
        /// <summary>
        /// How high (m) above the feet a surface may be and still be stepped onto. The game's spring lifts the player
        /// onto any ground it can see under the body, which in VR (a slim capsule that fits right up against things)
        /// means walking up onto crates, the mount frame and the tool bench, or being launched off their edges. The
        /// step stool is climbed a step at a time (its first step is 0.47 m), so it stays reachable.
        /// </summary>
        public static float StepUp = 0.55f;
        /// <summary>How fast (m/s) the ground under the feet may rise, so a step lifts the spring instead of kicking it.</summary>
        public static float StepRise = 1.2f;
        /// <summary>
        /// Damping of the game's floating spring (0 = the game's own 0.5). It damps on how fast the ground under it
        /// moves, not on how fast the body does, so raising it amplifies every twitch of that measurement into a shove -
        /// which is why the bounce is taken out with <see cref="SpringLift"/> instead. Left here to experiment with.
        /// </summary>
        public static float SpringDamping;

        /// <summary>
        /// Fastest (m/s) the spring may lift the body in the moment after a fall. The spring turns the speed of a landing
        /// into a bounce, two or three times over, which is sickening in a headset; capped for that moment it takes the
        /// landing and holds. It is only capped then - stepping onto a stool is the same spring lifting the body, and that
        /// has to stay brisk. Jumping is not affected: the game marks the body airborne as a jump is applied.
        /// 0 = uncapped, as the game has it.
        /// </summary>
        public static float SpringLift = 0.5f;

        static MelonLogger.Instance s_log;
        static AccessTools.FieldRef<MassSpringController, float> s_bottomOffset, s_cylinderHeight, s_springLength;
        static AccessTools.FieldRef<MassSpringController, Transform> s_baseRef;
        static AccessTools.FieldRef<MassSpringController, LocomotionPID> s_springPid;
        static AccessTools.FieldRef<LocomotionPID, float> s_springDamping;

        static MassSpringController s_msc;
        static CapsuleCollider s_capsule;
        static Rigidbody s_body;
        static Transform s_root, s_base, s_jiggle;
        static Vector3 s_baseHome, s_jiggleHome, s_jiggleScale;
        static float s_gameRadius, s_gamePushSpeed, s_gameSpringDamping;
        static Vector3 s_offset;
        static int s_mask;
        static int s_frame = -1;
        static float s_groundY;
        static bool s_hasGround;
        static float s_ledgeUntil;
        static float s_landedUntil;
        static Transform s_standingOn;
        static readonly RaycastHit[] s_hits = new RaycastHit[16];
        static readonly RaycastHit[] s_groundHits = new RaycastHit[32];
        static readonly Collider[] s_overlaps = new Collider[32];
        static readonly List<Collider> s_bodyNear = new List<Collider>();

        /// <summary>Root-local horizontal offset of the capsule and the feet from the rigidbody.</summary>
        public static Vector3 Offset => s_offset;

        public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            s_log = log;
            s_bottomOffset = AccessTools.FieldRefAccess<MassSpringController, float>("defaultBottomOffset");
            s_cylinderHeight = AccessTools.FieldRefAccess<MassSpringController, float>("defaultCapsuleHeight");
            s_springLength = AccessTools.FieldRefAccess<MassSpringController, float>("defaultSpringLength");
            try
            {
                s_springPid = AccessTools.FieldRefAccess<MassSpringController, LocomotionPID>("floatingPID");
                s_springDamping = AccessTools.FieldRefAccess<LocomotionPID, float>("derivativeGain");
            }
            catch (Exception e)
            {
                s_springPid = null;
                log.Warning("[PlayerBody] the floating spring's damping is not reachable; landings keep the game's bounce: " + e.Message);
            }
            s_baseRef = AccessTools.FieldRefAccess<MassSpringController, Transform>("baseTransform");
            harmony.Patch(AccessTools.Method(typeof(MassSpringController), "FixedUpdate"),
                postfix: new HarmonyMethod(typeof(PlayerBody).GetMethod(nameof(FixedUpdate_Postfix), AnyStatic)));
            harmony.Patch(AccessTools.Method(typeof(LocomotionRaycastTools), nameof(LocomotionRaycastTools.IsGrounded)),
                prefix: new HarmonyMethod(typeof(PlayerBody).GetMethod(nameof(IsGrounded_Prefix), AnyStatic)));
            harmony.Patch(AccessTools.Method(typeof(LocomotionRaycastTools), nameof(LocomotionRaycastTools.Stuck)),
                prefix: new HarmonyMethod(typeof(PlayerBody).GetMethod(nameof(Stuck_Prefix), AnyStatic)));
            VRRig.AfterCameraWrite += Follow;
            log.Msg("[PlayerBody] installed");
        }

        /// <summary>Takes over the scene's player body (after XR starts and after each scene load); safe to call repeatedly.</summary>
        public static void Attach()
        {
            if (!XRBootstrap.IsRunning) return;
            var msc = UnityEngine.Object.FindFirstObjectByType<MassSpringController>();
            if (msc == null) return;
            if (msc != s_msc)
            {
                var capsule = msc.GetComponent<CapsuleCollider>();
                var body = msc.GetComponent<Rigidbody>();
                if (capsule == null || body == null) return;
                s_msc = msc;
                s_capsule = capsule;
                s_body = body;
                s_root = msc.transform;
                s_base = s_baseRef(msc);
                s_jiggle = s_root.Find("jiggle_phy_capsule");
                s_baseHome = s_base != null ? s_base.localPosition : Vector3.zero;
                s_jiggleHome = s_jiggle != null ? s_jiggle.localPosition : Vector3.zero;
                s_jiggleScale = s_jiggle != null ? s_jiggle.localScale : Vector3.one;
                s_gameRadius = capsule.radius;
                s_gamePushSpeed = body.maxDepenetrationVelocity;
                s_gameSpringDamping = Spring() != null ? s_springDamping(Spring()) : 0f;
                s_offset = Vector3.zero;
                s_hasGround = false;
                s_ledgeUntil = 0f;
                s_standingOn = null;
                s_mask = CollisionMask(msc.gameObject.layer);
                s_log?.Msg($"[PlayerBody] {s_root.name}: game radius {s_gameRadius:0.00} m, push speed {s_gamePushSpeed:0.#} m/s, spring damping {s_gameSpringDamping:0.##}, collides with 0x{s_mask:X}");
            }
            ApplySettings();
        }

        /// <summary>Applies radius, push speed and the room-scale switch (after a pref change).</summary>
        public static void ApplySettings()
        {
            if (s_msc == null) return;
            s_capsule.radius = Radius > 0f ? Mathf.Clamp(Radius, MinRadius, s_gameRadius) : s_gameRadius;
            // The jiggle collider reads its size from its transform's scale.
            if (s_jiggle != null) s_jiggle.localScale = s_jiggleScale * (s_capsule.radius / s_gameRadius);
            s_body.maxDepenetrationVelocity = PushSpeed > 0f ? PushSpeed : s_gamePushSpeed;
            var pid = Spring();
            if (pid != null) s_springDamping(pid) = SpringDamping > 0f ? SpringDamping : s_gameSpringDamping;
            FitCapsule();
            if (!RoomScale && s_offset != Vector3.zero) SetOffset(Sweep(s_offset, Vector3.zero));
        }

        /// <summary>Gives the body back to the game (XR stop): the rigidbody moves to where the capsule was, so nothing overlaps.</summary>
        public static void Detach()
        {
            if (s_msc != null)
            {
                if (s_offset != Vector3.zero)
                {
                    var world = s_root.TransformVector(s_offset);
                    SetOffset(Vector3.zero);
                    s_body.position += world;
                    s_root.position += world;
                }
                s_capsule.radius = s_gameRadius;
                if (s_jiggle != null) s_jiggle.localScale = s_jiggleScale;
                s_body.maxDepenetrationVelocity = s_gamePushSpeed;
                var pid = Spring();
                if (pid != null) s_springDamping(pid) = s_gameSpringDamping;
                FitCapsule();
            }
            Forget();
        }

        /// <summary>Forgets the body once its scene is gone (an additive load keeps it).</summary>
        public static void OnSceneChanged()
        {
            if (s_msc == null) Forget();
        }

        /// <summary>A teleport recentres the head on the spawn point, so the body goes back under the rigidbody.</summary>
        public static void OnTeleport()
        {
            if (s_msc != null) SetOffset(Vector3.zero);
        }

        static LocomotionPID Spring() => s_springPid != null && s_msc != null ? s_springPid(s_msc) : null;

        static void Forget()
        {
            s_msc = null;
            s_capsule = null;
            s_body = null;
            s_root = s_base = s_jiggle = null;
            s_offset = Vector3.zero;
            s_hasGround = false;
            s_ledgeUntil = 0f;
            s_standingOn = null;
        }

        // Once a frame after the camera write, in gameplay: move the capsule toward the headset.
        static void Follow()
        {
            if (s_msc == null || s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            if (!RoomScale || !VRRig.Gameplay || !VRRig.HmdTracked) return;
            var want = s_root.InverseTransformVector(VRRig.HeadOffsetWorld);
            want.y = 0f;
            if ((want - s_offset).sqrMagnitude < 1e-6f) return;
            SetOffset(Settle(Sweep(s_offset, want)));
        }

        static void SetOffset(Vector3 offset)
        {
            s_offset = new Vector3(offset.x, 0f, offset.z);
            var center = s_capsule.center;
            s_capsule.center = new Vector3(s_offset.x, center.y, s_offset.z);
            // The spring writes Base's height every physics step and keeps x and z.
            if (s_base != null) s_base.localPosition = new Vector3(s_baseHome.x + s_offset.x, s_base.localPosition.y, s_baseHome.z + s_offset.z);
            if (s_jiggle != null) s_jiggle.localPosition = new Vector3(s_jiggleHome.x + s_offset.x, s_jiggleHome.y, s_jiggleHome.z + s_offset.z);
        }

        // The game sizes the capsule from its radius with a fixed bottom offset, so a slimmer one would lower the top (the eyes
        // follow it) and raise the bottom: refit it to the game's own top and bottom at the current posture.
        static void FitCapsule()
        {
            float bottom = s_bottomOffset(s_msc) - s_gameRadius;
            float top = s_bottomOffset(s_msc) + s_gameRadius + s_cylinderHeight(s_msc) * s_msc.Posture;
            float height = Mathf.Max(top - bottom, 2f * s_capsule.radius);
            s_capsule.height = height;
            s_capsule.center = new Vector3(s_offset.x, bottom + height * 0.5f, s_offset.z);
        }

        // Moves the capsule from one root-local offset toward another: it stops a skin's width before anything solid (further
        // from the dragon) and slides along it; geometry it already overlaps only keeps it from going deeper. Floors and
        // ceilings never stop it (Settle keeps it out of them).
        static Vector3 Sweep(Vector3 from, Vector3 to)
        {
            float radius = s_capsule.radius;
            float half = Mathf.Max(0f, s_capsule.height * 0.5f - radius);
            var up = s_root.up;
            var start = s_root.TransformPoint(new Vector3(from.x, s_capsule.center.y, from.z));
            var bodyPos = s_root.position;
            var bodyRot = s_root.rotation;
            var moved = Vector3.zero;
            var remaining = Horizontal(s_root.TransformVector(to - from));
            for (int iteration = 0; iteration < 4; iteration++)
            {
                float distance = remaining.magnitude;
                if (distance < 1e-4f) break;
                var dir = remaining / distance;
                var c = start + moved;
                int n = Physics.CapsuleCastNonAlloc(c + up * half, c - up * half, radius, dir, s_hits, distance + DragonGap + Skin, s_mask, QueryTriggerInteraction.Ignore);
                float nearest = float.MaxValue;
                float nearestSkin = Skin;
                var nearestNormal = Vector3.zero;
                var inside = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    var hit = s_hits[i];
                    var col = hit.collider;
                    if (col == null || col.attachedRigidbody == s_body || VRHands.IsHandCollider(col)) continue;
                    // Unity reports a collider the sweep starts inside with distance 0 and no point.
                    if (hit.distance <= 0f && hit.point == Vector3.zero)
                    {
                        var t = col.transform;
                        if (Physics.ComputePenetration(s_capsule, bodyPos + moved, bodyRot, col, t.position, t.rotation, out var outDir, out float depth)
                            && depth > 1e-4f && Mathf.Abs(outDir.y) < 0.7f && Vector3.Dot(dir, outDir) < -1e-3f)
                            inside += outDir;
                        continue;
                    }
                    if (Mathf.Abs(hit.normal.y) >= 0.7f) continue;
                    float skin = HandPatches.IsDragonCollider(col) ? DragonGap + Skin : Skin;
                    if (hit.distance - skin < nearest - nearestSkin)
                    {
                        nearest = hit.distance;
                        nearestSkin = skin;
                        nearestNormal = hit.normal;
                    }
                }
                if (inside != Vector3.zero)
                {
                    var plane = Horizontal(inside);
                    if (plane.sqrMagnitude < 1e-8f) break;
                    remaining = Horizontal(Vector3.ProjectOnPlane(remaining, plane.normalized));
                    continue;
                }
                if (nearest == float.MaxValue)
                {
                    moved += remaining;
                    break;
                }
                float travel = Mathf.Clamp(nearest - nearestSkin, 0f, distance);
                moved += dir * travel;
                var wall = Horizontal(nearestNormal);
                if (wall.sqrMagnitude < 1e-8f) break;
                remaining = Horizontal(Vector3.ProjectOnPlane(dir * (distance - travel), wall.normalized));
            }
            var local = s_root.InverseTransformVector(moved);
            return new Vector3(from.x + local.x, 0f, from.z + local.z);
        }

        // Backs the capsule off toward the body until it keeps DragonGap from the dragon and sinks into nothing deeper than the
        // body's own capsule does, so the dragon breathing against a head that leans into it (or an overhang above it) never
        // pushes the body; the physics engine only resolves what the body itself runs into.
        static Vector3 Settle(Vector3 offset)
        {
            if (offset.sqrMagnitude < 1e-8f) return offset;
            s_bodyNear.Clear();
            int n = OverlapAt(Vector3.zero);
            for (int i = 0; i < n; i++)
                if (!Ignored(s_overlaps[i])) s_bodyNear.Add(s_overlaps[i]);
            if (Clear(offset)) return offset;
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 6; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Clear(offset * mid)) lo = mid;
                else hi = mid;
            }
            return offset * lo;
        }

        static bool Clear(Vector3 offset)
        {
            int n = OverlapAt(offset);
            for (int i = 0; i < n; i++)
            {
                var col = s_overlaps[i];
                if (Ignored(col)) continue;
                float depth = Depth(offset, col);
                // The static world stops the capsule only where it overlaps; the dragon already within its gap.
                if (depth <= 0f && !HandPatches.IsDragonCollider(col)) continue;
                // What the body's own capsule already touches may stay touched, just not deeper.
                if (s_bodyNear.Contains(col) && depth <= Depth(Vector3.zero, col) + 1e-3f) continue;
                return false;
            }
            return true;
        }

        // Colliders within DragonGap of the capsule at a root-local offset.
        static int OverlapAt(Vector3 offset)
        {
            float radius = s_capsule.radius;
            float half = Mathf.Max(0f, s_capsule.height * 0.5f - radius);
            var c = s_root.TransformPoint(new Vector3(offset.x, s_capsule.center.y, offset.z));
            var up = s_root.up;
            return Physics.OverlapCapsuleNonAlloc(c + up * half, c - up * half, radius + DragonGap, s_overlaps, s_mask, QueryTriggerInteraction.Ignore);
        }

        // How deep the capsule at a root-local offset sinks into a collider (its centre holds the current offset).
        static float Depth(Vector3 offset, Collider col)
        {
            var t = col.transform;
            var position = s_root.position + s_root.TransformVector(offset - s_offset);
            return Physics.ComputePenetration(s_capsule, position, s_root.rotation, col, t.position, t.rotation, out _, out float depth) ? depth : 0f;
        }

        static bool Ignored(Collider col) =>
            col == null || col.attachedRigidbody == s_body || col.transform.IsChildOf(s_root) || VRHands.IsHandCollider(col);

        static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static int CollisionMask(int layer)
        {
            int mask = 0;
            for (int i = 0; i < 32; i++)
                if (!Physics.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
            return mask;
        }

        static void FixedUpdate_Postfix(MassSpringController __instance)
        {
            if (__instance != s_msc) return;
            CapSpringLift();
            FitCapsule();
            // The dragon moves between frames: settle again before the physics step sees the capsule.
            if (s_offset != Vector3.zero) SetOffset(Settle(s_offset));
        }

        const float LandingSpeed = 2.5f;    // m/s downwards: below this a drop is a step down, not a fall to absorb
        const float LandingWindow = 0.6f;   // s the spring is held back after such a fall

        // The spring has just answered a landing, and its answer is what throws the player back up. Anything the body
        // gains upwards while it stands on the ground is the spring's doing (a jump takes the body off the ground in the
        // same breath it is applied), so holding that back for a moment after a real fall takes the bounce out. Only for
        // that moment: climbing onto a stool is the same spring lifting the same body, and it has to stay brisk.
        static void CapSpringLift()
        {
            if (SpringLift <= 0f || s_body == null || !s_msc.IsGrounded) return;
            var v = s_body.linearVelocity;
            if (v.y < -LandingSpeed) s_landedUntil = Time.fixedTime + LandingWindow;
            if (Time.fixedTime > s_landedUntil || v.y <= SpringLift) return;
            v.y = SpringLift;
            s_body.linearVelocity = v;
        }

        // The posture check runs on the game's capsule size; refit first so it tests the capsule that will be simulated.
        static void Stuck_Prefix(CapsuleCollider collider)
        {
            if (s_msc != null && collider == s_capsule) FitCapsule();
        }

        // The game's ground rays start under the rigidbody; with the capsule offset they start under the capsule. Leaning or
        // stepping in the room never climbs onto, drops from or mounts what is only under the head: past a step, or onto a
        // mount the body is not on, the ground under the body holds. Ground more than a step above the feet is not ground
        // at all (StepUp), and the ground that is rises no faster than StepRise.
        static bool IsGrounded_Prefix(ref bool __result, ref float groundDistance, ref Vector3 groundNormal, ref Transform groundTransform,
            CapsuleCollider collider, float maxDistance, int groundMask, float minSlopeRadians, int quality)
        {
            if (s_msc == null || collider == null || collider != s_capsule) return true;
            var t = collider.transform;
            var down = -t.up;
            float radius = collider.radius;
            // The spring holds the body its rest length above the ground, so that is where the feet are.
            float rest = s_springLength != null ? s_springLength(s_msc) : maxDistance;
            float tooHigh = Mathf.Max(0f, rest - StepUp);
            bool atHead = Probe(t.position + t.TransformVector(s_offset), down, radius, maxDistance, tooHigh, groundMask, minSlopeRadians, quality,
                out float headDistance, out var headNormal, out var headGround);
            bool atBody = atHead;
            float bodyDistance = headDistance;
            var bodyNormal = headNormal;
            var bodyGround = headGround;
            if (s_offset != Vector3.zero)
                atBody = Probe(t.position, down, radius, maxDistance, tooHigh, groundMask, minSlopeRadians, quality,
                    out bodyDistance, out bodyNormal, out bodyGround);
            bool useBody = atBody && (!atHead || Mathf.Abs(headDistance - bodyDistance) > MaxStep
                || (headGround != bodyGround && headGround != null && headGround.TryGetComponent<MountableObject>(out _)));
            if (useBody)
            {
                groundDistance = bodyDistance;
                groundNormal = bodyNormal;
                groundTransform = bodyGround;
                __result = true;
            }
            else
            {
                groundDistance = headDistance;
                groundNormal = headNormal;
                groundTransform = headGround;
                __result = atHead;
            }
            s_standingOn = __result ? groundTransform : null;
            if (__result) groundDistance = Settled(groundDistance, t.position.y);
            else { s_hasGround = false; s_ledgeUntil = 0f; }
            return false;
        }

        // What the spring is told is where the ground IS, not what the rays just measured. The spring's damping works on
        // how fast that number changes, so raw measurements would turn head sway over an uneven floor, or a ray catching
        // the lip of a kerb one frame and missing it the next, into a vertical jitter. Tracking the ground's height in
        // world space instead leaves the body's own falling as the only fast change in it, which is exactly what damping
        // is for. Small moves of that height are low-passed, a step up is eased in, a small drop (the missed lip) is held
        // and then eased out, and a real drop or a fall passes straight through.
        const float SmoothedStep = 0.05f;   // m; larger changes are a step, not the ground shifting under the feet
        const float LedgeDrop = 0.12f;      // m; a fall larger than this is real, not a ray that missed the lip
        const float LedgeHold = 0.25f;      // s the higher ground is kept before the player is let down

        static float Settled(float measured, float originY)
        {
            float dt = Time.fixedDeltaTime;
            float ground = originY - measured;
            // A body on its way down hears the ground where it is: easing it in would hide the landing from the spring
            // until too late, and the spring would answer with a kick.
            if (!s_hasGround || (s_body != null && s_body.linearVelocity.y < -0.5f))
            {
                s_hasGround = true;
                s_groundY = ground;
                s_ledgeUntil = 0f;
                return originY - s_groundY;
            }
            float step = ground - s_groundY;
            if (Mathf.Abs(step) < SmoothedStep)
            {
                // The ground itself shifting - a breathing dragon, a floor that is not quite flat under a swaying head.
                // Followed exactly: a filter here sits inside the spring's own loop and sets it hunting.
                s_ledgeUntil = 0f;
                s_groundY = ground;
            }
            else if (step > 0f)
            {
                s_ledgeUntil = 0f;
                s_groundY = Mathf.Min(ground, s_groundY + StepRise * dt);          // a step up, eased in
            }
            else if (-step > LedgeDrop)
            {
                s_groundY = ground;                                                // off the edge: down at once
                s_ledgeUntil = 0f;
            }
            else
            {
                if (s_ledgeUntil <= 0f) s_ledgeUntil = Time.fixedTime + LedgeHold;
                if (Time.fixedTime >= s_ledgeUntil)
                    s_groundY = Mathf.Max(ground, s_groundY - StepRise * dt);
            }
            return originY - s_groundY;
        }

        // LocomotionRaycastTools.IsGrounded from another origin, ignoring ground closer than tooHigh: a surface more than
        // a step above the feet - a crate lid, a bench top, the rim of the mount frame, or the belly of the dragon you are
        // leaning in to wash, which used to throw the player up onto it. A dragon is walked up where it meets the floor or
        // reached from the stool, and jumping still lands you on anything.
        static bool Probe(Vector3 origin, Vector3 down, float radius, float maxDistance, float tooHigh, int groundMask, float minSlopeRadians, int quality,
            out float groundDistance, out Vector3 groundNormal, out Transform groundTransform)
        {
            float minNormalY = Mathf.Cos(minSlopeRadians);
            int points = Mathf.Max(quality, 2);
            groundDistance = maxDistance + 1f;
            groundNormal = Vector3.up;
            groundTransform = null;
            bool found = false;
            for (int i = 0; i < points; i++)
            {
                // The game's sunflower pattern (LocomotionRaycastTools.GetUniformPointsInCircle).
                float r = Mathf.Sqrt(i / (points - 1f)) * radius;
                float angle = 10.166408f * i;
                var p = origin + Vector3.forward * (Mathf.Sin(angle) * r) + Vector3.right * (Mathf.Cos(angle) * r);
                int n = Physics.RaycastNonAlloc(p, down, s_groundHits, maxDistance + 0.1f, groundMask);
                for (int k = 0; k < n; k++)
                {
                    var hit = s_groundHits[k];
                    if (hit.normal.y > minNormalY && hit.distance <= maxDistance + 0.001f && hit.distance < groundDistance)
                    {
                        // What you already stand on stays your ground however hard you land on it - which is also
                        // what lets a dragon be walked over once you are up there, its whole skin being one collider.
                        if (hit.distance < tooHigh && hit.transform != s_standingOn) continue;
                        groundNormal = hit.normal;
                        groundDistance = hit.distance;
                        groundTransform = hit.transform;
                        found = true;
                    }
                }
            }
            return found;
        }
    }
}
