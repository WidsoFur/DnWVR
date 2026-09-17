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

        static readonly Vector3[] s_points = new Vector3[Sections + 1];
        static readonly float[] s_radii = new float[Sections + 1];
        static int s_count;
        static int s_builtFor = -1;
        static bool s_failed;
        static AccessTools.FieldRef<WalkNWashPenetratorPhysicsApprox, PenetratorJiggleDeform> s_penetrator;

        public static void Initialize()
        {
            try { s_penetrator = AccessTools.FieldRefAccess<WalkNWashPenetratorPhysicsApprox, PenetratorJiggleDeform>("penetrator"); }
            catch (Exception e)
            {
                s_failed = true;
                Log.Warning("[PenisTube] penetrator field not found, hands pass through the penis: " + e.Message);
            }
        }

        /// <summary>The point of the tube's axis whose surface is nearest to p, with the tube radius and axis direction there.</summary>
        public static bool Closest(Vector3 p, out Vector3 center, out float radius, out Vector3 axis)
        {
            center = Vector3.zero;
            radius = 0f;
            axis = Vector3.forward;
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
            if (s_failed || s_penetrator == null) return false;
            try
            {
                var approx = HandPatches.ActivePenis();
                if (approx == null || !approx.isActiveAndEnabled) return false;
                var penetrator = s_penetrator(approx);
                if (penetrator == null || !penetrator.isActiveAndEnabled) return false;
                var spline = penetrator.GetSpline();
                if (spline == null) return false;
                float start = spline.GetLengthFromSubsection(1);
                float length = penetrator.GetSquashStretchedWorldLength();
                float lastAlong = -1f;
                void Add(float along, float girth)
                {
                    float distance = start + along;
                    s_points[s_count] = spline.GetPositionFromDistance(distance) + penetrator.GetWorldOffset(along, spline, distance);
                    s_radii[s_count] = girth;
                    s_count++;
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
            catch (Exception e)
            {
                s_failed = true;
                s_count = 0;
                Log.Warning("[PenisTube] disabled, hands pass through the penis: " + e);
            }
            return s_count >= 2;
        }
    }
}
