using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRUsages = UnityEngine.XR.CommonUsages;

namespace DnWVR.VR
{
    /// <summary>
    /// The two tracked controllers as one ordinary gamepad, so every &lt;Gamepad&gt; binding in the game's action assets
    /// works unchanged and AutoInputSwitcher shows Xbox glyphs, which match Touch controllers.
    /// </summary>
    [InputControlLayout(stateType = typeof(GamepadState), displayName = "DnW VR Gamepad", canRunInBackground = true)]
    public class DnWVRGamepad : Gamepad { }

    /// <summary>
    /// Feeds the XR controllers into the virtual gamepad and handles mod-side input (turning, grip claims, pause).
    /// Left Y belongs to the radial menu; the left menu button is never fed, as on Touch it is the SteamVR system button.
    /// </summary>
    public static class VRInput
    {
        public const string InterfaceName = "DnWVR";

        public static DnWVRGamepad Pad { get; private set; }
        public static bool SmoothTurn = false;
        public static float SnapTurnDegrees = 45f;
        public static float SmoothTurnDegPerSec = 120f;
        public static float TriggerThreshold = 0.5f;
        public static float GripThreshold = 0.5f;

        /// <summary>Raw controller state for mod-side consumers (radial menu, hands).</summary>
        public struct HandState
        {
            public bool Valid;
            public Vector2 Stick;
            public float Trigger, Grip;
            public bool TriggerPressed, GripPressed, Primary, Secondary, Menu, StickClick;
            public bool Tracked;
        }

        public static HandState Left, Right;

        /// <summary>Set by the radial menu while open: the game gets no controller input and turning stops.</summary>
        public static bool SuppressGameInput;

        static bool s_registered;
        static int s_snapArmed = 1; // 1 = ready, 0 = waiting for stick to return to centre
        static bool s_pausePress;   // one frame of Start, set when X is pressed and the game is not already paused
        static int s_pauseCheck;

        static AccessTools.FieldRef<MenuManager> s_menuManager;
        static AccessTools.FieldRef<MenuManager, Menu> s_currentMenu;

        public static void Initialize()
        {
            try
            {
                s_menuManager = AccessTools.StaticFieldRefAccess<MenuManager>(AccessTools.Field(typeof(MenuManager), "_instance"));
                s_currentMenu = AccessTools.FieldRefAccess<MenuManager, Menu>("_currentMenu");
            }
            catch (Exception e)
            {
                Log.Warning("[VRInput] MenuManager state not bound (X will not resume from the pause menu): " + e.Message);
            }
            if (!s_registered)
            {
                InputSystem.RegisterLayout<DnWVRGamepad>(matches: new InputDeviceMatcher().WithInterface(InterfaceName));
                InputSystem.onBeforeUpdate += OnBeforeInputUpdate;
                s_registered = true;
            }
        }

        /// <summary>
        /// Makes the game's own actions notice the virtual pad. An action resolves which controls it listens to when its
        /// map is enabled, and the game built and enabled its maps long before VR started, so the pad - added only once
        /// OpenXR is up - is missing from every one of them: the stick is fed to a device nothing listens to. Turning a
        /// map off and on again is what asks the Input System to look at the devices afresh.
        /// </summary>
        public static void RebindGameActions()
        {
            if (Pad == null) return;
            try
            {
                var asset = MenuManager.actions != null ? MenuManager.actions.asset : null;
                if (asset == null) return;
                foreach (var map in asset.actionMaps)
                {
                    if (!map.enabled) continue;
                    map.Disable();
                    map.Enable();
                }
                if (!Listens(asset)) BindPadByName(asset);
                if (Listens(asset)) Log.Msg("[VRInput] the game's actions now listen to the controllers");
                else Log.Warning("[VRInput] the game's Move action still does not listen to the virtual pad");
            }
            catch (Exception e)
            {
                Log.Warning("[VRInput] could not refresh the game's actions: " + e.Message);
            }
        }

        /// <summary>Whether the game's movement action has the virtual pad among the controls it listens to.</summary>
        static bool Listens(InputActionAsset asset)
        {
            var move = asset.FindAction("Player/Move");
            if (move == null) return false;
            foreach (var control in move.controls)
                if (control.device == Pad) return true;
            return false;
        }

