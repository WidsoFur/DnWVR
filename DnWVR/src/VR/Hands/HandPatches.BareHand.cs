using com.gatordragongames.washnwalk.tools;
using FluidRenderingForGames;
using HarmonyLib;
using UnityEngine;
using UnityEngine.VFX;

namespace DnWVR.VR
{
    // The bare paws: per-paw contact state and the replacement for PlapperHand's own eye-raycast update.
    public static partial class HandPatches
    {
        /// <summary>Contact state of one paw (index 0 = left controller, 1 = right), or of the sponge.</summary>
        sealed class TouchState
        {
            public bool InContact, Armed = true, HasLast, Stroking;
            public float NoContact, ContactStart, LastPlap = -10f, LastStroke = -10f, LastStrokeLog = -10f;
            public bool StrokeLogged, Held, OutwardLogged, ThroughLogged;
            public Vector3 LastProbe, LastFingers, LastHitPoint;
            // The surface of the latest contact frame, for a paw that ends up under it (HoldUnderSurface).
            public Vector3 LastNormal;
            public Collider LastCollider;
            public bool LastSurfaceOnly;
            // The contact of the latest slap or press (kept across Release, see ComesBackThrough).
            public Collider LastPlapCollider;
            public Vector3 LastPlapNormal, LastPlapPoint;
            public readonly TimedSamples Travel = new TimedSamples(64);

            public void RememberPlap(Contact c, float now)
            {
                LastPlap = now;
                LastPlapCollider = c.Collider;
                LastPlapNormal = c.Normal;
                LastPlapPoint = c.Point;
            }

            /// <summary>
            /// A contact on the far face of the part slapped or pressed within ComeBackTime: the same swing coming back through it.
            /// The face must lie behind the slapped one, so facing surfaces of one collider (inner thighs) still slap separately.
            /// </summary>
            public bool ComesBackThrough(Contact c, float now)
            {
                if (LastPlapCollider == null || c.Collider != LastPlapCollider || now - LastPlap >= ComeBackTime) return false;
                if (Vector3.Dot(c.Normal, LastPlapNormal) >= 0f) return false;
                var fromSlap = c.Point - LastPlapPoint;
                return Vector3.Dot(fromSlap, LastPlapNormal) < 0f && fromSlap.sqrMagnitude < 0.3f * 0.3f;
            }

            public void Release()
            {
                InContact = false;
                Armed = true;
                NoContact = 0f;
                Stroking = false;
                Held = false;
                LastCollider = null;
                Travel.Clear();
            }

            /// <summary>This frame's paw capsule, the start of next frame's sweep.</summary>
            public void Remember(Vector3 palm, Vector3 fingers)
            {
                LastProbe = palm;
                LastFingers = fingers;
                HasLast = true;
            }
        }

        static readonly TouchState[] s_bare = { new TouchState(), new TouchState() };
        static readonly TouchState s_sponge = new TouchState();

        /// <summary>
        /// Clears every paw's and the sponge's contact (XR stop, scene load) so a stale probe is not swept to the new hand
        /// position and counted as a touch.
        /// </summary>
        public static void ResetTouchState()
        {
            foreach (var st in s_bare)
            {
                st.Release();
                st.HasLast = false;
                st.LastPlapCollider = null;
            }
            s_sponge.Release();
            s_sponge.HasLast = false;
            s_sponge.LastPlapCollider = null;
        }

        static class PlapperVR
        {
            static AccessTools.FieldRef<PlapperHand, Transform> hand;
            static AccessTools.FieldRef<PlapperHand, FluidParticleSystemSettings> fluid;
            static AccessTools.FieldRef<PlapperHand, PhysicsMaterialExtension> wetMat, dryMat;
            static AccessTools.FieldRef<PlapperHand, VisualEffectAsset> vfx;
            static AccessTools.FieldRef<PlapperHand, Animator> animator;
            static AccessTools.FieldRef<PlapperHand, AudioSource> audio;
            static AccessTools.FieldRef<PlapperHand, float> fillAmount;
            static AccessTools.FieldRef<PlapperHand, Collider[]> hitColliders;
            static AccessTools.FieldRef<PlapperHand, Collider> hitCollider;

