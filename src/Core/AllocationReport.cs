using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>
/// Decoder for the runtime's GCAllocationTick event (Microsoft-Windows-DotNETRuntime, id 10, GC keyword, verbose).
/// Layout of version 2 and later: AllocationAmount u32, AllocationKind u32, ClrInstanceID u16, AllocationAmount64 u64,
/// TypeId pointer, TypeName UTF-16 zero terminated, HeapIndex u32, then (v4) Address pointer and ObjectSize u64.
/// Decoded from the raw payload so it does not depend on which TraceEvent parsers are registered.
/// </summary>
public static class AllocationTick
{
    public const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    public const int EventId = 10;
    /// <summary>GC keyword of the runtime provider. With verbose level this is what turns the allocation ticks on.</summary>
    public const long GcKeyword = 0x1;
    /// <summary>The runtime's nominal threshold: one tick per about 100 KB allocated by a thread.</summary>
    public const long NominalBytesPerTick = 102400;

    public static bool TryParse(byte[] payload, out long bytes, out bool large, out string typeName)
    {
        bytes = 0;
        large = false;
        typeName = "";
        if (payload.Length < 10) return false;
        uint amount32 = BitConverter.ToUInt32(payload, 0);
        large = BitConverter.ToUInt32(payload, 4) == 1;
        bytes = amount32;
        const int typeNameOffset = 10 + 8 + 8;   // after ClrInstanceID, AllocationAmount64, TypeId
        if (payload.Length < typeNameOffset + 2)
        {
            typeName = "(type not reported)";
            return true;
        }
        bytes = (long)BitConverter.ToUInt64(payload, 10);
        int end = typeNameOffset;
        while (end + 1 < payload.Length && (payload[end] != 0 || payload[end + 1] != 0)) end += 2;
        typeName = Encoding.Unicode.GetString(payload, typeNameOffset, end - typeNameOffset);
        if (typeName.Length == 0) typeName = "(type not reported)";
        return true;
    }
}

/// <summary>allocations.json: allocation bytes by owning mod, estimated from GC allocation ticks. Never full allocation tracing.</summary>
public static class AllocationReport
{
    public const int TopTypes = 10;
    public const int TopMethods = 10;
    const int TopOverall = 15;

    public static JsonObject Disabled(string reason) => new()
    {
        ["schema_version"] = BuildInfo.SchemaVersion,
        ["enabled"] = false,
        ["reason"] = reason,
    };

    sealed class OwnerAcc
    {
        public long Bytes;
        public int Ticks;
        public readonly Dictionary<int, long> TypeBytes = new();
        public readonly Dictionary<int, long> MethodBytes = new();   // frame index, -1 = runtime only / no frames
    }