        /// <summary>
        /// Binds the pad to the game's actions by its own layout name. The game binds the generic "&lt;Gamepad&gt;", which ought
        /// to cover any gamepad including this one, and for reasons that belong to the Input System it does not - so every
        /// gamepad binding is copied onto "&lt;DnWVRGamepad&gt;" as well. A binding can only be added while its map is off.
        /// </summary>
        static void BindPadByName(InputActionAsset asset)
        {
            const string generic = "<Gamepad>";
            string own = "<" + Pad.layout + ">";
            foreach (var map in asset.actionMaps)
            {
                var wanted = new List<KeyValuePair<InputAction, string>>();
                foreach (var action in map.actions)
                {
                    bool already = false;
                    foreach (var binding in action.bindings)
                        if (binding.effectivePath != null && binding.effectivePath.StartsWith(own)) { already = true; break; }
                    if (already) continue;
                    foreach (var binding in action.bindings)
                    {
                        if (binding.isComposite || binding.effectivePath == null) continue;
                        if (!binding.effectivePath.StartsWith(generic)) continue;
                        wanted.Add(new KeyValuePair<InputAction, string>(action, own + binding.effectivePath.Substring(generic.Length)));
                    }
                }
                if (wanted.Count == 0) continue;
                bool was = map.enabled;
                map.Disable();
                foreach (var pair in wanted)
                    pair.Key.AddBinding(pair.Value);
                if (was) map.Enable();
                Log.Msg($"[VRInput] {map.name}: {wanted.Count} controller bindings pointed at the pad by name");
            }
        }

        public static void AddDevice()
        {
            if (Pad != null) return;
            try
            {
                // The desktop window is rarely focused while a headset is worn: keep input and the
                // player loop alive regardless of focus.
                Application.runInBackground = true;
                try { InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus; }
                catch (Exception e) { Log.Warning("backgroundBehavior not applied: " + e.Message); }
                Pad = (DnWVRGamepad)InputSystem.AddDevice(new InputDeviceDescription
                {
                    interfaceName = InterfaceName,
                    product = "Quest Touch (VR)",
                    manufacturer = "DnWVR",
                });
                Log.Msg($"Virtual gamepad added: {Pad.displayName} (id {Pad.deviceId})");
            }
            catch (Exception e)
            {
                Log.Error("Failed to add virtual gamepad: " + e);
            }
        }

        public static void RemoveDevice()
        {
            if (Pad == null) return;
            try { InputSystem.RemoveDevice(Pad); } catch { }
            Pad = null;
        }

        static bool s_leftGripWas, s_rightGripWas;
        // A grip press the item laser claimed stays off Player/Plap until released; the grab itself runs in ItemLaser.Update.
        static bool s_leftGripConsumed, s_rightGripConsumed, s_pendingGrabL, s_pendingGrabR;
        static bool s_xWas, s_menuWas;

        /// <summary>
        /// The game takes player input: its Player action map is enabled (menus and StartDialogue dialogues disable it) and
        /// no between-level intermission runs. The flat game ignores Plap and interactions otherwise.
        /// </summary>
        public static bool GamePlayerInputEnabled
        {
            get
            {
                try { return !IntermissionFade.isRunning && MenuManager.actions.Player.enabled; }
                catch { return true; }
            }
        }

        /// <summary>Did a grip press on this controller go to the item laser since the last call? Reading clears it.</summary>
        public static bool TakePendingGrab(bool right)
        {
            bool pending = right ? s_pendingGrabR : s_pendingGrabL;
            if (right) s_pendingGrabR = false;
            else s_pendingGrabL = false;
            return pending;
        }

