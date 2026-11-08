using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>Densest 32 block chunk in a census.</summary>
public sealed class DenseChunk
{
    public int Cx, Cy, Cz, BlockX, BlockZ;
    public long Count;
    public List<KeyValuePair<string, long>> TopCodes = new();
}

/// <summary>
/// Entity census: counts of loaded entities by code and by mod domain, plus the densest chunk. Pure (no game references): the mod feeds it
/// (code, x, y, z) tuples from whichever entity list it can reach, and writes the result as census-start.json / census-end.json.
/// </summary>
public sealed class EntityCensus
{
    public const int ChunkSize = 32;
    public const string DefaultDomain = "game";

    public string Side = "server";
    public string Source = "";
    public double TMs;
    public string TsLocal = "";
    public double DurationMs;
    public long Total;
    public List<KeyValuePair<string, long>> ByCode = new();
    public List<KeyValuePair<string, long>> ByDomain = new();
    public DenseChunk? Densest;

    public static string DomainOf(string code)
    {
        int i = code.IndexOf(':');
        return i > 0 ? code.Substring(0, i) : DefaultDomain;
    }

    static int ChunkIndex(double v) => (int)Math.Floor(v / ChunkSize);

    static List<KeyValuePair<string, long>> Sorted(Dictionary<string, long> map) =>
        map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();

    /// <summary>Builds a census from (code, x, y, z) tuples. Single pass over the input, which is enumerated exactly once.</summary>
    public static EntityCensus Build(IEnumerable<(string Code, double X, double Y, double Z)> entities, string side)
    {
        var census = new EntityCensus { Side = side };
        var codes = new Dictionary<string, long>(StringComparer.Ordinal);
        var domains = new Dictionary<string, long>(StringComparer.Ordinal);
        var chunkCount = new Dictionary<(int, int, int), long>();
        var rows = new List<(string Code, int Cx, int Cy, int Cz)>();
        foreach (var (rawCode, x, y, z) in entities)
        {
            string code = string.IsNullOrEmpty(rawCode) ? "(no code)" : rawCode;
            census.Total++;
            codes[code] = codes.GetValueOrDefault(code) + 1;
            string domain = DomainOf(code);
            domains[domain] = domains.GetValueOrDefault(domain) + 1;
            var key = (ChunkIndex(x), ChunkIndex(y), ChunkIndex(z));
            chunkCount[key] = chunkCount.GetValueOrDefault(key) + 1;
            rows.Add((code, key.Item1, key.Item2, key.Item3));
        }
        census.ByCode = Sorted(codes);
        census.ByDomain = Sorted(domains);
        if (chunkCount.Count > 0)
        {
            // Highest count; ties go to the lowest coordinates so the answer is deterministic.
            var best = chunkCount.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item3).First();
            var inChunk = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var r in rows)
                if (r.Cx == best.Key.Item1 && r.Cy == best.Key.Item2 && r.Cz == best.Key.Item3)
                    inChunk[r.Code] = inChunk.GetValueOrDefault(r.Code) + 1;
            census.Densest = new DenseChunk
            {
                Cx = best.Key.Item1, Cy = best.Key.Item2, Cz = best.Key.Item3,
                BlockX = best.Key.Item1 * ChunkSize, BlockZ = best.Key.Item3 * ChunkSize,
                Count = best.Value,
                TopCodes = Sorted(inChunk).Take(5).ToList(),
            };
        }
        return census;
    }

    static JsonObject Counts(List<KeyValuePair<string, long>> items)
    {
        var o = new JsonObject();
        foreach (var kv in items) o[kv.Key] = kv.Value;
        return o;
    }

    public JsonObject ToJson(int schemaVersion)
    {
        var node = new JsonObject
        {
            ["schema_version"] = schemaVersion,
            ["enabled"] = true,
            ["side"] = Side,
            ["source"] = Source,
            ["t_ms"] = Math.Round(TMs, 1),
            ["ts_local"] = TsLocal,
            ["duration_ms"] = Math.Round(DurationMs, 3),
            ["total"] = Total,
            ["by_code"] = Counts(ByCode),
            ["by_domain"] = Counts(ByDomain),
        };
        if (Densest == null) node["densest_chunk"] = null;
        else
        {
            node["densest_chunk"] = new JsonObject
            {
                ["cx"] = Densest.Cx, ["cy"] = Densest.Cy, ["cz"] = Densest.Cz,
                ["block_x"] = Densest.BlockX, ["block_z"] = Densest.BlockZ,
                ["count"] = Densest.Count,
                ["top_codes"] = Counts(Densest.TopCodes),
            };
        }
        return node;
    }

    static List<KeyValuePair<string, long>> ReadCounts(JsonNode? node)
    {
        var list = new List<KeyValuePair<string, long>>();
        if (node is JsonObject o)
            foreach (var kv in o) list.Add(new KeyValuePair<string, long>(kv.Key, kv.Value?.GetValue<long>() ?? 0));
        return list;
    }

    /// <summary>Reads a census file back. Null when the node is absent or is a disabled placeholder.</summary>
    public static EntityCensus? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || o["enabled"]?.GetValue<bool>() != true) return null;
        var c = new EntityCensus
        {
            Side = o["side"]?.GetValue<string>() ?? "",
            Source = o["source"]?.GetValue<string>() ?? "",
            TMs = o["t_ms"]?.GetValue<double>() ?? 0,
            TsLocal = o["ts_local"]?.GetValue<string>() ?? "",
            DurationMs = o["duration_ms"]?.GetValue<double>() ?? 0,
            Total = o["total"]?.GetValue<long>() ?? 0,
            ByCode = ReadCounts(o["by_code"]),
            ByDomain = ReadCounts(o["by_domain"]),
        };
        if (o["densest_chunk"] is JsonObject d)
            c.Densest = new DenseChunk
            {
                Cx = d["cx"]?.GetValue<int>() ?? 0, Cy = d["cy"]?.GetValue<int>() ?? 0, Cz = d["cz"]?.GetValue<int>() ?? 0,
                BlockX = d["block_x"]?.GetValue<int>() ?? 0, BlockZ = d["block_z"]?.GetValue<int>() ?? 0,
                Count = d["count"]?.GetValue<long>() ?? 0,
                TopCodes = ReadCounts(d["top_codes"]),
            };
        return c;
    }

    public static JsonObject Disabled(int schemaVersion, string reason) =>
        new JsonObject { ["schema_version"] = schemaVersion, ["enabled"] = false, ["reason"] = reason };

    public static CensusDiff Diff(EntityCensus start, EntityCensus end) => CensusDiff.Between(start, end);
}

