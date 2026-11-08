using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SparkVS.Core;

public enum OwnerKind { Mod, Shared, Unknown }

public sealed record Owner(OwnerKind Kind, string Id, string Name);

/// <summary>Maps a module (path or simple name) to its owner. Front ends supply the implementation.</summary>
public interface IModResolver
{
    Owner Resolve(string module);
    /// <summary>How the mod mapping was built (goes in the report header).</summary>
    string Describe { get; }
}

public sealed class MemoryTypeDetail
{
    public string TypeName = "";
    public string Module = "";
    public long Count;
    public long Bytes;
    /// <summary>"" or "type-arg" when the owner came from an array element or generic argument.</summary>
    public string Via = "";
    /// <summary>Assembly bucket: game:Name, framework:Name, mod:Assembly, shared:Assembly, unknown:Assembly or unknown:(no module).</summary>
    public string Bucket = "";
    /// <summary>For unknown types only: "empty module", "unresolved type name" or "unmapped module".</summary>
    public string Reason = "";
}

/// <summary>Live bytes of one assembly bucket (by-assembly table).</summary>
public sealed class AssemblyRow
{
    public string Bucket = "";
    public OwnerKind Kind;
    public string OwnerId = "";
    public long Bytes;
    public long Count;
}

/// <summary>Why bytes landed in the unknown bucket.</summary>
public sealed class UnknownReason
{
    public string Reason = "";
    public long Bytes;
    public long Count;
}

public sealed class MemoryRow
{
    public int Rank;
    public string Id = "";
    public string Name = "";
    public OwnerKind Kind;
    public long LiveBytes;
    public long LiveObjects;
    public double PctOfLiveHeap;
    public long? GrossAllocBytes;
    public string TopTypes = "";
    public List<MemoryTypeDetail> Types = new();
}

public sealed class MemoryTable
{
    public HeapSnapshot Snapshot = new();
    public string Describe = "";
    public string GrossNote = "";
    public long TotalLiveBytes;
    public long TotalLiveObjects;
    public List<MemoryRow> Rows = new();
    /// <summary>Bytes per assembly bucket, largest first (every type in the snapshot is in exactly one entry).</summary>
    public List<AssemblyRow> ByAssembly = new();
    /// <summary>Unknown bucket split by reason, largest first.</summary>
    public List<UnknownReason> UnknownReasons = new();
    /// <summary>Unmapped modules inside the unknown bucket (simple names), largest first.</summary>
    public List<AssemblyRow> UnknownModules = new();
}

public static class MemoryReport
{
    public const string SharedId = "system/shared";
    public const string UnknownId = "unknown";

    public static readonly string[] HonestLimits =
    {
        "Live managed heap only, attributed by object type to owning assembly to owning mod. Game, VintagestoryAPI, System.*, Microsoft.*, and shared Harmony go to system/shared. Unmapped goes to unknown. Nothing is guessed.",
        "Content-only mods (JSON, no DLL) hold their data inside game types, so their footprint lands in system/shared.",
        "Native and unmanaged memory, including GPU / VRAM, is not per-mod separable and is not in this table (see the capture's gpu-memory output).",
        "Gross allocations are not live RAM. The two columns are separate and never merged.",
    };

    public const string CsvHeader = "rank,owner_kind,owner_id,owner_name,live_bytes,live_mb,live_objects,pct_of_live_heap,gross_alloc_bytes,top_types";

    static readonly Owner SharedOwner = new(OwnerKind.Shared, SharedId, SharedId);
    static readonly Owner UnknownOwner = new(OwnerKind.Unknown, UnknownId, UnknownId);

    /// <summary>Owner of one type: own module Mod, else first type-arg Mod, else Shared, else Unknown. Never guesses.</summary>
    public static Owner OwnerOf(HeapTypeStat t, IModResolver r, out string via)
        => Classify(t, r, out via, out _, out _);

