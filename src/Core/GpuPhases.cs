using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>The GL calls the recorder needs, so the ring and aggregation logic is testable without a GL context.</summary>
public interface IGpuTimer
{
    void GenQueries(int[] ids);
    /// <summary>glQueryCounter(id, GL_TIMESTAMP).</summary>
    void Stamp(int id);
    /// <summary>GL_QUERY_RESULT_AVAILABLE for the id; never blocks.</summary>
    bool IsAvailable(int id);
    /// <summary>GL_QUERY_RESULT as a 64-bit value, nanoseconds. Only called after IsAvailable returned true.</summary>
    long Result(int id);
}

/// <summary>Timestamp points inside one frame, in the order the client issues them (see gpuphases-notes.md).</summary>
public static class GpuBoundary
{
    public const int FrameStart = 0;       // Before, start
    public const int ShadowStart = 1;      // ShadowFar, start
    public const int ShadowFarDone = 2;    // ShadowFarDone, end
    public const int ShadowNearDone = 3;   // ShadowNearDone, end (only with shadow quality above 1)
    public const int OpaqueStart = 4;
    public const int OpaqueEnd = 5;
    public const int AfterOitEnd = 6;      // last 3D stage
    public const int AfterPostEnd = 7;     // AfterPostProcessing, end
    public const int AfterBlitEnd = 8;     // AfterBlit, end (after final composition and the blit)
    public const int DoneEnd = 9;          // Done, end (after the Ortho GUI pass)
    public const int Count = 10;
}

public static class GpuPhase
{
    public const int Frame = 0, Shadow = 1, Opaque = 2, Oit = 3, Post = 4, Final = 5, Ui = 6, Unattributed = 7;
    public const int Count = 8;
    public static readonly string[] Names = { "frame", "shadow", "opaque", "oit", "post", "final", "ui", "unattributed" };
    public static readonly string[] Boundaries =
    {
        "Before start to Done end (GPU timeline, not including present or vsync wait)",
        "ShadowFar start to ShadowNearDone end, or ShadowFarDone end when the near pass is off; absent in frames with shadows off",
        "Opaque start to Opaque end",
        "Opaque end to AfterOIT end: transparent framebuffer setup, the OIT stage, the merge pass and AfterOIT renderers",
        "AfterOIT end to AfterPostProcessing end: the engine post passes plus AfterPostProcessing renderers",
        "AfterPostProcessing end to AfterBlit end: final composition, AfterFinalComposition renderers, blit to the default framebuffer, AfterBlit renderers",
        "AfterBlit end to Done end: the Ortho GUI pass and Done renderers",
        "frame minus every listed phase present in the frame: Before to Opaque start (matrices, culling setup, other Before renderers) and gaps between stages",
    };
}

public struct PhaseStats
{
    public int Count;
    public double Mean, P50, P95, P99, Max;
}

/// <summary>
/// GPU time between render stage boundaries with GL timestamp queries. BeginFrame and Mark run on the client render thread
/// every frame of a capture and must not allocate or stall: results are read three frames late and a not-ready slot is dropped, never waited on.
/// </summary>
public sealed class GpuPhaseRecorder
{
    public const int Depth = 4;        // frames of queries in flight
    public const int ReadBehind = 3;   // slot read at the start of frame f is the one written in frame f-3
    public const int DefaultCapacity = 200_000;

    readonly IGpuTimer timer;
    readonly int[] ids = new int[Depth * GpuBoundary.Count];
    readonly bool[] written = new bool[Depth * GpuBoundary.Count];
    readonly long[] slotFrame = new long[Depth];
    readonly long[] ns = new long[GpuBoundary.Count];
    readonly float[][] samples = new float[GpuPhase.Count][];
    readonly int[] counts = new int[GpuPhase.Count];
    readonly int capacity;

    long frameIndex = -1;
    bool active, frameOn;
    long startStamp, blockTicks;