        static void OnBeforeInputUpdate()
        {
            if (!XR.XRBootstrap.IsRunning) return;
            // With XR devices present the Input System also runs a BeforeRender update each frame;
            // integrate turning and edge-detect buttons only once, in the Dynamic (frame) update.
            var type = InputState.currentUpdateType;
            if (type != InputUpdateType.Dynamic && type != InputUpdateType.Manual) return;
            ReadHand(XRNode.LeftHand, ref Left);
            ReadHand(XRNode.RightHand, ref Right);
            // The hand that closes its grip reaches for tools and interactables. A press whose item laser has a target
            // (from last frame) is claimed before the gamepad state is queued, or Player/Plap would fire this frame.
            bool lDown = Left.GripPressed && !s_leftGripWas, rDown = Right.GripPressed && !s_rightGripWas;
            if (lDown)
            {
                VRHands.NotifyGrip(false);
                if (ItemLaser.WantsGrip(false)) { s_leftGripConsumed = true; s_pendingGrabL = true; }
            }
            if (rDown)
            {
                VRHands.NotifyGrip(true);
                if (ItemLaser.WantsGrip(true)) { s_rightGripConsumed = true; s_pendingGrabR = true; }
            }
            if (!Left.GripPressed) s_leftGripConsumed = false;
            if (!Right.GripPressed) s_rightGripConsumed = false;
            s_leftGripWas = Left.GripPressed;
            s_rightGripWas = Right.GripPressed;

            // X pauses through the gamepad's Start; in the pause menu it resumes instead. The press that resumed is
            // kept off Start until X is released (Menu.Show also re-registers the pause action a frame late).
            bool xDown = Left.Valid && Left.Primary && !s_xWas;
            // A press made while the radial menu is open is the menu's own, and one made in the pause menu resumes
            // instead: neither reaches the game as a pause.
            if (xDown && !SuppressGameInput && !TryResumeFromPause())
            {
                s_pausePress = true;
                s_pauseCheck = 3;
                Log.Msg($"[VRInput] X -> pause (menu now: {CurrentMenuName()})");
            }
            // Diagnostic while the pause button is under investigation: says whether the game took the press.
            if (s_pauseCheck > 0 && --s_pauseCheck == 0)
                Log.Msg($"[VRInput] two frames after the press the menu is {CurrentMenuName()}");
            s_xWas = Left.Primary;
            // Diagnostic: the left menu button doubles as the SteamVR system button and is not fed to the game.
            if (Left.Menu && !s_menuWas) Log.Msg("[VRInput] left menu/system button seen by app (ignored)");
            s_menuWas = Left.Menu;

            HandleTurn();
            if (Pad != null) FeedGamepad();
        }

        static void ReadHand(XRNode node, ref HandState h)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            h.Valid = dev.isValid;
            if (!dev.isValid) { h = default; return; }
            dev.TryGetFeatureValue(XRUsages.primary2DAxis, out h.Stick);
            dev.TryGetFeatureValue(XRUsages.trigger, out h.Trigger);
            dev.TryGetFeatureValue(XRUsages.grip, out h.Grip);
            bool b;
            h.TriggerPressed = (dev.TryGetFeatureValue(XRUsages.triggerButton, out b) && b) || h.Trigger > TriggerThreshold;
            h.GripPressed = (dev.TryGetFeatureValue(XRUsages.gripButton, out b) && b) || h.Grip > GripThreshold;
            dev.TryGetFeatureValue(XRUsages.primaryButton, out h.Primary);
            dev.TryGetFeatureValue(XRUsages.secondaryButton, out h.Secondary);
            dev.TryGetFeatureValue(XRUsages.menuButton, out h.Menu);
            dev.TryGetFeatureValue(XRUsages.primary2DAxisClick, out h.StickClick);
            dev.TryGetFeatureValue(XRUsages.isTracked, out h.Tracked);
        }

        static void HandleTurn()
        {
            if (SuppressGameInput || !Right.Valid) { return; }
            float x = Right.Stick.x;
            if (SmoothTurn)
            {
                if (Mathf.Abs(x) > 0.2f)
                    VRRig.Turn(x * SmoothTurnDegPerSec * Time.unscaledDeltaTime, continuous: true);
                return;
            }
            if (s_snapArmed == 1 && Mathf.Abs(x) > 0.7f)
            {
                VRRig.Turn(Mathf.Sign(x) * SnapTurnDegrees);
                s_snapArmed = 0;
            }
            else if (s_snapArmed == 0 && Mathf.Abs(x) < 0.3f)
            {
                s_snapArmed = 1;
            }
        }

