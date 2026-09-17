using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DnWVR.XR;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Yarn.Unity;

namespace DnWVR.VR
{
    /// <summary>
    /// The sex scenes in VR. With the player (SexScene1-3) the player's baked body is hidden, the paws touch the dragon like in
    /// the wash, and the pace of both animations follows the paws on the dragon (resting on it or touching its penis): moving
    /// hands build the dragon's pleasure, resting hands hold it, near the climax resting hands are enough for the pace to
    /// climb, and letting go nearly stops the scene while the pleasure slowly fades.
    /// The scene's dialogue follows that pleasure instead of its timer: the loop's lines play at pleasure marks, its "keep
    /// going or finish?" questions are answered out of sight, and the dragon climaxes at full pleasure or when the player
    /// holds A (FinishHold). In the scene the player only watches (Ryan + Conrad) the lines keep the game's pace, once each,
    /// and A ends it the same way.
    /// </summary>
    public static class SexScene
    {
        enum Kind { None, Interactive, Spectator }

        // Where the loop pass under way leads: back into the loop (through the questions, out of sight) or to the climax.
        enum Plan { Loop, Finale }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const string LoopSuffix = "_SexScene_Loop", CumSuffix = "_SexScene_Cum";

        /// <summary>The mod runs the sex scenes: hands drive the pace, pleasure the dialogue, A the finish (off: the game's scenes).</summary>
        public static bool Interactive = true;
        /// <summary>Seconds of steady, lively hand motion from the start of a scene to its climax.</summary>
        public static float BuildUpSeconds = 60f;

        const float LoopOpeningWait = 3f;   // every loop opens with <<wait 4>> and closes with <<wait 2>>
        const float FirstLines = 0.35f;     // pleasure at which the loop's first lines play
        const float SecondLines = 0.7f;     // and its second lines
        const int WatchedPasses = 3;        // loop passes with lines in the scene the player watches; then it waits for A
        const float NearFinale = 0.8f;      // pleasure from which hands on the dragon make the pace climb by themselves
        const float RampSeconds = 8f;       // that climb to full pace, and from NearFinale to the climax without motion
        const float LetGoGrace = 0.5f;      // s a paw may lose contact (a stroke lifting off) before the hands count as let go
        const float ActivityTime = 0.5f;    // s, smoothing of the hands' motion
        const float PaceUpTime = 0.35f;     // s, smoothing of the animator values while speeding up
        const float PaceDownTime = 1.5f;    // s, and while calming down
        const float IdleSpeed = 0.1f;       // hands off the dragon: the scene nearly stops
        const float SlowSpeed = 0.35f, FastSpeed = 1.3f;

        // Alexander's face moves in the same clips as his body, so played faster his mouth chatters: his pace comes only from
        // the blend between his slow and fast animations, each at its own speed.
        static float TopSpeed => s_sceneName == "SexScene2" ? 1f : FastSpeed;

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int BlendId = Animator.StringToHash("Blend");

        static AccessTools.FieldRef<AnimationBlender, Animator[]> s_animators;
        static AccessTools.FieldRef<AnimationBlender, float> s_speedMultiplier;

        static Kind s_kind;
        static string s_sceneName;
        static Animator s_rig;
        static readonly List<Renderer> s_hidden = new List<Renderer>();
        static int s_frame = -1;
        static float s_activity, s_speed = 1f, s_blend, s_ramp;
        static int s_loggedQuarter = -1;
        static float s_lastOnDragon = -10f;

        static Plan s_plan;
        static int s_linesPlayed;
        static bool s_linesThisPass, s_loopEntered, s_finishRequested, s_finaleLatched, s_finaleChosen;

