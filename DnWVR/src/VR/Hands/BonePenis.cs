using System.Collections.Generic;
using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>
    /// A dragon whose penis is part of its body mesh (Alexander in his sex scene: bones Dick.x.001..Dick.x.head, with the DPG
    /// penetrator switched off) has no penis colliders, so the paws went through it. Capsules along those bones make it
    /// touchable like the other dragons' (they sit under the dragon, so they count as the dragon), and the bone chain gives
    /// PenisTube the tube the hands rest on. The radii were measured from the body mesh: vertices mostly weighted to each
    /// bone, around that bone's axis.
    /// </summary>
    internal static class BonePenis
    {
        // Base to tip, with the penis radius (m at scale 1) at each point; the tip ends a little past CumOutput.
        static readonly string[] Bones = { "Dick.x.001", "Dick.x.002", "Dick.x.003", "Dick.x.004", "Dick.x.head", "CumOutput" };
        static readonly float[] Radii = { 0.2f, 0.2f, 0.2f, 0.18f, 0.15f, 0.05f };

        static readonly Transform[] s_chain = new Transform[Bones.Length];
        static readonly HashSet<Collider> s_colliders = new HashSet<Collider>();
        static GameObject s_dragon;

        /// <summary>The penis colliders exist and follow the bones.</summary>
        public static bool Active => s_dragon != null && s_chain[0] != null;

        /// <summary>One of the capsules along the bone penis.</summary>
        public static bool Owns(Collider col) => col != null && s_colliders.Contains(col);

        /// <summary>Builds the capsules for the active dragon when its penis is bones only; safe to call repeatedly.</summary>
        public static void Attach()
        {
            if (!WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.gameObject == null) return;
            if (Active && s_dragon == dragon.gameObject) return;
            Reset();
            // A DPG penis brings its own colliders.
            if (HandPatches.ActivePenis() != null) return;
            var root = dragon.gameObject.transform;
            for (int i = 0; i < Bones.Length; i++)
            {
                s_chain[i] = FindDeep(root, Bones[i]);
                if (s_chain[i] != null) continue;
                Reset();
                return;
            }
            for (int i = 0; i + 1 < Bones.Length; i++) AddSegment(i);
            s_dragon = dragon.gameObject;
            Log.Msg($"[BonePenis] {s_dragon.name}: {s_colliders.Count} colliders along {Bones[0]}..{Bones[Bones.Length - 1]}");
        }

        /// <summary>Forgets the capsules (they go with their scene).</summary>
        public static void Reset()
        {
            foreach (var c in s_colliders)
                if (c != null) Object.Destroy(c.gameObject);
            s_colliders.Clear();
            for (int i = 0; i < s_chain.Length; i++) s_chain[i] = null;
            s_dragon = null;
        }

        /// <summary>The penis as tube points from base to tip with their radii; returns the count (0 without a bone penis).</summary>
        public static int Sample(Vector3[] points, float[] radii)
        {
            if (!Active) return 0;
            int n = Mathf.Min(points.Length, Bones.Length);
            for (int i = 0; i < n; i++)
            {
                var bone = s_chain[i];
                if (bone == null) return 0;
                points[i] = bone.position;
                radii[i] = Radii[i] * Mathf.Abs(bone.lossyScale.x);
            }
            return n;
        }

        // A capsule under bone i reaching to bone i + 1, so it bends and scales with the bones. The last one stops at the tip
        // instead of a radius past it.
        static void AddSegment(int i)
        {
            var bone = s_chain[i];
            var end = bone.InverseTransformPoint(s_chain[i + 1].position);
            float length = end.magnitude;
            if (length < 1e-4f) return;
            var axis = end / length;
            float scale = Mathf.Max(1e-4f, Mathf.Abs(bone.lossyScale.x));
            float radius = Mathf.Max(Radii[i], Radii[i + 1]) / scale;
            bool last = i + 2 == Bones.Length;
            float from = -radius;
            float to = last ? length + Radii[i + 1] / scale : length + radius;

            var go = new GameObject("DnWVR_PenisCollider") { layer = 0 };
            go.transform.SetParent(bone, false);
            go.transform.localPosition = axis * ((from + to) * 0.5f);
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = radius;
            capsule.height = Mathf.Max(to - from, 2f * radius);
            s_colliders.Add(capsule);
        }

        static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
