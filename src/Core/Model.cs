using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SparkVS.Core;

/// <summary>One resolved stack frame. Interned, so identical frames share an index.</summary>
public sealed class FrameInfo
{
    public string Method = "";
    public string Module = "";
    public bool Resolved = true;
}

public struct Sample
{
    public int ThreadId;
    public double TimeMs;   // ms since capture start
    public int StackId;     // index into SampleSet.Stacks
    public bool External;   // thread was in native code (GL driver, swap, wait) when sampled
}

/// <summary>One GC allocation tick: the runtime fires one about every 100 KB a thread allocates.</summary>
public struct AllocSample
{
    public int ThreadId;
    public double TimeMs;   // ms since capture start
    public int StackId;     // index into SampleSet.Stacks
    public long Bytes;      // bytes allocated since the thread's previous tick (AllocationAmount64)
    public int TypeId;      // index into SampleSet.TypeNames
    public bool Large;      // large object heap allocation
}

/// <summary>Decoded capture, independent of TraceEvent so the maths can be tested on synthetic data.</summary>
public sealed class SampleSet
{
    public List<FrameInfo> Frames = new();
    public List<int[]> Stacks = new();   // frame indexes, leaf first
    public List<Sample> Samples = new();
    public List<AllocSample> Allocs = new();
    public List<string> TypeNames = new();
}

public sealed class SpikeRecord
{
    public long StartQpc { get; set; }
    public double DurationMs { get; set; }
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
}

/// <summary>Sidecar written at capture stop. Everything the report needs that is not in the nettrace.</summary>
public sealed class CaptureMeta
{
    public string Label { get; set; } = "";
    public string World { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string StartedLocal { get; set; } = "";
    public long StartQpc { get; set; }
    public long EndQpc { get; set; }
    public long QpcFrequency { get; set; }
    public int MainThreadOsId { get; set; }
    public int ServerThreadOsId { get; set; }
    public bool IntegratedServer { get; set; }
    public string SamplingRateKnob { get; set; } = "";
    public double SpikeThresholdMs { get; set; } = 50;
    public Dictionary<string, string> AssemblyToMod { get; set; } = new();
    public List<string> GameAssemblies { get; set; } = new();
    public List<SpikeRecord> Spikes { get; set; } = new();
    public int SpikesDropped { get; set; }
    public long FrameCount { get; set; }
    public double BaselineFps { get; set; }
    public double CaptureFps { get; set; }
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
    public long SamplerBytesAllocated { get; set; }
    public long FrameHookBytesAllocated { get; set; }
    public long NettraceBytes { get; set; }

    /// <summary>True when the trace session also asked for GC allocation ticks.</summary>
    public bool AllocationSampling { get; set; }
    /// <summary>GC.GetTotalAllocatedBytes(false) at capture start and end, the measured cross-check for the tick estimate.</summary>
    public long GcAllocatedBytesStart { get; set; }
    public long GcAllocatedBytesEnd { get; set; }

    /// <summary>Set only for bundle captures: compact stats from the in-process series.</summary>
    public BundleSummary? Bundle { get; set; }
}

public sealed class MethodRow
{
    public string Method { get; set; } = "";
    public string Module { get; set; } = "";
    public string Owner { get; set; } = "";
    public double SelfMs { get; set; }
    public double TotalMs { get; set; }
    public double SelfPct { get; set; }
    public double TotalPct { get; set; }
}

public sealed class GroupRow
{
    public string Name { get; set; } = "";
    public double SelfMs { get; set; }
    public double InclusiveMs { get; set; }
    public double SelfPct { get; set; }
    public double InclusivePct { get; set; }
}

public sealed class PathRow
{
    public string Path { get; set; } = "";
    public double Ms { get; set; }
    public double Pct { get; set; }
}

public sealed class ThreadReport
{
    public int OsThreadId { get; set; }
    public string Role { get; set; } = "";
    public int Samples { get; set; }
    public double DurationMs { get; set; }
    public double ExternalPct { get; set; }
    public double IdlePct { get; set; }
    public double MeanIntervalMs { get; set; }
    public List<MethodRow> Methods { get; set; } = new();
    public List<GroupRow> ByAssembly { get; set; } = new();
    public List<GroupRow> ByMod { get; set; } = new();
    public List<PathRow> TopPaths { get; set; } = new();
}

public sealed class SpikeReport
{
    public double AtSec { get; set; }
    public double DurationMs { get; set; }
    public int SamplesInFrame { get; set; }
    public double NearestSampleMs { get; set; }
    public string NearestStack { get; set; } = "";
    public string DominantMethod { get; set; } = "";
    public string DominantOwner { get; set; } = "";
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
}

public sealed class Report
{
    /// <summary>Bundle captures only. Null (and so absent from the JSON) for start/stop captures.</summary>
    [JsonPropertyName("schema_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("bundle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BundleSummary? Bundle { get; set; }

    public string Label { get; set; } = "";
    public string World { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string StartedLocal { get; set; } = "";
    public double DurationSec { get; set; }
    public string ScopeNote { get; set; } = "";
    public string SamplingRateKnob { get; set; } = "";
    public int TotalSamples { get; set; }
    public int SamplesWithResolvedStack { get; set; }
    public int MainThreadSamples { get; set; }
    public double MainResolvedPct { get; set; }
    public double MainMeanIntervalMs { get; set; }
    public double BaselineFps { get; set; }
    public double CaptureFps { get; set; }
    public double CaptureFpsDeltaPct { get; set; }
    public long SamplerBytesAllocated { get; set; }
    public long FrameHookBytesAllocated { get; set; }
    public long NettraceBytes { get; set; }
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
    public long FrameCount { get; set; }
    public int SpikesOverThreshold { get; set; }
    public int SpikesDropped { get; set; }
    public double SpikeThresholdMs { get; set; }
    public List<SpikeReport> Spikes { get; set; } = new();
    public List<ThreadReport> Threads { get; set; } = new();
}

/// <summary>Frame statistics. Measured: every frame is timed with QueryPerformanceCounter.</summary>
public sealed class FramesStats
{
    [JsonPropertyName("frame_count")] public long FrameCount { get; set; }
    [JsonPropertyName("retained_frames")] public int RetainedFrames { get; set; }
    [JsonPropertyName("dropped_oldest_frames")] public long DroppedOldestFrames { get; set; }
    [JsonPropertyName("duration_ms")] public double DurationMs { get; set; }
    [JsonPropertyName("mean_frame_ms")] public double MeanFrameMs { get; set; }
    [JsonPropertyName("p50_frame_ms")] public double P50FrameMs { get; set; }
    [JsonPropertyName("p95_frame_ms")] public double P95FrameMs { get; set; }
    [JsonPropertyName("p99_frame_ms")] public double P99FrameMs { get; set; }
    [JsonPropertyName("max_frame_ms")] public double MaxFrameMs { get; set; }
    [JsonPropertyName("mean_fps")] public double MeanFps { get; set; }
    [JsonPropertyName("one_percent_low_fps")] public double OnePercentLowFps { get; set; }
    [JsonPropertyName("over_threshold_ms")] public double OverThresholdMs { get; set; }
    [JsonPropertyName("over_threshold_count")] public int OverThresholdCount { get; set; }
}

/// <summary>Server tick durations from the integrated server. Experimental until proven in game.</summary>
public sealed class TickStats
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("tick_count")] public int TickCount { get; set; }
    [JsonPropertyName("mean_mspt_ms")] public double MeanMsptMs { get; set; }
    [JsonPropertyName("p95_mspt_ms")] public double P95MsptMs { get; set; }
    [JsonPropertyName("max_mspt_ms")] public double MaxMsptMs { get; set; }
    [JsonPropertyName("mean_interval_ms")] public double MeanIntervalMs { get; set; }
}

public sealed class HeapStats
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("samples")] public int Samples { get; set; }
    [JsonPropertyName("start_total_memory_bytes")] public long StartTotalMemoryBytes { get; set; }
    [JsonPropertyName("end_total_memory_bytes")] public long EndTotalMemoryBytes { get; set; }
    [JsonPropertyName("peak_total_memory_bytes")] public long PeakTotalMemoryBytes { get; set; }
    [JsonPropertyName("mean_total_memory_bytes")] public long MeanTotalMemoryBytes { get; set; }
}

