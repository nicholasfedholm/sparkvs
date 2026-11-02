using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>1 Hz managed heap series. Sampled on a background thread from GC counters, not from the EventPipe session.</summary>
public sealed class HeapRecorder
{
    public struct Row
    {
        public double TMs;
        public string Ts;
        public long TotalMemoryBytes, HeapSizeBytes, CommittedBytes, FragmentedBytes, TotalAllocatedBytes, WorkingSetBytes;
        public int Gen0, Gen1, Gen2;
        public double GcPauseTimePercent;
    }

    readonly BundleClock clock;
    readonly List<Row> rows = new();
    readonly object gate = new();

    public HeapRecorder(BundleClock clock) { this.clock = clock; }

    public void Sample(double tMs)
    {
        var info = GC.GetGCMemoryInfo();
        var row = new Row
        {
            TMs = tMs,
            Ts = clock.IsoAt(tMs),
            TotalMemoryBytes = GC.GetTotalMemory(false),
            HeapSizeBytes = info.HeapSizeBytes,
            CommittedBytes = info.TotalCommittedBytes,
            FragmentedBytes = info.FragmentedBytes,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(false),
            WorkingSetBytes = Environment.WorkingSet,
            Gen0 = GC.CollectionCount(0),
            Gen1 = GC.CollectionCount(1),
            Gen2 = GC.CollectionCount(2),
            GcPauseTimePercent = info.PauseTimePercentage,
        };
        lock (gate) rows.Add(row);
    }

    public HeapStats Write(string dir, string? failure)
    {
        List<Row> copy;
        lock (gate) copy = rows.ToList();
        var stats = new HeapStats { Samples = copy.Count };
        if (copy.Count == 0)
        {
            stats.Enabled = false;
            stats.Reason = failure ?? "no heap samples were taken";
            File.WriteAllText(Path.Combine(dir, "heap.jsonl"), BundleWriter.DisabledHeader("heap", stats.Reason, clock));
            return stats;
        }

        var sb = new StringBuilder();
        sb.AppendLine(BundleWriter.Header("heap", clock, "1 Hz managed heap from GC counters on a background timer thread (GC.GetTotalMemory, GC.GetGCMemoryInfo, GC.CollectionCount). No second diagnostics attach.",
            new JsonObject
            {
                ["total_memory_bytes"] = "GC.GetTotalMemory(false), live plus not yet collected garbage",
                ["heap_size_bytes"] = "heap size as of the last GC (GCMemoryInfo.HeapSizeBytes)",
                ["committed_bytes"] = "GCMemoryInfo.TotalCommittedBytes",
                ["fragmented_bytes"] = "GCMemoryInfo.FragmentedBytes as of the last GC",
                ["total_allocated_bytes"] = "GC.GetTotalAllocatedBytes(false), cumulative since process start",
                ["working_set_bytes"] = "Environment.WorkingSet",
                ["gen0_count/gen1_count/gen2_count"] = "cumulative GC.CollectionCount since process start",
                ["gc_pause_time_percent"] = "GCMemoryInfo.PauseTimePercentage, percent of time paused in GC since process start",
            }));
        foreach (var r in copy)
        {
            sb.Append("{\"ts_local\":\"").Append(r.Ts).Append("\",\"t_ms\":").Append(BundleWriter.N(r.TMs, 1))
              .Append(",\"total_memory_bytes\":").Append(r.TotalMemoryBytes)
              .Append(",\"heap_size_bytes\":").Append(r.HeapSizeBytes)
              .Append(",\"committed_bytes\":").Append(r.CommittedBytes)
              .Append(",\"fragmented_bytes\":").Append(r.FragmentedBytes)
              .Append(",\"total_allocated_bytes\":").Append(r.TotalAllocatedBytes)
              .Append(",\"working_set_bytes\":").Append(r.WorkingSetBytes)
              .Append(",\"gen0_count\":").Append(r.Gen0)
              .Append(",\"gen1_count\":").Append(r.Gen1)
              .Append(",\"gen2_count\":").Append(r.Gen2)
              .Append(",\"gc_pause_time_percent\":").Append(BundleWriter.N(r.GcPauseTimePercent, 2))
              .Append("}\n");
        }
        File.WriteAllText(Path.Combine(dir, "heap.jsonl"), sb.ToString(), new UTF8Encoding(false));

        stats.Enabled = true;
        stats.StartTotalMemoryBytes = copy[0].TotalMemoryBytes;
        stats.EndTotalMemoryBytes = copy[^1].TotalMemoryBytes;
        stats.PeakTotalMemoryBytes = copy.Max(r => r.TotalMemoryBytes);
        stats.MeanTotalMemoryBytes = (long)copy.Average(r => (double)r.TotalMemoryBytes);
        return stats;
    }
}

