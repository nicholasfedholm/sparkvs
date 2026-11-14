using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

public static class ReportBuilder
{
    public const string MetaFile = "capture.meta.json";
    public const string TraceFile = "capture.nettrace";

    /// <summary>
    /// Builds the report files from a capture folder. A start/stop capture gets summary.json, summary.md and
    /// profile.speedscope.json exactly as before. A bundle capture (the meta has bundle stats) gets
    /// cpu-summary.json (the full report), speedscope.json, a headline summary.json and a labelled summary.md,
    /// and its frames-summary.json is completed with the stacks found.
    /// </summary>
    public static Report Run(string captureDir, Action<string>? progress = null, bool keepRaw = true, int maxThreads = 16)
    {
        var meta = JsonSerializer.Deserialize<CaptureMeta>(File.ReadAllText(Path.Combine(captureDir, MetaFile)))
            ?? throw new InvalidDataException("capture.meta.json is empty");
        string trace = Path.Combine(captureDir, TraceFile);
        var set = NettraceLoader.Load(trace, meta, progress);
        progress?.Invoke("samples=" + set.Samples.Count);
        var report = Analyzer.Build(set, meta, maxThreads);
        bool bundle = report.Bundle != null;

        if (bundle)
        {
            File.WriteAllText(Path.Combine(captureDir, "cpu-summary.json"), SummaryWriter.ToJson(report));
            var alloc = meta.AllocationSampling
                ? AllocationReport.Build(set, meta)
                : AllocationReport.Disabled("allocation sampling was off for this capture (AllocationSampling false in SparkVS.json)");
            BundleWriter.WriteJson(Path.Combine(captureDir, "allocations.json"), alloc);
            File.WriteAllText(Path.Combine(captureDir, "summary.json"), HeadlineJson(report, alloc, captureDir));
            File.WriteAllText(Path.Combine(captureDir, "summary.md"), SummaryWriter.ToMarkdown(report, true) + Environment.NewLine + AllocationReport.ToMarkdown(alloc) + Environment.NewLine + ExtraSections.Markdown(captureDir, CaptureSide.IsServer(report.Side)));
            using (var fs = File.Create(Path.Combine(captureDir, "speedscope.json")))
                Speedscope.Write(fs, set, report, report.Label);
            BundleWriter.AddStacksToFramesSummary(captureDir, report);
        }
        else
        {
            File.WriteAllText(Path.Combine(captureDir, "summary.json"), SummaryWriter.ToJson(report));
            File.WriteAllText(Path.Combine(captureDir, "summary.md"), SummaryWriter.ToMarkdown(report));
            using (var fs = File.Create(Path.Combine(captureDir, "profile.speedscope.json")))
                Speedscope.Write(fs, set, report, report.Label);
        }

        if (!keepRaw)
        {
            try { File.Delete(trace); } catch (IOException) { }
        }
        return report;
    }

