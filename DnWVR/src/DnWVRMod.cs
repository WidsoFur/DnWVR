using System;
using System.Collections;
using DnWVR.Diag;
using DnWVR.VR;
using DnWVR.XR;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(DnWVR.DnWVRMod), "DnWVR", "0.1.0", "WidsoFur")]
[assembly: MelonGame("Gator Dragon Games", "DragNWash")]

namespace DnWVR
{
    /// <summary>MelonLoader entry point: preferences, patch setup, OpenXR start/stop, per-scene setup and debug hotkeys.</summary>
    public class DnWVRMod : MelonMod
    {
        public static DnWVRMod Instance { get; private set; }
        public static MelonLogger.Instance Log => Instance.LoggerInstance;

        public static MelonPreferences_Category Prefs;
        public static MelonPreferences_Entry<bool> PrefEnableVR;
        public static MelonPreferences_Entry<bool> PrefSinglePassInstanced;
        public static MelonPreferences_Entry<bool> PrefDumpOnSceneLoad;
        public static MelonPreferences_Entry<int> PrefStartupRetries;
        public static MelonPreferences_Entry<bool> PrefSmoothTurn;
        public static MelonPreferences_Entry<float> PrefSnapTurnDegrees, PrefSmoothTurnSpeed;
        public static MelonPreferences_Entry<bool> PrefDebugAutoStartLevel;
        public static MelonPreferences_Entry<string> PrefPlapperOffsetPos, PrefPlapperOffsetEuler,
            PrefToolOffsetPos, PrefToolOffsetEuler, PrefSprayerOffsetPos, PrefLadderOffsetPos;
        public static MelonPreferences_Entry<bool> PrefPhysicalHands, PrefRestOnPenis, PrefRoomScaleBody;
        public static MelonPreferences_Entry<int> PrefPlayerHeightCm;
        public static MelonPreferences_Entry<bool> PrefPlayerDuckWithHead;
        public static MelonPreferences_Entry<float> PrefPlayerRadius, PrefPlayerPushSpeed, PrefPlayerStepUp,
            PrefPlayerStepRise, PrefPlayerSpringDamping, PrefPlayerSpringLift;
        public static MelonPreferences_Entry<bool> PrefSecondHand, PrefMenuHands;
        public static MelonPreferences_Entry<bool> PrefDebugRadialMenu, PrefDebugSceneMenu;
        public static MelonPreferences_Entry<bool> PrefDisableFluidFeature;
        public static MelonPreferences_Entry<bool> PrefDialogueCameraZoom, PrefDialogueAutoAdvance,
            PrefDialogueTriggerSkip, PrefUIOnTop;
        public static MelonPreferences_Entry<float> PrefSpongeReach;
        public static MelonPreferences_Entry<float> PrefDialogueLineSeconds, PrefSexSceneBuildUpSeconds,
            PrefSexSceneFinishHoldSeconds, PrefSexSceneWalkSpeed;
        public static MelonPreferences_Entry<bool> PrefInteractiveSexScenes, PrefSexSceneWalking, PrefDesktopView;
        public static MelonPreferences_Entry<float> PrefDesktopViewFov, PrefDesktopViewSmoothing;
        public static MelonPreferences_Entry<int> PrefDesktopViewHeight;
        public static MelonPreferences_Entry<bool> PrefCutsceneFollowAnimation;
        public static MelonPreferences_Entry<bool> PrefAlignYawOnCameraCut;
        public static MelonPreferences_Entry<bool> PrefLateLatchHead;
        public static MelonPreferences_Entry<bool> PrefDebugInteractionLog;
        public static MelonPreferences_Entry<bool> PrefStrokeRequiresMotion, PrefTouchDuringCutscenes,
            PrefTouchWorldSurfaces, PrefSpongeNeedsTrigger;
        public static MelonPreferences_Entry<float> PrefSlapSpeed, PrefHandPressSpeed;
        public static MelonPreferences_Entry<bool> PrefItemLaser, PrefItemLaserHandTargets;
        public static MelonPreferences_Entry<float> PrefItemLaserConeDeg;

        /// <summary>Log cutscene action changes and controller/interaction decisions (copied from PrefDebugInteractionLog).</summary>
        public static bool DebugInteractionLog = false;

        bool _lateLatchHooked;
        int _cameraSweep;

