using DnWVR.VR;

namespace DnWVR
{
    /// <summary>
    /// Every setting the mod has, in the order it is written to the settings file. The loader decides where that file
    /// lives and what a line in it looks like; this only says what the settings are and what each one is for.
    /// </summary>
    public static class Prefs
    {
        public static Pref<bool> EnableVR;
        public static Pref<bool> SinglePassInstanced;
        public static Pref<bool> DumpOnSceneLoad;
        public static Pref<int> StartupRetries;
        public static Pref<bool> SmoothTurn;
        public static Pref<float> SnapTurnDegrees, SmoothTurnSpeed;
        public static Pref<bool> DebugAutoStartLevel;
        public static Pref<string> PlapperOffsetPos, PlapperOffsetEuler,
            ToolOffsetPos, ToolOffsetEuler, SprayerOffsetPos, LadderOffsetPos;
        public static Pref<bool> PhysicalHands, RestOnPenis, RoomScaleBody;
        public static Pref<int> PlayerHeightCm, CameraLiftCm;
        public static Pref<bool> PlayerDuckWithHead;
        public static Pref<float> PlayerRadius, PlayerPushSpeed, PlayerStepUp,
            PlayerStepRise, PlayerSpringDamping, PlayerSpringLift;
        public static Pref<bool> SecondHand, MenuHands;
        public static Pref<bool> DebugRadialMenu, DebugSceneMenu;
        public static Pref<bool> DisableFluidFeature;
        public static Pref<bool> DialogueCameraZoom, DialogueAutoAdvance,
            DialogueTriggerSkip, UIOnTop;
        public static Pref<float> OptionsFontScale;
        public static Pref<bool> VRRenderOptimizations, LogPerformance;
        public static Pref<string> PerformancePreset;
        public static Pref<int> MenuWidthCm;
        public static Pref<float> SpongeReach;
        public static Pref<float> DialogueLineSeconds, SexSceneBuildUpSeconds,
            SexSceneFinishHoldSeconds, SexSceneWalkSpeed;
        public static Pref<bool> InteractiveSexScenes, SexSceneWalking, DesktopView;
        public static Pref<float> DesktopViewFov, DesktopViewSmoothing;
        public static Pref<int> DesktopViewHeight;
        public static Pref<bool> CutsceneFollowAnimation;
        public static Pref<bool> AlignYawOnCameraCut;
        public static Pref<bool> LateLatchHead;
        public static Pref<bool> DebugInteractionLog;
        public static Pref<bool> StrokeRequiresMotion, TouchDuringCutscenes,
            TouchWorldSurfaces, SpongeNeedsTrigger;
        public static Pref<float> SlapSpeed, HandPressSpeed;
        public static Pref<bool> ItemLaser, ItemLaserHandTargets;
        public static Pref<float> ItemLaserConeDeg;