        /// <summary>A scene with the player's body is loaded and its pace is the player's.</summary>
        public static bool Active => s_rig != null && s_kind == Kind.Interactive && Interactive && VRRig.Active;
        /// <summary>A sex scene is loaded and the mod runs it (its dialogue, A to finish, walking).</summary>
        public static bool InSexScene => s_kind != Kind.None && Interactive && VRRig.Active;
        /// <summary>The player watches the scene instead of taking part (Ryan + Conrad).</summary>
        public static bool Spectating => InSexScene && s_kind == Kind.Spectator;
        /// <summary>Name of the sex scene the mod runs, or null.</summary>
        public static string SceneName => s_kind != Kind.None ? s_sceneName : null;
        /// <summary>0..1: the dragon's pleasure.</summary>
        public static float Arousal { get; private set; }
        /// <summary>A paw is on the dragon, or was a moment ago.</summary>
        public static bool HandsOn => Time.unscaledTime - s_lastOnDragon < LetGoGrace;
        /// <summary>Holding A can finish the scene now: its loop is under way and its climax has not been decided yet.</summary>
        public static bool CanFinish => InSexScene && s_loopEntered && !FinaleDue && !s_finaleChosen && !GamePaused;
        /// <summary>The questions of the *_SexScene_Cum node are answered out of sight until the climax is chosen.</summary>
        public static bool OwnsPrompt => InSexScene && !s_finaleChosen && InCumNode();

        static bool FinaleDue => s_finishRequested || (s_kind == Kind.Interactive && s_finaleLatched);