        public override void OnInitializeMelon()
        {
            Instance = this;
            CreatePreferences();
            ApplyTunablePrefs();

            LoggerInstance.Msg($"DnWVR init. Unity {Application.unityVersion}, gfx {SystemInfo.graphicsDeviceType}, " +
                               $"screen {Screen.width}x{Screen.height}");
            LoggerInstance.Msg("Hotkeys: F5 head tracking, F6 reload prefs, F7 debug start level, F8 recenter, " +
                               "F9 dump diagnostics, F10 XR descriptors, F11 start/stop OpenXR");

            InstallModules();

            if (PrefEnableVR.Value)
                MelonCoroutines.Start(StartXRWhenReady());
        }

        /// <summary>Every setting in MelonPreferences.cfg, in the order it is written there.</summary>
        void CreatePreferences()
        {
            Prefs = MelonPreferences.CreateCategory("DnWVR", "Drag'n Wash VR");
            PrefEnableVR = Prefs.CreateEntry("EnableVR", true, "Start OpenXR when the game boots");
            PrefSinglePassInstanced = Prefs.CreateEntry("SinglePassInstanced", false,
                "Use single-pass instanced stereo (faster) instead of multi-pass (more compatible)");
            PrefDumpOnSceneLoad = Prefs.CreateEntry("DumpOnSceneLoad", false, "Dump scene diagnostics after each scene load");
            PrefStartupRetries = Prefs.CreateEntry("StartupRetries", 12,
                "How many times (5 s apart) to retry OpenXR init at startup while the headset is not ready");
            PrefSmoothTurn = Prefs.CreateEntry("SmoothTurn", false,
                "Turn smoothly while the right stick is held instead of snapping by a step. The VR section of the " +
                "options screen sets this too, and the row under it is whichever of the next two settings applies");
            PrefSnapTurnDegrees = Prefs.CreateEntry("SnapTurnDegrees", 45f,
                "Degrees turned by one flick of the right stick when turning snaps");
            PrefSmoothTurnSpeed = Prefs.CreateEntry("SmoothTurnSpeed", 120f,
                "Degrees a second the world turns while the right stick is held, when turning is smooth. Lower is " +
                "gentler on a stomach new to VR");
            PrefDebugAutoStartLevel = Prefs.CreateEntry("DebugAutoStartLevel", false,
                "Debug: automatically start a new game (first empty slot) from the main menu");
            PrefPlapperOffsetPos = Prefs.CreateEntry("PlapperOffsetPos", "0,0,0",
                "Hand mesh offset added to the built-in pose (fingers along the aim ray, thumb up, palm toward the other hand), " +
                "controller axes, metres x,y,z; authored for the left hand, mirrored on the right");
            PrefPlapperOffsetEuler = Prefs.CreateEntry("PlapperOffsetEuler", "0,0,0",
                "Hand mesh rotation added to the built-in pose (fingers along the aim ray, thumb up, palm toward the other " +
                "hand), controller axes, degrees x,y,z; mirrored on the right");
            PrefToolOffsetPos = Prefs.CreateEntry("ToolOffsetPos", "0,0,0",
                "Tool anchor offset added to the tool controller's aim pose (where it points), metres x,y,z in that frame; " +
                "authored for the right hand, mirrored on the left");
            PrefToolOffsetEuler = Prefs.CreateEntry("ToolOffsetEuler", "0,0,0",
                "Tool anchor rotation added to the tool controller's aim pose, degrees x,y,z; mirrored on the left");
            PrefSprayerOffsetPos = Prefs.CreateEntry("SprayerOffsetPos", "0,0,-0.2",
                "Where the pressure washer sits on the tool controller, on top of ToolOffsetPos, metres x,y,z along the aim pose " +
                "(negative z pulls it back into your hand)");
            PrefLadderOffsetPos = Prefs.CreateEntry("LadderOffsetPos", "0,-1,0",
                "Where the ladder sits on the tool controller, on top of ToolOffsetPos, metres x,y,z along the aim pose " +
                "(negative y brings its middle down onto your hand)");
            PrefPhysicalHands = Prefs.CreateEntry("PhysicalHands", true,
                "Hands stop at walls, props and the dragon (a controller inside them leaves the hand on the surface, and a paw " +
                "lays its palm onto it, like the flat game's hand); a held tool is kept out at its grip only. Off = hands go " +
                "wherever the controllers are");
            PrefRestOnPenis = Prefs.CreateEntry("HandsRestOnPenis", true,
                "Hands rest on the dragon's penis and slide along it, following its bend and motion. Off = they pass through it; " +
                "touching it still strokes");
            PrefRoomScaleBody = Prefs.CreateEntry("RoomScaleBody", true,
                "Your body (its collider and feet) follows you when you walk around your room, stopping at walls and the dragon. " +
                "Off = it stays where the stick puts it");
            PrefPlayerHeightCm = Prefs.CreateEntry("PlayerHeightCm", VRRig.GameHeightCm,
                "How high your eyes are over the floor, in centimetres - the number the VR section of the options " +
                "screen shows. The game stands you at 180, in the wash and in the sex scenes alike; give it your own " +
                "and everything is measured from there. The Calibrate button there reads it off the headset");
            PrefPlayerDuckWithHead = Prefs.CreateEntry("PlayerDuckWithHead", true,
                "Duck in your room and your body ducks with you: the top of your collider follows your head, so you fit " +
                "under what you have physically ducked under. Off = the body keeps the height the game gives it and only " +
                "the crouch button lowers it");
            PrefPlayerRadius = Prefs.CreateEntry("PlayerRadius", 0.2f,
                "Radius (m) of your body's collider in VR, small enough to stand right next to the dragon (the game's is 0.5; 0 " +
                "= the game's)");
            PrefPlayerStepUp = Prefs.CreateEntry("PlayerStepUp", 0.55f,
                "How high (m) a surface may be above your feet and still be stepped onto. Higher ground (crates, the mount " +
                "frame, the tool bench) is solid instead of a ramp; the step stool is still climbed step by step, and dragons " +
                "are climbed whatever their height. 2 or more switches the rule off");
            PrefPlayerStepRise = Prefs.CreateEntry("PlayerStepRise", 1.2f,
                "How fast (m/s) the ground under you may rise when you step up, so a step lifts you instead of launching you");
            PrefPlayerSpringLift = Prefs.CreateEntry("PlayerSpringLift", 0.5f,
                "Fastest (m/s) the spring under you may lift you in the moment after a fall, which is what turns a landing into " +
                "two or three bounces. Only then: stepping onto a stool is the same spring and stays brisk. Jumping is " +
                "unaffected. 0 = uncapped, as the game has it");
            PrefPlayerSpringDamping = Prefs.CreateEntry("PlayerSpringDamping", 0f,
                "Damping of that spring (0 = the game's 0.5). It works on how fast the ground under you moves rather than how " +
                "fast you do, so raising it also amplifies every shift of the floor into a shove - PlayerSpringLift is the " +
                "gentler cure");
            PrefPlayerPushSpeed = Prefs.CreateEntry("PlayerPushSpeed", 2f,
                "Fastest (m/s) the dragon and other moving things push your body out of them (the game's is 10; 0 = the game's)");
            PrefSecondHand = Prefs.CreateEntry("SecondHand", true,
                "Show a mirrored copy of the game's hand on the tool controller while it holds nothing (a held tool hides it)");
            PrefMenuHands = Prefs.CreateEntry("MenuHands", false,
                "Main menu and credits: simple hands on both controllers that poke the menu, with the menu within arm's reach. " +
                "Off = no hands there, the laser does the menus");
            PrefSlapSpeed = Prefs.CreateEntry("SlapSpeed", 1.0f,
                "Hand speed (m/s, relative to your body) that turns a touch into a slap");
            PrefHandPressSpeed = Prefs.CreateEntry("HandPressSpeed", 0.4f,
                "Push speed (m/s toward it) that presses a button, the phone or the water tap without a slap");
            PrefStrokeRequiresMotion = Prefs.CreateEntry("StrokeRequiresMotion", true,
                "A touching hand strokes only while it moves along the surface. Off = any contact strokes, like the flat game");
            PrefTouchDuringCutscenes = Prefs.CreateEntry("TouchDuringCutscenes", true,
                "Hands and sponge keep touching during cutscenes and in-level sex acts (dialogue always stops them, like the " +
                "flat game)");
            PrefInteractiveSexScenes = Prefs.CreateEntry("InteractiveSexScenes", true,
                "Sex scenes in VR: your hands on the dragon (resting on it or stroking its penis) set the pace and build its " +
                "pleasure, which brings its lines and its climax instead of the game's timer and keep-going questions; letting " +
                "go nearly stops the scene while the pleasure slowly fades. Hold A to finish sooner; in Ryan + Conrad you watch " +
                "and A ends the scene. Off = the game's automatic pace, timing and questions");
            PrefSexSceneBuildUpSeconds = Prefs.CreateEntry("SexSceneBuildUpSeconds", 60f,
                "Seconds of steady, lively hand motion on the dragon from the start of an interactive sex scene to its climax");
            PrefSexSceneFinishHoldSeconds = Prefs.CreateEntry("SexSceneFinishHoldSeconds", 3f,
                "Seconds to hold A in a sex scene to finish it");
            PrefSexSceneWalking = Prefs.CreateEntry("SexSceneWalking", true,
                "Walk around in the sex scenes with the left stick (not in Ryan's scene, where you lie under him); in " +
                "Alexander's scene you stand from the start");
            PrefSexSceneWalkSpeed = Prefs.CreateEntry("SexSceneWalkSpeed", 1.5f,
                "Walking speed in the sex scenes, m/s at full stick");
            PrefTouchWorldSurfaces = Prefs.CreateEntry("TouchWorldSurfaces", false,
                "Floors, walls and props give touch sounds and slap effects too (never game events)");
            PrefSpongeReach = Prefs.CreateEntry("SpongeReach", 0.1f,
                "How far (m) from the sponge a surface still counts as scrubbed. Bigger reaches further for the same " +
                "arm, which is what a small room asks for; too big and the sponge washes what it is not touching");
            PrefSpongeNeedsTrigger = Prefs.CreateEntry("SpongeNeedsTrigger", true,
                "The sponge only scrubs while the trigger is held. Off = it scrubs whatever it touches");
            PrefItemLaser = Prefs.CreateEntry("ItemLaser", true,
                "Point a controller at an item or station (ray + dot) and grip to pick it up, put it back or use it. Off = grip " +
                "uses the game's invisible cone from the last-gripped hand");
            PrefItemLaserHandTargets = Prefs.CreateEntry("ItemLaserHandTargets", false,
                "The item laser also targets things the game operates by hand (phone, dismiss button, water tap)");
            PrefItemLaserConeDeg = Prefs.CreateEntry("ItemLaserConeDeg", 8f,
                "How many degrees the item laser may miss an item by and still pick it (items within 10 cm of the ray always " +
                "count)");
            PrefDebugRadialMenu = Prefs.CreateEntry("DebugRadialMenu", false,
                "Debug: hold left Y + stick to quick-equip any tool station in the level");
            PrefDebugSceneMenu = Prefs.CreateEntry("DebugSceneMenu", false,
                "Debug: left Y opens a panel that loads any sex scene or reloads washing through the game's own scene flow " +
                "(needs a loaded game; a scene played to its end counts as watched in that save). Y stays with DebugRadialMenu " +
                "while that is on");
            PrefDisableFluidFeature = Prefs.CreateEntry("DisableFluidFeature", false,
                "Troubleshooting: turn off the game's fluid renderer feature");
            PrefDialogueCameraZoom = Prefs.CreateEntry("DialogueCameraZoom", false,
                "Let dialogue (<<LookAt>> and the <<LookFromTo>> window intros) move the camera like the flat game. Off = the " +
                "camera never moves, fades or turns during dialogue; sex scenes are unaffected");
            PrefDialogueAutoAdvance = Prefs.CreateEntry("DialogueAutoAdvance", true,
                "Dialogue lines advance on their own and dialogue never takes your controls (moving, touching and tools keep " +
                "working); answers are always picked with the laser pointer. Off = like the flat game: a button advances lines " +
                "and dialogue locks your controls");
            PrefDialogueLineSeconds = Prefs.CreateEntry("DialogueLineSeconds", 3f,
                "Seconds a fully shown dialogue line stays before the next one (with DialogueAutoAdvance)");
            PrefDialogueTriggerSkip = Prefs.CreateEntry("DialogueTriggerSkip", true,
                "A grip or trigger press skips dialogue lines outside interactive sex scenes: a line shows in full, then the " +
                "next one comes (either grip skips; the hand holding a tool and the pointer's hand keep their trigger; answers " +
                "need the laser)");
            PrefUIOnTop = Prefs.CreateEntry("UIOnTop", true,
                "Menus, HUD and dialogue draw over the world instead of hiding behind the dragon or walls (applies to panels " +
                "created after a scene load)");
            PrefDesktopView = Prefs.CreateEntry("DesktopView", true,
                "The desktop window shows its own view from between your eyes, shaped like the window (one extra render). Off = " +
                "the cropped left eye");
            PrefDesktopViewFov = Prefs.CreateEntry("DesktopViewFov", 60f, "Vertical field of view of the desktop view, in degrees");
            PrefDesktopViewHeight = Prefs.CreateEntry("DesktopViewHeight", 1080,
                "Height in pixels of the desktop view's render, at most the window's (0 = the window's height; lower is cheaper)");
            PrefDesktopViewSmoothing = Prefs.CreateEntry("DesktopViewSmoothing", 0.1f,
                "Seconds over which the desktop view follows your head, for calmer recordings (0 = exactly)");
            PrefCutsceneFollowAnimation = Prefs.CreateEntry("CutsceneFollowAnimation", false,
                "In cutscenes/sex scenes ride the game's animated camera exactly. Off = stay anchored where it settles, head " +
                "fully free");
            PrefAlignYawOnCameraCut = Prefs.CreateEntry("AlignYawOnCameraCut", true,
                "When the game cuts to a new camera spot, turn you once (under a fade) to face where it looks");
            PrefLateLatchHead = Prefs.CreateEntry("LateLatchHead", true,
                "Re-sample the headset pose right before rendering (lowest latency; turn off only to compare)");
            PrefDebugInteractionLog = Prefs.CreateEntry("DebugInteractionLog", false,
                "Debug: log cutscene action changes, pause/resume buttons and hand/item interaction decisions");
        }

