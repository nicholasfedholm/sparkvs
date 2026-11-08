using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace SparkVS.Core;

/// <summary>One detected teleport (a position jump between two samples, or a dimension change).</summary>
public sealed class TeleportInfo
{
    public string Player = "";
    public double FromX, FromY, FromZ, ToX, ToY, ToZ, Distance;
    public int FromDimension, ToDimension;
}

/// <summary>Remembers the last sampled position per player and reports jumps over the threshold. Not thread safe: one sampler thread owns it.</summary>
public sealed class TeleportDetector
{
    /// <summary>A jump of more than this many blocks between two 1 Hz samples counts as a teleport.</summary>
    public const double ThresholdBlocks = 64;

    readonly Dictionary<string, (double X, double Y, double Z, int Dim)> last = new(StringComparer.Ordinal);
    readonly double threshold;

    public TeleportDetector(double thresholdBlocks = ThresholdBlocks) { threshold = thresholdBlocks; }

    /// <summary>First sighting of a player never reports. Exactly the threshold is not a jump; a dimension change always is.</summary>
    public TeleportInfo? Observe(string player, double x, double y, double z, int dimension)
    {
        TeleportInfo? hit = null;
        if (last.TryGetValue(player, out var prev))
        {
            double dx = x - prev.X, dy = y - prev.Y, dz = z - prev.Z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist > threshold || dimension != prev.Dim)
                hit = new TeleportInfo { Player = player, FromX = prev.X, FromY = prev.Y, FromZ = prev.Z, ToX = x, ToY = y, ToZ = z, Distance = dist, FromDimension = prev.Dim, ToDimension = dimension };
        }
        last[player] = (x, y, z, dimension);
        return hit;
    }

    /// <summary>Forget players that are no longer online, so a rejoin does not read as a teleport.</summary>
    public void Retain(ICollection<string> online)
    {
        foreach (var name in last.Keys.Where(k => !online.Contains(k)).ToList()) last.Remove(name);
    }
}

/// <summary>Reports gen1 and gen2 GC count changes between samples. The first sample only sets the baseline.</summary>
public sealed class GcWatcher
{
    int gen1, gen2;
    bool primed;

    public bool Observe(int gen1Count, int gen2Count, out int gen1Delta, out int gen2Delta)
    {
        gen1Delta = primed ? gen1Count - gen1 : 0;
        gen2Delta = primed ? gen2Count - gen2 : 0;
        gen1 = gen1Count;
        gen2 = gen2Count;
        primed = true;
        return gen2Delta > 0;
    }
}

/// <summary>
/// events.jsonl: what happened during a capture besides the numeric series. Chunk event counters are bumped from game threads
/// (Interlocked only, no locks, no allocation) and flushed once per second into one row per non-empty second.
/// Every handler call is counted and timed so the capture can report its own cost.
/// </summary>
public sealed class EventLog
{
    readonly BundleClock clock;
    readonly object gate = new();
    readonly List<(double TMs, long Seq, string Line)> rows = new();
    long seq;
    long loaded, unloaded, clientReceived;
    long handlerCalls, handlerTicks;

    public EventLog(BundleClock clock) { this.clock = clock; }

    public BundleClock Clock => clock;

    // Counter handlers. The timed span covers the counter work only; the delegate dispatch itself is the game's.
    public void ChunkLoaded() => Bump(ref loaded);
    public void ChunkUnloaded() => Bump(ref unloaded);
    public void ClientChunkReceived() => Bump(ref clientReceived);

