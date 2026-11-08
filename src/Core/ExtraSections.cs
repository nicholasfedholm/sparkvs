using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>Headline and markdown for the bundle files written by the mod rather than by the report step.</summary>
public static class ExtraSections
{
    public static JsonObject? Load(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch { return null; }
    }

    public static JsonObject Headline(string file, JsonObject? doc)
    {
        var h = new JsonObject { ["detail_file"] = file };
        if (doc == null || doc["enabled"]?.GetValue<bool>() != true) return h;
        h["label"] = doc["label"]?.DeepClone();
        switch (file)
        {
            case "chunk-pipeline.json":
                var stages = doc["stages"] as JsonObject;
                var top = new JsonArray();
                if (stages != null)
                    foreach (var kv in stages.Where(k => k.Value?["total_ms"] != null).OrderByDescending(k => k.Value!["total_ms"]!.GetValue<double>()).Take(5))
                        top.Add(new JsonObject { ["stage"] = kv.Key, ["calls"] = kv.Value!["calls"]?.DeepClone(), ["total_ms"] = kv.Value["total_ms"]?.DeepClone(), ["p95_ms"] = kv.Value["p95_ms"]?.DeepClone() });
                h["stages_with_calls"] = stages?.Count(k => (k.Value?["calls"]?.GetValue<long>() ?? 0) > 0);
                h["top_stages_by_total_ms"] = top;
                h["upload"] = doc["upload"]?.DeepClone();
                break;
            case "patch-map.json":
                return PatchMapBuilder.Headline(doc);
            case "gpu-phases.json":
                h["frames_measured"] = doc["frames_measured"]?.DeepClone();
                h["frames_dropped_not_ready"] = doc["frames_dropped_not_ready"]?.DeepClone();
                var gp = new JsonObject();
                if (doc["phases"] is JsonObject ph)
                    foreach (var kv in ph) gp[kv.Key] = new JsonObject { ["mean_ms"] = kv.Value?["mean_ms"]?.DeepClone(), ["p95_ms"] = kv.Value?["p95_ms"]?.DeepClone() };
                h["phases"] = gp;
                h["overhead_percent_cost"] = doc["overhead_ab"]?["percent_cost"]?.DeepClone();
                break;
            case "asset-inventory.json":
                h["source_path"] = doc["source_path"]?.DeepClone();
                h["source_modified_local"] = doc["source_modified_local"]?.DeepClone();
                break;
        }
        return h;
    }

    public const string CensusStartFile = "census-start.json";
    public const string CensusEndFile = "census-end.json";
    public const string EventsFile = "events.jsonl";
    public const string OverheadKey = "sparkvs_overhead";

    /// <summary>The start/end census pair, or null with a reason when either is missing or disabled.</summary>
    public static CensusDiff? LoadCensusDiff(string dir, out string? reason, out EntityCensus? start, out EntityCensus? end)
    {
        var a = Load(Path.Combine(dir, CensusStartFile));
        var b = Load(Path.Combine(dir, CensusEndFile));
        start = EntityCensus.FromJson(a);
        end = EntityCensus.FromJson(b);
        if (start == null || end == null)
        {
            reason = start == null ? (a?["reason"]?.GetValue<string>() ?? CensusStartFile + " absent") : (b?["reason"]?.GetValue<string>() ?? CensusEndFile + " absent");
            return null;
        }
        reason = null;
        return EntityCensus.Diff(start, end);
    }

    /// <summary>The overhead block the mod stores in capture.meta.json (event handler cost, census durations).</summary>
    public static JsonObject? LoadOverhead(string dir) => Load(Path.Combine(dir, ReportBuilder.MetaFile))?[OverheadKey] as JsonObject;