        // Behind the pause menu, the debug menu or a stopped clock nothing finishes.
        static bool GamePaused
        {
            get
            {
                if (Time.timeScale <= 0f || DebugSceneMenu.IsOpen) return true;
                var gsm = GameStateManager.Instance;
                return gsm != null && gsm.IsPaused;
            }
        }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            s_animators = AccessTools.FieldRefAccess<AnimationBlender, Animator[]>("animators");
            s_speedMultiplier = AccessTools.FieldRefAccess<AnimationBlender, float>("speedMultiplier");
            harmony.Patch(AccessTools.Method(typeof(AnimationBlender), "Update"),
                prefix: new HarmonyMethod(typeof(SexScene).GetMethod(nameof(Update_Prefix), Any)));
            harmony.Patch(AccessTools.Method(typeof(YarnArbitraryCutsceneEnabler), nameof(YarnArbitraryCutsceneEnabler.EnableCutsceneObject)),
                prefix: new HarmonyMethod(typeof(SexScene).GetMethod(nameof(EnableCutsceneObject_Prefix), Any)));
            harmony.Patch(AccessTools.Method(typeof(YarnArbitraryCutsceneEnabler), nameof(YarnArbitraryCutsceneEnabler.ShowDragonFaceLayer)),
                prefix: new HarmonyMethod(typeof(SexScene).GetMethod(nameof(ShowDragonFaceLayer_Prefix), Any)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Yarn.Unity.DefaultActions"), "Wait", new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(SexScene).GetMethod(nameof(Wait_Prefix), Any)));
            harmony.Patch(AccessTools.Method(typeof(LinePresenter), nameof(LinePresenter.RunLineAsync), new[] { typeof(LocalizedLine), typeof(LineCancellationToken) }),
                prefix: new HarmonyMethod(typeof(SexScene).GetMethod(nameof(RunLine_Prefix), Any)));
            harmony.Patch(AccessTools.Method(typeof(OptionsPresenter), nameof(OptionsPresenter.RunOptionsAsync), new[] { typeof(DialogueOption[]), typeof(LineCancellationToken) }),
                prefix: new HarmonyMethod(typeof(SexScene).GetMethod(nameof(RunOptions_Prefix), Any)));
            Log.Msg("[SexScene] installed");
        }

        /// <summary>The player held A: the dragon climaxes after its last line.</summary>
        public static void RequestFinish()
        {
            if (!CanFinish) return;
            s_finishRequested = true;
            Log.Msg($"[SexScene] {s_sceneName}: the player finishes (pleasure {Arousal:0.00})");
        }

        static string CurrentNode()
        {
            try { return DialogCommands.IsDialogueRunning ? DialogCommands.CurrentNode : null; }
            catch { return null; }
        }

        static bool InCumNode()
        {
            var node = CurrentNode();
            return node != null && node.EndsWith(CumSuffix, StringComparison.Ordinal);
        }

        // Every scene loops <Name>_SexScene_Loop (a pause, lines picked by $<name>_sexscene_loops, a pause) into
        // <Name>_SexScene_Cum (a pause, then a question per counter value: 1 and above 1; keeping going adds one and loops,
        // finishing plays the climax; a counter of 0 goes straight to the climax). The loop's first pause lasts until there is
        // something new to show, and the counter picks what that is.
        static bool Wait_Prefix(float duration, ref IEnumerator __result)
        {
            if (!InSexScene || s_finaleChosen) return true;
            var node = CurrentNode();
            if (node == null) return true;
            if (node.EndsWith(LoopSuffix, StringComparison.Ordinal))
            {
                s_loopEntered = true;
                // Told apart by length, so a scene taken over mid-pass (VR started, the setting switched on) never takes the
                // closing pause for the opening one.
                if (duration > LoopOpeningWait)
                {
                    __result = HoldLoop(node, duration);
                    return false;
                }
                if (FinaleDue) s_plan = Plan.Finale;
                if (s_plan != Plan.Finale || s_linesThisPass) return true;
                __result = NoWait();
                return false;
            }
            // Until its question is answered the Cum node pauses only once, at its start.
            if (node.EndsWith(CumSuffix, StringComparison.Ordinal))
            {
                if (FinaleDue) s_plan = Plan.Finale;
                if (s_plan == Plan.Finale)
                {
                    SetLoops(node, FinaleBranch(node));
                    return true;
                }
                // Through the first question and back into the loop, out of sight.
                SetLoops(node, 1);
                __result = NoWait();
                return false;
            }
            return true;
        }

        static IEnumerator HoldLoop(string node, float duration)
        {
            s_linesThisPass = false;
            float start = Time.time;
            while (InSexScene)
            {
                if (FinaleDue)
                {
                    s_plan = Plan.Finale;
                    SetLoops(node, 0);
                    yield break;
                }
                if (Time.time - start >= duration)
                {
                    int lines = DueLines();
                    if (lines > 0)
                    {
                        s_plan = Plan.Loop;
                        s_linesPlayed = lines;
                        SetLoops(node, lines);
                        Log.Msg($"[SexScene] {s_sceneName}: lines {lines} (pleasure {Arousal:0.00})");
                        yield break;
                    }
                }
                yield return null;
            }
        }

        static IEnumerator NoWait()
        {
            yield break;
        }

        static int DueLines()
        {
            if (s_kind == Kind.Spectator) return s_linesPlayed < WatchedPasses ? s_linesPlayed + 1 : 0;
            if (s_linesPlayed < 1 && Arousal >= FirstLines) return 1;
            if (s_linesPlayed < 2 && Arousal >= SecondLines) return 2;
            return 0;
        }

        // The dragon's line before the climax: the first "close" question when the player finishes, the second at full
        // pleasure. Alexander's first branch asks the player two more questions first, so he always takes the second.
        static int FinaleBranch(string node)
        {
            if (node.StartsWith("Alexander", StringComparison.Ordinal)) return 2;
            return s_finishRequested ? 1 : 2;
        }

        static void SetLoops(string node, int value)
        {
            try
            {
                int suffix = node.EndsWith(LoopSuffix, StringComparison.Ordinal) ? LoopSuffix.Length : CumSuffix.Length;
                var name = "$" + node.Substring(0, node.Length - suffix).ToLowerInvariant() + "_sexscene_loops";
                DialogueVR.Runner?.VariableStorage?.SetValue(name, (float)value);
            }
            catch (Exception e)
            {
                Log.Warning("[SexScene] cannot set the loop counter: " + e.Message);
            }
        }

        // A pass through the questions back into the loop shows none of its lines.
        static bool RunLine_Prefix(ref YarnTask __result)
        {
            if (!InSexScene || s_finaleChosen) return true;
            var node = CurrentNode();
            if (node == null) return true;
            if (node.EndsWith(LoopSuffix, StringComparison.Ordinal))
            {
                s_linesThisPass = true;
                return true;
            }
            if (s_plan != Plan.Loop || !node.EndsWith(CumSuffix, StringComparison.Ordinal)) return true;
            // One frame, so the runner always finishes the line on its normal path.
            __result = YarnTask.Yield();
            return false;
        }

        // The questions are answered without the panel: keep going (the first option), and at the climax finish (the last).
        static bool RunOptions_Prefix(DialogueOption[] dialogueOptions, ref YarnTask<DialogueOption> __result)
        {
            if (dialogueOptions == null || dialogueOptions.Length == 0 || !OwnsPrompt) return true;
            int pick = 0;
            if (dialogueOptions.Length > 1 && s_plan == Plan.Finale)
            {
                pick = dialogueOptions.Length - 1;
                s_finaleChosen = true;
                Log.Msg($"[SexScene] {s_sceneName}: climax ({(s_finishRequested ? "the player finished" : "full pleasure")})");
            }
            __result = YarnTask<DialogueOption>.FromResult(dialogueOptions[pick]);
            return false;
        }

        // The scenes cut to third-person cameras around those questions and at the climax; the interactive scene stays in the
        // player's eyes (camera 0).
        static bool EnableCutsceneObject_Prefix(int index) => !(Active && index != 0);

        // A pass through the questions back into the loop shows none of its face either (Alexander's opens his mouth for a
        // few frames). One that turns into the climax keeps it.
        static bool ShowDragonFaceLayer_Prefix() => !(InSexScene && !s_finaleChosen && s_plan == Plan.Loop && !FinaleDue && InCumNode());

        /// <summary>Takes over a freshly loaded sex scene (hides the player's body there); safe to call repeatedly.</summary>
        public static void Attach()
        {
            if (!XRBootstrap.IsRunning) return;
            var scene = SceneManager.GetActiveScene().name;
            var kind = KindOf(scene);
            if (kind == Kind.None) return;
            if (kind == s_kind && scene == s_sceneName)
            {
                if (s_rig != null)
                {
                    HideBody();
                    BonePenis.Attach();
                }
                return;
            }
            if (kind == Kind.Interactive)
            {
                if (UnityEngine.Object.FindFirstObjectByType<AnimationBlender>() == null) return;
                s_rig = null;
                foreach (var animator in UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (animator.name != "Rig-Player-Cutscene") continue;
                    s_rig = animator;
                    break;
                }
                if (s_rig == null) return;
            }
            s_kind = kind;
            s_sceneName = scene;
            Arousal = 0f;
            s_activity = s_blend = s_ramp = 0f;
            s_speed = 1f;
            s_loggedQuarter = -1;
            s_lastOnDragon = -10f;
            ResetFlow();
            if (s_rig != null)
            {
                HideBody();
                // Alexander's penis is part of his body: give it colliders and a tube.
                BonePenis.Attach();
            }
            Log.Msg(kind == Kind.Interactive
                ? $"[SexScene] {scene}: player body hidden, pace and dialogue from the hands {(Interactive ? "on" : "off")}"
                : $"[SexScene] {scene}: watching, A ends the scene {(Interactive ? "on" : "off")}");
        }

        static Kind KindOf(string scene)
        {
            switch (scene)
            {
                case "SexScene1":
                case "SexScene2":
                case "SexScene3":
                    return Kind.Interactive;
                case "SexSceneRyanConrad":
                    return Kind.Spectator;
                default:
                    return Kind.None;
            }
        }

        static void ResetFlow()
        {
            s_plan = Plan.Loop;
            s_linesPlayed = 0;
            s_linesThisPass = s_loopEntered = s_finishRequested = s_finaleLatched = s_finaleChosen = false;
        }

        /// <summary>Shows the player's body again (XR stop).</summary>
        public static void Detach()
        {
            foreach (var r in s_hidden)
                if (r != null) r.enabled = true;
            s_hidden.Clear();
            s_rig = null;
            s_kind = Kind.None;
            s_sceneName = null;
            ResetFlow();
            BonePenis.Reset();
        }

        /// <summary>A scene was loaded: the next Attach decides whether it is a sex scene.</summary>
        public static void OnSceneChanged()
        {
            s_kind = Kind.None;
            s_sceneName = null;
            ResetFlow();
            BonePenis.Reset();
            if (s_rig != null) return;
            s_hidden.Clear();
        }

        static void HideBody()
        {
            // A CullUpdateTransforms animator (SexScene1) stops posing once its renderers are off, freezing the camera bone.
            s_rig.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var r in s_rig.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                r.enabled = false;
                s_hidden.Add(r);
            }
        }

