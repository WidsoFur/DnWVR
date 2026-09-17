using System;
using System.Reflection;
using com.gatordragongames.washnwalk.tools;
using HarmonyLib;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// Replaces the game's eye-raycast hand and sponge logic with contact detection at the tracked controllers, raising
    /// the same game events. Bare paws slap, press and stroke by touch alone; grip only interacts.
    /// This part holds the settings, the patches and the gate that says when touch counts; the rest of the class is in
    /// HandPatches.Contact.cs (what a probe touches), HandPatches.BareHand.cs (the paws) and HandPatches.Tools.cs.
    /// </summary>
    public static partial class HandPatches
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const int SurfaceMask = 385;   // layers 0, 7, 8: surfaces the game lets hands/sponge touch
        const int HitboxMask = 64;     // layer 6: HitboxTrigger volumes

        public static float ContactRadius = 0.1f;      // m; the sponge's contact probe
        public static float PlapSpeed = 1.0f;          // m/s hand speed that counts as a slap
        public static float ContactReleaseTime = 0.15f; // s without contact before the sponge can splat again

        // Touch: speeds are relative to the body (VRHands.TrackVel/PeakSpeed), never world space.
        public static float PressSpeed = 0.4f;         // m/s toward a button/phone hitbox that presses it without a slap
        public static float EnterPad = 0.012f;         // m; contact starts within the paw radii plus this
        public static float ExitRadius = 0.075f;       // m; contact holds until the palm centre is this far away
        public static float ReleaseTime = 0.06f;       // s without contact before a paw can slap again
        public static float PlapCooldown = 0.12f;      // s between slaps of one paw
        public static float PlapWindow = 0.1f;         // s after contact starts in which the peak speed can still slap
        public static float ComeBackTime = 0.5f;       // s after a slap in which the far face of that part never slaps (the swing coming back through it)
        public static bool StrokeRequiresMotion = true;
        public static float StrokeTravel = 0.01f;      // m along the surface within StrokeWindow that counts as stroking
        public static float StrokeWindow = 0.25f;
        public static float StrokeHold = 0.3f;         // s a stroke lasts after the motion stops
        public static bool TouchDuringCutscenes = true;
        public static bool TouchWorldSurfaces = false; // floors/props give sounds too (never game events)
        public static bool SpongeNeedsTrigger = true;


        public static void Apply(HarmonyLib.Harmony harmony)
        {
            PlapperVR.Bind();
            SpongeVR.Bind();
            InteractableVR.Bind();
            BindTouchGate();
            Patch(harmony, typeof(PlapperHand), "UseContinuous", nameof(PlapperUse_Prefix));
            Patch(harmony, typeof(PlapperHand), "UpdateNotInUse", nameof(PlapperIdle_Prefix));
            // StopUse stays unpatched: it only clears IsInteracting, so a flag set in grip mode or before the hands attached
            // still clears on the next grip release.
            Patch(harmony, typeof(PlapperHand), "StartUse", nameof(PlapperStart_Prefix));
            Patch(harmony, typeof(ToolModelSponge), "UseContinuous", nameof(SpongeUse_Prefix));
            Patch(harmony, typeof(ToolModelSponge), "UpdateNotInUse", nameof(SpongeIdle_Prefix));
            Patch(harmony, typeof(Interactable), "GetBestInteractable", nameof(GetBestInteractable_Prefix));
            Patch(harmony, typeof(Interacter), "OnAttackStarted", nameof(InteracterAttack_Prefix));
        }

        static void Patch(HarmonyLib.Harmony harmony, Type target, string method, string prefix)
        {
            try
            {
                var original = AccessTools.Method(target, method);
                if (original == null) { Log.Warning($"[HandPatches] {target.Name}.{method} not found; skipping"); return; }
                harmony.Patch(original, new HarmonyMethod(typeof(HandPatches).GetMethod(prefix, Any)));
                Log.Msg($"[HandPatches] patched {target.Name}.{method}");
            }
            catch (Exception e)
            {
                Log.Error($"[HandPatches] failed to patch {target.Name}.{method}: {e}");
            }
        }

        static bool Active => VRRig.Active && VRHands.Attached;

        static void LogT(string msg)
        {
            if (DnWVRMod.DebugInteractionLog) Log.Msg(msg);
        }

        static bool PlapperUse_Prefix(PlapperHand __instance)
        {
            if (!Active) return true;
            PlapperVR.Tick(__instance);
            return false;
        }

        static bool PlapperIdle_Prefix(PlapperHand __instance)
        {
            if (!Active) return true;
            PlapperVR.Tick(__instance);
            return false;
        }

        // Grip is not "using the hand": skip StartUse's InteractingMovementMode (0.5 m/s, no jump) and mount drop.
        static bool PlapperStart_Prefix() => !Active;

        static bool SpongeUse_Prefix(ToolModelSponge __instance)
        {
            if (!Active) return true;
            SpongeVR.Update(__instance, inUse: true);
            return false;
        }

        static bool SpongeIdle_Prefix(ToolModelSponge __instance)
        {
            if (!Active) return true;
            SpongeVR.Update(__instance, inUse: false);
            return false;
        }

        static bool GetBestInteractable_Prefix(Tool tool, ref Interactable __result)
        {
            if (!Active) return true;
            if (ItemLaser.Running)
            {
                // The laser picks per hand; the prompt and the cached target ToolTest reads follow what it points at.
                __result = ItemLaser.PromptTarget;
                InteractableVR.SetCached(__result);
                return false;
            }
            if (!VRHands.InteractHandTracked) return true;
            __result = InteractableVR.Select(tool, VRHands.InteractHand);
            return false;
        }

        // The item laser runs Interact itself (one grip = one Interact); the game's Plap handler would run it a second
        // time on the same station and put a just-picked tool straight back.
        static bool InteracterAttack_Prefix() => !(Active && ItemLaser.Running);

        /// <summary>A bare paw is on the active dragon, resting on its body or touching its penis (as of the last touch tick).</summary>
        internal static bool PawOnDragon(bool right)
        {
            var st = s_bare[right ? 1 : 0];
            return st.InContact && st.LastCollider != null && !st.LastSurfaceOnly && IsDragonCollider(st.LastCollider);
        }

        static int s_selfTickFrame = -1;

        /// <summary>
        /// Runs the bare paws' touch where the game's ToolTest does not tick PlapperHand (its ToolAnchor is inactive in the
        /// sex scenes). Once a frame, after the hands are placed.
        /// </summary>
        internal static void TickWithoutToolTest(PlapperHand ph)
        {
            if (!Active || ph == null) return;
            var tt = VRHands.ToolTest;
            if (tt != null && tt.isActiveAndEnabled) return;
            if (s_selfTickFrame == Time.frameCount) return;
            s_selfTickFrame = Time.frameCount;
            PlapperVR.Tick(ph);
        }

        // ------------------------------------------------------------------------------------
        // Touch gate
        // ------------------------------------------------------------------------------------

        static AccessTools.FieldRef<ToolTest, bool> s_interactEnabled;
        static int s_gateFrame = -1;
        static bool s_allowed = true;
        static bool s_loggedNoToolTest;

        static void BindTouchGate()
        {
            try { s_interactEnabled = AccessTools.FieldRefAccess<ToolTest, bool>("interactEnabled"); }
            catch (Exception e) { Log.Warning("[HandPatches] ToolTest.interactEnabled not bound (touch ignores dialogue): " + e.Message); }
        }

        // Cached per frame. ToolTest.interactEnabled is the game's interaction switch (off during dialogue).
        static bool TouchAllowed
        {
            get
            {
                if (s_gateFrame == Time.frameCount) return s_allowed;
                s_gateFrame = Time.frameCount;
                bool allowed = true;
                var tt = VRHands.ToolTest;
                // An inactive ToolTest (sex scenes) never ran its Start, so its switch says nothing.
                if (tt != null && tt.isActiveAndEnabled && s_interactEnabled != null) allowed = s_interactEnabled(tt);
                else if (tt == null && !s_loggedNoToolTest)
                {
                    s_loggedNoToolTest = true;
                    Log.Warning("[HandPatches] no ToolTest on ToolAnchor; touch is not gated on the game's interaction switch");
                }
                var gsm = GameStateManager.Instance;
                if (gsm != null && gsm.IsPaused) allowed = false;
                // Menus, DialogCommands dialogues and the between-level intermission turn the game's player input off.
                if (!VRInput.GamePlayerInputEnabled) allowed = false;
                if (!TouchDuringCutscenes && CutsceneState.Active) allowed = false;
                if (allowed != s_allowed) LogT($"[Touch] allowed -> {allowed}");
                s_allowed = allowed;
                return allowed;
            }
        }
    }
}
