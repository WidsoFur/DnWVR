using System.Collections.Generic;
using DnWVR.VR;
using DnWVR.XR;
using UnityEngine;
using UnityEngine.XR;

namespace DnWVR.Diag
{
    /// <summary>
    /// Every few seconds while VR runs, a line saying how long frames took and how long the GPU and the compositor took,
    /// as the runtime reports them. It is what tells a card that cannot keep up from a frame that is only waiting, and it
    /// is the number every performance change is judged by.
    /// </summary>
    public static class PerfLog
    {
        public static bool Enabled = true;

        const float Period = 5f;
        static readonly List<float> s_frames = new List<float>(1024);
        static float s_next;

        /// <summary>Starts a new window, so that no line mixes frames from before a change with frames after it.</summary>
        public static void Restart()
        {
            s_frames.Clear();
            s_next = Time.unscaledTime + Period;
        }

        /// <summary>Once a frame.</summary>
        public static void Tick()
        {
            if (!Enabled || !XRBootstrap.IsRunning)
            {
                s_frames.Clear();
                return;
            }
            s_frames.Add(Time.unscaledDeltaTime);
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + Period;
            if (s_frames.Count < 2) return;

            float total = 0f;
            foreach (float dt in s_frames) total += dt;
            s_frames.Sort();
            int count = s_frames.Count;
            float mean = total / count * 1000f;
            float p95 = s_frames[(int)(0.95f * (count - 1))] * 1000f;
            float worst = s_frames[count - 1] * 1000f;
            s_frames.Clear();

            var display = XRBootstrap.Loader != null ? XRBootstrap.Loader.GetLoadedSubsystem<XRDisplaySubsystem>() : null;
            string gpu = "n/a", compositor = "n/a", dropped = "n/a", refresh = "n/a";
            if (display != null)
            {
                if (display.TryGetAppGPUTimeLastFrame(out float g)) gpu = $"{g:0.0} ms";
                if (display.TryGetCompositorGPUTimeLastFrame(out float c)) compositor = $"{c:0.0} ms";
                if (display.TryGetDroppedFrameCount(out int d)) dropped = d.ToString();
                if (display.TryGetDisplayRefreshRate(out float hz)) refresh = $"{hz:0} Hz";
            }
            Log.Msg($"[Perf] {VRPerformance.Name}, {count} frames: mean {mean:0.0} ms, p95 {p95:0.0} ms, worst {worst:0.0} ms; app GPU {gpu}, " +
                    $"compositor {compositor}, dropped {dropped}, display {refresh}");
        }
    }
}
