using System;
using System.Collections.Generic;
using System.Reflection;
using com.gatordragongames.washnwalk.tools;
using HarmonyLib;
using UnityEngine;
using Yarn.Unity;

namespace DnWVR.VR
{
    /// <summary>
    /// Dialogue never takes the controllers away in VR: lines advance on a timer like the game's barks, the player keeps
    /// moving, touching and using tools, and a press of grip or trigger skips lines. Answers are picked with the laser pointer,
    /// which shows only for them and for menus.
    /// </summary>
    public static class DialogueVR
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Lines advance on their own and dialogue leaves the player's controls alone (off: the flat game's behaviour).</summary>
        public static bool AutoAdvance = true;
        /// <summary>Seconds a fully shown line stays before the next one.</summary>
        public static float LineSeconds = 3f;
        /// <summary>Grip or trigger skips dialogue lines outside interactive sex scenes: a line shows in full, then the next comes.</summary>
        public static bool SkipLines = true;

        static AccessTools.FieldRef<DialogCommands> s_instance;
        static AccessTools.FieldRef<DialogCommands, DialogueRunner> s_runner;
        static AccessTools.FieldRef<DialogCommands, LineAdvancer> s_lineAdvancer;
        static AccessTools.FieldRef<DialogCommands, LinePresenter> s_linePresenter;
        static AccessTools.FieldRef<DialogCommands, ToolTest> s_toolTest;
        static AccessTools.FieldRef<DialogCommands, Interacter> s_interacter;
        static AccessTools.FieldRef<GameStateManager, TicketLock> s_mouseNeeded;
        static AccessTools.FieldRef<TicketLock, List<TicketLock.Ticket>> s_heldLocks;
        static OptionsPresenter s_options;
        static float s_optionsLookup = -10f;
        static bool s_leftTriggerWas, s_rightTriggerWas;
        static bool s_leftGripWas, s_rightGripWas;

        static bool Active => VRRig.Active && AutoAdvance;

        /// <summary>The game's dialogue runner in the loaded scene, or null.</summary>
        internal static DialogueRunner Runner
        {
            get
            {
                var commands = s_instance != null ? s_instance() : null;
                return commands != null ? s_runner(commands) : null;
            }
        }

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            s_instance = AccessTools.StaticFieldRefAccess<DialogCommands>(AccessTools.Field(typeof(DialogCommands), "_instance"));
            s_runner = AccessTools.FieldRefAccess<DialogCommands, DialogueRunner>("dialogueRunner");
            s_lineAdvancer = AccessTools.FieldRefAccess<DialogCommands, LineAdvancer>("lineAdvancer");
            s_linePresenter = AccessTools.FieldRefAccess<DialogCommands, LinePresenter>("linePresenter");
            s_toolTest = AccessTools.FieldRefAccess<DialogCommands, ToolTest>("toolTest");
            s_interacter = AccessTools.FieldRefAccess<DialogCommands, Interacter>("interacter");
            s_mouseNeeded = AccessTools.FieldRefAccess<GameStateManager, TicketLock>("mouseNeeded");
            s_heldLocks = AccessTools.FieldRefAccess<TicketLock, List<TicketLock.Ticket>>("heldLocks");

            var start = AccessTools.Method(typeof(DialogCommands), nameof(DialogCommands.StartDialogue));
            harmony.Patch(start,
                prefix: new HarmonyMethod(typeof(DialogueVR).GetMethod(nameof(StartDialogue_Prefix), Any)),
                postfix: new HarmonyMethod(typeof(DialogueVR).GetMethod(nameof(StartDialogue_Postfix), Any)));
            harmony.Patch(AccessTools.Method(typeof(DialogCommands), "SetAutoAdvanceOff"),
                postfix: new HarmonyMethod(typeof(DialogueVR).GetMethod(nameof(SetAutoAdvanceOff_Postfix), Any)));
            Log.Msg("[DialogueVR] installed");
        }

        // The flat game steals the player's controls for a dialogue; in VR the player keeps them.
        static void StartDialogue_Prefix(ref bool disablePlayerInput)
        {
            if (Active) disablePlayerInput = false;
        }

        static void StartDialogue_Postfix()
        {
            if (!Active) return;
            var commands = s_instance();
            if (commands == null) return;
            s_toolTest(commands)?.EnableInteraction();
            s_interacter(commands)?.EnableInteraction();
            TimedLines(commands);
        }

        // Called by StartDialogue and EndBark to switch lines back to waiting for input; in VR they stay timed.
        static void SetAutoAdvanceOff_Postfix(DialogCommands __instance)
        {
            if (Active) TimedLines(__instance);
        }

        // The game's own bark setup: the presenter advances by itself and the advancer no longer waits for a button.
        static void TimedLines(DialogCommands commands)
        {
            var presenter = s_linePresenter(commands);
            if (presenter != null)
            {
                presenter.autoAdvance = true;
                presenter.autoAdvanceDelay = Mathf.Max(0.5f, LineSeconds);
            }
            var advancer = s_lineAdvancer(commands);
            if (advancer != null) advancer.enabled = false;
        }

        /// <summary>
        /// The laser pointer is wanted: dialogue answers are on screen (whoever started the dialogue), or
        /// something other than dialogue (a menu) holds the unlocked cursor. Plain dialogue lines no longer bring it up.
        /// </summary>
        public static bool PointerWanted()
        {
            if (Active && !SexScene.OwnsPrompt && OptionsShowing()) return true;
            var gsm = GameStateManager.Instance;
            if (gsm == null || s_mouseNeeded == null) return false;
            var locks = s_mouseNeeded(gsm);
            if (locks == null || !locks.GetLocked(TicketLock.LockFlags.MouseUnlock)) return false;
            if (!Active || s_heldLocks == null) return true;
            var held = s_heldLocks(locks);
            if (held == null) return true;
            foreach (var ticket in held)
                if (ticket != null && (ticket.lockFlags & TicketLock.LockFlags.MouseUnlock) != 0 && !(ticket.Owner is DialogCommands))
                    return true;
            return false;
        }

        /// <summary>
        /// Once a frame (from the laser): a grip or trigger press skips the line on screen. Answers wait for the laser, except
        /// the sex scenes' own questions (SexScene answers those out of sight).
        /// </summary>
        public static void Tick()
        {
            bool skip = SkipPressed();
            if (!Active) return;
            if (skip && !OptionsShowing()) SkipLine();
        }

        // Either grip skips, and so does a trigger on a hand free to use it: not the hand using a tool, and not the
        // pointer's hand while the laser is on. Never in a sex scene, where the hands belong to the dragons.
        static bool SkipPressed()
        {
            bool leftTrigger = VRInput.Left.Valid && VRInput.Left.TriggerPressed;
            bool rightTrigger = VRInput.Right.Valid && VRInput.Right.TriggerPressed;
            bool leftGrip = VRInput.Left.Valid && VRInput.Left.GripPressed;
            bool rightGrip = VRInput.Right.Valid && VRInput.Right.GripPressed;
            bool leftDown = leftTrigger && !s_leftTriggerWas;
            bool rightDown = rightTrigger && !s_rightTriggerWas;
            bool gripDown = (leftGrip && !s_leftGripWas) || (rightGrip && !s_rightGripWas);
            s_leftTriggerWas = leftTrigger;
            s_rightTriggerWas = rightTrigger;
            s_leftGripWas = leftGrip;
            s_rightGripWas = rightGrip;
            if (!SkipLines || SexScene.InSexScene) return false;
            if (VRHands.HoldingTool)
            {
                if (VRHands.ToolHandIsRight) rightDown = false;
                else leftDown = false;
            }
            if (VRLaser.PointerActive)
            {
                if (VRLaser.PointerHandIsRight) rightDown = false;
                else leftDown = false;
            }
            return leftDown || rightDown || gripDown;
        }

        // A line still typing shows in full; a shown line gives way to the next.
        static void SkipLine()
        {
            var commands = s_instance != null ? s_instance() : null;
            if (commands == null) return;
            var runner = s_runner(commands);
            var presenter = s_linePresenter(commands);
            if (runner == null || presenter == null || !runner.IsDialogueRunning) return;
            // Nothing to skip while no line is up (or one is still fading in or out).
            if (presenter.canvasGroup == null || presenter.canvasGroup.alpha < 0.5f) return;
            var text = presenter.lineText;
            if (text != null && text.textInfo != null && text.maxVisibleCharacters < text.textInfo.characterCount) runner.RequestHurryUpLine();
            else runner.RequestNextLine();
        }

        static bool OptionsShowing()
        {
            if (s_options == null)
            {
                if (Time.unscaledTime - s_optionsLookup < 1f) return false;
                s_optionsLookup = Time.unscaledTime;
                s_options = UnityEngine.Object.FindFirstObjectByType<OptionsPresenter>();
                if (s_options == null) return false;
            }
            var group = s_options.GetComponent<CanvasGroup>();
            return group != null ? group.interactable && group.alpha > 0.01f : s_options.isActiveAndEnabled;
        }
    }
}