        /// <summary>Creates them all, which is also what writes any that a player's settings file has not got yet.</summary>
        public static void Create()
        {
            PrefStore.Open();
            EnableVR = PrefStore.Create("EnableVR", true, "Start OpenXR when the game boots");
            SinglePassInstanced = PrefStore.Create("SinglePassInstanced", false,
                "Single-pass instanced stereo. This game build ships no single-pass shader variants, so it cannot work here; " +
                "leave it false");
            DumpOnSceneLoad = PrefStore.Create("DumpOnSceneLoad", false, "Dump scene diagnostics after each scene load");
            StartupRetries = PrefStore.Create("StartupRetries", 12,
                "How many times (5 s apart) to retry OpenXR init at startup while the headset is not ready");
            SmoothTurn = PrefStore.Create("SmoothTurn", false,
                "Turn smoothly while the right stick is held instead of snapping by a step. The VR section of the " +
                "options screen sets this too, and the row under it is whichever of the next two settings applies");
            SnapTurnDegrees = PrefStore.Create("SnapTurnDegrees", 45f,
                "Degrees turned by one flick of the right stick when turning snaps");
            SmoothTurnSpeed = PrefStore.Create("SmoothTurnSpeed", 120f,
                "Degrees a second the world turns while the right stick is held, when turning is smooth. Lower is " +
                "gentler on a stomach new to VR");
            DebugAutoStartLevel = PrefStore.Create("DebugAutoStartLevel", false,
                "Debug: automatically start a new game (first empty slot) from the main menu");
            PlapperOffsetPos = PrefStore.Create("PlapperOffsetPos", "0,0,0",
                "Hand mesh offset added to the built-in pose (fingers along the aim ray, thumb up, palm toward the other hand), " +
                "controller axes, metres x,y,z; authored for the left hand, mirrored on the right");
            PlapperOffsetEuler = PrefStore.Create("PlapperOffsetEuler", "0,0,0",
                "Hand mesh rotation added to the built-in pose (fingers along the aim ray, thumb up, palm toward the other " +
                "hand), controller axes, degrees x,y,z; mirrored on the right");
            ToolOffsetPos = PrefStore.Create("ToolOffsetPos", "0,0,0",
                "Tool anchor offset added to the tool controller's aim pose (where it points), metres x,y,z in that frame; " +
                "authored for the right hand, mirrored on the left");
            ToolOffsetEuler = PrefStore.Create("ToolOffsetEuler", "0,0,0",
                "Tool anchor rotation added to the tool controller's aim pose, degrees x,y,z; mirrored on the left");
            SprayerOffsetPos = PrefStore.Create("SprayerOffsetPos", "0,0,-0.2",
                "Where the pressure washer sits on the tool controller, on top of ToolOffsetPos, metres x,y,z along the aim pose " +
                "(negative z pulls it back into your hand)");
            LadderOffsetPos = PrefStore.Create("LadderOffsetPos", "0,-1,0",
                "Where the ladder sits on the tool controller, on top of ToolOffsetPos, metres x,y,z along the aim pose " +
                "(negative y brings its middle down onto your hand)");
            PhysicalHands = PrefStore.Create("PhysicalHands", true,
                "Hands stop at walls, props and the dragon (a controller inside them leaves the hand on the surface, and a paw " +
                "lays its palm onto it, like the flat game's hand); a held tool is kept out at its grip only. Off = hands go " +
                "wherever the controllers are");
            RestOnPenis = PrefStore.Create("HandsRestOnPenis", true,
                "Hands rest on the dragon's penis and slide along it, following its bend and motion. Off = they pass through it; " +
                "touching it still strokes");
            RoomScaleBody = PrefStore.Create("RoomScaleBody", true,
                "Your body (its collider and feet) follows you when you walk around your room, stopping at walls and the dragon. " +
                "Off = it stays where the stick puts it");
            PlayerHeightCm = PrefStore.Create("PlayerHeightCm", VRRig.GameHeightCm,
                "Your height in centimetres, measured from your feet - the number the VR section of the options screen " +
                "shows. It does not move the camera: it caps how tall your collider stands, so ducking in the room ducks " +
                "the body with you. The Calibrate button there reads it off the headset");
            CameraLiftCm = PrefStore.Create("CameraLiftCm", 0,
                "Lifts your view this many centimetres above where the game puts the head, without touching the body: " +
                "the collider keeps its height and the hands come up with the eyes. Negative lowers it");
            PlayerDuckWithHead = PrefStore.Create("PlayerDuckWithHead", true,
                "Duck in your room and your body ducks with you: the top of your collider follows your head, capped by " +
                "PlayerHeightCm, so you fit under what you have physically ducked under. The game's crouch button is a " +
                "separate thing either way. Off = the body keeps the height the game gives it");
            PlayerRadius = PrefStore.Create("PlayerRadius", 0.2f,
                "Radius (m) of your body's collider in VR, small enough to stand right next to the dragon (the game's is 0.5; 0 " +
                "= the game's)");
            PlayerStepUp = PrefStore.Create("PlayerStepUp", 0.55f,
                "How high (m) a surface may be above your feet and still be stepped onto. Higher ground (crates, the mount " +
                "frame, the tool bench) is solid instead of a ramp; the step stool is still climbed step by step, and dragons " +
                "are climbed whatever their height. 2 or more switches the rule off");
            PlayerStepRise = PrefStore.Create("PlayerStepRise", 1.2f,
                "How fast (m/s) the ground under you may rise when you step up, so a step lifts you instead of launching you");
            PlayerSpringLift = PrefStore.Create("PlayerSpringLift", 0.5f,
                "Fastest (m/s) the spring under you may lift you in the moment after a fall, which is what turns a landing into " +
                "two or three bounces. Only then: stepping onto a stool is the same spring and stays brisk. Jumping is " +
                "unaffected. 0 = uncapped, as the game has it");
            PlayerSpringDamping = PrefStore.Create("PlayerSpringDamping", 0f,
                "Damping of that spring (0 = the game's 0.5). It works on how fast the ground under you moves rather than how " +
                "fast you do, so raising it also amplifies every shift of the floor into a shove - PlayerSpringLift is the " +
                "gentler cure");
            PlayerPushSpeed = PrefStore.Create("PlayerPushSpeed", 2f,
                "Fastest (m/s) the dragon and other moving things push your body out of them (the game's is 10; 0 = the game's)");
            SecondHand = PrefStore.Create("SecondHand", true,
                "Show a mirrored copy of the game's hand on the tool controller while it holds nothing (a held tool hides it)");
            MenuHands = PrefStore.Create("MenuHands", false,
                "Main menu and credits: simple hands on both controllers that poke the menu, with the menu within arm's reach. " +
                "Off = no hands there, the laser does the menus");
            SlapSpeed = PrefStore.Create("SlapSpeed", 1.0f,
                "Hand speed (m/s, relative to your body) that turns a touch into a slap");
            HandPressSpeed = PrefStore.Create("HandPressSpeed", 0.4f,
                "Push speed (m/s toward it) that presses a button, the phone or the water tap without a slap");
            StrokeRequiresMotion = PrefStore.Create("StrokeRequiresMotion", true,
                "A touching hand strokes only while it moves along the surface. Off = any contact strokes, like the flat game");
            TouchDuringCutscenes = PrefStore.Create("TouchDuringCutscenes", true,
                "Hands and sponge keep touching during cutscenes and in-level sex acts (dialogue always stops them, like the " +
                "flat game)");
            InteractiveSexScenes = PrefStore.Create("InteractiveSexScenes", true,
                "Sex scenes in VR: your hands on the dragon (resting on it or stroking its penis) set the pace and build its " +
                "pleasure, which brings its lines and its climax instead of the game's timer and keep-going questions; letting " +
                "go nearly stops the scene while the pleasure slowly fades. Hold A to finish sooner; in Ryan + Conrad you watch " +
                "and A ends the scene. Off = the game's automatic pace, timing and questions");
            SexSceneBuildUpSeconds = PrefStore.Create("SexSceneBuildUpSeconds", 60f,
                "Seconds of steady, lively hand motion on the dragon from the start of an interactive sex scene to its climax");
            SexSceneFinishHoldSeconds = PrefStore.Create("SexSceneFinishHoldSeconds", 3f,
                "Seconds to hold A in a sex scene to finish it");
            SexSceneWalking = PrefStore.Create("SexSceneWalking", true,
                "Walk around in the sex scenes with the left stick (not in Ryan's scene, where you lie under him); in " +
                "Alexander's scene you stand from the start");
            SexSceneWalkSpeed = PrefStore.Create("SexSceneWalkSpeed", 1.5f,
                "Walking speed in the sex scenes, m/s at full stick");
            TouchWorldSurfaces = PrefStore.Create("TouchWorldSurfaces", false,
                "Floors, walls and props give touch sounds and slap effects too (never game events)");
            SpongeReach = PrefStore.Create("SpongeReach", 0.1f,
                "How far (m) from the sponge a surface still counts as scrubbed. Bigger reaches further for the same " +
                "arm, which is what a small room asks for; too big and the sponge washes what it is not touching");
            SpongeNeedsTrigger = PrefStore.Create("SpongeNeedsTrigger", true,
                "The sponge only scrubs while the trigger is held. Off = it scrubs whatever it touches");
            ItemLaser = PrefStore.Create("ItemLaser", true,
                "Point a controller at an item or station (ray + dot) and grip to pick it up, put it back or use it. Off = grip " +
                "uses the game's invisible cone from the last-gripped hand");
            ItemLaserHandTargets = PrefStore.Create("ItemLaserHandTargets", false,
                "The item laser also targets things the game operates by hand (phone, dismiss button, water tap)");
            ItemLaserConeDeg = PrefStore.Create("ItemLaserConeDeg", 8f,
                "How many degrees the item laser may miss an item by and still pick it (items within 10 cm of the ray always " +
                "count)");
            DebugRadialMenu = PrefStore.Create("DebugRadialMenu", false,
                "Debug: hold left Y + stick to quick-equip any tool station in the level");
            DebugSceneMenu = PrefStore.Create("DebugSceneMenu", false,
                "Debug: left Y opens a panel that loads any sex scene or reloads washing through the game's own scene flow " +
                "(needs a loaded game; a scene played to its end counts as watched in that save). Y stays with DebugRadialMenu " +
                "while that is on");
            DisableFluidFeature = PrefStore.Create("DisableFluidFeature", false,
                "Troubleshooting: turn off the game's fluid renderer feature");
            DialogueCameraZoom = PrefStore.Create("DialogueCameraZoom", false,
                "Let dialogue (<<LookAt>> and the <<LookFromTo>> window intros) move the camera like the flat game. Off = the " +
                "camera never moves, fades or turns during dialogue; sex scenes are unaffected");
            DialogueAutoAdvance = PrefStore.Create("DialogueAutoAdvance", true,
                "Dialogue lines advance on their own and dialogue never takes your controls (moving, touching and tools keep " +
                "working); answers are always picked with the laser pointer. Off = like the flat game: a button advances lines " +
                "and dialogue locks your controls");
            DialogueLineSeconds = PrefStore.Create("DialogueLineSeconds", 3f,
                "Seconds a fully shown dialogue line stays before the next one (with DialogueAutoAdvance)");
            DialogueTriggerSkip = PrefStore.Create("DialogueTriggerSkip", true,
                "A grip or trigger press skips dialogue lines outside interactive sex scenes: a line shows in full, then the " +
                "next one comes (either grip skips; the hand holding a tool and the pointer's hand keep their trigger; answers " +
                "need the laser)");
            VRRenderOptimizations = PrefStore.Create("VRRenderOptimizations", true,
                "Rendering savings that cost nothing visible in the headset: a cheaper SSAO, no fluid passes while nothing is " +
                "liquid, no bloom too faint to see, no copy of each eye that nothing reads, and FXAA instead of 4x MSAA on " +
                "the desktop window. Off = the game's own, for comparing");
            PerformancePreset = PrefStore.Create("PerformancePreset", "Quality",
                "The VR tab's Performance row. Quality = the game's own picture. Balanced = shadows with two cascades to 35 m " +
                "and medium soft edges, eyes at 90% resolution, and the desktop window at 720p without shadows. Fast = eyes at " +
                "80% and 2x MSAA as well. Fastest = eyes at 70%, and the window shows the left eye instead of a view of its own");
            LogPerformance = PrefStore.Create("LogPerformance", true,
                "Every 5 s in VR, a [Perf] line in the log: frame times and the GPU and compositor times the runtime reports. " +
                "Attach it when reporting slowness");
            MenuWidthCm = PrefStore.Create("MenuWidthCm", 230,
                "How wide (cm) the menu panels stand in front of you. This is the setting that makes a menu easier to " +
                "read: everything on the panel grows together, text and boxes alike");
            OptionsFontScale = PrefStore.Create("OptionsFontScale", 1f,
                "A nudge on the options screen's text size only, for when the panel is the size you want but the text " +
                "is not. The rows size their own text to fit, so a large nudge will crop it");
            UIOnTop = PrefStore.Create("UIOnTop", true,
                "Menus, HUD and dialogue draw over the world instead of hiding behind the dragon or walls (applies to panels " +
                "created after a scene load)");
            DesktopView = PrefStore.Create("DesktopView", true,
                "The desktop window shows its own view from between your eyes, shaped like the window (one extra render). Off = " +
                "the cropped left eye");
            DesktopViewFov = PrefStore.Create("DesktopViewFov", 60f, "Vertical field of view of the desktop view, in degrees");
            DesktopViewHeight = PrefStore.Create("DesktopViewHeight", 1080,
                "Height in pixels of the desktop view's render, at most the window's (0 = the window's height; lower is cheaper)");
            DesktopViewSmoothing = PrefStore.Create("DesktopViewSmoothing", 0.1f,
                "Seconds over which the desktop view follows your head, for calmer recordings (0 = exactly)");
            CutsceneFollowAnimation = PrefStore.Create("CutsceneFollowAnimation", false,
                "In cutscenes/sex scenes ride the game's animated camera exactly. Off = stay anchored where it settles, head " +
                "fully free");
            AlignYawOnCameraCut = PrefStore.Create("AlignYawOnCameraCut", true,
                "When the game cuts to a new camera spot, turn you once (under a fade) to face where it looks");
            LateLatchHead = PrefStore.Create("LateLatchHead", true,
                "Re-sample the headset pose right before rendering (lowest latency; turn off only to compare)");
            DebugInteractionLog = PrefStore.Create("DebugInteractionLog", false,
                "Debug: log cutscene action changes, pause/resume buttons and hand/item interaction decisions");
            PrefStore.Save();
        }

        /// <summary>Re-reads the settings file (F6), so an edit made while playing takes hold.</summary>
        public static void Reload() => PrefStore.Reload();

        /// <summary>Writes the settings file after the mod itself changed a setting, as the VR options rows do.</summary>
        public static void Save() => PrefStore.Save();
    }
}
