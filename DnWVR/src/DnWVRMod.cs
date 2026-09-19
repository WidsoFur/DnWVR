using System;
using System.Collections;
using DnWVR.Diag;
using DnWVR.VR;
using DnWVR.XR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DnWVR
{
    /// <summary>
    /// The mod itself: preferences, patch setup, OpenXR start and stop, per-scene setup and the debug hotkeys. A loader
    /// in src\Loader calls the four methods below and otherwise stays out of the way.
    /// </summary>
    public static class DnWVRMod
    {


        /// <summary>Log cutscene action changes and controller/interaction decisions (copied from Prefs.DebugInteractionLog).</summary>
        public static bool DebugInteractionLog = false;

        static bool s_lateLatchHooked;
        static int s_cameraSweep;

        /// <summary>Everything that happens once, when the game and the loader are up.</summary>
        public static void Initialize()
        {
            Prefs.Create();
            ApplyTunablePrefs();

            Log.Msg($"DnWVR init. Unity {Application.unityVersion}, gfx {SystemInfo.graphicsDeviceType}, " +
                    $"screen {Screen.width}x{Screen.height}");
            Log.Msg("Hotkeys: F5 head tracking, F6 reload prefs, F7 debug start level, F8 recenter, " +
                    "F9 dump diagnostics, F10 XR descriptors, F11 start/stop OpenXR");

            InstallModules();
            // A module may add settings of its own as it installs (the desktop mirror does), so the file is written
            // again once they all have.
            Prefs.Save();

            InstallCheck.Run();
            if (Prefs.EnableVR.Value)
                Host.StartCoroutine(StartXRWhenReady());
        }

        /// <summary>Each module binds game members by name; a renamed member must not take the whole mod down.</summary>
        static void InstallModules()
        {
            Guarded("CameraPatches", () => CameraPatches.Apply(Host.Harmony));
            Guarded("InputPatches", () => InputPatches.Apply(Host.Harmony));
            Guarded("HandPatches", () => HandPatches.Apply(Host.Harmony));
            Guarded("RenderTweaks", () => RenderTweaks.ApplyPatches(Host.Harmony));
            Guarded("VRUI", () => { VRUI.Initialize(); VRUI.ApplyPatches(Host.Harmony); });
            Guarded("DialogueVR", () => DialogueVR.Apply(Host.Harmony));
            Guarded("VRHands", () => VRHands.Initialize());
            Guarded("PlayerBody", () => PlayerBody.Install(Host.Harmony));
            Guarded("SexScene", () => SexScene.Install(Host.Harmony));
            Guarded("VRInput", () => VRInput.Initialize());
            Guarded("DesktopMirror", () => DesktopMirror.Install(Host.Harmony));
            Guarded("DesktopView", () => DesktopView.Install());
        }

        static IEnumerator StartXRWhenReady()
        {
            // Graphics must be fully initialized before the loader is created.
            yield return null;
            yield return null;
            int attempts = Mathf.Max(1, Prefs.StartupRetries.Value);
            for (int i = 1; i <= attempts; i++)
            {
                if (StartXR()) yield break;
                if (i < attempts)
                {
                    Log.Msg($"OpenXR not available yet (attempt {i}/{attempts}); retrying in 5 s. " +
                            "Put the headset on / connect Virtual Desktop, or press F11 later.");
                    yield return new WaitForSecondsRealtime(5f);
                }
            }
            // The game carries on flat from here, and a player who looks at the log should find out why and what to do.
            Log.Warning($"OpenXR did not start after {attempts} attempts, so the game stays flat. Start the VR runtime " +
                        "(SteamVR, Oculus, Virtual Desktop), make sure it is the active OpenXR runtime, then press F11. " +
                        "Player.log in AppData\\LocalLow\\Gator Dragon Games\\DragNWash has OpenXR's own report.");
        }

        static bool StartXR()
        {
            if (!XRBootstrap.Start(Prefs.SinglePassInstanced.Value)) return false;
            if (!s_lateLatchHooked)
            {
                Application.onBeforeRender += VRRig.LateLatch;
                s_lateLatchHooked = true;
            }
            AttachStaticCameraFollower();
            XRRenderFixes.Apply(Prefs.SinglePassInstanced.Value);
            VRInput.DisableStockXRBindingsEverywhere();
            VRInput.AddDevice();
            VRInput.RebindGameActions();
            InputPatches.ForceControllerGlyphs();
            VRHands.EnsureAnchors();
            VRHands.AttachGameHands();
            PlayerBody.Attach();
            SexScene.Attach();
            RadialToolMenu.Ensure();
            DebugSceneMenu.Ensure();
            VRFader.Ensure();
            FinishHold.Ensure();
            VRLaser.Ensure();
            ItemLaser.Ensure();
            MenuHands.Ensure();
            VRSettings.Ensure();
            RenderTweaks.ApplyToScene();
            VRUI.ConvertAll();
            Host.StartCoroutine(StereoDiagnosticsAfterDelay());
            return true;
        }

        static IEnumerator StereoDiagnosticsAfterDelay()
        {
            // The session becomes visible/focused a moment after StartSubsystems; report once it is.
            yield return new WaitForSecondsRealtime(3f);
            if (XRBootstrap.IsRunning) RenderTweaks.LogStereoState();
        }

        static void StopXR()
        {
            if (s_lateLatchHooked)
            {
                Application.onBeforeRender -= VRRig.LateLatch;
                s_lateLatchHooked = false;
            }
            VRRig.RestoreNearClip();
            VRHands.DetachGameHands(); // also destroys the twin hand
            ItemLaser.ResetState();
            HandPatches.ResetTouchState();
            VRInput.RemoveDevice();
            PlayerBody.Detach();
            SexScene.Detach();
            DesktopView.Disable();
            XRBootstrap.Stop();
        }

        /// <summary>A scene has finished loading.</summary>
        public static void SceneLoaded(int buildIndex, string sceneName)
        {
            Log.Msg($"Scene loaded: {sceneName} (#{buildIndex})");
            VRFader.Flash(0.8f); // hide the first frames of a new scene while the rig re-aligns
            VRRig.ResetForNewScene();
            VRHands.OnSceneChanged();
            PlayerBody.OnSceneChanged();
            SexScene.OnSceneChanged();
            SceneWalk.Reset();
            ItemLaser.ResetState();
            HandPatches.ResetTouchState();
            VRUI.OnSceneChanged();
            Host.StartCoroutine(AfterSceneLoad(sceneName));
        }

        static IEnumerator AfterSceneLoad(string sceneName)
        {
            yield return null;
            AdoptSceneCamera();
            AttachStaticCameraFollower();
            if (Prefs.DebugAutoStartLevel.Value && sceneName == "StartScene")
            {
                yield return new WaitForSeconds(2f);
                DebugStartLevel();
            }
            if (XRBootstrap.IsRunning)
            {
                Guarded("XRRenderFixes", () => XRRenderFixes.Apply(Prefs.SinglePassInstanced.Value));
                Guarded("VRInput", () =>
                {
                    VRInput.DisableStockXRBindingsEverywhere();
                    VRInput.AddDevice();
                    // A new scene builds its own action maps, and they resolve without the pad the same way the first ones did.
                    VRInput.RebindGameActions();
                });
                Guarded("InputPatches", InputPatches.ForceControllerGlyphs);
                Guarded("RenderTweaks", () => RenderTweaks.ApplyToScene());
                Guarded("VRUI", VRUI.ConvertAll);
                yield return TakeOverPlayer(sceneName);
            }
            if (Prefs.DumpOnSceneLoad.Value)
            {
                yield return new WaitForSeconds(1.5f);
                SafeDump(sceneName);
            }
        }

        // The hands, the body and the sex scenes all hang off the player, and a scene does not always have one the
        // frame after it loads. A miss used to be silent and final - the paws simply never appeared - so keep asking for
        // a while, and leave a line in the log either way. F11 twice does the same by hand.
        static IEnumerator TakeOverPlayer(string sceneName)
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
                    if (waited > 0.5f) Log.Msg($"Hands took {waited:0.0} s to appear in {sceneName}");
                    yield break;
                }
                if (waited > giveUpAfter)
                {
                    if (PlayerBody.Attached)
                        Log.Warning($"{sceneName} has a player to walk with but no hands to wash with; " +
                                    "press F11 twice to try again, and please report the log");
                    else
                        Log.Msg($"{sceneName} has no player to take over (menus and the credits have none)");
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
        }

        // A scene whose camera carries no MainCamera tag (the credits) leaves Camera.main null, and with it no head to
        // follow, no panels and no fade; the camera that renders the scene becomes the main one so VR works there too.
        static void AdoptSceneCamera()
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
            Log.Msg($"[VR] {best.name} carries no MainCamera tag; adopted it as this scene's camera");
        }

        static void AttachStaticCameraFollower()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent<OrbitCamera>() != null) return;
            if (cam.GetComponent<StaticCameraFollower>() == null)
                cam.gameObject.AddComponent<StaticCameraFollower>();
        }

        /// <summary>Every frame.</summary>
        public static void Tick()
        {
            DesktopView.Tick();
            SceneWalk.Tick();
            // A camera can also appear after the scene load that adopted one (or after VR starts).
            if (XRBootstrap.IsRunning && ++s_cameraSweep % 30 == 0 && Camera.main == null) AdoptSceneCamera();

            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f9Key.wasPressedThisFrame) SafeDump("manual");
            if (kb.f10Key.wasPressedThisFrame) SceneDumper.LogXRDescriptors();
            if (kb.f11Key.wasPressedThisFrame)
            {
                if (XRBootstrap.IsRunning) StopXR();
                else StartXR();
            }
            if (kb.f5Key.wasPressedThisFrame)
            {
                VRRig.TrackingEnabled = !VRRig.TrackingEnabled;
                Log.Msg($"Head tracking {(VRRig.TrackingEnabled ? "on" : "off")}");
            }
            if (kb.f7Key.wasPressedThisFrame) DebugStartLevel();
            if (kb.f6Key.wasPressedThisFrame)
            {
                Prefs.Reload();
                ApplyTunablePrefs();
                VRHands.ApplyOffsets();
                PlayerBody.ApplySettings();
                RenderTweaks.ReapplyFluidSwitch();
                Log.Msg("Preferences reloaded and applied");
            }
            if (kb.f8Key.wasPressedThisFrame)
            {
                VRRig.SampleHmd();
                VRRig.RecenterPosition();
                VRRig.RecenterYaw(LookControllerYawOrRig());
                Log.Msg("Recentered");
            }
        }

        static void Guarded(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Log.Error($"{what} failed to initialize: {e}"); }
        }

        static void ApplyTunablePrefs()
        {
            VRInput.SmoothTurn = Prefs.SmoothTurn.Value;
            VRInput.SnapTurnDegrees = Mathf.Clamp(Prefs.SnapTurnDegrees.Value, 1f, 180f);
            VRInput.SmoothTurnDegPerSec = Mathf.Clamp(Prefs.SmoothTurnSpeed.Value, 10f, 720f);
            VRHands.PhysicalHands = Prefs.PhysicalHands.Value;
            VRHands.SecondHand = Prefs.SecondHand.Value;
            VRHands.RestOnPenis = Prefs.RestOnPenis.Value;
            VRRig.HeightCm = Mathf.Clamp(Prefs.PlayerHeightCm.Value, VRSettings.MinHeightCm, VRSettings.MaxHeightCm);
            VRRig.CameraLiftCm = Mathf.Clamp(Prefs.CameraLiftCm.Value, VRSettings.MinLiftCm, VRSettings.MaxLiftCm);
            PlayerBody.RoomScale = Prefs.RoomScaleBody.Value;
            PlayerBody.DuckWithHead = Prefs.PlayerDuckWithHead.Value;
            PlayerBody.Radius = Prefs.PlayerRadius.Value;
            PlayerBody.PushSpeed = Prefs.PlayerPushSpeed.Value;
            PlayerBody.StepUp = Mathf.Max(0f, Prefs.PlayerStepUp.Value);
            PlayerBody.StepRise = Mathf.Max(0.01f, Prefs.PlayerStepRise.Value);
            PlayerBody.SpringDamping = Mathf.Max(0f, Prefs.PlayerSpringDamping.Value);
            PlayerBody.SpringLift = Mathf.Max(0f, Prefs.PlayerSpringLift.Value);
            MenuHands.Enabled = Prefs.MenuHands.Value;
            HandPatches.PlapSpeed = Prefs.SlapSpeed.Value;
            HandPatches.PressSpeed = Prefs.HandPressSpeed.Value;
            HandPatches.ContactRadius = Mathf.Clamp(Prefs.SpongeReach.Value, 0.02f, 0.3f);
            HandPatches.StrokeRequiresMotion = Prefs.StrokeRequiresMotion.Value;
            HandPatches.TouchDuringCutscenes = Prefs.TouchDuringCutscenes.Value;
            HandPatches.TouchWorldSurfaces = Prefs.TouchWorldSurfaces.Value;
            HandPatches.SpongeNeedsTrigger = Prefs.SpongeNeedsTrigger.Value;
            ItemLaser.Enabled = Prefs.ItemLaser.Value;
            ItemLaser.IncludeHandTargets = Prefs.ItemLaserHandTargets.Value;
            ItemLaser.ConeDeg = Prefs.ItemLaserConeDeg.Value;
            RenderTweaks.DisableFluidFeature = Prefs.DisableFluidFeature.Value;
            VRRig.DialogueCameraZoom = Prefs.DialogueCameraZoom.Value;
            VRRig.CutsceneFollowAnimation = Prefs.CutsceneFollowAnimation.Value;
            VRRig.AlignYawOnCameraCut = Prefs.AlignYawOnCameraCut.Value;
            VRRig.LateLatchEnabled = Prefs.LateLatchHead.Value;
            DesktopView.Enabled = Prefs.DesktopView.Value;
            DesktopView.FieldOfView = Prefs.DesktopViewFov.Value;
            DesktopView.Height = Prefs.DesktopViewHeight.Value;
            DesktopView.Smoothing = Prefs.DesktopViewSmoothing.Value;
            SexScene.Interactive = Prefs.InteractiveSexScenes.Value;
            SexScene.BuildUpSeconds = Prefs.SexSceneBuildUpSeconds.Value;
            FinishHold.HoldSeconds = Prefs.SexSceneFinishHoldSeconds.Value;
            SceneWalk.Enabled = Prefs.SexSceneWalking.Value;
            SceneWalk.Speed = Prefs.SexSceneWalkSpeed.Value;
            DialogueVR.AutoAdvance = Prefs.DialogueAutoAdvance.Value;
            DialogueVR.LineSeconds = Prefs.DialogueLineSeconds.Value;
            DialogueVR.SkipLines = Prefs.DialogueTriggerSkip.Value;
            VRUI.OnTop = Prefs.UIOnTop.Value;
            VRSettings.FontScale = Mathf.Clamp(Prefs.OptionsFontScale.Value, 0.5f, 3f);
            VRUI.MenuWidthMeters = Mathf.Clamp(Prefs.MenuWidthCm.Value, VRSettings.MinMenuCm, VRSettings.MaxMenuCm) * 0.01f;
            VRUI.ApplyMenuSize();
            DebugInteractionLog = Prefs.DebugInteractionLog.Value;
            VRHands.PlapperOffsetPos = ParseVector(Prefs.PlapperOffsetPos.Value);
            VRHands.PlapperOffsetEuler = ParseVector(Prefs.PlapperOffsetEuler.Value);
            VRHands.ToolOffsetPos = ParseVector(Prefs.ToolOffsetPos.Value);
            VRHands.ToolOffsetEuler = ParseVector(Prefs.ToolOffsetEuler.Value);
            HeldTool.SprayerOffset = ParseVector(Prefs.SprayerOffsetPos.Value);
            HeldTool.LadderOffset = ParseVector(Prefs.LadderOffsetPos.Value);
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

        static void DebugStartLevel()
        {
            try
            {
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "StartScene")
                {
                    Log.Msg("DebugStartLevel: only works from StartScene");
                    return;
                }
                int slot = 0;
                for (int i = 1; i <= 3; i++)
                    if (SaveManagerV1.GetSlotProgress(i) == 0) { slot = i; break; }
                if (slot == 0)
                {
                    Log.Msg("DebugStartLevel: no empty slot, using Continue");
                    MenuManager.TriggerEvent(new MenuEventUserIntent("Continue"));
                    return;
                }
                MenuManager.TriggerEvent(new MenuEventUserIntent("NewGame"));
                MenuManager.TriggerEvent(new MenuEventUserIntent("NewSlot" + slot));
                Log.Msg($"DebugStartLevel: new game in slot {slot}");
            }
            catch (Exception e)
            {
                Log.Error("DebugStartLevel failed: " + e);
            }
        }

        static float LookControllerYawOrRig()
        {
            try { return LookController.GetLookRotation().eulerAngles.y; }
            catch { return VRRig.RigYaw + VRRig.HmdLocalYaw; }
        }

        /// <summary>The game is closing.</summary>
        public static void Quit()
        {
            if (XRBootstrap.IsRunning) StopXR();
        }

        static void SafeDump(string tag)
        {
            try
            {
                var path = SceneDumper.DumpToFile(tag);
                Log.Msg($"Diagnostics written: {path}");
            }
            catch (Exception e)
            {
                Log.Error($"Dump failed: {e}");
            }
        }
    }
}