    /// <summary>A handler call that matched nothing (for example a client chunk marked dirty for a block edit). Still counted and timed.</summary>
    public void Ignored()
    {
        long t0 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref handlerCalls);
        Interlocked.Add(ref handlerTicks, Stopwatch.GetTimestamp() - t0);
    }

    void Bump(ref long counter)
    {
        long t0 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref counter);
        Interlocked.Increment(ref handlerCalls);
        Interlocked.Add(ref handlerTicks, Stopwatch.GetTimestamp() - t0);
    }

    public long HandlerCalls => Interlocked.Read(ref handlerCalls);

    /// <summary>Cumulative time spent in the counter handlers, ns.</summary>
    public long HandlerNs => (long)(Interlocked.Read(ref handlerTicks) * 1e9 / Stopwatch.Frequency);

    public int RowCount { get { lock (gate) return rows.Count; } }

    /// <summary>Totals so far, not yet flushed counters included.</summary>
    public (long Loaded, long Unloaded, long ClientReceived) PendingCounts =>
        (Interlocked.Read(ref loaded), Interlocked.Read(ref unloaded), Interlocked.Read(ref clientReceived));

    /// <summary>Closes the current one second bucket. Writes a row only when something was counted. Called by the 1 Hz sampler and once at stop.</summary>
    public bool Flush(double tMs)
    {
        long l = Interlocked.Exchange(ref loaded, 0), u = Interlocked.Exchange(ref unloaded, 0), c = Interlocked.Exchange(ref clientReceived, 0);
        if (l == 0 && u == 0 && c == 0) return false;
        Add(tMs, "chunks", "game", ("loaded", l), ("unloaded", u), ("client_received", c));
        return true;
    }

    public void Teleport(double tMs, TeleportInfo t) =>
        Add(tMs, "teleport", "game",
            ("player", t.Player),
            ("from", new JsonArray(Math.Round(t.FromX, 1), Math.Round(t.FromY, 1), Math.Round(t.FromZ, 1))),
            ("to", new JsonArray(Math.Round(t.ToX, 1), Math.Round(t.ToY, 1), Math.Round(t.ToZ, 1))),
            ("distance", Math.Round(t.Distance, 1)),
            ("from_dimension", t.FromDimension), ("to_dimension", t.ToDimension));

    public void Gc(double tMs, int gen2Delta, int gen1Delta) =>
        Add(tMs, "gc", "runtime", ("gen2_count_delta", gen2Delta), ("gen1_count_delta", gen1Delta));

    /// <summary>A row the tool itself caused (capture start/stop, census, heap snapshot).</summary>
    public void Tool(string type, params (string Key, object? Value)[] extra) =>
        Add(clock.MsSince(Stopwatch.GetTimestamp()), type, "tool", extra);

    public void ToolAt(double tMs, string type, params (string Key, object? Value)[] extra) => Add(tMs, type, "tool", extra);

    void Add(double tMs, string type, string source, params (string Key, object? Value)[] extra)
    {
        var o = new JsonObject
        {
            ["t_ms"] = Math.Round(tMs, 1),
            ["ts_local"] = clock.IsoAt(tMs),
            ["type"] = type,
            ["source"] = source,
        };
        foreach (var (key, value) in extra)
            o[key] = value switch { null => null, JsonNode n => n, string s => JsonValue.Create(s), int i => JsonValue.Create(i), long l => JsonValue.Create(l), double d => JsonValue.Create(d), bool b => JsonValue.Create(b), _ => JsonValue.Create(value.ToString()) };
        string line = o.ToJsonString();
        lock (gate) rows.Add((tMs, seq++, line));
    }

    /// <summary>Row lines in time order (ties keep insertion order).</summary>
    public List<string> Lines()
    {
        lock (gate) return rows.OrderBy(r => r.TMs).ThenBy(r => r.Seq).Select(r => r.Line).ToList();
    }

    public int CountOfType(string type)
    {
        string needle = "\"type\":\"" + type + "\"";
        lock (gate) return rows.Count(r => r.Line.Contains(needle, StringComparison.Ordinal));
    }

    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.Append(BundleWriter.Header("events", clock,
            "Discrete events during the capture. source game = something the game did, runtime = the .NET runtime, tool = SparkVS itself (a row here marks a moment the capture changed the thing it measures).",
            new JsonObject
            {
                ["type chunks"] = "one row per second with at least one chunk event: loaded/unloaded (server ChunkColumnLoaded/Unloaded) and client_received (client ChunkDirty NewlyLoaded) counts for the second ending at t_ms",
                ["type teleport"] = "an online player moved more than " + TeleportDetector.ThresholdBlocks + " blocks between two 1 Hz samples, or changed dimension",
                ["type gc"] = "a gen2 GC happened since the previous sample; deltas are of GC.CollectionCount",
                ["source tool"] = "capture_start, capture_stop, census_start, census_end, heap_snapshot_begin, heap_snapshot_end",
            })).Append('\n');
        foreach (var line in Lines()) sb.Append(line).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Summary of the written rows, for summary.md. Reads the file back so the report step needs no live state.</summary>
    public static EventsSummary Summarize(string path)
    {
        var s = new EventsSummary();
        if (!File.Exists(path)) return s;
        foreach (var line in File.ReadLines(path))
        {
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; } catch { continue; }
            string? type = o?["type"]?.GetValue<string>();
            if (o == null || type == null || type == "header") continue;
            s.Present = true;
            s.ByType[type] = s.ByType.GetValueOrDefault(type) + 1;
            if (type == "chunks")
            {
                s.Loaded += o["loaded"]?.GetValue<long>() ?? 0;
                s.Unloaded += o["unloaded"]?.GetValue<long>() ?? 0;
                s.ClientReceived += o["client_received"]?.GetValue<long>() ?? 0;
                long burst = (o["loaded"]?.GetValue<long>() ?? 0) + (o["client_received"]?.GetValue<long>() ?? 0);
                if (burst > s.PeakLoadedInSecond) { s.PeakLoadedInSecond = burst; s.PeakAtMs = o["t_ms"]?.GetValue<double>() ?? 0; }
            }
            else if (type == "teleport") s.Teleports.Add($"{o["player"]} {o["distance"]} blocks at t={o["t_ms"]} ms");
            else if (type == "gc") s.Gen2Collections += o["gen2_count_delta"]?.GetValue<long>() ?? 0;
            else if (o["source"]?.GetValue<string>() == "tool") s.ToolRows.Add($"{type} at t={o["t_ms"]} ms");
        }
        return s;
    }
}

public sealed class EventsSummary
{
    public bool Present;
    public Dictionary<string, int> ByType = new();
    public long Loaded, Unloaded, ClientReceived, PeakLoadedInSecond, Gen2Collections;
    public double PeakAtMs;
    public List<string> Teleports = new();
    public List<string> ToolRows = new();

    public string Markdown()
    {
        var sb = new StringBuilder();
        if (!Present) return "- Not collected: events.jsonl absent or empty\n\n";
        sb.AppendLine($"- Chunk events: {Loaded} server chunk columns loaded, {Unloaded} unloaded, {ClientReceived} client chunks received. Busiest second: {PeakLoadedInSecond} loads ending at t={PeakAtMs:F0} ms.");
        sb.AppendLine($"- Gen2 GCs seen by the 1 Hz sampler: {Gen2Collections} (in {ByType.GetValueOrDefault("gc")} samples). Teleports: {Teleports.Count}" + (Teleports.Count > 0 ? " (" + string.Join("; ", Teleports.Take(5)) + ")" : "") + ".");
        sb.AppendLine("- Tool-induced moments (SparkVS changed the thing it measures here): " + (ToolRows.Count == 0 ? "none" : string.Join("; ", ToolRows)) + ".");
        sb.AppendLine("- Series: events.jsonl.");
        sb.AppendLine();
        return sb.ToString();
    }
}
