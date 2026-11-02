using System;
using System.Diagnostics;

namespace SparkVS.Core;

/// <summary>
/// Integrated server tick durations. The server thread calls Record once per completed tick it observes;
/// everything is preallocated and guarded by one uncontended lock. Not touched by the client frame hook.
/// </summary>
public sealed class TickLog
{
    readonly object gate = new();
    readonly long[] stamp;
    readonly int[] msptMs;
    readonly float[] intervalMs;
    long count;
    long lastStamp;
    volatile bool active;

    public TickLog(int capacity)
    {
        stamp = new long[capacity];
        msptMs = new int[capacity];
        intervalMs = new float[capacity];
    }

    public bool Active => active;

    public void Begin()
    {
        lock (gate)
        {
            count = 0;
            lastStamp = 0;
            active = true;
        }
    }

    public void End()
    {
        lock (gate) active = false;
    }

    /// <summary>mspt is the work time of the tick that just completed, in whole ms as the game measures it.</summary>
    public void Record(long nowStamp, int mspt)
    {
        lock (gate)
        {
            if (!active) return;
            int slot = (int)(count % stamp.Length);
            stamp[slot] = nowStamp;
            msptMs[slot] = mspt;
            intervalMs[slot] = lastStamp == 0 ? float.NaN : (float)((nowStamp - lastStamp) * 1000.0 / Stopwatch.Frequency);
            lastStamp = nowStamp;
            count++;
        }
    }

    public readonly struct Entry
    {
        public readonly long Stamp;
        public readonly int MsptMs;
        public readonly float IntervalMs;
        public Entry(long stamp, int mspt, float interval) { Stamp = stamp; MsptMs = mspt; IntervalMs = interval; }
    }

    /// <summary>Copy of the retained ticks, oldest first, plus how many were overwritten.</summary>
    public Entry[] Snapshot(out long droppedOldest)
    {
        lock (gate)
        {
            int n = (int)Math.Min(count, stamp.Length);
            droppedOldest = Math.Max(0, count - stamp.Length);
            var result = new Entry[n];
            long first = count > stamp.Length ? count % stamp.Length : 0;
            for (int i = 0; i < n; i++)
            {
                int slot = (int)((first + i) % stamp.Length);
                result[i] = new Entry(stamp[slot], msptMs[slot], intervalMs[slot]);
            }
            return result;
        }
    }
}