        // Replaces the game's noise-driven pace for the player's body and the dragon while the scene is interactive.
        static bool Update_Prefix(AnimationBlender __instance)
        {
            if (!Active) return true;
            if (s_frame != Time.frameCount)
            {
                s_frame = Time.frameCount;
                Step(Mathf.Min(Time.deltaTime, 0.1f));
            }
            var animators = s_animators(__instance);
            if (animators == null) return false;
            // SlowDragonAnimation still slows the scene down after its climax.
            float speed = s_speed * s_speedMultiplier(__instance);
            foreach (var animator in animators)
            {
                if (animator == null) continue;
                animator.SetFloat(SpeedId, speed);
                animator.SetFloat(BlendId, s_blend);
            }
            return false;
        }

        static void Step(float dt)
        {
            bool left = HandPatches.PawOnDragon(false), right = HandPatches.PawOnDragon(true);
            if (left || right) s_lastOnDragon = Time.unscaledTime;
            bool handsOn = HandsOn;
            // Only paws on the dragon count.
            float motion = (left ? VRHands.TrackVel(false).magnitude : 0f) + (right ? VRHands.TrackVel(true).magnitude : 0f);
            s_activity = Mathf.Lerp(s_activity, motion, 1f - Mathf.Exp(-dt / ActivityTime));
            float drive = handsOn ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 1.5f, s_activity)) : 0f;

