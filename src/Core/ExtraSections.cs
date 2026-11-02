using System;
using System.IO;
using System.Linq;
using System.Text;
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
        return sb.ToString();
    }
}
