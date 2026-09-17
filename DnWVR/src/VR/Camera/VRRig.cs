using DnWVR.XR;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// Maps the camera pose the game requests onto the tracked headset: the request gives the position and, at most once
    /// per camera spot, the yaw; the HMD gives head rotation and small offsets, so physical turning is never fought.
    ///   camRot = rigRot * hmdLocalRot
    ///   camPos = basePos + rigRot * (hmdLocalPos - hmdNeutralPos)
    /// basePos is the game's head position in gameplay and the settled camera spot in cutscenes.
    /// </summary>
    public static class VRRig
    {
        public static bool TrackingEnabled = true;

        /// <summary>World yaw (degrees) of tracking space; changes only on turns, teleports and camera cuts.</summary>
        public static float RigYaw;
        /// <summary>Degrees. A jump of the requested yaw between two gameplay frames above this means the game turned the player.</summary>
        public static float YawSnapThreshold = 12f;
        /// <summary>Metres. A jump of the requested camera position between two gameplay frames above this is a teleport.</summary>
        public static float PositionCutThreshold = 0.5f;
        /// <summary>HMD position (tracking space) that maps onto the game's head position.</summary>
        public static Vector3 HmdNeutralPos;

        /// <summary>
        /// How high the game itself holds your eyes over the floor, in centimetres - in the wash and, through
        /// <see cref="SceneWalk"/>, in the sex scenes. At this height the camera is exactly the game's own.
        /// </summary>
        public const int GameHeightCm = 180;

        /// <summary>
        /// How high your own eyes are over the floor, in centimetres, from the VR section of the options screen. Above
        /// the game's height your eyes rise over the character's, below it they drop: the neutral head point moves the
        /// other way by the difference, which lifts the hands with the head, both being placed through this conversion.
        /// </summary>
        public static int HeightCm = GameHeightCm;

        /// <summary>Neutral head point with the height calibration folded in.</summary>
        static Vector3 Neutral =>
            new Vector3(HmdNeutralPos.x, HmdNeutralPos.y - (HeightCm - GameHeightCm) * 0.01f, HmdNeutralPos.z);

        // ---- cutscene behaviour (preferences) ----
        /// <summary>
        /// Let dialogue &lt;&lt;LookAt&gt;&gt; and &lt;&lt;LookFromTo&gt;&gt; (window intros) move the camera like the flat game.
        /// Off = both are dropped, so the camera never moves, fades or turns during dialogue.
        /// </summary>
        public static bool DialogueCameraZoom = false;
        /// <summary>Follow the cutscene camera's animation exactly instead of anchoring at the spot it settled on.</summary>
        public static bool CutsceneFollowAnimation = false;
        /// <summary>When the game settles on a new camera spot, turn the player once (under a fade) to face where it looks.</summary>
        public static bool AlignYawOnCameraCut = true;
        /// <summary>Metres. If an anchored cutscene camera drifts further than this (root motion, moving target), re-anchor under a fade.</summary>
        public static float CutsceneDriftThreshold = 1.0f;

        public static Vector3 HmdLocalPos { get; private set; }
        public static Quaternion HmdLocalRot { get; private set; } = Quaternion.identity;
        public static bool HasPose { get; private set; }
        public static bool HmdTracked { get; private set; }

        public static Camera Camera { get; private set; }
        static Vector3 s_wantedCamPos;
        static Quaternion s_rigRot = Quaternion.identity;
        static bool s_hasRequest;
        static bool s_neutralCaptured;
        static bool s_yawInitialized;
        static float s_prevWantedYaw;
        static Vector3 s_prevWantedPos;
        static bool s_rigChanged;

        // Current cutscene anchor; cleared during every camera flight.
        static Vector3 s_anchor;
        static bool s_anchorValid;
        static int s_anchorGen = -1;

        // The first cutscene spot after a scene load always sets the facing, or it would keep the previous level's yaw.
        // The budget (seconds) drains only on gameplay frames, at most 0.1 s each, so load hitches cannot use it up.
        const float SceneAlignWindow = 4f;
        static bool s_sceneAlignPending;
        static float s_sceneAlignBudget;

        public static Quaternion RigRotation => Quaternion.Euler(0f, RigYaw, 0f);
        public static bool Active => XRBootstrap.IsRunning && TrackingEnabled;
        public static bool HasRequest => s_hasRequest;
        /// <summary>The last camera request was gameplay: the camera is in the player's head, not at a cutscene spot.</summary>
        public static bool Gameplay { get; private set; }
        /// <summary>World offset of the headset from the head position the game requested (where the player walked in the room).</summary>
        public static Vector3 HeadOffsetWorld => s_rigRot * (HmdLocalPos - HmdNeutralPos);

        /// <summary>World position of the head's base (the camera before the headset's own offset), as last written.</summary>
        public static Vector3 BasePosition => s_wantedCamPos;

        internal static void MarkRigChanged() => s_rigChanged = true;

        /// <summary>Raised right after the camera transform is written (hands follow here).</summary>
        public static event System.Action AfterCameraWrite;
        /// <summary>Counts camera writes (LateUpdate and the late latch); caches of per-write state key on it.</summary>
        public static int WriteCount { get; private set; }

        /// <summary>True once after any discontinuous change of tracking space (turn, recenter, cut); cleared by the reader.</summary>
        public static bool ConsumeRigChanged()
        {
            bool v = s_rigChanged;
            s_rigChanged = false;
            return v;
        }

        /// <summary>Pull the latest HMD sample. Call once per frame before consumers, and again before render.</summary>
        public static void SampleHmd()
        {
            if (!XRBootstrap.IsRunning) { HasPose = false; return; }
            if (HeadPose.TryGet(out var pos, out var rot, out var tracked))
            {
                HmdLocalPos = pos;
                HmdLocalRot = rot;
                HmdTracked = tracked;
                HasPose = true;
                // The neutral point must come from a real tracked sample, not a zero pose during startup.
                if (!s_neutralCaptured && tracked && pos.sqrMagnitude > 1e-4f)
                {
                    HmdNeutralPos = pos;
                    s_neutralCaptured = true;
                }
            }
        }

        public static float HmdLocalYaw => HmdLocalRot.eulerAngles.y;
        public static float HmdLocalPitch => Mathf.Clamp(Mathf.Repeat(HmdLocalRot.eulerAngles.x + 180f, 360f) - 180f, -89f, 89f);

        /// <summary>World-space look rotation the game should believe in (rig + head yaw, head pitch, no roll).</summary>
        public static Quaternion GameLookRotation => Quaternion.Euler(HmdLocalPitch, RigYaw + HmdLocalYaw, 0f);

        /// <summary>Tracking-space pose (as reported by the XR input subsystem) to world space.</summary>
        public static void TrackingToWorld(Vector3 localPos, Quaternion localRot, out Vector3 worldPos, out Quaternion worldRot)
        {
            worldPos = s_wantedCamPos + s_rigRot * (localPos - Neutral);
            worldRot = s_rigRot * localRot;
        }

        /// <summary>
        /// Applies the camera pose the game asked for (OrbitCameraData.ApplyTo hook, StaticCameraFollower).
        /// The caller must have refreshed CutsceneState this frame.
        /// </summary>
        public static void ApplyRequestedPose(Camera cam, Vector3 wantedCamPos, Quaternion wantedRot)
        {
            Camera = cam;
            float wantedYaw = wantedRot.eulerAngles.y;
            Vector3 basePos = wantedCamPos;

            // A dropped dialogue camera move that is already running is not a camera spot: CutsceneState cancels it under
            // black, and until the blend back starts it counts as gameplay without the turn/cut checks.
            bool dialogueDropped = CutsceneState.DialogueCameraDropped;
            bool cutscene = CutsceneState.Active && !dialogueDropped;
            Gameplay = !cutscene;

            if (s_hasRequest && !cutscene)
                s_sceneAlignBudget -= Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            if (!s_yawInitialized || !s_hasRequest)
            {
                // First request in this scene: align the physical facing with the game's. A dropped dialogue camera looks
                // from its own spot, so use the player's look, which CameraPatches leaves alone until this request.
                float yaw = wantedYaw;
                if (dialogueDropped)
                {
                    try { yaw = LookController.GetLookRotation().eulerAngles.y; }
                    catch { }
                }
                RigYaw = Mathf.Repeat(yaw - HmdLocalYaw, 360f);
                s_yawInitialized = true;
                s_rigChanged = true;
                s_anchorValid = false;
            }
            else if (SceneWalk.Walking)
            {
                // The player walks the scene: its camera spots and cuts no longer place the head.
                basePos = SceneWalk.Base;
                s_anchorValid = false;
                s_anchorGen = -1;
            }
            else if (cutscene)
            {
                if (CutsceneState.InTransition)
                {
                    // Camera flight between two spots: follow the request while VRFader keeps the view black.
                    s_anchorValid = false;
                }
                else if (!s_anchorValid || s_anchorGen != CutsceneState.Generation)
                {
                    // A re-request of the spot already on screen (decided once by CutsceneState) never passes through a
                    // flight, so the anchor is still valid: adopt the new generation and change nothing else.
                    bool sameSpot = s_anchorValid && CutsceneState.SameSpotGeneration == CutsceneState.Generation;
                    s_anchorGen = CutsceneState.Generation;
                    if (!sameSpot)
                    {
                        // Settled on a new camera spot: anchor there, optionally face it once. The head stays free afterwards.
                        s_anchor = wantedCamPos;
                        s_anchorValid = true;
                        bool align = AlignYawOnCameraCut || (s_sceneAlignPending && s_sceneAlignBudget > 0f);
                        s_sceneAlignPending = false;
                        if (align)
                        {
                            float err = Mathf.DeltaAngle(RigYaw + HmdLocalYaw, wantedYaw);
                            if (Mathf.Abs(err) > 1f) SetRigYawKeepingHead(RigYaw + err);
                        }
                        s_rigChanged = true;
                        VRFader.Flash(0.25f);
                    }
                }
                else if (!CutsceneFollowAnimation && Vector3.Distance(wantedCamPos, s_anchor) > CutsceneDriftThreshold)
                {
                    // The spot itself moved away (root motion, moving LookFromTo source): re-anchor, never slide.
                    s_anchor = wantedCamPos;
                    s_rigChanged = true;
                    VRFader.Flash(0.35f);
                }

                if (s_anchorValid && !CutsceneFollowAnimation) basePos = s_anchor;
            }
            else
            {
                s_anchorValid = false;
                s_anchorGen = -1;
                if (!dialogueDropped)
                {
                    // Gameplay: LookController mirrors RigYaw + head yaw, so the request only disagrees when the
                    // game itself turned the player (respawn, scripted facing). Snap turns move both together.
                    float requestJump = Mathf.DeltaAngle(s_prevWantedYaw, wantedYaw);
                    float err = Mathf.DeltaAngle(RigYaw + HmdLocalYaw, wantedYaw);
                    if (Mathf.Abs(requestJump) > YawSnapThreshold && Mathf.Abs(err) > YawSnapThreshold)
                    {
                        SetRigYawKeepingHead(RigYaw + err);
                        VRFader.Flash(0.35f);
                    }
                    if (Vector3.Distance(s_prevWantedPos, wantedCamPos) > PositionCutThreshold)
                    {
                        s_rigChanged = true;
                        VRFader.Flash(0.35f);
                    }
                }
            }

            s_prevWantedYaw = wantedYaw;
            s_prevWantedPos = wantedCamPos;
            s_wantedCamPos = basePos;
            s_rigRot = Quaternion.Euler(0f, RigYaw, 0f);
            s_hasRequest = true;
            WriteCamera();
        }

        /// <summary>Pref LateLatchHead: re-sample the headset right before rendering (off = use the LateUpdate sample).</summary>
        public static bool LateLatchEnabled = true;

        /// <summary>Re-apply the latest request with the freshest HMD sample (onBeforeRender).</summary>
        public static void LateLatch()
        {
            if (!LateLatchEnabled || !s_hasRequest || Camera == null || !TrackingEnabled) return;
            SampleHmd();
            WriteCamera();
        }

        /// <summary>
        /// Largest near clip plane (metres) for the headset camera. Flat-screen cameras may use a far one (main menu: 0.3 m)
        /// that cuts off hands and arm's-reach menus; both eyes inherit it, as the display z range is global.
        /// </summary>
        public static float MaxNearClip = 0.05f;
        static Camera s_nearClampedCam;
        static float s_nearAuthored;

        static void ClampNearClip(Camera cam)
        {
            if (cam.nearClipPlane <= MaxNearClip) return;
            if (s_nearClampedCam != cam) RestoreNearClip();
            s_nearClampedCam = cam;
            s_nearAuthored = cam.nearClipPlane;
            cam.nearClipPlane = MaxNearClip;
        }

        /// <summary>Give the clamped camera its authored near plane back (XR stop).</summary>
        public static void RestoreNearClip()
        {
            if (s_nearClampedCam != null) s_nearClampedCam.nearClipPlane = s_nearAuthored;
            s_nearClampedCam = null;
        }

        static void WriteCamera()
        {
            if (Camera == null || !HasPose) return;
            ClampNearClip(Camera);
            var t = Camera.transform;
            t.rotation = s_rigRot * HmdLocalRot;
            t.position = s_wantedCamPos + s_rigRot * (HmdLocalPos - Neutral);
            WriteCount++;
            AfterCameraWrite?.Invoke();
        }

        // Keeps the eyes in place: the neutral point moves so the rotated offset (hmd - neutral) keeps its world position.
        static void SetRigYawKeepingHead(float newYaw, bool markChanged = true)
        {
            var oldRot = Quaternion.Euler(0f, RigYaw, 0f);
            newYaw = Mathf.Repeat(newYaw, 360f);
            var newRot = Quaternion.Euler(0f, newYaw, 0f);
            var offsetWorld = oldRot * (HmdLocalPos - HmdNeutralPos);
            HmdNeutralPos = HmdLocalPos - Quaternion.Inverse(newRot) * offsetWorld;
            RigYaw = newYaw;
            s_rigRot = newRot;
            if (markChanged) s_rigChanged = true;
        }

        /// <summary>
        /// Rotates tracking space around the player's head. A continuous (smooth-turn) step is not a rig change, so the
        /// hands keep following physically instead of snapping through geometry every frame the stick is held.
        /// </summary>
        public static void Turn(float degrees, bool continuous = false)
        {
            SetRigYawKeepingHead(RigYaw + degrees, markChanged: !continuous);
        }

        /// <summary>Make the current physical facing equal to the given world yaw (teleports, cutscene exits).</summary>
        public static void RecenterYaw(float worldYaw)
        {
            SetRigYawKeepingHead(worldYaw - HmdLocalYaw);
            s_yawInitialized = true;
        }

        /// <summary>Make the current physical head position the neutral one (room-scale re-center).</summary>
        public static void RecenterPosition()
        {
            HmdNeutralPos = HmdLocalPos;
            s_neutralCaptured = true;
            s_rigChanged = true;
        }

        /// <summary>Puts the head back on the game's head position horizontally, keeping the calibrated height.</summary>
        public static void RecenterHorizontal()
        {
            if (!s_neutralCaptured) return;
            HmdNeutralPos = new Vector3(HmdLocalPos.x, HmdNeutralPos.y, HmdLocalPos.z);
            s_rigChanged = true;
        }

        public static void ResetForNewScene()
        {
            s_hasRequest = false;
            Gameplay = false;
            s_yawInitialized = false;
            s_anchorValid = false;
            s_anchorGen = -1;
            s_sceneAlignPending = true;
            s_sceneAlignBudget = SceneAlignWindow;
            Camera = null;
        }
    }

    /// <summary>
    /// Static cameras (main menu, credits): the authored pose is fed through the rig exactly like the
    /// gameplay camera, so hands, laser and late-latching behave identically in every scene.
    /// </summary>
    public class StaticCameraFollower : MonoBehaviour
    {
        Vector3 _basePos;
        Quaternion _baseRot;
        Camera _cam;

        void OnEnable()
        {
            _basePos = transform.position;
            _baseRot = transform.rotation;
            _cam = GetComponent<Camera>();
        }

        void LateUpdate()
        {
            if (GetComponent<OrbitCamera>() != null) { enabled = false; return; }
            if (!VRRig.Active || _cam == null) return;
            VRRig.SampleHmd();
            if (!VRRig.HasPose) return;
            CutsceneState.Refresh();
            if (CutsceneState.InTransition && !SceneWalk.Walking) VRFader.ForceBlackNow();
            VRRig.ApplyRequestedPose(_cam, _basePos, _baseRot);
        }
    }
}