    public static string OverheadMarkdown(string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Overhead of the event and census collection [measured]").AppendLine();
        var o = LoadOverhead(dir);
        if (o == null) return sb.AppendLine("- Not recorded: capture.meta.json has no " + OverheadKey + " block.").AppendLine().ToString();
        sb.AppendLine($"- Chunk event handlers: {o["event_handler_calls"]} calls, {o["event_handler_ns"]} ns cumulative ({o["event_handler_ns_per_call"]} ns per call). Handlers were subscribed only during the capture window; they bump an Interlocked counter and nothing else.");
        sb.AppendLine($"- Entity census: start {o["census_start_ms"]} ms, end {o["census_end_ms"]} ms (one pass over the loaded entity list each; runs on the capture thread, so it is a one time stall, not a rate).");
        sb.AppendLine($"- 1 Hz events sampler: {o["events_sampler_ticks"]} ticks, {o["events_sampler_errors"]} errors.");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Entity census, events and overhead sections shared by the client bundle summary and the server bundle summary.</summary>
    public static string CensusEventsMarkdown(string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Entity census [measured]").AppendLine();
        var diff = LoadCensusDiff(dir, out string? reason, out var start, out var end);
        if (diff == null) sb.AppendLine("- Not collected: " + reason).AppendLine();
        else
        {
            sb.AppendLine($"Census cost: start {start!.DurationMs:F1} ms, end {end!.DurationMs:F1} ms. Densest chunk at end: " + (end.Densest == null ? "none" : $"chunk ({end.Densest.Cx},{end.Densest.Cy},{end.Densest.Cz}) near block x={end.Densest.BlockX} z={end.Densest.BlockZ}, {end.Densest.Count} entities, mostly {string.Join(", ", end.Densest.TopCodes.Take(3).Select(kv => kv.Key + " " + kv.Value))}") + ". Files: census-start.json, census-end.json.").AppendLine();
            sb.Append(diff.Markdown(10));
        }
        sb.Append(EntityTickReport.MarkdownFromDir(dir));
        sb.AppendLine("## Events [measured]").AppendLine();
        sb.Append(EventLog.Summarize(Path.Combine(dir, EventsFile)).Markdown());
        sb.Append(OverheadMarkdown(dir));
        return sb.ToString();
    }

    /// <summary>summary.md of a server bundle: there is no CPU report, so the bundle's own series, census, events and overhead make up the whole summary.</summary>
    public static string ServerSummaryMarkdown(string dir)
    {
        var sb = new StringBuilder();
        var meta = Load(Path.Combine(dir, ReportBuilder.MetaFile));
        sb.AppendLine("# SparkVS server capture: " + (meta?["label"]?.GetValue<string>() is { Length: > 0 } l ? l : Path.GetFileName(dir))).AppendLine();
        sb.AppendLine($"Mode: server. Game: {meta?["game_version"]}. Started: {meta?["started_local"]}. Duration: {meta?["duration_sec"]} s (requested {meta?["requested_seconds"]} s). Entity source: {meta?["entity_source"]}.").AppendLine();
        sb.AppendLine("No client frames, CPU stacks or heap dump in a server bundle; see capture.meta.json for what ran.").AppendLine();
        BundleSummary? bundle = null;
        try { bundle = meta?["bundle"]?.Deserialize<BundleSummary>(); } catch { }
        if (bundle != null)
        {
            var t = bundle.Tick;
            sb.AppendLine("## Server tick [experimental]").AppendLine();
            sb.AppendLine(t.Enabled ? $"- {t.TickCount:N0} ticks: mean {t.MeanMsptMs:F2} ms, p95 {t.P95MsptMs:F1} ms, max {t.MaxMsptMs:F1} ms of work per tick. Series: tick.jsonl." : "- Not collected: " + t.Reason).AppendLine();
            var h = bundle.Heap;
            sb.AppendLine("## Managed heap [measured]").AppendLine();
            sb.AppendLine(h.Enabled ? $"- {h.Samples} samples at 1 Hz. GC.GetTotalMemory: start {h.StartTotalMemoryBytes / 1048576.0:F0} MB, end {h.EndTotalMemoryBytes / 1048576.0:F0} MB, peak {h.PeakTotalMemoryBytes / 1048576.0:F0} MB. Series: heap.jsonl." : "- Not collected: " + h.Reason).AppendLine();
        }
        sb.Append(CensusEventsMarkdown(dir));
        return sb.ToString();
    }

    public static string Markdown(string dir)
    {
        var sb = new StringBuilder();
        var cp = Load(Path.Combine(dir, "chunk-pipeline.json"));
        sb.AppendLine("## Chunk pipeline").AppendLine();
        if (cp?["enabled"]?.GetValue<bool>() != true)
            sb.AppendLine("- Not collected: " + (cp?["reason"]?.GetValue<string>() ?? "file absent")).AppendLine();
        else
        {
            sb.AppendLine("Label: measured (wall ms inside the patched method). Stages with no hook or no calls are null with a reason in chunk-pipeline.json.").AppendLine();
            sb.AppendLine("| Stage | Calls | Total ms | Mean ms | p95 ms | Max ms |").AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var kv in (JsonObject)cp["stages"]!)
            {
                var s = kv.Value!;
                string F(string k) => s[k] == null ? "null" : s[k]!.ToJsonString();
                sb.AppendLine($"| {kv.Key} | {F("calls")} | {F("total_ms")} | {F("mean_ms")} | {F("p95_ms")} | {F("max_ms")} |");
            }
            var up = cp["upload"];
            sb.AppendLine().AppendLine($"Upload: {up?["mesh_parts_uploaded"]} mesh parts, {up?["vertices_uploaded"]} vertices, about {up?["bytes_uploaded_estimated"]} bytes (estimated). Queue depth series: {cp["queue_depth"]?["samples"]?.ToJsonString() ?? "none"} samples.").AppendLine();
        }
        var pm = Load(Path.Combine(dir, "patch-map.json"));
        sb.AppendLine("## Harmony patch map").AppendLine();
        if (pm?["enabled"]?.GetValue<bool>() != true) sb.AppendLine("- Not collected: " + (pm?["reason"]?.GetValue<string>() ?? "file absent")).AppendLine();
        else
        {
            foreach (var side in new[] { "client", "server" })
            {
                var s = pm["sides"]?[side];
                if (s?["available"]?.GetValue<bool>() == true)
                    sb.AppendLine($"- {side}: {s["patched_methods_total"]} patched methods, {s["patches_total"]} patches, {s["owners_total"]} owners, {s["methods_patched_by_multiple_owners"]} methods patched by more than one owner.");
                else sb.AppendLine($"- {side}: not available, {s?["reason"]}");
            }
            sb.AppendLine("- Detail: patch-map.json (owners and kinds per shared method).").AppendLine();
        }
        var ai = Load(Path.Combine(dir, "asset-inventory.json"));
        sb.AppendLine("## Asset inventory").AppendLine();
        if (ai?["enabled"]?.GetValue<bool>() != true) sb.AppendLine("- Not collected: " + (ai?["reason"]?.GetValue<string>() ?? "file absent")).AppendLine();
        else sb.AppendLine($"- Label: static. Copied from {ai["source_path"]} (scanned {ai["source_modified_local"]}). Not measured during this capture.").AppendLine();
        sb.Append(GpuPhasesWriter.Markdown(Load(Path.Combine(dir, "gpu-phases.json"))));
        sb.Append(CensusEventsMarkdown(dir));
        return sb.ToString();
    }
}
