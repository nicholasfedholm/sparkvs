using System;
using System.Collections.Generic;
using System.Globalization;

namespace SparkVS.Core;

/// <summary>Aggregated live-object statistics for one managed type.</summary>
public sealed class HeapTypeStat
{
    public ulong TypeId;
    public string TypeName = "";
    /// <summary>Module path (or simple name). May be empty when unknown or dynamic.</summary>
    public string Module = "";
    /// <summary>Modules of array element and generic argument types, recursive, deduped.</summary>
    public List<string> ArgModules = new();
    public long Count;
    public long Bytes;
}

/// <summary>One try at a heap dump. Every try forces its own full GC, so each one is recorded with its pause.</summary>
public sealed class SnapshotAttempt
{
    public int Attempt;
    public int BufferMB;
    /// <summary>Dump GC pause in ms, -1 when the dump GC end was not observed.</summary>
    public double GcPauseMs = -1;
    public long EventsLost;
    public bool Complete;
    public double ElapsedMs;
    /// <summary>"complete", "events lost", "dump GC not observed", "timeout", "reader error" or "exception".</summary>
    public string Outcome = "";
}

/// <summary>Result of one live-heap snapshot (per-type counts and bytes only, never nodes or edges).</summary>
public sealed class HeapSnapshot
{
    public string Route = "";
    public int Pid;
    public string ProcessName = "";
    public DateTime TakenUtc;
    public List<HeapTypeStat> Types = new();
    public long LiveObjects;
    public long LiveBytes;
    public double GcPauseMs;
    public double ElapsedMs;
    public long EventsLost;
    /// <summary>Finished, no reader error, dump GC observed, and zero events lost (see SnapshotPolicy.IsComplete).</summary>
    public bool Complete;
    public List<string> Warnings = new();
    public long WorkingSetBytes;
    public long PrivateBytes;
    /// <summary>TotalHeapSize from GCHeapStats after the induced GC, -1 if unknown.</summary>
    public long GcHeapSizeAfterBytes = -1;
    /// <summary>Number of dump attempts made (each forced a full GC).</summary>
    public int Attempts = 1;
    public List<SnapshotAttempt> AttemptLog = new();
    /// <summary>"stable" or "timeout" for the first snapshot of a sparkx watch, "immediate" for a single snapshot, "" when not applicable.</summary>
    public string StartCondition = "";
}

/// <summary>Pure rules for snapshot reliability, shared by the mod, sparkx and the tests.</summary>
public static class SnapshotPolicy
{
    public const int DefaultAttempts = 3;
    public const int MaxBufferMB = 4000;

    /// <summary>Complete means finished, no reader error, dump GC end observed (state 2), and no events lost.</summary>
    public static bool IsComplete(bool finished, bool readerError, int state, long eventsLost)
        => finished && !readerError && state == 2 && eventsLost == 0;

    /// <summary>Circular buffer for attempt n (0-based): the base grown by 50 % per attempt, clamped to 4000 MB.</summary>
    public static int BufferForAttempt(int baseMB, int attempt)
    {
        double mb = baseMB;
        for (int i = 0; i < attempt; i++) mb *= 1.5;
        return (int)Math.Min(MaxBufferMB, Math.Max(baseMB, Math.Round(mb)));
    }

    /// <summary>
    /// True when the last 3 samples exist, are all positive, and (max - min) / max is under pct percent.
    /// Samples are GC heap bytes, oldest first.
    /// </summary>
    public static bool HeapStable(IReadOnlyList<long> samples, double pct = 5.0)
    {
        if (samples.Count < 3) return false;
        long max = long.MinValue, min = long.MaxValue;
        for (int i = samples.Count - 3; i < samples.Count; i++)
        {
            long v = samples[i];
            if (v <= 0) return false;
            if (v > max) max = v;
            if (v < min) min = v;
        }
        return 100.0 * (max - min) / max < pct;
    }

    /// <summary>Timeline marker text, for example "snapshot:start complete=false events_lost=6861 attempts=3".</summary>
    public static string Marker(string label, HeapSnapshot s)
    {
        var inv = CultureInfo.InvariantCulture;
        string m = "snapshot:" + label + " complete=" + (s.Complete ? "true" : "false")
            + " events_lost=" + s.EventsLost.ToString(inv) + " attempts=" + s.Attempts.ToString(inv);
        if (s.StartCondition.Length > 0) m += " start_condition=" + s.StartCondition;
        return m;
    }
}