    public static JsonObject Build(SampleSet set, CaptureMeta meta)
    {
        if (!meta.AllocationSampling) return Disabled("allocation sampling was switched off for this capture (AllocationSampling false in SparkVS.json)");
        if (set.Allocs.Count == 0)
            return Disabled("allocation sampling was on but no GCAllocationTick events were decoded from the trace (no thread allocated 100 KB in the window, or the runtime did not emit them)");

        var attribution = new Attribution(meta.AssemblyToMod, meta.GameAssemblies);
        var owners = new Dictionary<string, OwnerAcc>(StringComparer.Ordinal);
        var ownerByStack = new Dictionary<int, (string Owner, int Frame)>();
        var typesAll = new Dictionary<int, long>();
        var methodsAll = new Dictionary<(string Owner, int Frame), long>();
        long total = 0, large = 0;
        int noStack = 0, resolvedFrames = 0, totalOwnerFrames = 0;

        foreach (var a in set.Allocs)
        {
            if (!ownerByStack.TryGetValue(a.StackId, out var billed))
            {
                var stack = set.Stacks[a.StackId];
                billed = (attribution.OwnerOfStack(stack, set.Frames), attribution.OwnerFrameOfStack(stack, set.Frames));
                ownerByStack[a.StackId] = billed;
            }
            if (set.Stacks[a.StackId].Length == 0) noStack++;
            if (billed.Frame >= 0)
            {
                totalOwnerFrames++;
                if (set.Frames[billed.Frame].Resolved) resolvedFrames++;
            }
            if (!owners.TryGetValue(billed.Owner, out var acc)) owners[billed.Owner] = acc = new OwnerAcc();
            acc.Bytes += a.Bytes;
            acc.Ticks++;
            acc.TypeBytes[a.TypeId] = acc.TypeBytes.GetValueOrDefault(a.TypeId) + a.Bytes;
            acc.MethodBytes[billed.Frame] = acc.MethodBytes.GetValueOrDefault(billed.Frame) + a.Bytes;
            typesAll[a.TypeId] = typesAll.GetValueOrDefault(a.TypeId) + a.Bytes;
            methodsAll[(billed.Owner, billed.Frame)] = methodsAll.GetValueOrDefault((billed.Owner, billed.Frame)) + a.Bytes;
            total += a.Bytes;
            if (a.Large) large += a.Bytes;
        }

        double Pct(long part) => total > 0 ? Math.Round(part * 100.0 / total, 2) : 0;
        JsonArray Types(Dictionary<int, long> bytes, int n, long denominator) => new(bytes.OrderByDescending(kv => kv.Value).Take(n)
            .Select(kv => (JsonNode?)new JsonObject
            {
                ["type"] = set.TypeNames[kv.Key],
                ["allocated_bytes"] = kv.Value,
                ["percent_of_owner"] = denominator > 0 ? Math.Round(kv.Value * 100.0 / denominator, 2) : 0,
            }).ToArray());
        JsonObject MethodNode(int frame, long bytes, long denominator)
        {
            var node = new JsonObject
            {
                ["method"] = frame < 0 ? "(runtime frames only)" : Analyzer.ShortName(set.Frames[frame].Method),
                ["module"] = frame < 0 ? "" : set.Frames[frame].Module,
                ["allocated_bytes"] = bytes,
                ["percent_of_owner"] = denominator > 0 ? Math.Round(bytes * 100.0 / denominator, 2) : 0,
            };
            return node;
        }

        var byOwner = new JsonArray();
        foreach (var kv in owners.OrderByDescending(o => o.Value.Bytes))
        {
            var acc = kv.Value;
            byOwner.Add(new JsonObject
            {
                ["owner"] = kv.Key,
                ["kind"] = kv.Key == Attribution.Game || kv.Key == Attribution.Runtime || kv.Key == Attribution.Patched || kv.Key == Attribution.Unknown ? "bucket" : "mod",
                ["allocated_bytes"] = acc.Bytes,
                ["percent_of_total"] = Pct(acc.Bytes),
                ["ticks"] = acc.Ticks,
                ["top_types"] = Types(acc.TypeBytes, TopTypes, acc.Bytes),
                ["top_methods"] = new JsonArray(acc.MethodBytes.OrderByDescending(m => m.Value).Take(TopMethods)
                    .Select(m => (JsonNode?)MethodNode(m.Key, m.Value, acc.Bytes)).ToArray()),
            });
        }

        long unknownBytes = owners.TryGetValue(Attribution.Unknown, out var unk) ? unk.Bytes : 0;
        int unknownTicks = unk?.Ticks ?? 0;
        double durationMs = meta.QpcFrequency > 0 ? (meta.EndQpc - meta.StartQpc) * 1000.0 / meta.QpcFrequency : 0;
        long gcDelta = meta.GcAllocatedBytesEnd - meta.GcAllocatedBytesStart;

        var root = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["enabled"] = true,
            ["label"] = "estimated",
            ["capture"] = new JsonObject { ["label"] = meta.Label, ["start_local"] = meta.StartedLocal, ["duration_ms"] = Math.Round(durationMs, 1) },
            ["total_allocated_bytes"] = total,
            ["total_allocated_bytes_note"] = "sum of the bytes each allocation tick reports since that thread's previous tick; an estimate, not a full trace",
            ["sampling"] = new JsonObject
            {
                ["method"] = "EventPipe GCAllocationTick on the existing SparkVS session (Microsoft-Windows-DotNETRuntime, GC keyword 0x1, verbose), stack captured with each tick",
                ["sampling_rate"] = "one tick per about 100 KB allocated per thread (runtime's fixed allocation-context threshold), about 1 in 100000 bytes",
                ["nominal_bytes_per_tick"] = AllocationTick.NominalBytesPerTick,
                ["ticks"] = set.Allocs.Count,
                ["mean_bytes_per_tick"] = Math.Round((double)total / set.Allocs.Count, 0),
                ["smallest_resolvable_bytes"] = "an owner billed less than about 100 KB in the window can read as zero ticks or one tick; small percentages are noisy",
                ["ticks_without_stack"] = noStack,
                ["owner_frame_resolved_percent"] = totalOwnerFrames > 0 ? Math.Round(resolvedFrames * 100.0 / totalOwnerFrames, 2) : 0,
                ["owner_frame_resolved_percent_note"] = "share of billed frames that have a method name; this is symbol quality, not attribution quality. See unknown_bucket for the share no owner could be named for",
                ["unknown_percent_of_total"] = Pct(unknownBytes),
                ["attribution"] = "same rule as the CPU report: the first frame up from the allocation site that is not .NET runtime code; a mod only when the game's mod list says that assembly is the mod's",
            },
            ["large_object_heap_bytes"] = large,
            ["cross_check"] = new JsonObject
            {
                ["gc_total_allocated_bytes_delta"] = gcDelta,
                ["gc_total_allocated_bytes_note"] = "GC.GetTotalAllocatedBytes(false) at capture start and end, every thread, measured",
                ["estimate_vs_gc_delta_percent"] = gcDelta > 0 ? Math.Round((total - gcDelta) * 100.0 / gcDelta, 2) : null,
            },
            ["unknown_bucket"] = new JsonObject
            {
                ["allocated_bytes"] = unknownBytes,
                ["percent_of_total"] = Pct(unknownBytes),
                ["ticks"] = unknownTicks,
                ["note"] = "stacks whose first non-runtime frame belongs to no loaded mod and no game assembly, or that carry no frames; never guessed into a mod",
            },
            ["by_owner"] = byOwner,
            ["top_types_overall"] = Types(typesAll, TopOverall, total),
            ["top_methods_overall"] = new JsonArray(methodsAll.OrderByDescending(m => m.Value).Take(TopOverall)
                .Select(m =>
                {
                    var node = MethodNode(m.Key.Frame, m.Value, total);
                    node["owner"] = m.Key.Owner;
                    node.Remove("percent_of_owner");
                    node["percent_of_total"] = Pct(m.Value);
                    return (JsonNode?)node;
                }).ToArray()),
        };
        return root;
    }

    /// <summary>Short table for summary.md, from the finished JSON so the two always agree.</summary>
    public static string ToMarkdown(JsonObject node)
    {
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("## Allocations by owner (estimated, GC allocation ticks, about 1 per 100 KB)");
        if (node["enabled"]?.GetValue<bool>() != true)
        {
            sb.AppendLine().AppendLine("Not collected: " + node["reason"]?.GetValue<string>());
            return sb.ToString();
        }
        long total = node["total_allocated_bytes"]!.GetValue<long>();
        sb.AppendLine().AppendLine($"Total about {total / 1048576.0:F1} MB over {node["capture"]!["duration_ms"]!.GetValue<double>() / 1000.0:F0} s ({node["sampling"]!["ticks"]!.GetValue<int>()} ticks). GC counter cross-check delta: {node["cross_check"]!["estimate_vs_gc_delta_percent"]?.ToJsonString() ?? "n/a"} %.");
        sb.AppendLine().AppendLine("| owner | MB | % | top type | top method |").AppendLine("|---|---|---|---|---|");
        foreach (var o in node["by_owner"]!.AsArray().Take(12))
        {
            var t = o!["top_types"]!.AsArray().FirstOrDefault();
            var m = o["top_methods"]!.AsArray().FirstOrDefault();
            sb.AppendLine($"| {o["owner"]} | {o["allocated_bytes"]!.GetValue<long>() / 1048576.0:F2} | {o["percent_of_total"]} | {t?["type"]} | {m?["method"]} |");
        }
        return sb.ToString();
    }

    /// <summary>Headline numbers for summary.json.</summary>
    public static JsonObject Headline(JsonObject node)
    {
        var h = new JsonObject { ["label"] = "estimated", ["detail_file"] = "allocations.json", ["enabled"] = node["enabled"]?.DeepClone() };
        if (node["enabled"]?.GetValue<bool>() != true)
        {
            h["reason"] = node["reason"]?.DeepClone();
            return h;
        }
        h["total_allocated_bytes"] = node["total_allocated_bytes"]?.DeepClone();
        h["ticks"] = node["sampling"]!["ticks"]?.DeepClone();
        h["unknown_percent"] = node["unknown_bucket"]!["percent_of_total"]?.DeepClone();
        h["top_owners"] = new JsonArray(node["by_owner"]!.AsArray().Take(8).Select(o => (JsonNode?)new JsonObject
        {
            ["owner"] = o!["owner"]?.DeepClone(),
            ["allocated_bytes"] = o["allocated_bytes"]?.DeepClone(),
            ["percent_of_total"] = o["percent_of_total"]?.DeepClone(),
        }).ToArray());
        return h;
    }
}
