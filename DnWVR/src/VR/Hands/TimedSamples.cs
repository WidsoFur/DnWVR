using UnityEngine;

namespace DnWVR.VR
{
    /// <summary>A small ring of timestamped values: the largest or the total over the last few tenths of a second.</summary>
    internal sealed class TimedSamples
    {
        readonly float[] _time, _value;
        int _next, _count;

        public TimedSamples(int capacity)
        {
            _time = new float[capacity];
            _value = new float[capacity];
        }

        public void Add(float time, float value)
        {
            _time[_next] = time;
            _value[_next] = value;
            _next = (_next + 1) % _time.Length;
            if (_count < _time.Length) _count++;
        }

        public void Clear() => _count = 0;

        public float Max(float now, float window) => Fold(now, window, max: true);
        public float Sum(float now, float window) => Fold(now, window, max: false);

        float Fold(float now, float window, bool max)
        {
            float r = 0f;
            // Newest first; times only grow, so the first sample outside the window ends the walk.
            for (int i = 1; i <= _count; i++)
            {
                int k = (_next - i + _time.Length) % _time.Length;
                if (now - _time[k] > window) break;
                r = max ? Mathf.Max(r, _value[k]) : r + _value[k];
            }
            return r;
        }
    }
}
