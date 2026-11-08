using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>
/// Pure accumulation and table code for per entity code server tick timing (item 7). The Harmony wrapper in the mod times each
/// Entity.OnGameTick call and calls Add with the elapsed QPC ticks; keys are reference identity objects (EntityProperties), converted
/// to code strings only when the table is built. Not thread safe on purpose: only the owner thread adds (see AcceptsThread).
/// </summary>
public sealed class EntityTickAccumulator
{
    public sealed class Slot { public long Calls, TotalTicks, MaxTicks; }

    readonly Dictionary<object, Slot> slots = new(ReferenceEqualityComparer.Instance);
    static readonly object NullKey = new();
    int ownerThread;
    volatile bool frozen;

    public long CallsTotal { get; private set; }
    public long TicksTotal { get; private set; }
    public long DroppedOffThread, DroppedFrozen;
    public int KeyCount => slots.Count;

    /// <summary>First thread to ask becomes the owner; later calls from other threads are refused. Returns true for the owner.</summary>
    public bool AcceptsThread(int managedThreadId)
    {
        int cur = ownerThread;
        if (cur == 0) cur = System.Threading.Interlocked.CompareExchange(ref ownerThread, managedThreadId, 0) is 0 ? managedThreadId : ownerThread;
        if (cur == managedThreadId) return true;
        DroppedOffThread++;
        return false;
    }

    public void Freeze() => frozen = true;
    public bool Frozen => frozen;

    public void Add(object? key, long ticks)
    {
        if (frozen) { DroppedFrozen++; return; }
        key ??= NullKey;
        if (!slots.TryGetValue(key, out var s)) slots[key] = s = new Slot();
        s.Calls++;
        s.TotalTicks += ticks;
        if (ticks > s.MaxTicks) s.MaxTicks = ticks;
        CallsTotal++;
        TicksTotal += ticks;
    }

    /// <summary>Copy of the slots; retries once if an in-flight add changes the dictionary during the copy.</summary>
    public List<KeyValuePair<object, Slot>> Snapshot()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return slots.Select(kv => new KeyValuePair<object, Slot>(kv.Key, new Slot { Calls = kv.Value.Calls, TotalTicks = kv.Value.TotalTicks, MaxTicks = kv.Value.MaxTicks })).ToList(); }
            catch (InvalidOperationException) when (attempt < 2) { }
        }
    }

    static readonly object CalibrationKey = new();
    static long calibrationSink;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void NoOp() => calibrationSink++;

    /// <summary>Times the wrapper's own timing code (two Stopwatch reads, try/finally, dictionary add, a no-op call) over n calls. Returns ns per call.</summary>
    public static double Calibrate(int n = 1_000_000)
    {
        var scratch = new EntityTickAccumulator();
        scratch.AcceptsThread(Environment.CurrentManagedThreadId);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            try { NoOp(); }
            finally { if (scratch.AcceptsThread(Environment.CurrentManagedThreadId)) scratch.Add(CalibrationKey, Stopwatch.GetTimestamp() - t0); }
        }
        long end = Stopwatch.GetTimestamp();
        return (end - start) * 1e9 / Stopwatch.Frequency / Math.Max(1, n);
    }
}

public static class EntityTickReport
{
    public const string FileName = "entity-ticks.json";
    public const string Note = "Wall time of Entity.OnGameTick as called from ServerSystemEntitySimulation.TickEntities on the server main thread, per entity code. Includes any nested work the tick does. Threadsafe behaviors that tick on physics worker threads are not included. Rows are keyed by EntityProperties and merged by code. total_ms includes the wrapper's own overhead (wrapper_overhead_estimate_ns per call).";

    public static JsonObject Disabled(string reason) => new() { ["enabled"] = false, ["reason"] = reason };