/// <summary>1 Hz VRAM series of this process.</summary>
public sealed class GpuRecorder
{
    public struct Row
    {
        public double TMs;
        public string Ts;
        public GpuReading Reading;
    }

    readonly BundleClock clock;
    readonly List<Row> rows = new();
    readonly object gate = new();
    GpuMemoryReader? reader;
    readonly bool dxgiReads;
    readonly string dxgiOffReason;

    public string? UnavailableReason { get; private set; }
    public string SetupNote { get; private set; } = "";

    public const string DefaultDxgiOffReason = "DXGI reads were switched off in config (GpuMemoryDxgi=false in SparkVS.json), so only PDH is reported";

    public GpuRecorder(BundleClock clock, bool dxgiReads = true, string? dxgiOffReason = null)
    {
        this.clock = clock;
        this.dxgiReads = dxgiReads;
        this.dxgiOffReason = dxgiOffReason ?? DefaultDxgiOffReason;
    }

    /// <summary>Runs on the sampler thread so the COM objects live and die on one thread.</summary>
    public void Setup(int pid)
    {
        reader = new GpuMemoryReader(pid, dxgiReads);
        SetupNote = reader.UnavailableReason;
        if (!reader.DxgiAvailable && !reader.PdhAvailable) UnavailableReason = reader.UnavailableReason;
    }

    public void Teardown()
    {
        reader?.Dispose();
        reader = null;
    }

    public void Sample(double tMs)
    {
        if (reader == null || !reader.TryRead(out var reading)) return;
        lock (gate) rows.Add(new Row { TMs = tMs, Ts = clock.IsoAt(tMs), Reading = reading });
    }