        /// <summary>Each module binds game members by name; a renamed member must not take the whole mod down.</summary>
        void InstallModules()
        {
            Guarded("CameraPatches", () => CameraPatches.Apply(HarmonyInstance, LoggerInstance));
            Guarded("InputPatches", () => InputPatches.Apply(HarmonyInstance, LoggerInstance));
            Guarded("HandPatches", () => HandPatches.Apply(HarmonyInstance, LoggerInstance));
            Guarded("RenderTweaks", () => RenderTweaks.ApplyPatches(HarmonyInstance, LoggerInstance));
            Guarded("VRUI", () => { VRUI.Initialize(LoggerInstance); VRUI.ApplyPatches(HarmonyInstance, LoggerInstance); });
            Guarded("DialogueVR", () => DialogueVR.Apply(HarmonyInstance, LoggerInstance));
            Guarded("VRHands", () => VRHands.Initialize(LoggerInstance));
            Guarded("PlayerBody", () => PlayerBody.Install(HarmonyInstance, LoggerInstance));
            Guarded("SexScene", () => SexScene.Install(HarmonyInstance, LoggerInstance));
            Guarded("VRInput", () => VRInput.Initialize(LoggerInstance));
            Guarded("DesktopMirror", () => DesktopMirror.Install(HarmonyInstance, LoggerInstance));
            Guarded("DesktopView", () => DesktopView.Install(LoggerInstance));
            Guarded("VirtualMouse", () => VirtualMouse.Initialize(LoggerInstance));
        }