    /// <summary>summary.json of a bundle: the headline numbers of every section, each with its label, and where the detail lives.</summary>
    public static string HeadlineJson(Report r, JsonObject? alloc = null, string? captureDir = null)
    {
        var b = r.Bundle!;
        var main = Analyzer.FindMain(r);
        var topMods = new JsonArray();
        if (main != null)
            foreach (var g in main.ByMod.Take(8))
                topMods.Add(new JsonObject { ["owner"] = g.Name, ["self_ms"] = Math.Round(g.SelfMs, 1), ["self_percent"] = Math.Round(g.SelfPct, 2), ["inclusive_percent"] = Math.Round(g.InclusivePct, 2) });

        var node = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["sparkvs_version"] = b.SparkVsVersion,
            ["label"] = r.Label,
            ["world"] = r.World,
            ["game_version"] = r.GameVersion,
            ["start_local"] = b.StartLocal,
            ["duration_ms"] = Math.Round(r.DurationSec * 1000.0, 1),
            ["sections"] = new JsonObject
            {
                ["frames"] = new JsonObject { ["label"] = "measured", ["detail_file"] = "frames-summary.json", ["data"] = JsonSerializer.SerializeToNode(b.Frames) },
                ["tick"] = new JsonObject { ["label"] = "experimental", ["detail_file"] = "tick.jsonl", ["data"] = JsonSerializer.SerializeToNode(b.Tick) },
                ["heap"] = new JsonObject { ["label"] = "measured", ["detail_file"] = "heap.jsonl", ["data"] = JsonSerializer.SerializeToNode(b.Heap) },
                ["vram_dedicated"] = new JsonObject { ["label"] = "experimental", ["label_note"] = "stays experimental until the corrected gate (DXGI CurrentUsage vs PDH Local Usage, resident vs resident) passes in a live settled run. " + VramGate.Describe(b.Vram?.VramGate), ["detail_file"] = "gpu-memory.jsonl", ["data"] = JsonSerializer.SerializeToNode(b.Vram) },
                ["cpu_sampling"] = new JsonObject
                {
                    ["label"] = "estimated",
                    ["detail_file"] = "cpu-summary.json",
                    ["flamegraph_file"] = "speedscope.json",
                    ["main_thread_samples"] = r.MainThreadSamples,
                    ["main_thread_resolved_percent"] = Math.Round(r.MainResolvedPct, 2),
                    ["top_owners_main_thread"] = topMods,
                },
            },
            ["note"] = "Per-mod GPU time is excluded permanently: draw calls are batched across mods.",
        };
        var sections = (JsonObject)node["sections"]!;
        var notCollected = new JsonArray();
        var reasons = new JsonObject();
        bool server = CaptureSide.IsServer(r.Side);
        if (server)
        {
            // A dedicated server has no frames, GPU or render phases: those sections and files are left out, not faked.
            node["side"] = CaptureSide.Server;
            sections.Remove("frames");
            sections.Remove("vram_dedicated");
            foreach (var file in CaptureSide.ClientOnlyFiles)
            {
                notCollected.Add(file);
                reasons[file] = CaptureSide.ClientOnlyReason;
            }
        }
        if (alloc != null) AddSection(sections, notCollected, reasons, "allocations", "allocations.json", AllocationReport.Headline(alloc), alloc);
        if (captureDir != null)
        {
            var extra = new[] { ("chunk_pipeline", "chunk-pipeline.json"), ("patch_map", "patch-map.json"), ("asset_inventory", "asset-inventory.json"), ("vram_by_mod", "vram-by-mod.json"), ("gpu_phases", "gpu-phases.json") };
            if (server) extra = extra.Where(e => !CaptureSide.ClientOnlyFiles.Contains(e.Item2)).ToArray();
            foreach (var (key, file) in extra)
            {
                var doc = ExtraSections.Load(Path.Combine(captureDir, file));
                AddSection(sections, notCollected, reasons, key, file, ExtraSections.Headline(file, doc), doc);
            }
            // gameevents.jsonl is a JSONL series, not a json document with an enabled flag, and it exists only when EventLog was on for the capture.
            string eventsFile = Path.Combine(captureDir, EventLine.FileName);
            if (File.Exists(eventsFile))
            {
                try { sections["game_events"] = EventSummary.FromJsonl(eventsFile).ToJson(); }
                catch (Exception ex) { notCollected.Add(EventLine.FileName); reasons[EventLine.FileName] = "gameevents.jsonl could not be summarised: " + ex.Message; }
            }
        }
        if (captureDir != null) AddCensusAndEvents(captureDir, sections, notCollected, reasons);
        node["not_collected"] = notCollected;
        node["not_collected_reasons"] = reasons;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Entity census diff, event counts and the cost of collecting them, for summary.json.</summary>
    static void AddCensusAndEvents(string dir, JsonObject sections, JsonArray notCollected, JsonObject reasons)
    {
        var diff = ExtraSections.LoadCensusDiff(dir, out string? reason, out var start, out var end);
        if (diff == null) { notCollected.Add("census-start.json/census-end.json"); reasons["census-start.json/census-end.json"] = reason ?? "absent"; }
        else
        {
            sections["entity_census"] = new JsonObject
            {
                ["label"] = "measured",
                ["detail_file"] = "census-end.json",
                ["side"] = end!.Side,
                ["census_duration_ms"] = new JsonObject { ["start"] = Math.Round(start!.DurationMs, 2), ["end"] = Math.Round(end.DurationMs, 2) },
                ["densest_chunk_at_end"] = end.ToJson(BuildInfo.SchemaVersion)["densest_chunk"]?.DeepClone(),
                ["diff"] = diff.ToJson(10),
            };
        }
        string eventsPath = Path.Combine(dir, ExtraSections.EventsFile);
        if (!File.Exists(eventsPath)) { notCollected.Add(ExtraSections.EventsFile); reasons[ExtraSections.EventsFile] = "file absent from the bundle"; }
        else
        {
            var ev = EventLog.Summarize(eventsPath);
            sections["events"] = new JsonObject
            {
                ["label"] = "measured",
                ["detail_file"] = ExtraSections.EventsFile,
                ["rows_by_type"] = JsonSerializer.SerializeToNode(ev.ByType),
                ["server_chunks_loaded"] = ev.Loaded,
                ["server_chunks_unloaded"] = ev.Unloaded,
                ["client_chunks_received"] = ev.ClientReceived,
                ["busiest_second_loads"] = ev.PeakLoadedInSecond,
                ["gen2_collections_seen"] = ev.Gen2Collections,
                ["teleports"] = ev.Teleports.Count,
                ["tool_rows"] = ev.ToolRows.Count,
            };
        }
        var overhead = ExtraSections.LoadOverhead(dir);
        if (overhead != null) sections["event_collection_overhead"] = new JsonObject { ["label"] = "measured", ["detail_file"] = ReportBuilder.MetaFile, ["data"] = overhead.DeepClone() };
    }

    static void AddSection(JsonObject sections, JsonArray notCollected, JsonObject reasons, string key, string file, JsonObject headline, JsonObject? doc)
    {
        if (doc != null && doc["enabled"]?.GetValue<bool>() == true) { sections[key] = headline; return; }
        notCollected.Add(file);
        reasons[file] = doc?["reason"]?.GetValue<string>() ?? "file absent from the bundle";
    }
}