    /// <summary>Build the file. resolve maps a key (EntityProperties or null) to a code string and domain.</summary>
    public static JsonObject Build(EntityTickAccumulator acc, Func<object, (string Code, string Domain)> resolve, long frequency, double overheadNs, double windowSeconds, string patchTarget, string patchNote)
    {
        var merged = new Dictionary<string, (string Domain, long Calls, long Total, long Max)>(StringComparer.Ordinal);
        foreach (var kv in acc.Snapshot())
        {
            (string code, string domain) = ResolveSafe(resolve, kv.Key);
            merged.TryGetValue(code, out var m);
            merged[code] = (domain, m.Calls + kv.Value.Calls, m.Total + kv.Value.TotalTicks, Math.Max(m.Max, kv.Value.MaxTicks));
        }
        double toMs = 1000.0 / frequency, toUs = 1e6 / frequency;
        long totalTicks = merged.Values.Sum(v => v.Total);
        var rows = new JsonArray();
        foreach (var kv in merged.OrderByDescending(k => k.Value.Total).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            var v = kv.Value;
            rows.Add(new JsonObject
            {
                ["code"] = kv.Key,
                ["domain"] = v.Domain,
                ["calls"] = v.Calls,
                ["total_ms"] = Math.Round(v.Total * toMs, 3),
                ["mean_us"] = v.Calls > 0 ? Math.Round(v.Total * toUs / v.Calls, 3) : 0,
                ["max_us"] = Math.Round(v.Max * toUs, 1),
                ["share_pct"] = totalTicks > 0 ? Math.Round(100.0 * v.Total / totalTicks, 2) : 0,
            });
        }
        return new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["enabled"] = true,
            ["note"] = Note,
            ["patch_target"] = patchTarget,
            ["patch_note"] = patchNote,
            ["window_seconds"] = Math.Round(windowSeconds, 1),
            ["wrapped_calls_total"] = acc.CallsTotal,
            ["wrapped_total_ms"] = Math.Round(acc.TicksTotal * toMs, 3),
            ["wrapper_overhead_estimate_ns"] = Math.Round(overheadNs, 1),
            ["wrapper_overhead_total_ms_estimate"] = Math.Round(overheadNs * acc.CallsTotal / 1e6, 3),
            ["calls_dropped_off_owner_thread"] = acc.DroppedOffThread,
            ["calls_dropped_after_stop"] = acc.DroppedFrozen,
            ["distinct_codes"] = merged.Count,
            ["rows"] = rows,
        };
    }

    static (string, string) ResolveSafe(Func<object, (string Code, string Domain)> resolve, object key)
    {
        try { return resolve(key); }
        catch (Exception) { return ("(unknown)", ""); }
    }

    /// <summary>"Entity tick time" section for summary.md: top rows by total time.</summary>
    public static string Markdown(JsonNode? file, int top = 15)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Entity tick time [measured]").AppendLine();
        if (file?["enabled"]?.GetValue<bool>() != true)
            return sb.AppendLine("- Not collected: " + (file?["reason"]?.GetValue<string>() ?? "entity-ticks.json absent")).AppendLine().ToString();
        sb.AppendLine($"Wrapped {file["wrapped_calls_total"]} Entity.OnGameTick calls ({file["wrapped_total_ms"]} ms) over {file["window_seconds"]} s; wrapper cost about {file["wrapper_overhead_estimate_ns"]} ns per call. Threadsafe behaviors on physics threads are not included. File: {FileName}.").AppendLine();
        sb.AppendLine("| Code | Calls | Total ms | Mean us | Max us | Share % |").AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var r in (file["rows"] as JsonArray ?? new JsonArray()).Take(top))
            sb.AppendLine($"| {r!["code"]} | {r["calls"]} | {r["total_ms"]} | {r["mean_us"]} | {r["max_us"]} | {r["share_pct"]} |");
        return sb.AppendLine().ToString();
    }

    public static string MarkdownFromDir(string dir)
    {
        try
        {
            string p = Path.Combine(dir, FileName);
            return Markdown(File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null);
        }
        catch (Exception e) { return Markdown(Disabled("could not read " + FileName + ": " + e.Message)); }
    }
}
