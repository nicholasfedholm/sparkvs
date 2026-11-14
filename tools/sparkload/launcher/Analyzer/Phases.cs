using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SparkLoad.Analyzer
{
    /// <summary>
    /// The one place for the run phase table (Vintage Story 1.22.7). phases.json in the run folder overrides it.
    /// Accepted shapes: {"0":"Start",...}, {"Start":0,...}, [{"value":0,"name":"Start"},...] or ["Start","Initialization",...].
    /// passes.json (optional, same shapes) names the column.pass values.
    /// </summary>
    internal static class RunPhases
    {
        public const int RunGameValue = 8;

        private static readonly string[] DefaultNames =
        {
            "Start", "Initialization", "Configuration", "LoadAssets", "AssetsFinalize",
            "LoadGamePre", "GameReady", "WorldReady", "RunGame"
        };

        public static Dictionary<int, string> Defaults()
        {
            var d = new Dictionary<int, string>();
            for (int i = 0; i < DefaultNames.Length; i++) d[i] = DefaultNames[i];
            return d;
        }

        public static Dictionary<int, string> Load(string runDir, TextWriter log) =>
            LoadTable(Path.Combine(runDir, "phases.json"), Defaults(), log);

        public static Dictionary<int, string> LoadPasses(string runDir, TextWriter log) =>
            LoadTable(Path.Combine(runDir, "passes.json"), new Dictionary<int, string>(), log);

        public static string Name(Dictionary<int, string> table, long value) =>
            table.TryGetValue((int)value, out var n) ? n : "phase" + value.ToString(CultureInfo.InvariantCulture);

        private static Dictionary<int, string> LoadTable(string path, Dictionary<int, string> dflt, TextWriter log)
        {
            if (!File.Exists(path)) return dflt;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var res = new Dictionary<int, string>();
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        if (int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && p.Value.ValueKind == JsonValueKind.String)
                            res[k] = p.Value.GetString() ?? "";
                        else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int v))
                            res[v] = p.Name;
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var e in root.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String) res[idx] = e.GetString() ?? "";
                        else if (e.ValueKind == JsonValueKind.Object)
                        {
                            int val = idx;
                            string? nm = null;
                            foreach (var p in e.EnumerateObject())
                            {
                                string pn = p.Name.ToLowerInvariant();
                                if ((pn == "value" || pn == "id" || pn == "v") && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int vv)) val = vv;
                                else if (pn == "name" && p.Value.ValueKind == JsonValueKind.String) nm = p.Value.GetString();
                            }
                            if (nm != null) res[val] = nm;
                        }
                        idx++;
                    }
                }
                if (res.Count == 0) return dflt;
                log.WriteLine("analyzer: loaded " + Path.GetFileName(path) + " (" + res.Count + " entries)");
                return res;
            }
            catch (Exception ex)
            {
                log.WriteLine("analyzer: ignoring unreadable " + Path.GetFileName(path) + ": " + Txt.Clean(ex.Message));
                return dflt;
            }
        }
    }
}