public sealed class DeltaItem
{
    public string Key = "";
    public long Start, End;
    public long Delta => End - Start;
}

/// <summary>Difference between two censuses, changed keys only, largest absolute change first.</summary>
public sealed class CensusDiff
{
    public long StartTotal, EndTotal;
    public long TotalDelta => EndTotal - StartTotal;
    public string StartSide = "", EndSide = "";
    public List<DeltaItem> ByCode = new();
    public List<DeltaItem> ByDomain = new();
    public List<string> Appeared = new();
    public List<string> Vanished = new();

    static List<DeltaItem> Deltas(List<KeyValuePair<string, long>> a, List<KeyValuePair<string, long>> b)
    {
        var left = a.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var right = b.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return left.Keys.Union(right.Keys, StringComparer.Ordinal)
            .Select(k => new DeltaItem { Key = k, Start = left.GetValueOrDefault(k), End = right.GetValueOrDefault(k) })
            .Where(d => d.Delta != 0)
            .OrderByDescending(d => Math.Abs(d.Delta))
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();
    }

    public static CensusDiff Between(EntityCensus start, EntityCensus end)
    {
        var diff = new CensusDiff
        {
            StartTotal = start.Total, EndTotal = end.Total, StartSide = start.Side, EndSide = end.Side,
            ByCode = Deltas(start.ByCode, end.ByCode),
            ByDomain = Deltas(start.ByDomain, end.ByDomain),
        };
        diff.Appeared = diff.ByCode.Where(d => d.Start == 0 && d.End > 0).Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        diff.Vanished = diff.ByCode.Where(d => d.Start > 0 && d.End == 0).Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return diff;
    }

    static JsonArray Items(List<DeltaItem> items, int top) =>
        new JsonArray(items.Take(top).Select(d => (JsonNode?)new JsonObject { ["key"] = d.Key, ["start"] = d.Start, ["end"] = d.End, ["delta"] = d.Delta }).ToArray());

    public JsonObject ToJson(int top = 10) => new()
    {
        ["start_total"] = StartTotal,
        ["end_total"] = EndTotal,
        ["total_delta"] = TotalDelta,
        ["start_side"] = StartSide,
        ["end_side"] = EndSide,
        ["top_code_changes"] = Items(ByCode, top),
        ["top_domain_changes"] = Items(ByDomain, top),
        ["codes_changed"] = ByCode.Count,
        ["appeared_codes"] = new JsonArray(Appeared.Take(top).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        ["vanished_codes"] = new JsonArray(Vanished.Take(top).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
    };

    static void Table(StringBuilder sb, string title, List<DeltaItem> items, int top)
    {
        sb.AppendLine($"| {title} | Start | End | Delta |").AppendLine("|---|---:|---:|---:|");
        foreach (var d in items.Take(top))
            sb.AppendLine($"| {d.Key.Replace("|", "\\|")} | {d.Start} | {d.End} | {(d.Delta > 0 ? "+" : "")}{d.Delta} |");
        if (items.Count == 0) sb.AppendLine("| (no change) | | | |");
        sb.AppendLine();
    }

    public string Markdown(int top = 10)
    {
        var sb = new StringBuilder();
        string sign = TotalDelta > 0 ? "+" : "";
        sb.AppendLine($"Loaded entities: {StartTotal.ToString("N0", CultureInfo.InvariantCulture)} at start, {EndTotal.ToString("N0", CultureInfo.InvariantCulture)} at end ({sign}{TotalDelta.ToString("N0", CultureInfo.InvariantCulture)}). Source side: {StartSide}" + (StartSide != EndSide ? " then " + EndSide : "") + ".").AppendLine();
        Table(sb, "Code", ByCode, top);
        Table(sb, "Domain", ByDomain, top);
        sb.AppendLine("- Appeared (absent at start): " + (Appeared.Count == 0 ? "none" : string.Join(", ", Appeared.Take(top)) + (Appeared.Count > top ? $" and {Appeared.Count - top} more" : "")));
        sb.AppendLine("- Vanished (absent at end): " + (Vanished.Count == 0 ? "none" : string.Join(", ", Vanished.Take(top)) + (Vanished.Count > top ? $" and {Vanished.Count - top} more" : "")));
        sb.AppendLine();
        return sb.ToString();
    }
}
