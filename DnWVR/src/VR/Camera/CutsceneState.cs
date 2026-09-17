using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CutsceneData = WalkNWashCutsceneNode.CutsceneData;
using CutsceneMachine = WalkNWashCutsceneNode.CutsceneStateMachine;
using GAction = GatorDragonGames.Action<WalkNWashCutsceneNode.CutsceneStateMachine, WalkNWashCutsceneNode.CutsceneData>;
using SM = GatorDragonGames.StateMachine<WalkNWashCutsceneNode.CutsceneStateMachine, WalkNWashCutsceneNode.CutsceneData>;

namespace DnWVR.VR
{
    /// <summary>
    /// Read-only view of the game's camera graph: the running cutscene action and its blend between camera spots, and the
    /// active sex-scene shot and its flight. Call Refresh() after the graph has processed (OrbitCamera.LateUpdate /
    /// onBeforeRender). Its one write cancels a dialogue camera move that is already running when VR should drop it.
    /// </summary>
    public static class CutsceneState
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Metres / degrees: a transition whose two ends are closer than this re-requests the spot already on screen.
        // Loose enough for a frame of animation sway, far below any real camera move.
        const float SamePoseDistance = 0.1f;
        const float SamePoseAngle = 5f;

        static AccessTools.FieldRef<WalkNWashOrbitCamera, WalkNWashCutsceneNode> s_node;
        static AccessTools.FieldRef<WalkNWashCutsceneNode, float> s_lerpWeight;
        static AccessTools.FieldRef<WalkNWashCutsceneNode, CutsceneMachine> s_machine;
        static AccessTools.FieldRef<SM, float> s_transitionTime;
        static AccessTools.FieldRef<SM, float> s_transitionDuration;
        static AccessTools.FieldRef<SM, CutsceneData> s_prevState;
        static AccessTools.FieldRef<SM, CutsceneData> s_currentState;
        static AccessTools.FieldRef<SM, GAction> s_currentAction;
        static AccessTools.FieldRef<OrbitCameraArbitraryCuts, ArbitraryCut[]> s_cuts;
        static AccessTools.FieldRef<OrbitCameraArbitraryCuts, float> s_timer;
        static bool s_bound;
        static bool s_loggedRefreshError;

        /// <summary>The node's blend weight: 0 = player camera, 1 = cutscene camera.</summary>
        public static float Weight { get; private set; }
        /// <summary>A cutscene action runs (or the blend back to the player camera is still in progress).</summary>
        public static bool Active { get; private set; }
        /// <summary>The game is flying the camera between two spots (never show this in a headset).</summary>
        public static bool InTransition { get; private set; }
        /// <summary>Increments whenever the camera spot may have changed: new cutscene action or new sex-scene shot.</summary>
        public static int Generation { get; private set; }
        /// <summary>
        /// Equals Generation when that change re-requested the spot already on screen. Decided once, on the first frame
        /// with the new action's real pose, so an animated camera cannot flip the decision back and forth.
        /// </summary>
        public static int SameSpotGeneration { get; private set; } = -1;
        /// <summary>Type name of the running cutscene action (diagnostics).</summary>
        public static string ActionName { get; private set; } = "";
        /// <summary>
        /// The running action is a dialogue camera move (LookAt / LookFromTo) that VR does not show (VRRig.Active and
        /// DialogueCameraZoom off). It is cancelled on the first Refresh that sees it; VRRig treats it like gameplay.
        /// </summary>
        public static bool DialogueCameraDropped { get; private set; }

        static object s_lastAction;
        static object s_cancelledAction;
        static int s_cancelFrame = -10;
        static object s_lastCuts;
        static int s_lastShot = -1;
        static int s_pendingGeneration = -1;

        // Filled by the OrbitCameraArbitraryCuts.Process postfix.
        static int s_arbFrame = -10;
        static object s_arbCuts;
        static int s_arbShot = -1;
        static bool s_arbInTransition;