        IEnumerator StartXRWhenReady()
        {
            // Graphics must be fully initialized before the loader is created.
            yield return null;
            yield return null;
            int attempts = Mathf.Max(1, PrefStartupRetries.Value);
            for (int i = 1; i <= attempts; i++)
            {
                if (StartXR()) yield break;
                if (i < attempts)
                {
                    LoggerInstance.Msg($"OpenXR not available yet (attempt {i}/{attempts}); retrying in 5 s. " +
                                       "Put the headset on / connect Virtual Desktop, or press F11 later.");
                    yield return new WaitForSecondsRealtime(5f);
                }
            }
        }

        bool StartXR()
        {
            if (!XRBootstrap.Start(LoggerInstance, PrefSinglePassInstanced.Value)) return false;
            if (!_lateLatchHooked)
            {
                Application.onBeforeRender += VRRig.LateLatch;
                _lateLatchHooked = true;
            }
            AttachStaticCameraFollower();
            XRRenderFixes.Apply(LoggerInstance, PrefSinglePassInstanced.Value);
            VRInput.DisableStockXRBindingsEverywhere();
            VRInput.AddDevice();
            InputPatches.ForceControllerGlyphs();
            VRHands.EnsureAnchors();
            VRHands.AttachGameHands();
            PlayerBody.Attach();
            SexScene.Attach();
            RadialToolMenu.Ensure(LoggerInstance);
            DebugSceneMenu.Ensure(LoggerInstance);
            VRFader.Ensure(LoggerInstance);
            FinishHold.Ensure(LoggerInstance);
            VRLaser.Ensure(LoggerInstance);
            ItemLaser.Ensure(LoggerInstance);
            MenuHands.Ensure(LoggerInstance);
            VRSettings.Ensure(LoggerInstance);
            RenderTweaks.ApplyToScene(LoggerInstance);
            VRUI.ConvertAll();
            MelonCoroutines.Start(StereoDiagnosticsAfterDelay());
            return true;
        }

