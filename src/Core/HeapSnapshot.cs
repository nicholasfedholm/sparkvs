using System;
using System.Collections.Generic;

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
    public bool Complete;
    public List<string> Warnings = new();
    public long WorkingSetBytes;
    public long PrivateBytes;
    /// <summary>TotalHeapSize from GCHeapStats after the induced GC, -1 if unknown.</summary>
    public long GcHeapSizeAfterBytes = -1;
}
