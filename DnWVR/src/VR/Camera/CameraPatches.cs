using System;
using System.Reflection;
using DnWVR.XR;
using HarmonyLib;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// Harmony hooks that hand the camera to the VR rig while the game keeps its own look state. Each patch is applied
    /// on its own, so a member missing after a game update is logged, not fatal.
    /// </summary>
    public static class CameraPatches
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static AccessTools.FieldRef<LookController, Vector2> s_smoothedLook;
        static AccessTools.FieldRef<LookController, Vector2> s_smoothingVelocity;
        static AccessTools.FieldRef<LookController, float> s_capsuleSmoothdamp, s_viewTopDistance;
        static bool s_loggedLookAtSkip;
        static bool s_loggedLookFromToSkip;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            s_smoothedLook = AccessTools.FieldRefAccess<LookController, Vector2>("smoothedLook");
            s_smoothingVelocity = AccessTools.FieldRefAccess<LookController, Vector2>("smoothingVelocity");
            s_capsuleSmoothdamp = AccessTools.FieldRefAccess<LookController, float>("capsuleSmoothdamp");
            s_viewTopDistance = AccessTools.FieldRefAccess<LookController, float>("viewTopDistance");

            Patch(harmony, typeof(OrbitCameraData), "ApplyTo", prefix: nameof(ApplyTo_Prefix));
            Patch(harmony, typeof(LookController), "LateUpdate",
                prefix: nameof(LookLateUpdate_Prefix), postfix: nameof(LookLateUpdate_Postfix));
            Patch(harmony, typeof(LookController), "AddLookRotation", prefix: nameof(SkipWhenVR));
            Patch(harmony, typeof(OrbitCameraShake), "ApplyShake", prefix: nameof(SkipWhenVR));
            Patch(harmony, typeof(ArbitraryCut), "Update", prefix: nameof(SkipWhenVR));
            Patch(harmony, typeof(PlayerController), "Teleport", postfix: nameof(Teleport_Postfix));
            Patch(harmony, typeof(WalkNWashOrbitCamera), "LookAtGameObject", prefix: nameof(LookAt_Prefix));
            Patch(harmony, typeof(WalkNWashOrbitCamera), "LookFromWindowAtGameObject", prefix: nameof(LookFromTo_Prefix));
            CutsceneState.Bind(harmony);
        }

        static void Patch(HarmonyLib.Harmony harmony, Type target, string method, string prefix = null, string postfix = null)
        {
            try
            {
                var original = AccessTools.Method(target, method);
                if (original == null)
                {
                    Log.Warning($"[CameraPatches] {target.Name}.{method} not found; skipping");
                    return;
                }
                var pre = prefix != null ? new HarmonyMethod(typeof(CameraPatches).GetMethod(prefix, Any)) : null;
                var post = postfix != null ? new HarmonyMethod(typeof(CameraPatches).GetMethod(postfix, Any)) : null;
                harmony.Patch(original, pre, post);
                Log.Msg($"[CameraPatches] patched {target.Name}.{method}");
            }
            catch (Exception e)
            {
                Log.Error($"[CameraPatches] failed to patch {target.Name}.{method}: {e}");
            }
        }

        // ---- OrbitCameraData.ApplyTo: the rig owns the camera transform ---------------------

        static bool ApplyTo_Prefix(ref OrbitCameraData __instance, Camera cam)
        {
            if (!VRRig.Active || cam == null) return true;
            VRRig.SampleHmd();
            // No headset pose yet (session still starting): let the game place its camera as usual.
            if (!VRRig.HasPose) return true;

            cam.cullingMask = __instance.cullingMask;
            // No fieldOfView write: URP overwrites it from each eye's culling projection on every pass.

            var rot = __instance.rotation.normalized;
            Vector3 dir;
            var sp = __instance.screenPoint;
            if (Mathf.Abs(sp.x - 0.5f) < 1e-4f && Mathf.Abs(sp.y - 0.5f) < 1e-4f)
            {
                dir = rot * Vector3.forward;
            }
            else
            {
                float tanHalfV = Mathf.Tan(__instance.fov * 0.5f * Mathf.Deg2Rad);
                float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
                float tanHalfH = tanHalfV * aspect;
                dir = (rot * new Vector3((sp.x - 0.5f) * 2f * tanHalfH, (sp.y - 0.5f) * 2f * tanHalfV, 1f)).normalized;
            }
            var wantedCamPos = __instance.position - dir * __instance.distance;

            // ApplyTo runs inside OrbitCamera.LateUpdate right after the graph processed: the cutscene state is current.
            CutsceneState.Refresh();
            // Black must be on before this frame's UI is rebuilt, or the first step of the flight is visible.
            if (CutsceneState.InTransition && !SceneWalk.Walking) VRFader.ForceBlackNow();
            VRRig.ApplyRequestedPose(cam, wantedCamPos, rot);
            return false;
        }

        // ---- LookController.LateUpdate: mirror the HMD into the look state ------------------

        // The game hangs the head off the top of the capsule, so a body shortened to follow the player's own head would
        // lower the eyes by the duck - and the headset then lowers them again by the same amount, moving the view at twice
        // the head's speed. The head stays at the standing top instead; the game's own crouch is in that number already.
        static void LookLateUpdate_Postfix(LookController __instance)
        {
            if (!VRRig.Active || !PlayerBody.Attached || s_capsuleSmoothdamp == null || s_viewTopDistance == null) return;
            s_capsuleSmoothdamp(__instance) = PlayerBody.StandingTop - s_viewTopDistance(__instance);
        }

        static void LookLateUpdate_Prefix(LookController __instance)
        {
            if (!VRRig.Active) return;
            VRRig.SampleHmd();
            if (!VRRig.HasPose) return;
            // Before the rig's first request in this scene the look state is still the flat player's and VRRig may read
            // it, so a stale RigYaw must not overwrite it.
            if (!VRRig.HasRequest) return;
            LookController.SetLookRotation(VRRig.GameLookRotation);
            __instance.LookInput = Vector2.zero;
            s_smoothedLook(__instance) = Vector2.zero;
            s_smoothingVelocity(__instance) = Vector2.zero;
        }

        // ---- Stray rotation sources: AddLookRotation, ApplyShake, ArbitraryCut --------------

        static bool SkipWhenVR() => !VRRig.Active;

        // ---- Dialogue look-at -----------------------------------------------------------------

        // Yarn <<LookAt target>> moves the camera onto the head-to-target line 4 m before the target; in VR the player
        // turns their head instead. Without a target it is a cancel and still runs, so a running cutscene can end.
        static bool LookAt_Prefix(GameObject target)
        {
            if (!VRRig.Active || VRRig.DialogueCameraZoom || target == null) return true;
            if (!s_loggedLookAtSkip)
            {
                s_loggedLookAtSkip = true;
                Log.Msg("[CameraPatches] dialogue LookAt ignored in VR (camera stays in your head; set DialogueCameraZoom = true to restore)");
            }
            return false;
        }

        // Yarn <<LookFromTo from to>> (window intros) flies the camera onto `from` looking at `to`. Dropped in VR like
        // <<LookAt>>, which the intros interleave with it; a missing argument is a cancel and still runs.
        static bool LookFromTo_Prefix(GameObject from, GameObject to)
        {
            if (!VRRig.Active || VRRig.DialogueCameraZoom || from == null || to == null) return true;
            if (!s_loggedLookFromToSkip)
            {
                s_loggedLookFromToSkip = true;
                Log.Msg("[CameraPatches] dialogue LookFromTo ignored in VR (camera stays in your head; set DialogueCameraZoom = true to restore)");
            }
            return false;
        }

        // ---- Teleport -----------------------------------------------------------------------

        static void Teleport_Postfix(Quaternion rotation)
        {
            if (!VRRig.Active) return;
            VRRig.RecenterYaw(rotation.eulerAngles.y);
            // The head lands on the spawn point wherever the player stands in the room, and the body under it.
            VRRig.RecenterHorizontal();
            PlayerBody.OnTeleport();
            VRFader.Flash(0.35f);
        }
    }
}