            float buildUp = Mathf.Max(5f, BuildUpSeconds);
            if (s_finaleChosen)
            {
                Arousal = 1f;
                s_ramp = 1f;
            }
            else if (!handsOn)
            {
                // Let go: the scene nearly stops and the pleasure slowly fades.
                Arousal = Mathf.Clamp01(Arousal - 0.25f / buildUp * dt);
                s_ramp = Mathf.MoveTowards(s_ramp, 0f, dt / PaceDownTime);
            }
            else
            {
                // Moving hands build pleasure and resting hands hold it; near the climax resting hands make it climb too.
                float rise = drive / buildUp;
                if (Arousal >= NearFinale)
                {
                    s_ramp = Mathf.MoveTowards(s_ramp, 1f, dt / RampSeconds);
                    rise = Mathf.Max(rise, (1f - NearFinale) / RampSeconds);
                }
                else s_ramp = Mathf.MoveTowards(s_ramp, 0f, dt / PaceDownTime);
                Arousal = Mathf.Clamp01(Arousal + rise * dt);
            }
            // Full pleasure decides the climax for good.
            if (Arousal >= 1f) s_finaleLatched = true;

            bool driven = handsOn || s_finaleChosen;
            float pace = s_finaleChosen ? 1f : Mathf.Max(drive, s_ramp);
            float targetSpeed = driven ? Mathf.Lerp(SlowSpeed, TopSpeed, pace) : IdleSpeed;
            float targetBlend = driven ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.9f, pace)) : 0f;
            float k = 1f - Mathf.Exp(-dt / (targetSpeed > s_speed ? PaceUpTime : PaceDownTime));
            s_speed = Mathf.Lerp(s_speed, targetSpeed, k);
            s_blend = Mathf.Lerp(s_blend, targetBlend, k);

            int quarter = Mathf.FloorToInt(Arousal * 4f);
            if (quarter != s_loggedQuarter)
            {
                s_loggedQuarter = quarter;
                if (DnWVRMod.DebugInteractionLog) Log.Msg($"[SexScene] pleasure {Arousal:0.00} (hands {(handsOn ? "on" : "off")}, activity {s_activity:0.00} m/s, speed {s_speed:0.00}, blend {s_blend:0.00})");
            }
        }
    }
}