            public static void Bind()
            {
                hand = AccessTools.FieldRefAccess<PlapperHand, Transform>("hand");
                fluid = AccessTools.FieldRefAccess<PlapperHand, FluidParticleSystemSettings>("fluid");
                wetMat = AccessTools.FieldRefAccess<PlapperHand, PhysicsMaterialExtension>("wetSpongeMat");
                dryMat = AccessTools.FieldRefAccess<PlapperHand, PhysicsMaterialExtension>("drySpongeMat");
                vfx = AccessTools.FieldRefAccess<PlapperHand, VisualEffectAsset>("plapperVisualEffect");
                animator = AccessTools.FieldRefAccess<PlapperHand, Animator>("animator");
                audio = AccessTools.FieldRefAccess<PlapperHand, AudioSource>("spongeAudioSource");
                fillAmount = AccessTools.FieldRefAccess<PlapperHand, float>("fillAmount");
                hitColliders = AccessTools.FieldRefAccess<PlapperHand, Collider[]>("hitColliders");
                hitCollider = AccessTools.FieldRefAccess<PlapperHand, Collider>("hitCollider");
                try { s_penSegments = AccessTools.FieldRefAccess<WalkNWashPenetratorPhysicsApprox, GameObject[]>("colliderPrefabs"); }
                catch { s_penSegments = null; } // penis colliders then count only through TouchWorldSurfaces
            }

            /// <summary>
            /// One frame of both bare paws, in place of PlapperHand's own eye-raycast update: the real hand uses
            /// PlapperHand's animator and audio source, the twin its own copies, and game events always come from ph.
            /// </summary>
            public static void Tick(PlapperHand ph)
            {
                var h = hand(ph);
                if (h == null) return;
                // Rest pose: the mesh sits where the controller is; nothing reaches toward a raycast target.
                h.localPosition = Vector3.zero;
                h.localRotation = Quaternion.identity;
                // The animators have already run (ToolTest.LateUpdate): keep both paws' parked slap colliders behind the hand.
                VRHands.ParkSlapColliders();
                VRHands.PinTwinHand();

                var frame = new Frame(ph);
                Collider realContact = null;
                bool anyContact = false;
                for (int i = 0; i < 2; i++)
                {
                    bool right = i == 1;
                    // The tool controller carries the twin paw, a copy; the game's own PlapperHand is the other one.
                    bool real = right != VRHands.ToolHandIsRight;
                    var contact = TickPaw(ph, frame, right, real);
                    if (contact == null) continue;
                    anyContact = true;
                    if (real) realContact = contact;
                }

                hitCollider(ph) = realContact;
                if (anyContact) fillAmount(ph) = Mathf.MoveTowards(fillAmount(ph), 0f, Time.deltaTime * 0.1f);
            }

            /// <summary>What both paws share this frame: the clock, the touch gate and the surfaces they sound like.</summary>
            readonly struct Frame
            {
                public readonly float Dt;
                public readonly float Now;
                public readonly bool Allowed;
                public readonly FluidParticleSystemSettings Fluid;
                /// <summary>The paw's own surface, wet once it carries soap: half of every impact sound.</summary>
                public readonly PhysicsMaterialExtension Material;
                public readonly PhysicsMaterialExtensionDatabase Impacts;

                public Frame(PlapperHand ph)
                {
                    Dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
                    Now = Time.unscaledTime;
                    Allowed = TouchAllowed;
                    Fluid = fluid(ph);
                    Material = fillAmount(ph) > 0.1f ? wetMat(ph) : dryMat(ph);
                    Impacts = PhysicsMaterialExtensionDatabase.GetDatabase();
                }
            }