    /// <summary>
    /// Owner plus the assembly bucket and, for unknown, the reason. A module the resolver calls Unknown but that
    /// AssemblyBuckets knows as a game or framework assembly is Shared (bucket game:Name or framework:Name), so a bare
    /// module name such as VintagestoryAPI never lands in unknown.
    /// </summary>
    public static Owner Classify(HeapTypeStat t, IModResolver r, out string via, out string bucket, out string reason)
    {
        via = "";
        reason = "";
        bool noModule = string.IsNullOrEmpty(t.Module);
        string simple = AssemblyBuckets.SimpleName(t.Module);
        Owner own = noModule ? UnknownOwner : r.Resolve(t.Module);
        var ab = noModule ? default : AssemblyBuckets.Classify(t.Module);
        if (own.Kind == OwnerKind.Unknown && ab.Kind != AssemblyKind.None) own = SharedOwner;
        if (own.Kind == OwnerKind.Mod)
        {
            bucket = "mod:" + simple;
            return own;
        }
        foreach (var am in t.ArgModules)
        {
            if (string.IsNullOrEmpty(am)) continue;
            var a = r.Resolve(am);
            if (a.Kind == OwnerKind.Mod)
            {
                via = "type-arg";
                bucket = "mod-type-arg:" + a.Id;
                return a;
            }
        }
        if (own.Kind == OwnerKind.Shared)
        {
            if (own.Name.StartsWith("game:", StringComparison.Ordinal) || own.Name.StartsWith("framework:", StringComparison.Ordinal)) bucket = own.Name;
            else if (ab.Kind != AssemblyKind.None) bucket = ab.Bucket;
            else bucket = "shared:" + (simple.Length > 0 ? simple : "(unnamed)");
            return SharedOwner;
        }
        bucket = "unknown:" + (noModule ? "(no module)" : simple);
        if (t.TypeName.StartsWith("<type 0x", StringComparison.Ordinal)) reason = "unresolved type name";
        else if (noModule) reason = "empty module";
        else reason = "unmapped module";
        return UnknownOwner;
    }

    public static MemoryTable Build(HeapSnapshot snap, IModResolver r,
        IReadOnlyDictionary<string, long>? grossBytesByOwnerId, string grossNote)
    {
        var table = new MemoryTable { Snapshot = snap, Describe = r.Describe, GrossNote = grossNote ?? "" };
        var rows = new Dictionary<string, MemoryRow>(StringComparer.Ordinal);

        MemoryRow RowFor(Owner o)
        {
            string key = o.Kind.ToString() + "|" + o.Id;
            if (!rows.TryGetValue(key, out var row))
            {
                row = new MemoryRow { Id = o.Id, Name = o.Name, Kind = o.Kind };
                rows[key] = row;
            }
            return row;
        }

        RowFor(SharedOwner);
        RowFor(UnknownOwner);

        var byAsm = new Dictionary<string, AssemblyRow>(StringComparer.Ordinal);
        var unkReasons = new Dictionary<string, UnknownReason>(StringComparer.Ordinal);
        var unkModules = new Dictionary<string, AssemblyRow>(StringComparer.Ordinal);
        foreach (var t in snap.Types)
        {
            var o = Classify(t, r, out string via, out string bucket, out string reason);
            var row = RowFor(o);
            row.LiveBytes += t.Bytes;
            row.LiveObjects += t.Count;
            row.Types.Add(new MemoryTypeDetail { TypeName = t.TypeName, Module = t.Module, Count = t.Count, Bytes = t.Bytes, Via = via, Bucket = bucket, Reason = reason });
            table.TotalLiveBytes += t.Bytes;
            table.TotalLiveObjects += t.Count;

            string akey = bucket + "|" + o.Kind + "|" + o.Id;
            if (!byAsm.TryGetValue(akey, out var ar)) byAsm[akey] = ar = new AssemblyRow { Bucket = bucket, Kind = o.Kind, OwnerId = o.Id };
            ar.Bytes += t.Bytes;
            ar.Count += t.Count;
            if (o.Kind == OwnerKind.Unknown)
            {
                if (!unkReasons.TryGetValue(reason, out var ur)) unkReasons[reason] = ur = new UnknownReason { Reason = reason };
                ur.Bytes += t.Bytes;
                ur.Count += t.Count;
                if (reason == "unmapped module")
                {
                    if (!unkModules.TryGetValue(bucket, out var um)) unkModules[bucket] = um = new AssemblyRow { Bucket = bucket, Kind = OwnerKind.Unknown, OwnerId = UnknownId };
                    um.Bytes += t.Bytes;
                    um.Count += t.Count;
                }
            }
        }
        table.ByAssembly = byAsm.Values.OrderByDescending(x => x.Bytes).ThenBy(x => x.Bucket, StringComparer.Ordinal).ToList();
        table.UnknownReasons = unkReasons.Values.OrderByDescending(x => x.Bytes).ThenBy(x => x.Reason, StringComparer.Ordinal).ToList();
        table.UnknownModules = unkModules.Values.OrderByDescending(x => x.Bytes).ThenBy(x => x.Bucket, StringComparer.Ordinal).ToList();

        foreach (var row in rows.Values)
        {
            row.Types.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            row.PctOfLiveHeap = table.TotalLiveBytes > 0 ? 100.0 * row.LiveBytes / table.TotalLiveBytes : 0;
            var sb = new StringBuilder();
            int n = 0;
            foreach (var d in row.Types)
            {
                if (n >= 5) break;
                if (sb.Length > 0) sb.Append("; ");
                string label = Trunc(d.TypeName, 80);
                if (row.Kind == OwnerKind.Unknown)
                {
                    string asm = SimpleModuleName(d.Module);
                    if (asm.Length > 0) label += " [" + asm + "]";
                }
                sb.Append(label).Append('=').Append(Mb(d.Bytes).ToString("0.00", CultureInfo.InvariantCulture));
                n++;
            }
            row.TopTypes = sb.ToString();
            if (grossBytesByOwnerId != null && grossBytesByOwnerId.TryGetValue(row.Id, out long g))
                row.GrossAllocBytes = g;
        }

        var ordered = rows.Values.OrderByDescending(x => x.LiveBytes).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Rank = i + 1;
        table.Rows = ordered;
        return table;
    }

