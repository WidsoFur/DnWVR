using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// Walking in the sex scenes (all but Ryan's, where the player lies under him): the left stick moves the player like in
    /// the wash, at standing eye height over the floor. A body from knee to head is stopped by walls, furniture and the
    /// dragons, and the floor follows only steps a person climbs, never a bed, a table or a dragon's back. Conrad's scene
    /// starts in its own first-person spot and the first step stands the player up; Alexander's spot sits the player low,
    /// so there, and in the scene the player watches (instead of riding its camera cuts), the player stands from the start.
    /// </summary>
    public static class SceneWalk
    {
        /// <summary>The stick walks in the sex scenes.</summary>
        public static bool Enabled = true;
        /// <summary>Walking speed (m/s) at full stick.</summary>
        public static float Speed = 1.5f;
        /// <summary>Eye height (m) over the floor while standing; the wash's is 1.8.</summary>
        public static float EyeHeight = 1.8f;

        const float StepHeight = 0.35f;     // floor rises the walk climbs; anything higher is an obstacle
        const float BodyRadius = 0.25f;
        const float Skin = 0.02f;
        const float Deadzone = 0.2f;
        const float MaxFromDragon = 8f;     // scenes without walls end in the void
        const float FloorProbe = 4f;
        const float FloorSpeed = 3f;        // m/s the eyes follow a step in the floor

        static readonly RaycastHit[] s_hits = new RaycastHit[16];
        static bool s_walking;
        static Vector3 s_base;
        static float s_floor;

        /// <summary>The player walks the scene: the head's base is <see cref="Base"/> instead of the scene's camera spot.</summary>
        public static bool Walking => s_walking;
        /// <summary>World position of the head's base (before the headset's own offset).</summary>
        public static Vector3 Base => s_base;

        static bool Allowed => Enabled && SexScene.InSexScene && SexScene.SceneName != "SexScene1";

        /// <summary>A new scene starts where its camera puts the player.</summary>
        public static void Reset() => s_walking = false;

        /// <summary>Every frame (Update), before the camera is written.</summary>
        public static void Tick()
        {
            if (!Allowed || !VRRig.HasRequest)
            {
                s_walking = false;
                return;
            }
            var stick = VRInput.Left.Valid ? VRInput.Left.Stick : Vector2.zero;
            float amount = Mathf.InverseLerp(Deadzone, 1f, stick.magnitude);
            if (!s_walking)
            {
                bool standAtStart = SexScene.Spectating || SexScene.SceneName == "SexScene2";
                bool start = standAtStart ? Time.timeSinceLevelLoad > 1f : amount > 0f;
                if (!start || !CutsceneState.Active || CutsceneState.InTransition) return;
                Begin();
            }
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (amount > 0f)
            {
                var dir = Quaternion.Euler(0f, VRRig.RigYaw + VRRig.HmdLocalYaw, 0f) * new Vector3(stick.x, 0f, stick.y).normalized;
                Move(dir * (Speed * amount * dt));
            }
            if (FindFloor(s_base + VRRig.HeadOffsetWorld, s_base.y, s_floor + StepHeight, out float floor))
                s_floor = Mathf.MoveTowards(s_floor, floor, FloorSpeed * dt);
            s_base.y = s_floor + EyeHeight;
        }

        static void Begin()
        {
            var start = VRRig.BasePosition;
            s_floor = FindFloor(start, start.y + 0.5f, float.PositiveInfinity, out float floor) ? floor : DragonFloor(start.y - EyeHeight);
            float eyes = s_floor + EyeHeight;
            // Standing up out of a kneeling or lying pose is a jump: hide it.
            if (Mathf.Abs(eyes - start.y) > 0.25f) VRFader.Flash(0.3f);
            s_base = new Vector3(start.x, eyes, start.z);
            s_walking = true;
            VRRig.MarkRigChanged();
        }

        // Without a floor collider the dragons stand on the floor.
        static float DragonFloor(float fallback) =>
            WalkNWashSceneState.TryGetActiveDragon(out var dragon) && dragon.gameObject != null ? dragon.gameObject.transform.position.y : fallback;

        static void Move(Vector3 step)
        {
            float distance = step.magnitude;
            if (distance < 1e-5f) return;
            var dir = step / distance;
            if (!Blocked(dir, distance, out float free, out var normal))
            {
                s_base += step;
            }
            else
            {
                s_base += dir * free;
                // Slide along what stopped the body.
                var wall = new Vector3(normal.x, 0f, normal.z);
                if (wall.sqrMagnitude > 1e-6f)
                {
                    var slide = Vector3.ProjectOnPlane(dir * (distance - free), wall.normalized);
                    slide.y = 0f;
                    float slideDistance = slide.magnitude;
                    if (slideDistance > 1e-5f)
                    {
                        var slideDir = slide / slideDistance;
                        if (Blocked(slideDir, slideDistance, out float slideFree, out _)) slideDistance = slideFree;
                        s_base += slideDir * slideDistance;
                    }
                }
            }
            if (WalkNWashSceneState.TryGetActiveDragon(out var dragon) && dragon.gameObject != null)
            {
                var center = dragon.gameObject.transform.position;
                var flat = new Vector3(s_base.x - center.x, 0f, s_base.z - center.z);
                if (flat.magnitude > MaxFromDragon)
                {
                    flat = flat.normalized * MaxFromDragon;
                    s_base = new Vector3(center.x + flat.x, s_base.y, center.z + flat.z);
                }
            }
        }

        // The body from above a step to the head, where the player's head is in the room.
        static bool Blocked(Vector3 dir, float distance, out float free, out Vector3 normal)
        {
            free = distance;
            normal = Vector3.zero;
            var head = s_base + VRRig.HeadOffsetWorld;
            var knees = new Vector3(head.x, Mathf.Min(s_floor + StepHeight + BodyRadius, head.y), head.z);
            int n = Physics.CapsuleCastNonAlloc(knees, head, BodyRadius, dir, s_hits, distance + Skin, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var hit = s_hits[i];
                // Already inside (the player walked into it in the room): let the body out.
                if (hit.collider == null || hit.distance <= 0f || VRHands.IsHandCollider(hit.collider)) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    normal = hit.normal;
                }
            }
            if (nearest == float.MaxValue) return false;
            free = Mathf.Clamp(nearest - Skin, 0f, distance);
            return true;
        }

        static bool FindFloor(Vector3 at, float fromY, float highest, out float floor)
        {
            floor = 0f;
            int n = Physics.RaycastNonAlloc(new Vector3(at.x, fromY, at.z), Vector3.down, s_hits, FloorProbe, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var hit = s_hits[i];
                var col = hit.collider;
                if (col == null || hit.normal.y < 0.5f || hit.point.y > highest || VRHands.IsHandCollider(col) || IsDragon(col)) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    floor = hit.point.y;
                }
            }
            return nearest != float.MaxValue;
        }

        // Any dragon of the scene (Ryan + Conrad has two): its skin collider lives under its descriptor.
        static bool IsDragon(Collider col) =>
            HandPatches.IsDragonCollider(col) || col.GetComponentInParent<WalkNWashDragonDescriptor>() != null;
    }
}