            /// <summary>One paw: what it touches this frame, and the slap, press, stroke and scrape that follow. Null = nothing.</summary>
            static Collider TickPaw(PlapperHand ph, in Frame frame, bool right, bool real)
            {
                var st = s_bare[right ? 1 : 0];
                var src = real ? audio(ph) : VRHands.TwinAudio;
                if (src != null) src.volume = Mathf.MoveTowards(src.volume, 0f, frame.Dt * 4f);

                // A controller holding a tool has no paw; the twin paw only counts while it is shown.
                if (!VRHands.IsBareHand(right) || (!real && !VRHands.TwinActive))
                {
                    st.Release();
                    st.HasLast = false;
                    return null;
                }
                // The palm centre is the probe for travel and sounds; the fingers widen what counts as touching.
                VRHands.ContactPaw(right, out var probe, out var fingers);
                if (!frame.Allowed)
                {
                    st.Release();
                    st.Remember(probe, fingers);
                    return null;
                }
                string side = right ? "R" : "L";

                bool touching = FindTouch(st, probe, fingers, ph.transform, out var c, out bool surfaceOnly);
                if (!touching && st.InContact && HoldUnderSurface(st, probe, fingers, out c))
                {
                    touching = true;
                    surfaceOnly = st.LastSurfaceOnly;
                    if (!st.Held) { st.Held = true; LogT($"[Touch] {side} under {c.Collider.name}: contact held"); }
                }
                else st.Held = false;

                if (!touching)
                {
                    if (st.InContact)
                    {
                        st.NoContact += frame.Dt;
                        if (st.NoContact > ReleaseTime) { st.Release(); LogStroke(st, right, null); }
                    }
                    st.Remember(probe, fingers);
                    return null;
                }

                if (!st.InContact)
                {
                    st.InContact = true;
                    st.ContactStart = frame.Now;
                    st.LastHitPoint = probe; // no scrape burst from a stale point
                    st.OutwardLogged = false;
                    st.ThroughLogged = false;
                }
                st.NoContact = 0f;

                // Slap on arrival: the window covers the probe lagging a frame behind the controller (ToolTest runs first).
                if (st.Armed && frame.Now - st.ContactStart <= PlapWindow && frame.Now - st.LastPlap > PlapCooldown)
                    SlapOrPress(ph, frame, st, right, real ? animator(ph) : VRHands.TwinAnimator, c, surfaceOnly);
                Stroke(ph, frame, st, right, probe, c, surfaceOnly);
                Scrape(frame, st, src, probe, c);

                st.LastHitPoint = probe;
                st.LastNormal = c.Normal;
                st.LastCollider = c.Collider;
                st.LastSurfaceOnly = surfaceOnly;
                st.Remember(probe, fingers);
                return c.Collider;
            }

            // A fast paw slaps the surface; a slower one pushing into a button, the phone or the tap presses it instead.
            static void SlapOrPress(PlapperHand ph, in Frame frame, TouchState st, bool right, Animator anim, Contact c, bool surfaceOnly)
            {
                string side = right ? "R" : "L";
                float peak = VRHands.PeakSpeed(right);
                var vel = VRHands.TrackVel(right);
                if (st.ComesBackThrough(c, frame.Now))
                {
                    // Disarmed for the whole contact (Release re-arms), so it cannot slap once ComeBackTime runs out partway through.
                    st.Armed = false;
                    if (!st.ThroughLogged) { st.ThroughLogged = true; LogT($"[Touch] {side} no slap: came back through {c.Collider.name}"); }
                    return;
                }
                if (peak > PlapSpeed)
                {
                    // A paw moving away from the surface (a follow-through coming back out, a brush lifting off) never slaps.
                    if (Vector3.Dot(vel, c.Normal) > 0f)
                    {
                        if (!st.OutwardLogged) { st.OutwardLogged = true; LogT($"[Touch] {side} no slap v={peak:0.00}: moving away from {c.Collider.name}"); }
                        return;
                    }
                    Slap(ph, c, anim, frame.Impacts, frame.Material, surfaceOnly);
                    st.Armed = false; st.RememberPlap(c, frame.Now);
                    LogT($"[Touch] {side} slap v={peak:0.00} on {c.Collider.name}{(surfaceOnly ? " (surface only)" : "")}");
                    return;
                }
                if (surfaceOnly) return;
                float approach = Vector3.Dot(vel, -c.Normal);
                // Hitboxes around the contact point, where the game's hand sits when it touches (fingertips too).
                var hb = approach > PressSpeed ? FindPressHitbox(c.Point, 0.1f) : null;
                if (hb == null) return;
                // A button/phone press: that hitbox only, no dragon PlapEvent.
                hb.Hit(frame.Fluid, HitboxTrigger.HitType.Plap);
                st.Armed = false; st.RememberPlap(c, frame.Now);
                LogT($"[Touch] {side} press v={approach:0.00} on {hb.name}");
            }