        static void FeedGamepad()
        {
            var s = new GamepadState();
            if (!SuppressGameInput)
            {
                s.leftStick = Left.Valid ? Left.Stick : Vector2.zero;
                // Never fed: pitch comes from the HMD and the rig does the turning.
                s.rightStick = Vector2.zero;
                uint buttons = 0;
                bool toolTrigger = VRHands.ToolHandIsRight ? Right.TriggerPressed : Left.TriggerPressed;
                if (toolTrigger) buttons |= 1u << (int)GamepadButton.RightShoulder;          // Attack = use tool (tool hand's trigger)
                // Plap = dialogue advance / interact (slaps and strokes are by touch); a press the item laser took is not Plap.
                if ((Left.GripPressed && !s_leftGripConsumed) || (Right.GripPressed && !s_rightGripConsumed)) buttons |= 1u << (int)GamepadButton.LeftShoulder;
                if (Right.Primary && !SexScene.InSexScene) buttons |= 1u << (int)GamepadButton.South; // A: Jump / Submit (finishes a sex scene instead: FinishHold)
                if (Right.Secondary) buttons |= 1u << (int)GamepadButton.East;              // B: Crouch / Cancel
                    // The game's pause acts on the button being down rather than on the press, so a held X would open and
                // shut the menu every frame: it gets a single frame's press.
                if (s_pausePress) { buttons |= 1u << (int)GamepadButton.Start; s_pausePress = false; }
                if (Left.StickClick) buttons |= 1u << (int)GamepadButton.LeftStick;
                s.buttons = buttons;
            }
            InputSystem.QueueStateEvent(Pad, s);
        }

        // Sends the "Resume" intent the pause menu's button sends. Checked by component type, as the StartScene main
        // menu (MenuMain) must not receive it.
        /// <summary>The menu the game believes is open, for the log.</summary>
        static string CurrentMenuName()
        {
            try
            {
                if (s_menuManager == null || s_currentMenu == null) return "unreadable";
                var manager = s_menuManager();
                var menu = manager != null ? s_currentMenu(manager) : null;
                return menu != null ? menu.GetType().Name : "none";
            }
            catch
            {
                return "unreadable";
            }
        }

        static bool TryResumeFromPause()
        {
            if (s_menuManager == null || s_currentMenu == null) return false;
            try
            {
                var manager = s_menuManager();
                var current = manager != null ? s_currentMenu(manager) : null;
                if (!(current is MenuPause)) return false;
                MenuManager.TriggerEvent(new MenuEventUserIntent("Resume"));
                LogI("[VRInput] X -> resume");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[VRInput] resume failed: " + e.Message);
                return false;
            }
        }

        static void LogI(string msg)
        {
            if (DnWVRMod.DebugInteractionLog) Log.Msg(msg);
        }

        /// <summary>
        /// Blanks the stock &lt;XRController&gt;/&lt;XRHMD&gt; bindings, which would fire on either hand once OpenXR devices
        /// exist, so only the virtual gamepad speaks for the controllers.
        /// </summary>
        public static void DisableStockXRBindings(InputActionAsset asset, string label)
        {
            if (asset == null) return;
            int count = 0;
            try
            {
                // Binding re-resolution is lazy, so no DeferBindingResolution scope is needed.
                {
                    foreach (var map in asset.actionMaps)
                    {
                        var bindings = map.bindings;
                        for (int i = 0; i < bindings.Count; i++)
                        {
                            var b = bindings[i];
                            if (string.IsNullOrEmpty(b.path)) continue;
                            if (!b.path.Contains("<XRController>") && !b.path.Contains("<XRHMD>")) continue;
                            if (!string.IsNullOrEmpty(b.overridePath) && b.overridePath.Length == 0) continue;
                            map.ApplyBindingOverride(i, new InputBinding { overridePath = "" });
                            count++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"DisableStockXRBindings({label}) failed: {e.Message}");
            }
            if (count > 0) Log.Msg($"Disabled {count} stock XR bindings in {label}");
        }

        public static void DisableStockXRBindingsEverywhere()
        {
            var seen = new HashSet<InputActionAsset>();
            try
            {
                var runtime = MenuManager.actions.asset;
                if (runtime != null && seen.Add(runtime)) DisableStockXRBindings(runtime, "MenuManager.actions");
            }
            catch (Exception e) { Log.Warning("MenuManager.actions unavailable: " + e.Message); }
            foreach (var pi in UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (pi.actions != null && seen.Add(pi.actions)) DisableStockXRBindings(pi.actions, "PlayerInput:" + pi.name);
                pi.neverAutoSwitchControlSchemes = true;
            }
            // The serialized InputSystem_Actions asset (UI input module, MenuIntents, hints) and the package's
            // DefaultInputActions (EndScene / sex-scene event systems) are not reachable through any component.
            try
            {
                foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
                {
                    if (asset == null || !seen.Add(asset)) continue;
                    DisableStockXRBindings(asset, "asset:" + asset.name);
                }
            }
            catch (Exception e) { Log.Warning("asset sweep failed: " + e.Message); }
        }
    }
}
