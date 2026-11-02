using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>
/// asset-inventory.json: a copy of the newest static inventory made by sparkvs\asset_inventory.py. It is a disk scan of
/// the Mods folder, not something measured during the capture, and is labelled so.
/// </summary>
public static class AssetInventoryCopy
{
    public static JsonObject Build(string? folder, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Off("inventory folder not found: " + (folder ?? "(not configured)") + " (run sparkvs\\asset_inventory.py)");
        var newest = new DirectoryInfo(folder).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest == null) return Off("no .json inventory file in " + folder + " (run sparkvs\\asset_inventory.py)");
        JsonNode? content;
        try { content = JsonNode.Parse(File.ReadAllText(newest.FullName)); }
        catch (Exception ex) { return Off("newest inventory file " + newest.FullName + " is not readable JSON: " + ex.GetType().Name); }
        if (content is not JsonObject inv) return Off("newest inventory file " + newest.FullName + " is not a JSON object");

        return new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["enabled"] = true,
            ["label"] = "static",
            ["label_note"] = "disk scan of the Mods folder taken at source_modified_local, not measured during this capture; mods changed since then are not reflected",
            ["source_path"] = newest.FullName,
            ["source_modified_local"] = BundleClock.Iso(newest.LastWriteTime),
            ["source_bytes"] = newest.Length,
            ["copied_local"] = BundleClock.Iso(nowLocal),
            ["source_age_hours"] = Math.Round((nowLocal - newest.LastWriteTime).TotalHours, 1),
            ["inventory"] = inv,
        };
    }

    static JsonObject Off(string reason) => new() { ["schema_version"] = BuildInfo.SchemaVersion, ["enabled"] = false, ["reason"] = reason };
}