    public long FramesMeasured { get; private set; }
    public long FramesIssued { get; private set; }
    public long DroppedNotReady { get; private set; }
    public long Incomplete { get; private set; }
    public long Overflow { get; private set; }
    public bool Active => active;
    public int Capacity => capacity;

    public GpuPhaseRecorder(IGpuTimer timer, int capacity = DefaultCapacity)
    {
        this.timer = timer;
        this.capacity = capacity;
        for (int i = 0; i < GpuPhase.Count; i++) samples[i] = new float[capacity];
        timer.GenQueries(ids);
        ResetSlots();
    }

    void ResetSlots()
    {
        for (int i = 0; i < Depth; i++) slotFrame[i] = -1;
        Array.Clear(written);
    }

    /// <summary>Starts a capture. blockTicks above 0 alternates off and on blocks of that length, off first. Clears counts and samples.</summary>
    public void Begin(long stamp, long blockTicks)
    {
        startStamp = stamp;
        this.blockTicks = blockTicks;
        frameIndex = -1;
        frameOn = false;
        FramesMeasured = FramesIssued = DroppedNotReady = Incomplete = Overflow = 0;
        Array.Clear(counts);
        ResetSlots();
        active = true;
    }

    public void End() { active = false; frameOn = false; }

    /// <summary>Whether the A/B schedule has queries on at this stamp. Always true when blockTicks is 0.</summary>
    public static bool BlockIsOn(long stamp, long startStamp, long blockTicks) =>
        blockTicks <= 0 || (((stamp - startStamp) / blockTicks) & 1) == 1;

    /// <summary>Called at the start of the frame (Before stage, lowest order). Reads the slot from three frames ago, then opens this frame's slot.</summary>
    public void BeginFrame(long now)
    {
        if (!active) { frameOn = false; return; }
        frameIndex++;
        ReadOld();
        bool on = BlockIsOn(now, startStamp, blockTicks);
        frameOn = on;
        int cur = (int)(frameIndex % Depth);
        Array.Clear(written, cur * GpuBoundary.Count, GpuBoundary.Count);
        slotFrame[cur] = on ? frameIndex : -1;
        if (on) { FramesIssued++; Mark(GpuBoundary.FrameStart); }
    }

    public void Mark(int boundary)
    {
        if (!frameOn) return;
        int at = (int)(frameIndex % Depth) * GpuBoundary.Count + boundary;
        timer.Stamp(ids[at]);
        written[at] = true;
    }

    void ReadOld()
    {
        long target = frameIndex - ReadBehind;
        if (target < 0) return;
        int slot = (int)(target % Depth);
        if (slotFrame[slot] != target) return;
        slotFrame[slot] = -1;
        int o = slot * GpuBoundary.Count;
        if (!written[o + GpuBoundary.FrameStart] || !written[o + GpuBoundary.OpaqueStart] || !written[o + GpuBoundary.OpaqueEnd] || !written[o + GpuBoundary.AfterOitEnd]
            || !written[o + GpuBoundary.AfterPostEnd] || !written[o + GpuBoundary.AfterBlitEnd] || !written[o + GpuBoundary.DoneEnd])
        {
            Incomplete++;
            return;
        }
        if (!timer.IsAvailable(ids[o + GpuBoundary.DoneEnd])) { DroppedNotReady++; return; }
        for (int b = 0; b < GpuBoundary.Count; b++)
            if (written[o + b]) ns[b] = timer.Result(ids[o + b]);
        bool shadow = written[o + GpuBoundary.ShadowStart] && (written[o + GpuBoundary.ShadowNearDone] || written[o + GpuBoundary.ShadowFarDone]);
        int shadowEnd = written[o + GpuBoundary.ShadowNearDone] ? GpuBoundary.ShadowNearDone : GpuBoundary.ShadowFarDone;
        Aggregate(shadow, shadowEnd);
    }