        IEnumerator StereoDiagnosticsAfterDelay()
        {
            // The session becomes visible/focused a moment after StartSubsystems; report once it is.
            yield return new WaitForSecondsRealtime(3f);
            if (XRBootstrap.IsRunning) RenderTweaks.LogStereoState(LoggerInstance);
        }

        void StopXR()
        {
            if (_lateLatchHooked)
            {
                Application.onBeforeRender -= VRRig.LateLatch;
                _lateLatchHooked = false;
            }
            VRRig.RestoreNearClip();
            VRHands.DetachGameHands(); // also destroys the twin hand
            ItemLaser.ResetState();
            HandPatches.ResetTouchState();
            VRInput.RemoveDevice();
            PlayerBody.Detach();
            SexScene.Detach();
            DesktopView.Disable();
            XRBootstrap.Stop(LoggerInstance);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            LoggerInstance.Msg($"Scene loaded: {sceneName} (#{buildIndex})");
            VRFader.Flash(0.8f); // hide the first frames of a new scene while the rig re-aligns
            VRRig.ResetForNewScene();
            VRHands.OnSceneChanged();
            PlayerBody.OnSceneChanged();
            SexScene.OnSceneChanged();
            SceneWalk.Reset();
            ItemLaser.ResetState();
            HandPatches.ResetTouchState();
            VRUI.OnSceneChanged();
            MelonCoroutines.Start(AfterSceneLoad(sceneName));
        }