    public VramStats Write(string dir)
    {
        List<Row> copy;
        lock (gate) copy = rows.ToList();
        var stats = new VramStats { Samples = copy.Count };
        if (copy.Count == 0)
        {
            stats.Enabled = false;
            stats.Reason = UnavailableReason ?? "no GPU memory reading succeeded" + (SetupNote.Length > 0 ? ": " + SetupNote : "");
            File.WriteAllText(Path.Combine(dir, "gpu-memory.jsonl"), BundleWriter.DisabledHeader("gpu-memory", stats.Reason, clock));
            return stats;
        }

        var sb = new StringBuilder();
        string pdhNote = "PDH here is the GPU Process Memory counter for this pid, the same counter the bench runner logs to captures/pdh.csv (gpu_dedicated_bytes), so the in-process PDH column and pdh.csv are one reading, not two sources; agreement between them proves nothing. Task Manager's per-process dedicated GPU memory is also this counter.";
        string description = dxgiReads
            ? "1 Hz VRAM of this process. Primary source DXGI QueryVideoMemoryInfo (dedicated = local segment group, shared = non-local). PDH GPU Process Memory is read in the same tick for the side by side gate and as the fallback. " + pdhNote
            : "1 Hz VRAM of this process from PDH GPU Process Memory only (source pdh). DXGI is switched off: " + dxgiOffReason + ". " + pdhNote;
        var header = JsonNode.Parse(BundleWriter.Header("gpu-memory", clock, description,
            new JsonObject
            {
                ["dedicated_bytes"] = dxgiReads ? "process usage in the local (dedicated VRAM) segment group of the adapter with the most usage" : "PDH Dedicated Usage summed over this pid's adapters (DXGI off)",
                ["shared_bytes"] = dxgiReads ? "process usage in the non-local (shared system memory) segment group of the same adapter" : "PDH Shared Usage summed over this pid's adapters (DXGI off)",
                ["budget_bytes"] = "OS VRAM budget for this process on that adapter (DXGI only, 0 for pdh)",
                ["source"] = "dxgi or pdh (pdh only when DXGI is unavailable, reports zero, or is switched off)",
                ["pdh_dedicated_bytes/pdh_shared_bytes"] = "PDH values read in the same tick, null when PDH is unavailable. Dedicated is committed memory (Task Manager's column), including allocations the driver demoted",
                ["pdh_local_bytes/pdh_nonlocal_bytes"] = "PDH Local Usage and Non Local Usage: resident memory, the same quantity as DXGI CurrentUsage of the local and non-local segment groups",
            }))!.AsObject();
        header["dxgi_enabled"] = dxgiReads;
        header["dxgi_off_reason"] = dxgiReads ? null : dxgiOffReason;
        header["pdh_independent_of_runner_pdh_csv"] = false;
        header["pdh_note"] = pdhNote;
        var vramGate = VramGate.Compute(copy.Select(r => r.Reading).ToList());
        header["vram_gate"] = vramGate.DeepClone();
        header["vram_gate_note"] = "DXGI CurrentUsage and PDH Local Usage both read dxgkrnl per-process bookkeeping through different APIs, so agreement proves interop, adapter selection and process attribution, not that the kernel numbers are right.";
        sb.AppendLine(header.ToJsonString());
        foreach (var r in copy)
        {
            var g = r.Reading;
            sb.Append("{\"ts_local\":\"").Append(r.Ts).Append("\",\"t_ms\":").Append(BundleWriter.N(r.TMs, 1))
              .Append(",\"dedicated_bytes\":").Append(g.DedicatedBytes)
              .Append(",\"shared_bytes\":").Append(g.SharedBytes)
              .Append(",\"budget_bytes\":").Append(g.BudgetBytes)
              .Append(",\"source\":\"").Append(g.Source).Append('"')
              .Append(",\"adapter\":").Append(JsonSerializer.Serialize(g.Adapter))
              .Append(",\"pdh_dedicated_bytes\":").Append(g.PdhDedicatedBytes?.ToString(CultureInfo.InvariantCulture) ?? "null")
              .Append(",\"pdh_shared_bytes\":").Append(g.PdhSharedBytes?.ToString(CultureInfo.InvariantCulture) ?? "null")
              .Append(",\"pdh_local_bytes\":").Append(g.PdhLocalBytes?.ToString(CultureInfo.InvariantCulture) ?? "null")
              .Append(",\"pdh_nonlocal_bytes\":").Append(g.PdhNonLocalBytes?.ToString(CultureInfo.InvariantCulture) ?? "null")
              .Append("}\n");
        }
        File.WriteAllText(Path.Combine(dir, "gpu-memory.jsonl"), sb.ToString(), new UTF8Encoding(false));

        stats.Enabled = true;
        stats.VramGate = vramGate;
        stats.Source = copy.Select(r => r.Reading.Source).Distinct().Count() == 1 ? copy[0].Reading.Source : "mixed(" + string.Join(",", copy.Select(r => r.Reading.Source).Distinct()) + ")";
        stats.Adapter = copy.GroupBy(r => r.Reading.Adapter).OrderByDescending(g => g.Count()).First().Key;
        stats.StartDedicatedBytes = copy[0].Reading.DedicatedBytes;
        stats.EndDedicatedBytes = copy[^1].Reading.DedicatedBytes;
        stats.PeakDedicatedBytes = copy.Max(r => r.Reading.DedicatedBytes);
        stats.MeanDedicatedBytes = (long)copy.Average(r => (double)r.Reading.DedicatedBytes);
        var withPdh = copy.Where(r => r.Reading.PdhDedicatedBytes != null).ToList();
        if (withPdh.Count > 0)
        {
            stats.PdhMeanDedicatedBytes = (long)withPdh.Average(r => (double)r.Reading.PdhDedicatedBytes!.Value);
            double dxgiMean = withPdh.Average(r => (double)r.Reading.DedicatedBytes);
            if (stats.PdhMeanDedicatedBytes > 0)
                stats.DxgiVsPdhMeanDiffPercent = Math.Round((dxgiMean - stats.PdhMeanDedicatedBytes.Value) / stats.PdhMeanDedicatedBytes.Value * 100.0, 2);
        }
        return stats;
    }
}