            // Stroking is distance travelled along the surface over a window, so controller jitter does not count as motion.
            static void Stroke(PlapperHand ph, in Frame frame, TouchState st, bool right, Vector3 probe, Contact c, bool surfaceOnly)
            {
                if (st.HasLast) st.Travel.Add(frame.Now, Vector3.ProjectOnPlane(probe - st.LastProbe, c.Normal).magnitude);
                if (st.Travel.Sum(frame.Now, StrokeWindow) > StrokeTravel) st.LastStroke = frame.Now;
                st.Stroking = !StrokeRequiresMotion || frame.Now - st.LastStroke < StrokeHold;
                if (st.Stroking && !surfaceOnly)
                {
                    PlapperHand.RubEvent?.Invoke(c.Collider, c.Point, c.Normal);
                    HitHitboxes(c.Point, 0.1f, hitColliders(ph), frame.Fluid, HitboxTrigger.HitType.HandRub);
                }
                LogStroke(st, right, c.Collider);
            }

            // The surface's scrape sound, as loud and as high as the paw is moving along it.
            static void Scrape(in Frame frame, TouchState st, AudioSource src, Vector3 probe, Contact c)
            {
                if (src == null) return;
                if (frame.Impacts != null)
                {
                    frame.Impacts.TryGetImpactInfo(c.Collider.sharedMaterial, frame.Material, PhysicsMaterialExtension.PhysicsResponseType.Scrape, out _, out var hasScrape, out _, out var scrape);
                    if (hasScrape)
                    {
                        src.resource = scrape.soundEffect;
                        if (!src.isPlaying) src.Play();
                    }
                }
                float moved = Vector3.Distance(probe, st.LastHitPoint);
                src.volume = Mathf.Clamp01(src.volume + moved * 8f);
                src.pitch = Mathf.Clamp(0.9f + moved * 2f, 0f, 1.5f);
            }

            static void Slap(PlapperHand ph, Contact c, Animator anim, PhysicsMaterialExtensionDatabase db, PhysicsMaterialExtension mat, bool surfaceOnly)
            {
                if (db != null)
                {
                    // Same as the game: A = outward normal (hand side), B = into the surface.
                    db.TryGetImpactInfo(c.Collider.sharedMaterial, mat, PhysicsMaterialExtension.PhysicsResponseType.Soft, out var hasA, out var hasB, out var a, out var b);
                    if (hasA) a.Apply(c.Point, c.Normal);
                    if (hasB) b.Apply(c.Point, -c.Normal);
                }
                if (vfx(ph) != null) VisualEffectHelper.SpawnVFX(vfx(ph), c.Point + c.Normal * 0.05f, c.Normal);
                if (anim != null) ph.StartCoroutine(PulseBool(anim, "Plap"));
                if (surfaceOnly) return;
                PlapperHand.PlapEvent?.Invoke(c.Collider, c.Point, c.Normal);
                HitHitboxes(c.Point, 0.1f, hitColliders(ph), fluid(ph), HitboxTrigger.HitType.Plap);
            }

            static void LogStroke(TouchState st, bool right, Collider col)
            {
                if (st.Stroking == st.StrokeLogged) return;
                float now = Time.unscaledTime;
                if (now - st.LastStrokeLog < 0.5f) return; // rate limit; the state is logged once it settles
                st.StrokeLogged = st.Stroking;
                st.LastStrokeLog = now;
                LogT($"[Touch] {(right ? "R" : "L")} stroke {(st.Stroking ? "on" : "off")}{(col != null ? " " + col.name : "")}");
            }

