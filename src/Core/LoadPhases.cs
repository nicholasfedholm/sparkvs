using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;

namespace SparkVS.Core;

/// <summary>
/// Per-mod load phase durations recorded by a Harmony hook on ModLoader.TryRunModPhase. Rows are added only during game
/// load (one per mod and phase), so a lock and a list are fine.
/// </summary>
public sealed class LoadPhaseLog
{
    public static readonly string[] PhaseNames = { "StartPre", "Start", "AssetsLoaded", "AssetsFinalize", "StartClientSide", "StartServerSide" };
    public static readonly string[] Sides = { "client", "server" };

    static string[] PhasesOf(string side) => new[] { "StartPre", "Start", "AssetsLoaded", "AssetsFinalize", side == "client" ? "StartClientSide" : "StartServerSide" };

    public readonly record struct Row(string ModId, string Phase, string Side, double Ms);

    readonly object gate = new();
    readonly List<Row> rows = new();
    long firstStamp, lastStamp;
    string? installFailure;
    bool installed;

    public bool Installed => installed;
    public int Count { get { lock (gate) return rows.Count; } }

    public void MarkInstalled() { installed = true; }
    public void MarkFailed(string reason) { installFailure = reason; installed = false; }

    public void Add(string modId, string phase, string side, long startStamp, long endStamp)
    {
        double ms = (endStamp - startStamp) * 1000.0 / Stopwatch.Frequency;
        lock (gate)
        {
            if (rows.Count == 0 || startStamp < firstStamp) firstStamp = startStamp;
            if (endStamp > lastStamp) lastStamp = endStamp;
            rows.Add(new Row(modId, phase, side, ms));
        }
    }

    /// <summary>
    /// load_phases for context.json. modIds is the loaded mod list, so a mod with no recorded phase still appears,
    /// with null and a reason, instead of being silently absent.
    /// </summary>
    public JsonObject ToJson(IReadOnlyCollection<string> modIds)
    {
        List<Row> copy;
        double? boot = null;
        lock (gate)
        {
            copy = rows.ToList();
            if (rows.Count > 0) boot = Math.Round((lastStamp - firstStamp) * 1000.0 / Stopwatch.Frequency, 1);
        }
        if (!installed && copy.Count == 0)
            return new JsonObject
            {
                ["enabled"] = false,
                ["reason"] = installFailure ?? "load phase hook was not installed (LoadPhases false in config)",
            };

        var mods = new JsonObject();
        var ids = modIds.Concat(copy.Select(r => r.ModId)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase);
        var sideSeen = Sides.ToDictionary(sd => sd, sd => copy.Any(r => r.Side == sd));
        foreach (var id in ids)
        {
            var node = new JsonObject();
            var mine = copy.Where(r => string.Equals(r.ModId, id, StringComparison.OrdinalIgnoreCase)).ToList();
            double all = 0;
            foreach (var side in Sides)
            {
                if (!sideSeen[side])
                {
                    node[side] = null;
                    node[side + "_reason"] = "no " + side + " mod phases ran in this process (no " + side + " side started here, or SparkVS hooked in after it)";
                    continue;
                }
                var sn = new JsonObject();
                double sum = 0;
                int any = 0;
                foreach (var phase in PhasesOf(side))
                {
                    var hits = mine.Where(r => r.Side == side && r.Phase == phase).ToList();
                    if (hits.Count == 0)
                    {
                        sn[phase + "_ms"] = null;
                        sn[phase + "_reason"] = "no " + phase + " call recorded on the " + side + " (mod has no ModSystem implementing it or ShouldLoad was false for this side)";
                    }
                    else
                    {
                        double ms = hits.Sum(r => r.Ms);
                        sn[phase + "_ms"] = Math.Round(ms, 3);
                        sum += ms;
                        any++;
                    }
                }
                sn["total_ms"] = any == 0 ? null : Math.Round(sum, 3);
                all += sum;
                node[side] = sn;
            }
            node["total_ms"] = mine.Count == 0 ? null : Math.Round(all, 3);
            mods[id] = node;
        }

        return new JsonObject
        {
            ["enabled"] = true,
            ["label"] = "measured",
            ["source"] = "Harmony prefix and postfix on ModLoader.TryRunModPhase, wall time of each mod's phase call",
            ["phases"] = new JsonArray(PhaseNames.Select(p => (JsonNode?)p).ToArray()),
            ["sides_recorded"] = new JsonArray(Sides.Where(sd => sideSeen[sd]).Select(sd => (JsonNode?)sd).ToArray()),
            ["total_boot_ms"] = boot,
            ["total_boot_ms_note"] = "wall time from the first recorded mod phase start to the last recorded phase end in this process; excludes game startup before the first mod phase. SparkVS hooks in from its own constructor, so phases that ran before SparkVS was constructed are not recorded.",
            ["mods_total"] = mods.Count,
            ["rows_recorded"] = copy.Count,
            ["per_mod"] = mods,
        };
    }
}