public static class BundleWriter
{
    /// <summary>Over this many bytes per capture the frames file switches to compact number arrays.</summary>
    public const long FramesMaxBytes = 50L * 1000 * 1000;
    const int ObjectLineBytes = 100;   // measured upper bound for one ts_local/t_ms/frame_ms/gc object line

    /// <summary>Files that stay disabled stubs on purpose. The reason text is the one the audit plan names.</summary>
    public static readonly (string File, int Step, string What)[] Stubs =
    {
        ("vram-by-mod.json", 0, "deferred, see capture-stack-audit/PLAN.md (the static texture-pixel inventory in asset-inventory.json is the proxy)"),
    };

    /// <summary>Files with their own writers. If a writer did not run, a disabled placeholder is written so the bundle layout stays complete.</summary>
    public static readonly string[] WriterFiles = { "allocations.json", "chunk-pipeline.json", "patch-map.json", "asset-inventory.json", "gpu-phases.json" };

    public static void WritePlaceholdersForMissing(string dir, string reason)
    {
        foreach (var file in WriterFiles)
        {
            string path = Path.Combine(dir, file);
            if (File.Exists(path)) continue;
            WriteJson(path, new JsonObject { ["schema_version"] = BuildInfo.SchemaVersion, ["enabled"] = false, ["reason"] = reason });
        }
    }

    public static string N(double v, int digits) => v.ToString("F" + digits, CultureInfo.InvariantCulture);

