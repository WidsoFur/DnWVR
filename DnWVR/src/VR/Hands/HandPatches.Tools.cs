using System;
using System.Collections.Generic;
using System.Reflection;
using com.gatordragongames.washnwalk.tools;
using FluidRenderingForGames;
using HarmonyLib;
using UnityEngine;

namespace DnWVR.VR
{
    // The sponge and the item interactables, which touch the world the same way the paws do.
    public static partial class HandPatches
    {
        static class SpongeVR
        {
            static AccessTools.FieldRef<ToolModelSponge, Transform> sponge;
            static AccessTools.FieldRef<ToolModelSponge, FluidParticleSystemSettings> fluid;
            static AccessTools.FieldRef<ToolModelSponge, PhysicsMaterialExtension> wetMat, dryMat;
            static AccessTools.FieldRef<ToolModelSponge, AudioSource> audio;
            static AccessTools.FieldRef<ToolModelSponge, float> fillAmount, playTimer;
            static AccessTools.FieldRef<ToolModelSponge, bool> splatted;
            static AccessTools.FieldRef<ToolModelSponge, Vector3> lastHitPoint;
            static AccessTools.FieldRef<ToolModelSponge, Collider[]> hitColliders;
            static AccessTools.FieldRef<ToolModelSponge, Collider> hitCollider;
            static FieldInfo spongeRubbedField;
            static MethodInfo updateAnimatorFill;

            public static void Bind()
            {
                sponge = AccessTools.FieldRefAccess<ToolModelSponge, Transform>("sponge");
                fluid = AccessTools.FieldRefAccess<ToolModelSponge, FluidParticleSystemSettings>("fluid");
                wetMat = AccessTools.FieldRefAccess<ToolModelSponge, PhysicsMaterialExtension>("wetSpongeMat");
                dryMat = AccessTools.FieldRefAccess<ToolModelSponge, PhysicsMaterialExtension>("drySpongeMat");
                audio = AccessTools.FieldRefAccess<ToolModelSponge, AudioSource>("spongeAudioSource");
                fillAmount = AccessTools.FieldRefAccess<ToolModelSponge, float>("fillAmount");
                playTimer = AccessTools.FieldRefAccess<ToolModelSponge, float>("playTimer");
                splatted = AccessTools.FieldRefAccess<ToolModelSponge, bool>("splatted");
                lastHitPoint = AccessTools.FieldRefAccess<ToolModelSponge, Vector3>("lastHitPoint");
                hitColliders = AccessTools.FieldRefAccess<ToolModelSponge, Collider[]>("hitColliders");
                hitCollider = AccessTools.FieldRefAccess<ToolModelSponge, Collider>("hitCollider");
                spongeRubbedField = AccessTools.Field(typeof(ToolModelSponge), "spongeRubbed");
                updateAnimatorFill = AccessTools.Method(typeof(ToolModelSponge), "UpdateAnimatorFillAmount");
            }