    static float Ms(long fromNs, long toNs) => toNs > fromNs ? (toNs - fromNs) / 1_000_000f : 0f;

    void Aggregate(bool shadow, int shadowEnd)
    {
        if (counts[GpuPhase.Frame] >= capacity) { Overflow++; return; }
        float frame = Ms(ns[GpuBoundary.FrameStart], ns[GpuBoundary.DoneEnd]);
        float opaque = Ms(ns[GpuBoundary.OpaqueStart], ns[GpuBoundary.OpaqueEnd]);
        float oit = Ms(ns[GpuBoundary.OpaqueEnd], ns[GpuBoundary.AfterOitEnd]);
        float post = Ms(ns[GpuBoundary.AfterOitEnd], ns[GpuBoundary.AfterPostEnd]);
        float fin = Ms(ns[GpuBoundary.AfterPostEnd], ns[GpuBoundary.AfterBlitEnd]);
        float ui = Ms(ns[GpuBoundary.AfterBlitEnd], ns[GpuBoundary.DoneEnd]);
        float sum = opaque + oit + post + fin + ui;
        Add(GpuPhase.Frame, frame);
        if (shadow)
        {
            float sh = Ms(ns[GpuBoundary.ShadowStart], ns[shadowEnd]);
            Add(GpuPhase.Shadow, sh);
            sum += sh;
        }
        Add(GpuPhase.Opaque, opaque);
        Add(GpuPhase.Oit, oit);
        Add(GpuPhase.Post, post);
        Add(GpuPhase.Final, fin);
        Add(GpuPhase.Ui, ui);
        Add(GpuPhase.Unattributed, frame > sum ? frame - sum : 0f);
        FramesMeasured++;
    }

    void Add(int phase, float ms) => samples[phase][counts[phase]++] = ms;

    public int SampleCount(int phase) => counts[phase];

    /// <summary>Count, mean and nearest-rank percentiles. Sorts the phase samples in place, so call it once the capture is over.</summary>
    public PhaseStats Stats(int phase)
    {
        int n = counts[phase];
        var s = new PhaseStats { Count = n };
        if (n == 0) return s;
        var a = samples[phase];
        Array.Sort(a, 0, n);
        double sum = 0;
        for (int i = 0; i < n; i++) sum += a[i];
        s.Mean = sum / n;
        s.P50 = Rank(a, n, 0.50);
        s.P95 = Rank(a, n, 0.95);
        s.P99 = Rank(a, n, 0.99);
        s.Max = a[n - 1];
        return s;
    }

    static double Rank(float[] sorted, int n, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * n) - 1, 0, n - 1)];
}

public sealed class GpuAbBlock
{
    public int Index;
    public bool On;
    public double StartMs, EndMs;
    public int Frames;
    public double MeanFps;   // 0 when no frame counted
}

public sealed class GpuAbResult
{
    public double BlockSeconds, DropSeconds;
    public List<GpuAbBlock> Blocks = new();
    public double MeanFpsOff, MeanFpsOn;
    public int BlocksOff, BlocksOn;
    public double? PercentCost;

    public JsonObject ToJson()
    {
        var blocks = new JsonArray();
        foreach (var b in Blocks)
            blocks.Add(new JsonObject
            {
                ["index"] = b.Index,
                ["state"] = b.On ? "on" : "off",
                ["start_ms"] = Math.Round(b.StartMs, 1),
                ["end_ms"] = Math.Round(b.EndMs, 1),
                ["frames"] = b.Frames,
                ["mean_fps"] = b.Frames > 0 ? Math.Round(b.MeanFps, 2) : null,
            });
        return new JsonObject
        {
            ["block_seconds"] = BlockSeconds,
            ["drop_first_seconds_per_block"] = DropSeconds,
            ["blocks"] = blocks,
            ["blocks_off"] = BlocksOff,
            ["blocks_on"] = BlocksOn,
            ["mean_fps_off"] = BlocksOff > 0 ? Math.Round(MeanFpsOff, 2) : null,
            ["mean_fps_on"] = BlocksOn > 0 ? Math.Round(MeanFpsOn, 2) : null,
            ["percent_cost"] = PercentCost == null ? null : Math.Round(PercentCost.Value, 2),
            ["note"] = "Blocks alternate off, on, off, on. Query issue is off in the off blocks; the renderers stay registered in both arms, so their registration and call cost is present in both and is not part of percent_cost. Mean fps pools the frames of all blocks of an arm (frames divided by frame time), after dropping the first second of each block. percent_cost = (off - on) / off * 100.",
        };
    }
}

