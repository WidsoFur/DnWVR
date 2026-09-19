using System;
using DPG;
using HarmonyLib;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// The active dragon's penis as a smooth tube, rebuilt from the data its shader bends the mesh with: the penetrator's
    /// spline and the girth profile baked from the mesh. The game's own collider segments approximate the same shape a frame
    /// late with overlapping pieces; the tube is rebuilt on every camera write, so late-latched hands match the rendered penis.
    /// A penis made of body bones (BonePenis) gives its bone chain instead.
    /// </summary>
    internal static class PenisTube
    {
        const int Sections = 24;
        const float MinRadius = 0.001f;
        // Room around the tube in which a hand already gets it built: well over a palm, the skin and the shaft's sideways
        // offsets, plus a write's worth of hand travel, so a hand has its side on the shaft before it can reach it.
        const float Margin = 0.35f;
        // The widest the tube is taken to be before it has been built.
        const float MinReach = 0.15f;

        static readonly Vector3[] s_points = new Vector3[Sections + 1];
        static readonly float[] s_radii = new float[Sections + 1];
        static int s_count;
        static int s_builtFor = -1;
        static bool s_failed;
        static AccessTools.FieldRef<WalkNWashPenetratorPhysicsApprox, PenetratorJiggleDeform> s_penetratorField;
        static AccessTools.FieldRef<Penetrator, PenetratorData> s_dataField;

        // Once per camera write, for both hands: the penis and where along its spline it runs.
        static int s_resolvedFor = -1;
        static bool s_resolved;
        static PenetratorJiggleDeform s_penetrator;
        static CatmullSpline s_spline;
        static float s_start, s_length, s_maxRadius;
        static Vector3 s_base, s_tip;

        // The girth along the shaft, which holds for a frame: where the sections sit and how thick the tube is there.
        static readonly float[] s_along = new float[Sections + 1];
        static readonly float[] s_girth = new float[Sections + 1];
        static int s_profileCount;
        static int s_profileFrame = -1;
        static float s_profileLength;
        static PenetratorJiggleDeform s_profileOf;

        public static void Initialize()
        {
            try { s_penetratorField = AccessTools.FieldRefAccess<WalkNWashPenetratorPhysicsApprox, PenetratorJiggleDeform>("penetrator"); }
            catch (Exception e)
            {
                s_failed = true;
                Log.Warning("[PenisTube] penetrator field not found, hands pass through the penis: " + e.Message);
            }
            try { s_dataField = AccessTools.FieldRefAccess<Penetrator, PenetratorData>("penetratorData"); }
            catch (Exception e) { Log.Warning("[PenisTube] penetrator data not found, the tube's offsets take the long way: " + e.Message); }
        }

        /// <summary>
        /// The point of the tube's axis whose surface is nearest to p, with the tube radius and axis direction there. A hand
        /// that holds the shaft gets it however far its controller has gone, so that it keeps the side it rests on.
        /// </summary>
        public static bool Closest(Vector3 p, bool held, out Vector3 center, out float radius, out Vector3 axis)
        {
            center = Vector3.zero;
            radius = 0f;
            axis = Vector3.forward;
            if (!BonePenis.Active && (!Resolve() || (!held && !Near(p)))) return false;
            if (!Build()) return false;
            float best = float.MaxValue;
            for (int i = 0; i + 1 < s_count; i++)
            {
                var a = s_points[i];
                var ab = s_points[i + 1] - a;
                float length2 = ab.sqrMagnitude;
                float t = length2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / length2) : 0f;
                var c = a + ab * t;
                float r = Mathf.Lerp(s_radii[i], s_radii[i + 1], t);
                float gap = Vector3.Distance(p, c) - r;
                if (gap >= best) continue;
                best = gap;
                center = c;
                radius = r;
                if (length2 > 1e-10f) axis = ab / Mathf.Sqrt(length2);
            }
            return true;
        }

        // Whether p is near enough to the tube for building it to matter. The axis is no shorter than either chord to its
        // ends, so all of it lies inside the ellipsoid around base and tip whose string is its length; a point further out
        // than the tube's reach cannot be touching it. Building asks the girth of every blend shape of the body many times
        // over, twice a frame, and a hand that is nowhere near should not pay for it.
        static bool Near(Vector3 p)
        {
            float reach = Mathf.Max(s_maxRadius, MinReach) + Margin;
            return Vector3.Distance(p, s_base) + Vector3.Distance(p, s_tip) <= s_length + 2f * reach;
        }

        static bool Resolve()
        {
            if (s_resolvedFor == VRRig.WriteCount) return s_resolved;
            s_resolvedFor = VRRig.WriteCount;
            s_resolved = false;
            if (s_failed || s_penetratorField == null) return false;
            try
            {
                var approx = HandPatches.ActivePenis();
                if (approx == null || !approx.isActiveAndEnabled) return false;
                var penetrator = s_penetratorField(approx);
                if (penetrator == null || !penetrator.isActiveAndEnabled) return false;
                var spline = penetrator.GetSpline();
                if (spline == null) return false;
                s_penetrator = penetrator;
                s_spline = spline;
                s_start = spline.GetLengthFromSubsection(1);
                s_length = penetrator.GetSquashStretchedWorldLength();
                s_base = spline.GetPositionFromDistance(s_start);
                s_tip = spline.GetPositionFromDistance(s_start + s_length);
                s_resolved = true;
            }
            catch (Exception e) { Fail(e); }
            return s_resolved;
        }

        // Samples the penis the way WalkNWashPenetratorPhysicsApprox places its segments, only finer and up to date.
        static bool Build()
        {
            if (s_builtFor == VRRig.WriteCount) return s_count >= 2;
            s_builtFor = VRRig.WriteCount;
            s_count = 0;
            if (BonePenis.Active)
            {
                s_count = BonePenis.Sample(s_points, s_radii);
                return s_count >= 2;
            }
            if (!Resolve()) return false;
            try
            {
                var penetrator = s_penetrator;
                var spline = s_spline;
                if (s_profileOf != penetrator || s_profileFrame != Time.frameCount || s_profileLength != s_length) Profile(penetrator);
                var data = s_dataField != null ? s_dataField(penetrator) : null;
                float squash = penetrator.GetSquashAndStretchRatio();
                float widest = 0f;
                for (int i = 0; i < s_profileCount; i++)
                {
                    float along = s_along[i], distance = s_start + along;
                    // Penetrator.GetWorldOffset also works out a reference frame it then throws away.
                    var offset = data != null ? data.GetWorldOffset(along / squash) : penetrator.GetWorldOffset(along, spline, distance);
                    s_points[i] = spline.GetPositionFromDistance(distance) + offset;
                    s_radii[i] = s_girth[i];
                    widest = Mathf.Max(widest, s_girth[i]);
                }
                s_count = s_profileCount;
                s_maxRadius = widest;
            }
            catch (Exception e) { Fail(e); }
            return s_count >= 2;
        }

        // The girth for this frame; the offsets turn with the dragon and are worked out on every write.
        static void Profile(PenetratorJiggleDeform penetrator)
        {
            s_profileOf = penetrator;
            s_profileFrame = Time.frameCount;
            s_profileLength = s_length;
            s_profileCount = 0;
            float length = s_length;
            float lastAlong = -1f;
            void Add(float along, float girth)
            {
                s_along[s_profileCount] = along;
                s_girth[s_profileCount] = girth;
                s_profileCount++;
                lastAlong = along;
            }
            // The tube's end is a ball around its last point: it ends where that ball reaches the tip, not a radius past it.
            float inside = 0f;
            for (int i = 0; i <= Sections; i++)
            {
                float along = length * i / Sections;
                float girth = penetrator.GetWorldGirthRadius(along);
                if (along + girth > length)
                {
                    float lo = inside, hi = along;
                    for (int k = 0; k < 10; k++)
                    {
                        float mid = 0.5f * (lo + hi);
                        if (mid + penetrator.GetWorldGirthRadius(mid) > length) hi = mid;
                        else lo = mid;
                    }
                    girth = penetrator.GetWorldGirthRadius(lo);
                    if (girth > MinRadius && lo > lastAlong + 1e-4f) Add(lo, girth);
                    break;
                }
                inside = along;
                if (girth > MinRadius) Add(along, girth);
            }
        }

        static void Fail(Exception e)
        {
            s_failed = true;
            s_resolved = false;
            s_count = 0;
            s_profileOf = null;
            Log.Warning("[PenisTube] disabled, hands pass through the penis: " + e);
        }
    }
}