    public static string Header(string series, BundleClock clock, string description, JsonObject fields)
    {
        var h = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["type"] = "header",
            ["series"] = series,
            ["description"] = description,
            ["start_local"] = clock.StartIso,
            ["start_qpc"] = clock.StartQpc,
            ["qpc_frequency_hz"] = clock.Frequency,
            ["time_note"] = "t_ms is ms since start_local (capture start). ts_local is start_local plus t_ms.",
            ["fields"] = fields,
        };
        return h.ToJsonString();
    }

    public static string DisabledHeader(string series, string reason, BundleClock clock) =>
        new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["type"] = "header",
            ["series"] = series,
            ["enabled"] = false,
            ["reason"] = reason,
            ["start_local"] = clock.StartIso,
        }.ToJsonString() + "\n";

    public static void WriteJson(string path, JsonNode node)
    {
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static void WriteStubs(string dir)
    {
        foreach (var (file, step, what) in Stubs)
        {
            var node = new JsonObject
            {
                ["schema_version"] = BuildInfo.SchemaVersion,
                ["enabled"] = false,
                ["reason"] = what,
            };
            if (step > 0) node["planned_in_step"] = step;
            WriteJson(Path.Combine(dir, file), node);
        }
    }

    /// <summary>frames.jsonl and frames-summary.json. Runs off the render thread after the capture.</summary>
    public static FramesStats WriteFrames(string dir, FrameLog log, BundleClock clock, double thresholdMs, IReadOnlyList<SpikeRecord> spikes, int spikesDropped)
    {
        int n = log.RetainedFrames;
        var ms = new float[n];
        for (int i = 0; i < n; i++) ms[i] = log.RingFrameMs(i);

        bool compact = (long)n * ObjectLineBytes > FramesMaxBytes;
        var sb = new StringBuilder(1 << 20);
        using (var file = new FileStream(Path.Combine(dir, "frames.jsonl"), FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false), 1 << 16))
        {
            var fields = compact
                ? new JsonObject { ["row"] = "[t_ms, frame_ms, gc_flags]" }
                : new JsonObject { ["ts_local"] = "ISO 8601 local time of the frame end", ["t_ms"] = "frame end, ms since capture start", ["frame_ms"] = "frame duration, ms", ["gc_flags"] = "bit0 gen0, bit1 gen1, bit2 gen2 collected during this frame" };
            var header = JsonNode.Parse(Header("frames", clock, "Every rendered frame in the capture window, timed with QueryPerformanceCounter in the render hook.", fields))!.AsObject();
            header["format"] = compact ? "compact_number_arrays" : "jsonl_objects";
            header["frame_count"] = log.Frames;
            header["retained_frames"] = n;
            header["dropped_oldest_frames"] = log.DroppedOldest;
            header["gc_flags"] = "bit0 gen0, bit1 gen1, bit2 gen2 collected during this frame";
            if (compact) header["compact_reason"] = $"{n} frames at about {ObjectLineBytes} bytes per object line would exceed {FramesMaxBytes / 1000000} MB";
            writer.Write(header.ToJsonString());
            writer.Write('\n');
            for (int i = 0; i < n; i++)
            {
                double t = clock.MsSince(log.RingEnd(i));
                sb.Clear();
                if (compact)
                    sb.Append('[').Append(N(t, 1)).Append(',').Append(N(ms[i], 2)).Append(',').Append(log.RingGcFlags(i)).Append("]\n");
                else
                    sb.Append("{\"ts_local\":\"").Append(clock.IsoAt(t)).Append("\",\"t_ms\":").Append(N(t, 1))
                      .Append(",\"frame_ms\":").Append(N(ms[i], 2)).Append(",\"gc_flags\":").Append(log.RingGcFlags(i)).Append("}\n");
                writer.Write(sb.ToString());
            }
        }

        var stats = ComputeStats(ms, log, thresholdMs, spikes.Count + spikesDropped);
        var summary = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["start_local"] = clock.StartIso,
            ["label"] = "measured",
            ["present_mode"] = null,
            ["present_mode_note"] = "not knowable in process; the bench runner records it from PresentMon. An fps number is only quotable with its present mode.",
            ["percentile_method"] = "nearest rank over retained frames",
            ["one_percent_low_definition"] = "1000 divided by the mean frame_ms of the slowest 1 percent of frames",
            ["stats"] = JsonSerializer.SerializeToNode(stats),
            ["over_threshold_frames_dropped"] = spikesDropped,
            ["stack_status"] = "pending_report",
            ["stack_note"] = "nearest_stack and dominant_* are filled when the report is built (.sparkvs report, or automatically after .sparkvs capture); until then they are null.",
        };
        var list = new JsonArray();
        foreach (var s in spikes.OrderByDescending(s => s.DurationMs))
        {
            double t = clock.MsSince(s.StartQpc);
            list.Add(new JsonObject
            {
                ["ts_local"] = clock.IsoAt(t),
                ["t_ms"] = Math.Round(t, 1),
                ["frame_ms"] = Math.Round(s.DurationMs, 2),
                ["gc_gen0_delta"] = s.Gen0,
                ["gc_gen1_delta"] = s.Gen1,
                ["gc_gen2_delta"] = s.Gen2,
                ["nearest_stack"] = null,
                ["nearest_sample_gap_ms"] = null,
                ["dominant_method"] = null,
                ["dominant_owner"] = null,
            });
        }
        summary["over_threshold_frames"] = list;
        WriteJson(Path.Combine(dir, "frames-summary.json"), summary);
        return stats;
    }

    public static FramesStats ComputeStats(float[] ms, FrameLog log, double thresholdMs, int overCount)
    {
        var stats = new FramesStats
        {
            FrameCount = log.Frames,
            RetainedFrames = ms.Length,
            DroppedOldestFrames = log.DroppedOldest,
            OverThresholdMs = thresholdMs,
            OverThresholdCount = overCount,
        };
        if (ms.Length == 0) return stats;
        var sorted = (float[])ms.Clone();
        Array.Sort(sorted);
        double sum = 0;
        foreach (var v in sorted) sum += v;
        stats.DurationMs = sum;
        stats.MeanFrameMs = sum / sorted.Length;
        stats.P50FrameMs = Rank(sorted, 50);
        stats.P95FrameMs = Rank(sorted, 95);
        stats.P99FrameMs = Rank(sorted, 99);
        stats.MaxFrameMs = sorted[^1];
        stats.MeanFps = stats.MeanFrameMs > 0 ? 1000.0 / stats.MeanFrameMs : 0;
        int worst = Math.Max(1, (int)Math.Ceiling(sorted.Length * 0.01));
        double worstMean = 0;
        for (int i = sorted.Length - worst; i < sorted.Length; i++) worstMean += sorted[i];
        worstMean /= worst;
        stats.OnePercentLowFps = worstMean > 0 ? 1000.0 / worstMean : 0;
        return stats;
    }

    static double Rank(float[] sorted, double percent)
    {
        int idx = (int)Math.Ceiling(percent / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    /// <summary>tick.jsonl. When the probe could not be built or saw no ticks the file says enabled false and why.</summary>
    public static TickStats WriteTick(string dir, TickLog? log, BundleClock clock, string? probeFailure)
    {
        long dropped = 0;
        var entries = log == null ? Array.Empty<TickLog.Entry>() : log.Snapshot(out dropped);
        var stats = new TickStats { TickCount = entries.Length };
        if (entries.Length == 0)
        {
            stats.Enabled = false;
            stats.Reason = probeFailure ?? "no server ticks were observed during the capture (no integrated server in this process, or the tick listener never fired)";
            File.WriteAllText(Path.Combine(dir, "tick.jsonl"), DisabledHeader("tick", stats.Reason, clock));
            return stats;
        }

        var sb = new StringBuilder(1 << 16);
        var header = JsonNode.Parse(Header("tick", clock,
            "Integrated server work time per tick, read from the game's own per tick Stopwatch (ServerMain.StatsCollector tickTimes) by a game tick listener on the server thread, one row per completed tick.",
            new JsonObject
            {
                ["ts_local"] = "local time the listener observed the tick",
                ["t_ms"] = "ms since capture start at observation, which is shortly after the tick it reports",
                ["mspt_ms"] = "work time of the tick that just completed, whole ms (the game timer has 1 ms resolution)",
                ["interval_ms"] = "ms since the previous observed tick, null for the first. Equals the tick period including the game's sleep, not work time.",
            }))!.AsObject();
        header["label"] = "experimental";
        header["dropped_oldest_ticks"] = dropped;
        sb.Append(header.ToJsonString()).Append('\n');
        foreach (var e in entries)
        {
            double t = clock.MsSince(e.Stamp);
            sb.Append("{\"ts_local\":\"").Append(clock.IsoAt(t)).Append("\",\"t_ms\":").Append(N(t, 1))
              .Append(",\"mspt_ms\":").Append(e.MsptMs)
              .Append(",\"interval_ms\":").Append(float.IsNaN(e.IntervalMs) ? "null" : N(e.IntervalMs, 1)).Append("}\n");
        }
        File.WriteAllText(Path.Combine(dir, "tick.jsonl"), sb.ToString(), new UTF8Encoding(false));

        var mspt = entries.Select(e => (double)e.MsptMs).OrderBy(v => v).ToArray();
        stats.Enabled = true;
        stats.MeanMsptMs = mspt.Average();
        stats.P95MsptMs = mspt[Math.Clamp((int)Math.Ceiling(0.95 * mspt.Length) - 1, 0, mspt.Length - 1)];
        stats.MaxMsptMs = mspt[^1];
        var intervals = entries.Where(e => !float.IsNaN(e.IntervalMs)).Select(e => (double)e.IntervalMs).ToArray();
        stats.MeanIntervalMs = intervals.Length > 0 ? intervals.Average() : 0;
        return stats;
    }

    /// <summary>Fills the over threshold rows of frames-summary.json with the stacks the report found.</summary>
    public static void AddStacksToFramesSummary(string dir, Report report)
    {
        string path = Path.Combine(dir, "frames-summary.json");
        if (!File.Exists(path)) return;
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (root["over_threshold_frames"] is not JsonArray list) return;

        // Report spikes carry AtSec and DurationMs; rows here carry t_ms and frame_ms of the same frames.
        foreach (var node in list.OfType<JsonObject>())
        {
            double t = node["t_ms"]!.GetValue<double>();
            var match = report.Spikes.OrderBy(s => Math.Abs(s.AtSec * 1000.0 - t)).FirstOrDefault();
            if (match == null || Math.Abs(match.AtSec * 1000.0 - t) > 1.0) continue;
            node["nearest_stack"] = match.NearestStack;
            node["nearest_sample_gap_ms"] = Math.Round(match.NearestSampleMs, 1);
            node["samples_in_frame"] = match.SamplesInFrame;
            node["dominant_method"] = match.DominantMethod;
            node["dominant_owner"] = match.DominantOwner;
        }
        root["stack_status"] = "filled";
        root["stack_note"] = "nearest_stack is the sampled main thread stack closest to the middle of the frame; sampling at the configured interval is an estimate, not a trace.";
        WriteJson(path, root);
    }
}
