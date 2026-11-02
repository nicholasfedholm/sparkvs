using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SparkVS.Core;

public sealed class DeltaRow
{
    public string Key { get; set; } = "";
    public double A { get; set; }
    public double B { get; set; }
}

public static class SummaryWriter
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ToJson(Report report) => JsonSerializer.Serialize(report, JsonOptions);
    public static Report? FromJson(string json) => JsonSerializer.Deserialize<Report>(json);

    static string N(double v, int digits = 1) => v.ToString("F" + digits, CultureInfo.InvariantCulture);
    static string Cell(string s) => s.Replace("|", "\\|");

    public static string ToMarkdown(Report r, bool labelled = false)
    {
        string Tag(string label) => labelled ? " [" + label + "]" : "";
        var sb = new StringBuilder();
        sb.AppendLine("# SparkVS capture: " + (r.Label.Length > 0 ? r.Label : r.StartedLocal));
        sb.AppendLine();
        sb.AppendLine($"World: {r.World}. Game: {r.GameVersion}. Started: {r.StartedLocal}. Duration: {N(r.DurationSec)} s.");
        sb.AppendLine();
        sb.AppendLine(r.ScopeNote);
        sb.AppendLine();
        if (labelled && r.Bundle != null) AppendBundle(sb, r);

        var main = r.Threads.FirstOrDefault(t => t.Role == "client-main");
        if (main == null)
        {
            sb.AppendLine("No client main thread samples in this capture.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Client main thread: top 15 methods by self time" + Tag("estimated"));
            sb.AppendLine();
            sb.AppendLine("| Self ms | Self % | Total % | Method | Assembly | Owner |");
            sb.AppendLine("|---:|---:|---:|---|---|---|");
            foreach (var m in main.Methods.Take(15))
                sb.AppendLine($"| {N(m.SelfMs, 0)} | {N(m.SelfPct)} | {N(m.TotalPct)} | {Cell(Analyzer.ShortName(m.Method))} | {Cell(m.Module)} | {Cell(m.Owner)} |");
            sb.AppendLine();
            sb.AppendLine("## Client main thread: by owning mod" + Tag("estimated"));
            sb.AppendLine();
            sb.AppendLine("Self bills each sample to the first non runtime frame up from the leaf. Inclusive is any frame of that owner anywhere in the stack.");
            sb.AppendLine();
            AppendGroups(sb, main.ByMod, 15, "Owner");
            sb.AppendLine("## Client main thread: by assembly" + Tag("estimated"));
            sb.AppendLine();
            AppendGroups(sb, main.ByAssembly, 15, "Assembly");
            sb.AppendLine("## Client main thread: top call paths (leaf side, 8 frames)" + Tag("estimated"));
            sb.AppendLine();
            sb.AppendLine("| Ms | % | Path |");
            sb.AppendLine("|---:|---:|---|");
            foreach (var p in main.TopPaths.Take(10))
                sb.AppendLine($"| {N(p.Ms, 0)} | {N(p.Pct)} | {Cell(p.Path)} |");
            sb.AppendLine();
        }

        sb.AppendLine($"## Frames over {N(r.SpikeThresholdMs, 0)} ms: {r.SpikesOverThreshold} of {r.FrameCount}" + (r.SpikesDropped > 0 ? $" ({r.SpikesDropped} not recorded, buffer full)" : "") + Tag("measured: frame times; estimated: the dominant method and owner"));
        sb.AppendLine();
        sb.AppendLine("| At s | Frame ms | Samples in frame | Dominant method | Owner | Gen0/1/2 |");
        sb.AppendLine("|---:|---:|---:|---|---|---|");
        foreach (var s in r.Spikes.Take(15))
            sb.AppendLine($"| {N(s.AtSec)} | {N(s.DurationMs)} | {s.SamplesInFrame} | {Cell(s.DominantMethod)} | {Cell(s.DominantOwner)} | {s.Gen0}/{s.Gen1}/{s.Gen2} |");
        sb.AppendLine();
        sb.AppendLine($"GC collections during capture: gen0 {r.Gen0}, gen1 {r.Gen1}, gen2 {r.Gen2}.");
        sb.AppendLine();

        sb.AppendLine("## Capture overhead" + Tag("measured"));
        sb.AppendLine();
        sb.AppendLine($"- Fps in the seconds before the session vs during it: {N(r.BaselineFps)} vs {N(r.CaptureFps)} ({N(r.CaptureFpsDeltaPct)} %). The scene can change during a capture, so the bench A/B is the real gate.");
        sb.AppendLine($"- Stream reader thread allocated {r.SamplerBytesAllocated:N0} bytes; frame hook allocated {r.FrameHookBytesAllocated:N0} bytes.");
        sb.AppendLine($"- Trace size {N(r.NettraceBytes / 1048576.0)} MB. Runtime sampling rate: {(r.SamplingRateKnob.Length > 0 ? r.SamplingRateKnob + " (DOTNET_EventPipeThreadSamplingRate, hex ms, set at game launch)" : "not set, runtime default of about 1 ms")}.");
        sb.AppendLine($"- Stack capture: {r.SamplesWithResolvedStack:N0} of {r.TotalSamples:N0} samples resolved; main thread {r.MainThreadSamples:N0} samples, {N(r.MainResolvedPct)} % resolved, mean interval {N(r.MainMeanIntervalMs, 2)} ms.");
        sb.AppendLine();

        sb.AppendLine("## Threads" + Tag("estimated"));
        sb.AppendLine();
        sb.AppendLine("| Thread | Role | Samples | Idle % | Native % | Top self method | Owner |");
        sb.AppendLine("|---:|---|---:|---:|---:|---|---|");
        foreach (var t in r.Threads)
        {
            var top = t.Methods.FirstOrDefault();
            sb.AppendLine($"| {t.OsThreadId} | {t.Role} | {t.Samples} | {N(t.IdlePct)} | {N(t.ExternalPct)} | {Cell(top == null ? "" : Analyzer.ShortName(top.Method))} | {Cell(top?.Owner ?? "")} |");
        }
        return sb.ToString();
    }

    /// <summary>Bundle sections at the top of summary.md. Every heading says whether it is measured, estimated or experimental.</summary>
    static void AppendBundle(StringBuilder sb, Report r)
    {
        var b = r.Bundle!;
        sb.AppendLine("Labels: **measured** is read straight from a counter or timer; **estimated** is derived by sampling or inference; **experimental** is a new hook not yet proven in a game run. Bundle schema_version " + b.SchemaVersion + ", SparkVS " + b.SparkVsVersion + ", capture start " + b.StartLocal + ".");
        sb.AppendLine();

        var f = b.Frames;
        sb.AppendLine("## Frames [measured]");
        sb.AppendLine();
        sb.AppendLine($"- {f.FrameCount:N0} frames in {N(f.DurationMs / 1000.0, 1)} s, mean {N(f.MeanFps)} fps (mean frame {N(f.MeanFrameMs, 2)} ms). Present mode is not measured in process; the bench runner records it, and no fps number here is quotable without it.");
        sb.AppendLine($"- Frame ms: p50 {N(f.P50FrameMs, 2)}, p95 {N(f.P95FrameMs, 2)}, p99 {N(f.P99FrameMs, 2)}, max {N(f.MaxFrameMs, 2)}. 1 percent low {N(f.OnePercentLowFps)} fps (mean of the slowest 1 percent of frames).");
        sb.AppendLine($"- {f.OverThresholdCount} frames over {N(f.OverThresholdMs, 0)} ms, listed with nearest stack and GC deltas in frames-summary.json. Per frame series: frames.jsonl" + (f.DroppedOldestFrames > 0 ? $" ({f.DroppedOldestFrames} oldest frames overwritten, ring full)." : "."));
        sb.AppendLine();

        var t = b.Tick;
        sb.AppendLine("## Server tick, integrated server [experimental]");
        sb.AppendLine();
        if (t.Enabled)
            sb.AppendLine($"- {t.TickCount:N0} ticks: mean {N(t.MeanMsptMs, 2)} ms, p95 {N(t.P95MsptMs, 1)} ms, max {N(t.MaxMsptMs, 1)} ms of work per tick (whole ms resolution). Mean tick period {N(t.MeanIntervalMs, 1)} ms. Series: tick.jsonl.");
        else
            sb.AppendLine("- Not collected: " + t.Reason);
        sb.AppendLine();

        var h = b.Heap;
        sb.AppendLine("## Managed heap [measured]");
        sb.AppendLine();
        if (h.Enabled)
            sb.AppendLine($"- {h.Samples} samples at 1 Hz. Managed memory (GC.GetTotalMemory): start {h.StartTotalMemoryBytes / 1048576.0:F0} MB, end {h.EndTotalMemoryBytes / 1048576.0:F0} MB, peak {h.PeakTotalMemoryBytes / 1048576.0:F0} MB, mean {h.MeanTotalMemoryBytes / 1048576.0:F0} MB. GC counts and pause percent in heap.jsonl.");
        else
            sb.AppendLine("- Not collected: " + h.Reason);
        sb.AppendLine();

        var v = b.Vram;
        sb.AppendLine("## VRAM, dedicated, this process [experimental until the DXGI versus PDH Local gate passes in a live run]");
        sb.AppendLine();
        if (v.Enabled)
        {
            sb.AppendLine($"- Source {v.Source}, adapter {v.Adapter}, {v.Samples} samples at 1 Hz. Dedicated: start {v.StartDedicatedBytes / 1048576.0:F0} MB, end {v.EndDedicatedBytes / 1048576.0:F0} MB, peak {v.PeakDedicatedBytes / 1048576.0:F0} MB, mean {v.MeanDedicatedBytes / 1048576.0:F0} MB.");
            if (v.PdhMeanDedicatedBytes != null)
                sb.AppendLine($"- PDH cross-check, same ticks: mean {v.PdhMeanDedicatedBytes.Value / 1048576.0:F0} MB, DXGI differs by {N(v.DxgiVsPdhMeanDiffPercent ?? 0, 1)} percent. Series: gpu-memory.jsonl.");
            else
                sb.AppendLine("- PDH cross-check unavailable in this run.");
            sb.AppendLine("- " + VramGate.Describe(v.VramGate));
        }
        else
            sb.AppendLine("- Not collected: " + v.Reason);
        sb.AppendLine();

        sb.AppendLine("## Not collected in this build");
        sb.AppendLine();
        sb.AppendLine("- Deferred on purpose, enabled false: vram-by-mod.json (deferred, see capture-stack-audit/PLAN.md).");
        sb.AppendLine("- Per-mod GPU time is excluded permanently: draw calls are batched across mods, so GPU time cannot be attributed to one mod.");
        sb.AppendLine();
    }

    static void AppendGroups(StringBuilder sb, List<GroupRow> rows, int count, string title)
    {
        sb.AppendLine($"| {title} | Self ms | Self % | Inclusive % |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var g in rows.Take(count))
            sb.AppendLine($"| {Cell(g.Name)} | {N(g.SelfMs, 0)} | {N(g.SelfPct)} | {N(g.InclusivePct)} |");
        sb.AppendLine();
    }

    /// <summary>Delta rows between two captures in ms per second of capture, so differing lengths compare.</summary>
    public static List<DeltaRow> Compare(Report a, Report b, string? role, bool byTotal, int top)
    {
        var left = Rates(a, role, byTotal);
        var right = Rates(b, role, byTotal);
        return left.Keys.Union(right.Keys)
            .Select(k => new DeltaRow { Key = k, A = left.GetValueOrDefault(k), B = right.GetValueOrDefault(k) })
            .OrderByDescending(d => System.Math.Abs(d.B - d.A))
            .Take(top)
            .ToList();
    }

    static Dictionary<string, double> Rates(Report r, string? role, bool byTotal)
    {
        var map = new Dictionary<string, double>();
        double seconds = r.DurationSec > 0 ? r.DurationSec : 1;
        foreach (var t in r.Threads)
        {
            if (role != null && t.Role != role) continue;
            foreach (var m in t.Methods)
            {
                var key = t.Role + " | " + Analyzer.ShortName(m.Method);
                map[key] = map.GetValueOrDefault(key) + (byTotal ? m.TotalMs : m.SelfMs) / seconds;
            }
        }
        return map;
    }
}