            public static void Update(ToolModelSponge sp, bool inUse)
            {
                var s = sponge(sp);
                if (s == null) return;
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                playTimer(sp) = Mathf.MoveTowards(playTimer(sp), 0f, dt);
                s.localPosition = Vector3.zero;
                s.localRotation = Quaternion.identity;

                var pos = s.position;
                // Relative to the body: only picks the direction of the point/normal ray, walking must not steer it.
                var vel = VRHands.TrackVel(VRHands.ToolHandIsRight);
                var st = s_sponge;

                var src = audio(sp);
                if (src != null) src.volume = Mathf.MoveTowards(src.volume, 0f, dt * 4f);

                bool contact = FindContact(pos, vel, ContactRadius + 0.02f, sp.transform, out var c);
                hitCollider(sp) = contact ? c.Collider : null;
                if (!contact)
                {
                    st.NoContact += dt;
                    if (st.NoContact > ContactReleaseTime) splatted(sp) = false;
                    updateAnimatorFill?.Invoke(sp, null);
                    return;
                }
                st.NoContact = 0f;
                // Without SpongeNeedsTrigger it rubs on contact alone; never while the game has interaction off.
                if ((!inUse && SpongeNeedsTrigger) || !TouchAllowed) { updateAnimatorFill?.Invoke(sp, null); return; }

                var f = fluid(sp);
                fillAmount(sp) = Mathf.MoveTowards(fillAmount(sp), 0f, dt * 0.1f);
                var mat = fillAmount(sp) > 0.1f ? wetMat(sp) : dryMat(sp);
                var db = PhysicsMaterialExtensionDatabase.GetDatabase();
                if (db != null)
                {
                    db.TryGetImpactInfo(c.Collider.sharedMaterial, mat, PhysicsMaterialExtension.PhysicsResponseType.Scrape, out _, out var hasScrape, out _, out var scrape);
                    if (hasScrape && src != null)
                    {
                        src.resource = scrape.soundEffect;
                        if (!src.isPlaying) src.Play();
                    }
                    if (!splatted(sp))
                    {
                        // Same convention as the bare hand: A = outward normal (sponge side), B = into the surface.
                        db.TryGetImpactInfo(c.Collider.sharedMaterial, mat, PhysicsMaterialExtension.PhysicsResponseType.Soft, out var hasA, out var hasB, out var a, out var b);
                        if (hasA) a.Apply(c.Point, c.Normal);
                        if (hasB) b.Apply(c.Point, -c.Normal);
                        splatted(sp) = true;
                    }
                }

                if (src != null)
                {
                    float moved = Vector3.Distance(pos, lastHitPoint(sp));
                    src.volume = Mathf.Clamp01(src.volume + moved * 8f);
                    src.pitch = Mathf.Clamp(0.9f + moved * 2f, 0f, 1.5f);
                }
                lastHitPoint(sp) = pos;

                // The game's decals take a normal pointing INTO the surface (its sponge.forward).
                var into = -c.Normal;
                (spongeRubbedField?.GetValue(null) as ToolModelSponge.SpongeRubAction)?.Invoke(sp, c.Collider, c.Point, into);
                if (f != null)
                {
                    f.OnFluidCollision(new FluidParticleSystem.ParticleCollision
                    {
                        position = c.Point,
                        normal = into,
                        collider = c.Collider,
                        size = f.particleBaseSize,
                        color = f.color,
                        heightStrength = 1.5f * f.heightStrengthBase * fillAmount(sp),
                        stretch = into * f.particleBaseSize,
                    });
                }
                HitHitboxes(pos, 0.1f, hitColliders(sp), f, HitboxTrigger.HitType.SpongeRub);
                updateAnimatorFill?.Invoke(sp, null);
            }
        }

        // ------------------------------------------------------------------------------------
        // Interactable selection
        // ------------------------------------------------------------------------------------

        static class InteractableVR
        {
            static AccessTools.FieldRef<List<Interactable>> interactables;
            static AccessTools.FieldRef<Interactable> best;
            static Func<Interactable, Tool, bool> canInteract;

            public static void Bind()
            {
                interactables = AccessTools.StaticFieldRefAccess<List<Interactable>>(AccessTools.Field(typeof(Interactable), "_interactables"));
                best = AccessTools.StaticFieldRefAccess<Interactable>(AccessTools.Field(typeof(Interactable), "bestInteractable"));
                canInteract = AccessTools.MethodDelegate<Func<Interactable, Tool, bool>>(AccessTools.Method(typeof(Interactable), "CanInteract"), null, true);
            }

            /// <summary>The game's static bestInteractable (Interactable.GetCachedInteractable), set from the item laser's pick.</summary>
            public static void SetCached(Interactable i) => best() = i;

            /// <summary>Laser off: same scoring as the game (40 degree cone, 3 m), but from the controller instead of the eyes.</summary>
            public static Interactable Select(Tool tool, Transform hand)
            {
                Interactable result = null;
                float bestScore = 0f;
                var origin = hand.position;
                var forward = hand.forward;
                var list = interactables();
                if (list != null)
                {
                    foreach (var it in list)
                    {
                        if (it == null || !it.gameObject.activeInHierarchy || !canInteract(it, tool)) continue;
                        var v = it.transform.position - origin;
                        float dist = v.magnitude;
                        float angle = Vector3.Angle(forward, v.normalized);
                        float score = (1f - angle / 40f) * (10f / Mathf.Max(dist, 1f));
                        if (dist > 3f) score = 0f;
                        if (score > bestScore) { bestScore = score; result = it; }
                    }
                }
                best() = result;
                return result;
            }
        }
    }
}