        IEnumerator AfterSceneLoad(string sceneName)
        {
            yield return null;
            AdoptSceneCamera();
            AttachStaticCameraFollower();
            if (PrefDebugAutoStartLevel.Value && sceneName == "StartScene")
            {
                yield return new WaitForSeconds(2f);
                DebugStartLevel();
            }
            if (XRBootstrap.IsRunning)
            {
                Guarded("XRRenderFixes", () => XRRenderFixes.Apply(LoggerInstance, PrefSinglePassInstanced.Value));
                Guarded("VRInput", () => { VRInput.DisableStockXRBindingsEverywhere(); VRInput.AddDevice(); });
                Guarded("InputPatches", InputPatches.ForceControllerGlyphs);
                Guarded("RenderTweaks", () => RenderTweaks.ApplyToScene(LoggerInstance));
                Guarded("VRUI", VRUI.ConvertAll);
                yield return TakeOverPlayer(sceneName);
            }
            if (PrefDumpOnSceneLoad.Value)
            {
                yield return new WaitForSeconds(1.5f);
                SafeDump(sceneName);
            }
        }

        // The hands, the body and the sex scenes all hang off the player, and a scene does not always have one the
        // frame after it loads. A miss used to be silent and final - the paws simply never appeared - so keep asking for
        // a while, and leave a line in the log either way. F11 twice does the same by hand.
        IEnumerator TakeOverPlayer(string sceneName)
        {
            const float giveUpAfter = 15f;
            float start = Time.realtimeSinceStartup;
            while (true)
            {
                Guarded("VRHands", () => { VRHands.EnsureAnchors(); VRHands.AttachGameHands(); });
                Guarded("PlayerBody", PlayerBody.Attach);
                Guarded("SexScene", SexScene.Attach);
                float waited = Time.realtimeSinceStartup - start;
                if (VRHands.Attached)
                {
                    if (waited > 0.5f) LoggerInstance.Msg($"Hands took {waited:0.0} s to appear in {sceneName}");
                    yield break;
                }
                if (waited > giveUpAfter)
                {
                    if (PlayerBody.Attached)
                        LoggerInstance.Warning($"{sceneName} has a player to walk with but no hands to wash with; " +
                                               "press F11 twice to try again, and please report the log");
                    else
                        LoggerInstance.Msg($"{sceneName} has no player to take over (menus and the credits have none)");
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
        }

        // A scene whose camera carries no MainCamera tag (the credits) leaves Camera.main null, and with it no head to
        // follow, no panels and no fade; the camera that renders the scene becomes the main one so VR works there too.
        void AdoptSceneCamera()
        {
            if (Camera.main != null) return;
            Camera best = null;
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || cam.targetTexture != null) continue;
                if (cam.name.StartsWith("DnWVR", StringComparison.Ordinal)) continue;
                if (best == null || cam.depth > best.depth) best = cam;
            }
            if (best == null) return;
            best.tag = "MainCamera";
            LoggerInstance.Msg($"[VR] {best.name} carries no MainCamera tag; adopted it as this scene's camera");
        }