    public static void Write(string dir, MemoryTable t, int topN, string frontEnd)
    {
        Directory.CreateDirectory(dir);
        var enc = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(dir, "memory.md"), RenderMarkdown(t, topN, frontEnd), enc);
        File.WriteAllText(Path.Combine(dir, "memory.csv"), RenderCsv(t), enc);
        File.WriteAllText(Path.Combine(dir, "memory.json"), RenderJson(t, topN, frontEnd), enc);
    }

    public static void WriteCsvOnly(string dir, MemoryTable t)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "memory.csv"), RenderCsv(t), new UTF8Encoding(false));
    }

    /// <summary>Rows shown in the markdown: topN mod rows plus the shared and unknown buckets always.</summary>
    public static List<MemoryRow> ShownRows(MemoryTable t, int topN)
    {
        if (topN < 1) topN = 1;
        int mods = 0;
        var shown = new List<MemoryRow>();
        foreach (var row in t.Rows)
        {
            if (row.Kind == OwnerKind.Mod)
            {
                if (mods >= topN) continue;
                mods++;
            }
            shown.Add(row);
        }
        return shown;
    }

    public static string RenderMarkdown(MemoryTable t, int topN, string frontEnd)
    {
        var s = t.Snapshot;
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append("# Per-mod memory (live managed heap)\n\n");
        sb.Append("- Front end: ").Append(frontEnd).Append('\n');
        sb.Append("- Route: ").Append(s.Route).Append('\n');
        sb.Append("- Process: pid ").Append(s.Pid.ToString(inv)).Append(' ').Append(s.ProcessName).Append('\n');
        sb.Append("- Taken (UTC): ").Append(s.TakenUtc.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\n');
        sb.Append("- Mod mapping: ").Append(t.Describe).Append('\n');
        sb.Append("- Process totals: working set ").Append(Mb(s.WorkingSetBytes).ToString("0.0", inv))
            .Append(" MB, private bytes ").Append(Mb(s.PrivateBytes).ToString("0.0", inv))
            .Append(" MB, GC heap after GC ")
            .Append(s.GcHeapSizeAfterBytes >= 0 ? Mb(s.GcHeapSizeAfterBytes).ToString("0.0", inv) + " MB" : "unknown").Append('\n');
        sb.Append("- Live objects: ").Append(t.TotalLiveObjects.ToString("N0", inv))
            .Append(", live bytes (summed over types): ").Append(Mb(t.TotalLiveBytes).ToString("0.0", inv)).Append(" MB\n");
        sb.Append("- Dump GC pause: ").Append(s.GcPauseMs.ToString("0.0", inv)).Append(" ms (snapshot took ")
            .Append(s.ElapsedMs.ToString("0", inv)).Append(" ms)\n");
        sb.Append("- Events lost: ").Append(s.EventsLost.ToString(inv)).Append(", complete: ").Append(s.Complete ? "yes" : "NO").Append('\n');
        sb.Append("- Snapshot stamp: complete=").Append(s.Complete ? "true" : "false").Append(" events_lost=").Append(s.EventsLost.ToString(inv))
            .Append(" attempts=").Append(s.Attempts.ToString(inv)).Append(" start_condition=").Append(s.StartCondition.Length > 0 ? s.StartCondition : "n/a").Append('\n');
        foreach (var w in s.Warnings) sb.Append("- WARNING: ").Append(w).Append('\n');
        if (!s.Complete)
            sb.Append("\n> PARTIAL SNAPSHOT (lower bound): this snapshot is not complete (events lost ").Append(s.EventsLost.ToString(inv))
                .Append(", attempts ").Append(s.Attempts.ToString(inv)).Append("). Every byte and object number below is a lower bound.\n");
        if (s.AttemptLog.Count > 0)
        {
            sb.Append("\n## Attempts\n\n| attempt | buffer MB | outcome | events lost | dump GC pause ms | took ms |\n|---:|---:|---|---:|---:|---:|\n");
            foreach (var a in s.AttemptLog)
                sb.Append("| ").Append(a.Attempt.ToString(inv)).Append(" | ").Append(a.BufferMB.ToString(inv)).Append(" | ").Append(a.Outcome)
                    .Append(" | ").Append(a.EventsLost.ToString(inv)).Append(" | ").Append(a.GcPauseMs >= 0 ? a.GcPauseMs.ToString("0.0", inv) : "n/a")
                    .Append(" | ").Append(a.ElapsedMs.ToString("0", inv)).Append(" |\n");
        }
        sb.Append("\n## Honest limits\n\n");
        for (int i = 0; i < HonestLimits.Length; i++)
            sb.Append((i + 1).ToString(inv)).Append(". ").Append(HonestLimits[i]).Append('\n');

        bool anyGross = t.Rows.Any(x => x.GrossAllocBytes.HasValue);
        string partial = s.Complete ? "" : " (PARTIAL, lower bound)";
        sb.Append("\n## Owners by live bytes\n\n");
        sb.Append("| rank | owner | kind | live MB").Append(partial).Append(" | live objects | % of live heap | gross alloc MB | top types |\n");
        sb.Append("|---:|---|---|---:|---:|---:|---:|---|\n");
        foreach (var row in ShownRows(t, topN))
        {
            sb.Append("| ").Append(row.Rank.ToString(inv))
                .Append(" | ").Append(MdCell(row.Id == row.Name ? row.Id : row.Name + " (" + row.Id + ")"))
                .Append(" | ").Append(row.Kind.ToString().ToLowerInvariant())
                .Append(" | ").Append(Mb(row.LiveBytes).ToString("0.00", inv))
                .Append(" | ").Append(row.LiveObjects.ToString("N0", inv))
                .Append(" | ").Append(row.PctOfLiveHeap.ToString("0.00", inv))
                .Append(" | ").Append(row.GrossAllocBytes.HasValue ? Mb(row.GrossAllocBytes.Value).ToString("0.00", inv) : "n/a")
                .Append(" | ").Append(MdCell(row.TopTypes))
                .Append(" |\n");
        }
        int hidden = t.Rows.Count(x => x.Kind == OwnerKind.Mod) - Math.Min(Math.Max(topN, 1), t.Rows.Count(x => x.Kind == OwnerKind.Mod));
        if (hidden > 0) sb.Append("\n").Append(hidden.ToString(inv)).Append(" more mod rows are in memory.csv and memory.json.\n");
        if (anyGross || t.GrossNote.Length > 0)
            sb.Append("\nGross allocation column: ").Append(t.GrossNote.Length > 0 ? t.GrossNote : "measured separately from live bytes").Append('\n');
        else
            sb.Append("\nGross allocation column: not measured in this run.\n");

        const int AsmShown = 25;
        sb.Append("\n## By assembly (live bytes, top ").Append(Math.Min(AsmShown, t.ByAssembly.Count).ToString(inv)).Append(" of ").Append(t.ByAssembly.Count.ToString(inv))
            .Append("; all in memory.json)\n\n| bucket | owner kind | owner | live MB").Append(partial).Append(" | live objects |\n|---|---|---|---:|---:|\n");
        foreach (var a in t.ByAssembly.Take(AsmShown))
            sb.Append("| ").Append(MdCell(a.Bucket)).Append(" | ").Append(a.Kind.ToString().ToLowerInvariant()).Append(" | ").Append(MdCell(a.OwnerId))
                .Append(" | ").Append(Mb(a.Bytes).ToString("0.00", inv)).Append(" | ").Append(a.Count.ToString("N0", inv)).Append(" |\n");

        var unk = t.Rows.FirstOrDefault(x => x.Kind == OwnerKind.Unknown);
        sb.Append("\n## Unknown bucket\n\n");
        if (unk == null || unk.LiveBytes == 0)
            sb.Append("Nothing landed in unknown.\n");
        else
        {
            sb.Append("Unknown holds ").Append(Mb(unk.LiveBytes).ToString("0.00", inv)).Append(" MB in ").Append(unk.LiveObjects.ToString("N0", inv))
                .Append(" objects").Append(partial).Append(". Why (each type is counted once):\n\n| reason | live MB | live objects |\n|---|---:|---:|\n");
            foreach (var u in t.UnknownReasons)
                sb.Append("| ").Append(u.Reason).Append(" | ").Append(Mb(u.Bytes).ToString("0.00", inv)).Append(" | ").Append(u.Count.ToString("N0", inv)).Append(" |\n");
            if (t.UnknownModules.Count > 0)
            {
                sb.Append("\nUnmapped modules (top 10):\n\n| module | live MB | live objects |\n|---|---:|---:|\n");
                foreach (var m in t.UnknownModules.Take(10))
                    sb.Append("| ").Append(MdCell(m.Bucket.Substring("unknown:".Length))).Append(" | ").Append(Mb(m.Bytes).ToString("0.00", inv)).Append(" | ").Append(m.Count.ToString("N0", inv)).Append(" |\n");
            }
            sb.Append("\nTop 10 types in unknown:\n\n| type | module | live MB | live objects | reason |\n|---|---|---:|---:|---|\n");
            foreach (var d in unk.Types.Take(10))
                sb.Append("| ").Append(MdCell(Trunc(d.TypeName, 90))).Append(" | ").Append(MdCell(d.Module.Length > 0 ? SimpleModuleName(d.Module) : "(none)"))
                    .Append(" | ").Append(Mb(d.Bytes).ToString("0.00", inv)).Append(" | ").Append(d.Count.ToString("N0", inv)).Append(" | ").Append(d.Reason).Append(" |\n");
        }
        return sb.ToString();
    }

    public static string RenderCsv(MemoryTable t)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');
        foreach (var row in t.Rows)
        {
            sb.Append(row.Rank.ToString(inv)).Append(',')
                .Append(row.Kind.ToString().ToLowerInvariant()).Append(',')
                .Append(Csv(row.Id)).Append(',')
                .Append(Csv(row.Name)).Append(',')
                .Append(row.LiveBytes.ToString(inv)).Append(',')
                .Append(Mb(row.LiveBytes).ToString("0.00", inv)).Append(',')
                .Append(row.LiveObjects.ToString(inv)).Append(',')
                .Append(row.PctOfLiveHeap.ToString("0.00", inv)).Append(',')
                .Append(row.GrossAllocBytes.HasValue ? row.GrossAllocBytes.Value.ToString(inv) : "").Append(',')
                .Append(Csv(row.TopTypes)).Append('\n');
        }
        return sb.ToString();
    }

    public static string RenderJson(MemoryTable t, int topN, string frontEnd)
    {
        var s = t.Snapshot;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("front_end", frontEnd);
            w.WriteString("route", s.Route);
            w.WriteNumber("pid", s.Pid);
            w.WriteString("process", s.ProcessName);
            w.WriteString("taken_utc", s.TakenUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("mod_mapping", t.Describe);
            w.WriteNumber("working_set_bytes", s.WorkingSetBytes);
            w.WriteNumber("private_bytes", s.PrivateBytes);
            w.WriteNumber("gc_heap_after_bytes", s.GcHeapSizeAfterBytes);
            w.WriteNumber("live_objects", t.TotalLiveObjects);
            w.WriteNumber("live_bytes", t.TotalLiveBytes);
            w.WriteNumber("gc_pause_ms", Math.Round(s.GcPauseMs, 1));
            w.WriteNumber("elapsed_ms", Math.Round(s.ElapsedMs, 0));
            w.WriteNumber("events_lost", s.EventsLost);
            w.WriteBoolean("complete", s.Complete);
            w.WriteBoolean("partial", !s.Complete);
            w.WriteNumber("attempts", s.Attempts);
            w.WriteString("start_condition", s.StartCondition);
            w.WriteStartArray("attempt_log");
            foreach (var a in s.AttemptLog)
            {
                w.WriteStartObject();
                w.WriteNumber("attempt", a.Attempt);
                w.WriteNumber("buffer_mb", a.BufferMB);
                w.WriteString("outcome", a.Outcome);
                w.WriteNumber("events_lost", a.EventsLost);
                w.WriteBoolean("complete", a.Complete);
                if (a.GcPauseMs >= 0) w.WriteNumber("gc_pause_ms", Math.Round(a.GcPauseMs, 1)); else w.WriteNull("gc_pause_ms");
                w.WriteNumber("elapsed_ms", Math.Round(a.ElapsedMs, 0));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("warnings");
            foreach (var x in s.Warnings) w.WriteStringValue(x);
            w.WriteEndArray();
            w.WriteStartArray("honest_limits");
            foreach (var x in HonestLimits) w.WriteStringValue(x);
            w.WriteEndArray();
            w.WriteString("gross_note", t.GrossNote);
            w.WriteStartArray("owners");
            foreach (var row in t.Rows)
            {
                w.WriteStartObject();
                w.WriteNumber("rank", row.Rank);
                w.WriteString("owner_kind", row.Kind.ToString().ToLowerInvariant());
                w.WriteString("owner_id", row.Id);
                w.WriteString("owner_name", row.Name);
                w.WriteNumber("live_bytes", row.LiveBytes);
                w.WriteNumber("live_objects", row.LiveObjects);
                w.WriteNumber("pct_of_live_heap", Math.Round(row.PctOfLiveHeap, 2));
                if (row.GrossAllocBytes.HasValue) w.WriteNumber("gross_alloc_bytes", row.GrossAllocBytes.Value);
                else w.WriteNull("gross_alloc_bytes");
                w.WriteStartArray("top_types");
                int n = 0;
                foreach (var d in row.Types)
                {
                    if (n++ >= Math.Max(topN, 5)) break;
                    w.WriteStartObject();
                    w.WriteString("type", d.TypeName);
                    w.WriteString("module", d.Module);
                    w.WriteNumber("count", d.Count);
                    w.WriteNumber("bytes", d.Bytes);
                    w.WriteString("via", d.Via);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("by_assembly");
            foreach (var a in t.ByAssembly)
            {
                w.WriteStartObject();
                w.WriteString("bucket", a.Bucket);
                w.WriteString("owner_kind", a.Kind.ToString().ToLowerInvariant());
                w.WriteString("owner_id", a.OwnerId);
                w.WriteNumber("live_bytes", a.Bytes);
                w.WriteNumber("live_objects", a.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            var unk = t.Rows.FirstOrDefault(x => x.Kind == OwnerKind.Unknown);
            w.WriteStartObject("unknown");
            w.WriteNumber("live_bytes", unk?.LiveBytes ?? 0);
            w.WriteNumber("live_objects", unk?.LiveObjects ?? 0);
            w.WriteStartArray("reasons");
            foreach (var u in t.UnknownReasons)
            {
                w.WriteStartObject();
                w.WriteString("reason", u.Reason);
                w.WriteNumber("live_bytes", u.Bytes);
                w.WriteNumber("live_objects", u.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("unmapped_modules");
            foreach (var m in t.UnknownModules.Take(10))
            {
                w.WriteStartObject();
                w.WriteString("module", m.Bucket.Substring("unknown:".Length));
                w.WriteNumber("live_bytes", m.Bytes);
                w.WriteNumber("live_objects", m.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("top_types");
            if (unk != null)
                foreach (var d in unk.Types.Take(10))
                {
                    w.WriteStartObject();
                    w.WriteString("type", d.TypeName);
                    w.WriteString("module", d.Module);
                    w.WriteNumber("count", d.Count);
                    w.WriteNumber("bytes", d.Bytes);
                    w.WriteString("reason", d.Reason);
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(ms.ToArray()) + "\n";
    }

    public static double Mb(long bytes) => bytes / 1048576.0;

    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 3) + "...";

    static string SimpleModuleName(string module)
    {
        if (string.IsNullOrEmpty(module)) return "";
        string f = module.Replace('/', '\\');
        int i = f.LastIndexOf('\\');
        if (i >= 0) f = f.Substring(i + 1);
        int dot = f.LastIndexOf('.');
        if (dot > 0 && (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            f = f.Substring(0, dot);
        return f;
    }

    static string Csv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    static string MdCell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