/// <summary>Dedicated VRAM of this process. Start, end, peak and mean over the 1 Hz series.</summary>
public sealed class VramStats
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("adapter")] public string Adapter { get; set; } = "";
    [JsonPropertyName("samples")] public int Samples { get; set; }
    [JsonPropertyName("start_dedicated_bytes")] public long StartDedicatedBytes { get; set; }
    [JsonPropertyName("end_dedicated_bytes")] public long EndDedicatedBytes { get; set; }
    [JsonPropertyName("peak_dedicated_bytes")] public long PeakDedicatedBytes { get; set; }
    [JsonPropertyName("mean_dedicated_bytes")] public long MeanDedicatedBytes { get; set; }
    [JsonPropertyName("pdh_mean_dedicated_bytes")] public long? PdhMeanDedicatedBytes { get; set; }
    [JsonPropertyName("dxgi_vs_pdh_mean_diff_percent")] public double? DxgiVsPdhMeanDiffPercent { get; set; }
    [JsonPropertyName("vram_gate")] public System.Text.Json.Nodes.JsonObject? VramGate { get; set; }
}

public sealed class BundleSummary
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("start_local")] public string StartLocal { get; set; } = "";
    [JsonPropertyName("sparkvs_version")] public string SparkVsVersion { get; set; } = "";
    [JsonPropertyName("frames")] public FramesStats Frames { get; set; } = new();
    [JsonPropertyName("tick")] public TickStats Tick { get; set; } = new();
    [JsonPropertyName("heap")] public HeapStats Heap { get; set; } = new();
    [JsonPropertyName("vram")] public VramStats Vram { get; set; } = new();
}
