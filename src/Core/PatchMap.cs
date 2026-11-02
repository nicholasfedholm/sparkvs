using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>One patch on a method: who owns it, which kind, and the patch method's name.</summary>
public readonly record struct PatchEntry(string Owner, string Kind, string PatchMethod);

public sealed class PatchedMethod
{
    public string Method { get; init; } = "";
    public List<PatchEntry> Patches { get; } = new();
}

/// <summary>A snapshot of Harmony.GetAllPatchedMethods taken on one side. Taken by the mod, summarised here without Harmony.</summary>
public sealed class PatchSnapshot
{
    public string Side { get; init; } = "";
    public DateTime TakenLocal { get; init; }
    public List<PatchedMethod> Methods { get; init; } = new();
    public string? FailureReason { get; init; }
}

public static class PatchMapBuilder
{
    public const string Prefix = "prefix", Postfix = "postfix", Transpiler = "transpiler", Finalizer = "finalizer";
    public const int MaxMethodsListed = 400;

    public static JsonObject Disabled(string reason) => new()
    {
        ["schema_version"] = BuildInfo.SchemaVersion,
        ["enabled"] = false,
        ["reason"] = reason,
    };

    public static JsonObject Summarise(PatchSnapshot snap)
    {
        if (snap.FailureReason != null)
            return new JsonObject { ["available"] = false, ["reason"] = snap.FailureReason };

        var byKind = new Dictionary<string, int> { [Prefix] = 0, [Postfix] = 0, [Transpiler] = 0, [Finalizer] = 0 };
        var owners = new HashSet<string>(StringComparer.Ordinal);
        int patches = 0;
        foreach (var m in snap.Methods)
            foreach (var p in m.Patches)
            {
                patches++;
                owners.Add(p.Owner);
                byKind[p.Kind] = byKind.GetValueOrDefault(p.Kind) + 1;
            }

        var multi = snap.Methods
            .Select(m => (Method: m, Owners: m.Patches.Select(p => p.Owner).Distinct(StringComparer.Ordinal).ToList()))
            .Where(x => x.Owners.Count > 1)
            .OrderByDescending(x => x.Owners.Count).ThenByDescending(x => x.Method.Patches.Count).ThenBy(x => x.Method.Method, StringComparer.Ordinal)
            .ToList();

        var listed = new JsonArray();
        foreach (var (method, ownerList) in multi.Take(MaxMethodsListed))
        {
            var ownerNodes = new JsonArray();
            foreach (var owner in ownerList.OrderBy(o => o, StringComparer.Ordinal))
            {
                var mine = method.Patches.Where(p => p.Owner == owner).ToList();
                ownerNodes.Add(new JsonObject
                {
                    ["owner"] = owner,
                    ["kinds"] = new JsonArray(mine.Select(p => p.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()),
                    ["patch_methods"] = new JsonArray(mine.Select(p => (JsonNode?)(p.Kind + " " + p.PatchMethod)).ToArray()),
                });
            }
            listed.Add(new JsonObject { ["method"] = method.Method, ["owner_count"] = ownerList.Count, ["patch_count"] = method.Patches.Count, ["owners"] = ownerNodes });
        }

        var ownerCounts = new JsonObject();
        foreach (var g in snap.Methods.SelectMany(m => m.Patches).GroupBy(p => p.Owner).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(40))
            ownerCounts[g.Key] = g.Count();

        return new JsonObject
        {
            ["available"] = true,
            ["taken_local"] = BundleClock.Iso(snap.TakenLocal),
            ["patched_methods_total"] = snap.Methods.Count,
            ["patches_total"] = patches,
            ["owners_total"] = owners.Count,
            ["patches_by_kind"] = new JsonObject { [Prefix] = byKind[Prefix], [Postfix] = byKind[Postfix], [Transpiler] = byKind[Transpiler], [Finalizer] = byKind[Finalizer] },
            ["methods_patched_by_multiple_owners"] = multi.Count,
            ["methods_listed"] = Math.Min(multi.Count, MaxMethodsListed),
            ["patches_per_owner_top40"] = ownerCounts,
            ["multi_owner_methods"] = listed,
        };
    }

    public static JsonObject Build(PatchSnapshot? client, PatchSnapshot? server, string? clientReason, string? serverReason)
    {
        if (client == null && server == null)
            return Disabled(clientReason ?? serverReason ?? "no Harmony snapshot was taken");
        var sides = new JsonObject
        {
            ["client"] = client != null ? Summarise(client) : new JsonObject { ["available"] = false, ["reason"] = clientReason ?? "no client snapshot" },
            ["server"] = server != null ? Summarise(server) : new JsonObject { ["available"] = false, ["reason"] = serverReason ?? "no server snapshot" },
        };
        return new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["enabled"] = true,
            ["label"] = "measured",
            ["source"] = "Harmony.GetAllPatchedMethods and Harmony.GetPatchInfo, read once per side",
            ["shared_process_note"] = "Singleplayer runs client and server in one process and one Harmony instance, so the two snapshots see the same patches except for those applied between their timestamps. A dedicated server has no client snapshot.",
            ["excluded_owners"] = "owners starting with sparkvs. (this mod's own chunk pipeline timers and load phase hook) are left out",
            ["sides"] = sides,
        };
    }

    public static JsonObject Headline(JsonObject node)
    {
        var h = new JsonObject { ["label"] = "measured", ["detail_file"] = "patch-map.json" };
        foreach (var side in new[] { "client", "server" })
        {
            var s = node["sides"]?[side];
            if (s?["available"]?.GetValue<bool>() == true)
                h[side] = new JsonObject
                {
                    ["patched_methods_total"] = s["patched_methods_total"]?.DeepClone(),
                    ["patches_total"] = s["patches_total"]?.DeepClone(),
                    ["methods_patched_by_multiple_owners"] = s["methods_patched_by_multiple_owners"]?.DeepClone(),
                };
            else h[side] = null;
        }
        return h;
    }
}