public static class GpuPhaseAb
{
    /// <summary>
    /// Per block fps from the frame log ring, by frame end stamp, over [startStamp, endStamp). Off block first.
    /// Runs off the render thread after the capture.
    /// </summary>
    public static GpuAbResult Compute(FrameLog log, long startStamp, long endStamp, double blockSeconds, double dropSeconds = 1.0)
    {
        var r = new GpuAbResult { BlockSeconds = blockSeconds, DropSeconds = dropSeconds };
        if (blockSeconds <= 0 || endStamp <= startStamp) return r;
        long freq = Stopwatch.Frequency;
        long blockTicks = (long)(blockSeconds * freq);
        long dropTicks = (long)(dropSeconds * freq);
        int blockCount = (int)((endStamp - startStamp + blockTicks - 1) / blockTicks);
        var frames = new int[blockCount];
        var ms = new double[blockCount];
        for (int i = 0; i < log.RetainedFrames; i++)
        {
            long e = log.RingEnd(i);
            if (e < startStamp || e >= endStamp) continue;
            int b = (int)((e - startStamp) / blockTicks);
            if (e < startStamp + b * blockTicks + dropTicks) continue;
            frames[b]++;
            ms[b] += log.RingFrameMs(i);
        }
        int[] armFrames = new int[2];
        double[] armMs = new double[2];
        for (int b = 0; b < blockCount; b++)
        {
            bool on = (b & 1) == 1;
            var blk = new GpuAbBlock
            {
                Index = b,
                On = on,
                StartMs = b * blockTicks * 1000.0 / freq,
                EndMs = (Math.Min(startStamp + (b + 1) * blockTicks, endStamp) - startStamp) * 1000.0 / freq,
                Frames = frames[b],
                MeanFps = ms[b] > 0 ? frames[b] / (ms[b] / 1000.0) : 0,
            };
            r.Blocks.Add(blk);
            if (frames[b] == 0) continue;
            int arm = on ? 1 : 0;
            armFrames[arm] += frames[b];
            armMs[arm] += ms[b];
            if (on) r.BlocksOn++; else r.BlocksOff++;
        }
        if (armMs[0] > 0) r.MeanFpsOff = armFrames[0] / (armMs[0] / 1000.0);
        if (armMs[1] > 0) r.MeanFpsOn = armFrames[1] / (armMs[1] / 1000.0);
        if (r.BlocksOff > 0 && r.BlocksOn > 0 && r.MeanFpsOff > 0) r.PercentCost = (r.MeanFpsOff - r.MeanFpsOn) / r.MeanFpsOff * 100.0;
        return r;
    }
}

public sealed class GpuInfo
{
    public string Vendor = "", Renderer = "";
    public int TimerBits;
}

public static class GpuPhasesWriter
{
    public const string Note = "GPU time per stage interval measured with GL timestamp queries; per-mod GPU time excluded permanently because draws are batched across mods";