        public static void Bind(HarmonyLib.Harmony harmony)
        {
            try
            {
                s_node = AccessTools.FieldRefAccess<WalkNWashOrbitCamera, WalkNWashCutsceneNode>("cutsceneNode");
                s_lerpWeight = AccessTools.FieldRefAccess<WalkNWashCutsceneNode, float>("lerpWeight");
                s_machine = AccessTools.FieldRefAccess<WalkNWashCutsceneNode, CutsceneMachine>("stateMachine");
                s_transitionTime = AccessTools.FieldRefAccess<SM, float>("transitionTime");
                s_transitionDuration = AccessTools.FieldRefAccess<SM, float>("transitionDuration");
                s_prevState = AccessTools.FieldRefAccess<SM, CutsceneData>("prevState");
                s_currentState = AccessTools.FieldRefAccess<SM, CutsceneData>("currentState");
                s_currentAction = AccessTools.FieldRefAccess<SM, GAction>("currentAction");
                s_cuts = AccessTools.FieldRefAccess<OrbitCameraArbitraryCuts, ArbitraryCut[]>("cuts");
                s_timer = AccessTools.FieldRefAccess<OrbitCameraArbitraryCuts, float>("timer");
                s_bound = true;
            }
            catch (Exception e)
            {
                Log.Error("[CutsceneState] could not bind the camera graph state (cutscenes will be treated as gameplay): " + e);
                return;
            }

            try
            {
                var process = AccessTools.Method(typeof(OrbitCameraArbitraryCuts), "Process");
                if (process == null)
                {
                    Log.Warning("[CutsceneState] OrbitCameraArbitraryCuts.Process not found; sex-scene shot changes will not be faded");
                    return;
                }
                harmony.Patch(process, postfix: new HarmonyMethod(typeof(CutsceneState).GetMethod(nameof(ArbitraryProcess_Postfix), Any)));
                Log.Msg("[CutsceneState] patched OrbitCameraArbitraryCuts.Process");
            }
            catch (Exception e)
            {
                Log.Error("[CutsceneState] failed to patch OrbitCameraArbitraryCuts.Process: " + e);
            }
        }

        // Mirrors the game's own shot selection in OrbitCameraArbitraryCuts.Process.
        static void ArbitraryProcess_Postfix(OrbitCameraArbitraryCuts __instance)
        {
            try
            {
                var cuts = s_cuts(__instance);
                s_arbFrame = Time.frameCount;
                s_arbCuts = cuts;
                if (cuts == null || cuts.Length == 0)
                {
                    s_arbShot = -1;
                    s_arbInTransition = false;
                    return;
                }
                if (cuts.Length == 1 || __instance.interval <= 0f)
                {
                    s_arbShot = 0;
                    s_arbInTransition = false;
                    return;
                }
                float num = Mathf.Repeat(s_timer(__instance) / __instance.interval, cuts.Length);
                s_arbShot = Mathf.FloorToInt(num);
                s_arbInTransition = (Mathf.CeilToInt(num) - num) * __instance.interval < __instance.transitionDuration;
            }
            catch
            {
                s_arbInTransition = false;
            }
        }

        static void SetIdle()
        {
            Weight = 0f;
            Active = false;
            InTransition = false;
            DialogueCameraDropped = false;
        }

        // LookAtGameObject(null) is the game's own cancel (CameraPatches lets a null target through). The state machine
        // switches to the default action at once; InTransition reports the blend back from the next Tick on.
        static void CancelDialogueCamera(object action)
        {
            s_cancelledAction = action;
            s_cancelFrame = Time.frameCount;
            try
            {
                WalkNWashOrbitCamera.LookAtGameObject(null);
                Log.Msg($"[CutsceneState] dialogue {action.GetType().Name} was already running in VR: cancelled (DialogueCameraZoom is off)");
            }
            catch (Exception e)
            {
                Log.Warning($"[CutsceneState] cancelling dialogue {action.GetType().Name} failed: {e.Message}");
            }
        }

        // Where OrbitCameraData.ApplyTo would put the camera (centre-screen approximation).
        static Vector3 ResolvedPosition(OrbitCameraData d)
        {
            return d.position - d.rotation.normalized * Vector3.forward * d.distance;
        }

