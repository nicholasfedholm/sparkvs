using System;
using System.Diagnostics;

namespace SparkVS.Core;

/// <summary>
/// Per frame timing into preallocated arrays. Record runs on the client main thread every frame
/// of a capture and must not allocate; the tests assert that and the mod reports the running count.
/// </summary>
public sealed class FrameLog
{
    readonly long[] spikeStart;
    readonly float[] spikeMs;
    readonly int[] spikeGen0, spikeGen1, spikeGen2;
    readonly long thresholdTicks;

    // Optional ring of every frame (bundle captures). Preallocated once; Record only writes into it.
    readonly long[] ringEnd;
    readonly float[] ringMs;
    readonly byte[] ringGc;   // bit0 gen0, bit1 gen1, bit2 gen2 collected during the frame

    long lastStamp;
    int lastGen0, lastGen1, lastGen2;
    int spikeCount;

    public long Frames { get; private set; }
    public int Dropped { get; private set; }
    public long AllocatedBytes { get; private set; }
    public int SpikeCount => spikeCount;

    public int RingCapacity => ringEnd.Length;

    /// <summary>Frames currently held in the ring, oldest first.</summary>
    public int RetainedFrames => (int)Math.Min(Frames, ringEnd.Length);

    /// <summary>Frames overwritten because the ring was full.</summary>
    public long DroppedOldest => Math.Max(0, Frames - ringEnd.Length);

    public FrameLog(int capacity, double thresholdMs, int ringCapacity = 0)
    {
        ringEnd = new long[ringCapacity];
        ringMs = new float[ringCapacity];
        ringGc = new byte[ringCapacity];
        spikeStart = new long[capacity];
        spikeMs = new float[capacity];
        spikeGen0 = new int[capacity];
        spikeGen1 = new int[capacity];
        spikeGen2 = new int[capacity];
        thresholdTicks = (long)(thresholdMs * Stopwatch.Frequency / 1000.0);
    }

    /// <summary>Starts the frame clock without counting a frame.</summary>
    public void Reset(long stamp)
    {
        lastStamp = stamp;
        lastGen0 = GC.CollectionCount(0);
        lastGen1 = GC.CollectionCount(1);
        lastGen2 = GC.CollectionCount(2);
        Frames = 0;
        spikeCount = 0;
        Dropped = 0;
    }

    public void Record(long stamp)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long ticks = stamp - lastStamp;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        if (ticks >= thresholdTicks)
        {
            if (spikeCount < spikeStart.Length)
            {
                spikeStart[spikeCount] = lastStamp;
                spikeMs[spikeCount] = (float)(ticks * 1000.0 / Stopwatch.Frequency);
                spikeGen0[spikeCount] = g0 - lastGen0;
                spikeGen1[spikeCount] = g1 - lastGen1;
                spikeGen2[spikeCount] = g2 - lastGen2;
                spikeCount++;
            }
            else Dropped++;
        }
        if (ringEnd.Length > 0)
        {
            int slot = (int)(Frames % ringEnd.Length);
            ringEnd[slot] = stamp;
            ringMs[slot] = (float)(ticks * 1000.0 / Stopwatch.Frequency);
            ringGc[slot] = (byte)((g0 != lastGen0 ? 1 : 0) | (g1 != lastGen1 ? 2 : 0) | (g2 != lastGen2 ? 4 : 0));
        }
        lastGen0 = g0;
        lastGen1 = g1;
        lastGen2 = g2;
        lastStamp = stamp;
        Frames++;
        AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
    }

    int RingSlot(int i) => (int)((Frames > ringEnd.Length ? Frames % ringEnd.Length + i : i) % ringEnd.Length);

    /// <summary>End stamp (QPC ticks) of the i-th retained frame, oldest first.</summary>
    public long RingEnd(int i) => ringEnd[RingSlot(i)];
    public float RingFrameMs(int i) => ringMs[RingSlot(i)];
    public byte RingGcFlags(int i) => ringGc[RingSlot(i)];

    public SpikeRecord SpikeAt(int i) => new()
    {
        StartQpc = spikeStart[i],
        DurationMs = spikeMs[i],
        Gen0 = spikeGen0[i],
        Gen1 = spikeGen1[i],
        Gen2 = spikeGen2[i],
    };
}