    public static JsonObject Disabled(string reason, GpuInfo? info = null)
    {
        var node = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["label"] = "experimental",
            ["enabled"] = false,
            ["reason"] = reason,
        };
        if (info != null)
        {
            node["gl_vendor"] = info.Vendor;
            node["gl_renderer"] = info.Renderer;
            node["timer_bits"] = info.TimerBits;
        }
        node["note"] = Note;
        return node;
    }

    public static JsonObject Build(GpuPhaseRecorder rec, GpuInfo info, GpuAbResult? ab)
    {
        var phases = new JsonObject();
        var boundaries = new JsonObject();
        for (int p = 0; p < GpuPhase.Count; p++)
        {
            var s = rec.Stats(p);
            boundaries[GpuPhase.Names[p]] = GpuPhase.Boundaries[p];
            phases[GpuPhase.Names[p]] = new JsonObject
            {
                ["count"] = s.Count,
                ["mean_ms"] = Round(s.Mean),
                ["p50_ms"] = Round(s.P50),
                ["p95_ms"] = Round(s.P95),
                ["p99_ms"] = Round(s.P99),
                ["max_ms"] = Round(s.Max),
            };
        }
        return new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["label"] = "experimental",
            ["enabled"] = true,
            ["gl_vendor"] = info.Vendor,
            ["gl_renderer"] = info.Renderer,
            ["timer_bits"] = info.TimerBits,
            ["boundaries"] = boundaries,
            ["phases"] = phases,
            ["frames_measured"] = rec.FramesMeasured,
            ["frames_dropped_not_ready"] = rec.DroppedNotReady,
            ["frames_incomplete"] = rec.Incomplete,
            ["overflow"] = rec.Overflow,
            ["overhead_ab"] = ab?.ToJson(),
            ["note"] = Note,
        };
    }

    static double Round(double v) => Math.Round(v, 4);

    /// <summary>Markdown block for summary.md.</summary>
    public static string Markdown(JsonObject? doc)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## GPU time by render phase [experimental]").AppendLine();
        if (doc?["enabled"]?.GetValue<bool>() != true)
        {
            sb.AppendLine("- Not collected: " + (doc?["reason"]?.GetValue<string>() ?? "file absent")).AppendLine();
            return sb.ToString();
        }
        sb.AppendLine($"Label: experimental (GL timestamp queries, not yet proven in a game run). {doc["gl_renderer"]}, {doc["timer_bits"]} timer bits. {doc["frames_measured"]} frames measured, {doc["frames_dropped_not_ready"]} dropped not ready, {doc["frames_incomplete"]} incomplete, overflow {doc["overflow"]}. Present and vsync wait are not in these numbers.").AppendLine();
        sb.AppendLine("| Phase | Frames | Mean ms | p50 ms | p95 ms | p99 ms | Max ms |").AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var kv in (JsonObject)doc["phases"]!)
        {
            var s = kv.Value!;
            string F(string k) => s[k]!.GetValue<double>().ToString("F3", CultureInfo.InvariantCulture);
            sb.AppendLine($"| {kv.Key} | {s["count"]} | {F("mean_ms")} | {F("p50_ms")} | {F("p95_ms")} | {F("p99_ms")} | {F("max_ms")} |");
        }
        sb.AppendLine();
        if (doc["overhead_ab"] is JsonObject ab)
        {
            string Fps(string k) => ab[k] == null ? "n/a" : ab[k]!.GetValue<double>().ToString("F1", CultureInfo.InvariantCulture);
            sb.AppendLine($"- In-launch A/B ({ab["block_seconds"]} s blocks, {ab["blocks_off"]} off, {ab["blocks_on"]} on): mean {Fps("mean_fps_off")} fps off, {Fps("mean_fps_on")} fps on, cost {(ab["percent_cost"] == null ? "n/a" : ab["percent_cost"]!.GetValue<double>().ToString("F2", CultureInfo.InvariantCulture) + " percent")}. Renderer registration cost is in both arms.");
        }
        sb.AppendLine("- Per-mod GPU time is excluded permanently: draws are batched across mods. Detail: gpu-phases.json.").AppendLine();
        return sb.ToString();
    }
}