        static void AttachStaticCameraFollower()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent<OrbitCamera>() != null) return;
            if (cam.GetComponent<StaticCameraFollower>() == null)
                cam.gameObject.AddComponent<StaticCameraFollower>();
        }

        public override void OnUpdate()
        {
            DesktopView.Tick();
            SceneWalk.Tick();
            // A camera can also appear after the scene load that adopted one (or after VR starts).
            if (XRBootstrap.IsRunning && ++_cameraSweep % 30 == 0 && Camera.main == null) AdoptSceneCamera();

            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f9Key.wasPressedThisFrame) SafeDump("manual");
            if (kb.f10Key.wasPressedThisFrame) SceneDumper.LogXRDescriptors(LoggerInstance);
            if (kb.f11Key.wasPressedThisFrame)
            {
                if (XRBootstrap.IsRunning) StopXR();
                else StartXR();
            }
            if (kb.f5Key.wasPressedThisFrame)
            {
                VRRig.TrackingEnabled = !VRRig.TrackingEnabled;
                LoggerInstance.Msg($"Head tracking {(VRRig.TrackingEnabled ? "on" : "off")}");
            }
            if (kb.f7Key.wasPressedThisFrame) DebugStartLevel();
            if (kb.f6Key.wasPressedThisFrame)
            {
                MelonPreferences.Load();
                ApplyTunablePrefs();
                VRHands.ApplyOffsets();
                PlayerBody.ApplySettings();
                RenderTweaks.ReapplyFluidSwitch(LoggerInstance);
                LoggerInstance.Msg("Preferences reloaded and applied");
            }
            if (kb.f8Key.wasPressedThisFrame)
            {
                VRRig.SampleHmd();
                VRRig.RecenterPosition();
                VRRig.RecenterYaw(LookControllerYawOrRig());
                LoggerInstance.Msg("Recentered");
            }
        }

        void Guarded(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { LoggerInstance.Error($"{what} failed to initialize: {e}"); }
        }

        static void ApplyTunablePrefs()
        {
            VRInput.SmoothTurn = PrefSmoothTurn.Value;
            VRInput.SnapTurnDegrees = Mathf.Clamp(PrefSnapTurnDegrees.Value, 1f, 180f);
            VRInput.SmoothTurnDegPerSec = Mathf.Clamp(PrefSmoothTurnSpeed.Value, 10f, 720f);
            VRHands.PhysicalHands = PrefPhysicalHands.Value;
            VRHands.SecondHand = PrefSecondHand.Value;
            VRHands.RestOnPenis = PrefRestOnPenis.Value;
            VRRig.HeightCm = Mathf.Clamp(PrefPlayerHeightCm.Value, VRSettings.MinHeightCm, VRSettings.MaxHeightCm);
            PlayerBody.RoomScale = PrefRoomScaleBody.Value;
            PlayerBody.DuckWithHead = PrefPlayerDuckWithHead.Value;
            PlayerBody.Radius = PrefPlayerRadius.Value;
            PlayerBody.PushSpeed = PrefPlayerPushSpeed.Value;
            PlayerBody.StepUp = Mathf.Max(0f, PrefPlayerStepUp.Value);
            PlayerBody.StepRise = Mathf.Max(0.01f, PrefPlayerStepRise.Value);
            PlayerBody.SpringDamping = Mathf.Max(0f, PrefPlayerSpringDamping.Value);
            PlayerBody.SpringLift = Mathf.Max(0f, PrefPlayerSpringLift.Value);
            MenuHands.Enabled = PrefMenuHands.Value;
            HandPatches.PlapSpeed = PrefSlapSpeed.Value;
            HandPatches.PressSpeed = PrefHandPressSpeed.Value;
            HandPatches.ContactRadius = Mathf.Clamp(PrefSpongeReach.Value, 0.02f, 0.3f);
            HandPatches.StrokeRequiresMotion = PrefStrokeRequiresMotion.Value;
            HandPatches.TouchDuringCutscenes = PrefTouchDuringCutscenes.Value;
            HandPatches.TouchWorldSurfaces = PrefTouchWorldSurfaces.Value;
            HandPatches.SpongeNeedsTrigger = PrefSpongeNeedsTrigger.Value;
            ItemLaser.Enabled = PrefItemLaser.Value;
            ItemLaser.IncludeHandTargets = PrefItemLaserHandTargets.Value;
            ItemLaser.ConeDeg = PrefItemLaserConeDeg.Value;
            RenderTweaks.DisableFluidFeature = PrefDisableFluidFeature.Value;
            VRRig.DialogueCameraZoom = PrefDialogueCameraZoom.Value;
            VRRig.CutsceneFollowAnimation = PrefCutsceneFollowAnimation.Value;
            VRRig.AlignYawOnCameraCut = PrefAlignYawOnCameraCut.Value;
            VRRig.LateLatchEnabled = PrefLateLatchHead.Value;
            DesktopView.Enabled = PrefDesktopView.Value;
            DesktopView.FieldOfView = PrefDesktopViewFov.Value;
            DesktopView.Height = PrefDesktopViewHeight.Value;
            DesktopView.Smoothing = PrefDesktopViewSmoothing.Value;
            SexScene.Interactive = PrefInteractiveSexScenes.Value;
            SexScene.BuildUpSeconds = PrefSexSceneBuildUpSeconds.Value;
            FinishHold.HoldSeconds = PrefSexSceneFinishHoldSeconds.Value;
            SceneWalk.Enabled = PrefSexSceneWalking.Value;
            SceneWalk.Speed = PrefSexSceneWalkSpeed.Value;
            DialogueVR.AutoAdvance = PrefDialogueAutoAdvance.Value;
            DialogueVR.LineSeconds = PrefDialogueLineSeconds.Value;
            DialogueVR.SkipLines = PrefDialogueTriggerSkip.Value;
            VRUI.OnTop = PrefUIOnTop.Value;
            DebugInteractionLog = PrefDebugInteractionLog.Value;
            VRHands.PlapperOffsetPos = ParseVector(PrefPlapperOffsetPos.Value);
            VRHands.PlapperOffsetEuler = ParseVector(PrefPlapperOffsetEuler.Value);
            VRHands.ToolOffsetPos = ParseVector(PrefToolOffsetPos.Value);
            VRHands.ToolOffsetEuler = ParseVector(PrefToolOffsetEuler.Value);
            HeldTool.SprayerOffset = ParseVector(PrefSprayerOffsetPos.Value);
            HeldTool.LadderOffset = ParseVector(PrefLadderOffsetPos.Value);
        }

        static Vector3 ParseVector(string s)
        {
            try
            {
                var p = s.Split(',');
                if (p.Length != 3) return Vector3.zero;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                return new Vector3(float.Parse(p[0].Trim(), inv), float.Parse(p[1].Trim(), inv), float.Parse(p[2].Trim(), inv));
            }
            catch { return Vector3.zero; }
        }

        void DebugStartLevel()
        {
            try
            {
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "StartScene")
                {
                    LoggerInstance.Msg("DebugStartLevel: only works from StartScene");
                    return;
                }
                int slot = 0;
                for (int i = 1; i <= 3; i++)
                    if (SaveManagerV1.GetSlotProgress(i) == 0) { slot = i; break; }
                if (slot == 0)
                {
                    LoggerInstance.Msg("DebugStartLevel: no empty slot, using Continue");
                    MenuManager.TriggerEvent(new MenuEventUserIntent("Continue"));
                    return;
                }
                MenuManager.TriggerEvent(new MenuEventUserIntent("NewGame"));
                MenuManager.TriggerEvent(new MenuEventUserIntent("NewSlot" + slot));
                LoggerInstance.Msg($"DebugStartLevel: new game in slot {slot}");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("DebugStartLevel failed: " + e);
            }
        }

        static float LookControllerYawOrRig()
        {
            try { return LookController.GetLookRotation().eulerAngles.y; }
            catch { return VRRig.RigYaw + VRRig.HmdLocalYaw; }
        }

        public override void OnApplicationQuit()
        {
            if (XRBootstrap.IsRunning) StopXR();
        }

        private void SafeDump(string tag)
        {
            try
            {
                var path = SceneDumper.DumpToFile(tag);
                LoggerInstance.Msg($"Diagnostics written: {path}");
            }
            catch (Exception e)
            {
                LoggerInstance.Error($"Dump failed: {e}");
            }
        }
    }
}
