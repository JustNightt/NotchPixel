using System;
using OpenPixel.Core;

namespace OpenPixel.Core
{
    // ========================================================================
    // Rolling 60-sample history for the PC Stats widget sparklines.
    // Fed from the 1 Hz tick so the graphs are real measurements, never
    // synthesised curves.
    // ========================================================================
    public static class TelemetryHistory
    {
        public const int Capacity = 60;

        private static readonly float[] _cpu = new float[Capacity];
        private static readonly float[] _gpu = new float[Capacity];
        private static readonly float[] _ram = new float[Capacity];
        private static readonly float[] _netDown = new float[Capacity];
        private static readonly float[] _netUp = new float[Capacity];
        private static int _count;
        private static int _head;
        private static long _lastSampleMs;

        public static int Count => _count;

        // Samples at most once per second, so a 16 ms render loop cannot flood it.
        public static void Sample(HardwareTelemetry t, bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now - _lastSampleMs < 900) return;
            _lastSampleMs = now;

            _cpu[_head] = t.CpuPercent;
            _gpu[_head] = t.GpuPercentAvailable ? t.GpuPercent : 0.0f;
            _ram[_head] = t.RamPercent;
            _netDown[_head] = t.NetDownKbps;
            _netUp[_head] = t.NetUpKbps;

            _head = (_head + 1) % Capacity;
            if (_count < Capacity) ++_count;
        }

        public static void Copy(string series, float[] destination)
        {
            if (destination == null) return;
            float[] source = series switch
            {
                "gpu" => _gpu,
                "ram" => _ram,
                "down" => _netDown,
                "up" => _netUp,
                _ => _cpu
            };

            int n = Math.Min(destination.Length, _count);
            for (int i = 0; i < n; ++i)
            {
                int idx = (_head - n + i + Capacity * 2) % Capacity;
                destination[i] = source[idx];
            }
            for (int i = n; i < destination.Length; ++i) destination[i] = 0.0f;
        }

        public static float Peak(string series)
        {
            var buffer = new float[Capacity];
            Copy(series, buffer);
            float max = 0.0f;
            for (int i = 0; i < _count; ++i) if (buffer[i] > max) max = buffer[i];
            return max;
        }
    }
}