            // Sweeps the palm sphere and finger capsule from last frame's pose (a fast paw can cross a surface between frames),
            // else overlaps; Game kinds win over Surface, and a button or phone that is only a hitbox volume (layer 6) counts too.
            static bool FindTouch(TouchState st, Vector3 probe, Vector3 fingers, Transform ignoreRoot, out Contact c, out bool surfaceOnly)
            {
                c = default;
                surfaceOnly = false;
                // Starts within the paw's own radii plus EnterPad; once touching, holds out to ExitRadius around the palm and
                // the same extra margin around the fingers.
                float palmR = st.InContact ? ExitRadius : VRHands.PalmRadius + EnterPad;
                float fingerR = VRHands.FingerRadius + (st.InContact ? ExitRadius - VRHands.PalmRadius : EnterPad);
                int near = -1;
                Collider best = null;
                var bestKind = TouchKind.None;
                bool swept = false;
                RaycastHit sweptHit = default;
                int mask = SurfaceMask;
                var motion = st.HasLast ? probe - st.LastProbe : Vector3.zero;
                float dist = motion.magnitude;
                // The capsule is swept along its finger end's motion, which also covers a wrist flick that leaves the palm in place.
                var fingerMotion = st.HasLast ? fingers - st.LastFingers : Vector3.zero;
                float fingerDist = fingerMotion.magnitude;

                for (int s = 0; s < 2 && bestKind != TouchKind.Game; s++)
                {
                    RaycastHit hit = default;
                    bool cast = s == 0
                        ? dist > 1e-3f && Physics.SphereCast(st.LastProbe, VRHands.PalmRadius, motion / dist, out hit, dist, SurfaceMask, QueryTriggerInteraction.Ignore)
                        : fingerDist > 1e-3f && Physics.CapsuleCast(st.LastProbe, st.LastFingers, VRHands.FingerRadius, fingerMotion / fingerDist, out hit, fingerDist, SurfaceMask, QueryTriggerInteraction.Ignore);
                    if (!cast || !Usable(hit.collider, ignoreRoot)) continue;
                    var kind = Classify(hit.collider, probe, fingers, ref near);
                    if (kind > bestKind) { best = hit.collider; bestKind = kind; swept = true; sweptHit = hit; }
                }
                if (bestKind != TouchKind.Game)
                {
                    int n = OverlapPaw(probe, fingers, palmR, fingerR, SurfaceMask);
                    for (int i = 0; i < n; i++)
                    {
                        var col = s_overlap[i];
                        if (!Usable(col, ignoreRoot)) continue;
                        var kind = Classify(col, probe, fingers, ref near);
                        if (kind == TouchKind.Game) { best = col; bestKind = kind; swept = false; break; }
                        if (kind == TouchKind.Surface && best == null) { best = col; bestKind = kind; }
                    }
                }
                if (best == null)
                {
                    int n = OverlapPaw(probe, fingers, palmR, fingerR, HitboxMask);
                    for (int i = 0; i < n; i++)
                    {
                        if (s_overlap[i] != null && s_overlap[i].TryGetComponent<HitboxPlapInteractable>(out _))
                        {
                            best = s_overlap[i]; bestKind = TouchKind.Game; mask = HitboxMask;
                            break;
                        }
                    }
                }
                if (best == null) return false;

                c.Collider = best;
                surfaceOnly = bestKind == TouchKind.Surface;
                if (swept)
                {
                    c.Point = sweptHit.point;
                    c.Normal = sweptHit.normal;
                }
                else if (!PawContact(best, probe, fingers, palmR, fingerR, ref c))
                {
                    if (!s_loggedNoPenetration)
                    {
                        s_loggedNoPenetration = true;
                        LogT($"[Touch] no penetration result against {best.name}; contact normal from the ray fallback");
                    }
                    RayPointNormal(probe, motion, mask, ref c);
                }
                return true;
            }

            static System.Collections.IEnumerator PulseBool(Animator an, string name)
            {
                an.SetBool(name, true);
                yield return null;
                yield return null;
                if (an != null) an.SetBool(name, false);
            }
        }
    }
}