        static bool SamePose(in CutsceneData a, in CutsceneData b)
        {
            if (a.lerpWeight < 0.999f || b.lerpWeight < 0.999f) return false;
            if (Vector3.Distance(ResolvedPosition(a.data), ResolvedPosition(b.data)) > SamePoseDistance) return false;
            return Quaternion.Angle(a.data.rotation.normalized, b.data.rotation.normalized) <= SamePoseAngle;
        }

        public static void Refresh()
        {
            if (!s_bound) { SetIdle(); return; }
            try
            {
                var oc = WalkNWashOrbitCamera.GetInstance();
                if (oc == null) { SetIdle(); return; }
                var node = s_node(oc);
                if (node == null) { SetIdle(); return; }
                var sm = s_machine(node);
                if (sm == null) { SetIdle(); return; }

                Weight = s_lerpWeight(node);
                var action = s_currentAction(sm);
                bool isDefault = action == null || action is WalkNWashCutsceneNode.CutsceneActionDefault;
                var prev = s_prevState(sm);
                var cur = s_currentState(sm);
                float transitionTime = s_transitionTime(sm);
                bool timing = transitionTime < s_transitionDuration(sm);

                Active = !isDefault || Weight > 0.001f;

                int generationBefore = Generation;
                if (!ReferenceEquals(action, s_lastAction))
                {
                    s_lastAction = action;
                    Generation++;
                    ActionName = action != null ? action.GetType().Name : "";
                    if (DnWVRMod.DebugInteractionLog)
                        Log.Msg($"[CutsceneState] action -> {(action != null ? ActionName : "(none)")} gen {Generation}");
                }
                bool arbRecent = !isDefault && s_arbFrame >= Time.frameCount - 1;
                if (arbRecent && (!ReferenceEquals(s_arbCuts, s_lastCuts) || s_arbShot != s_lastShot))
                {
                    s_lastCuts = s_arbCuts;
                    s_lastShot = s_arbShot;
                    Generation++;
                }

                // Same-spot decision, once per change: the new action writes its first real pose on the Tick after the
                // transition starts (transitionTime > 0). A shot change without a transition is always a new spot.
                if (Generation != generationBefore) s_pendingGeneration = Generation;
                if (s_pendingGeneration == Generation)
                {
                    if (!timing)
                    {
                        s_pendingGeneration = -1;
                    }
                    else if (transitionTime > 0f)
                    {
                        if (SamePose(prev, cur)) SameSpotGeneration = Generation;
                        s_pendingGeneration = -1;
                    }
                }
                bool undecided = s_pendingGeneration == Generation; // requested, nothing has moved on screen yet
                bool sameSpot = SameSpotGeneration == Generation;
                // A transition from "no cutscene" to "no cutscene" (a stray cancel) changes nothing on screen either.
                bool nothingToShow = isDefault && prev.lerpWeight == 0f && cur.lerpWeight == 0f;
                bool blending = timing && !nothingToShow && !sameSpot && !undecided;

                // A LookAt/LookFromTo that started before CameraPatches could drop it is cancelled here. The cancel
                // frame is black too: the camera shows the dialogue spot until the blend back starts on the next Tick.
                bool dialogueCamera = action is WalkNWashCutsceneLookAt.LookAtAction || action is WalkNWashCutsceneLookFromTo.LookFromToAction;
                DialogueCameraDropped = dialogueCamera && VRRig.Active && !VRRig.DialogueCameraZoom;
                bool cancelNow = DialogueCameraDropped && !ReferenceEquals(action, s_cancelledAction);

                InTransition = blending || (arbRecent && s_arbInTransition) || cancelNow || s_cancelFrame == Time.frameCount;

                if (cancelNow) CancelDialogueCamera(action);
            }
            catch (Exception e)
            {
                SetIdle();
                if (!s_loggedRefreshError)
                {
                    s_loggedRefreshError = true;
                    Log.Warning("[CutsceneState] refresh failed (will keep retrying): " + e);
                }
            }
        }
    }
}
