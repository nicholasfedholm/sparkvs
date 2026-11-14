using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace SparkLoad.Launch
{
    /// <summary>Hidden `sparkload tracecheck --file raw.nettrace [--require A,B]` command: reads a nettrace with TraceEvent.</summary>
    internal static class TraceCheck
    {
        public static int Run(string[] args, TextWriter o)
        {
            string? file = null;
            string require = "SparkLoad,Microsoft-Windows-DotNETRuntime";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
                else if (args[i] == "--require" && i + 1 < args.Length) require = args[++i];
            }
            if (file == null) { o.WriteLine("tracecheck: --file is required"); return 2; }
            if (!File.Exists(file)) { o.WriteLine("tracecheck: file not found"); return 2; }

            byte[] head = new byte[8];
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n = fs.Read(head, 0, 8);
                if (n < 8) { o.WriteLine("magic=FAIL (file shorter than 8 bytes)"); return 1; }
            }
            string magic = Encoding.ASCII.GetString(head);
            bool magicOk = magic == "Nettrace";
            o.WriteLine("magic=" + (magicOk ? "OK" : "FAIL") + " (" + magic + ")");
            if (!magicOk) return 1;

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var firstMs = new Dictionary<string, double>();
            var names = new Dictionary<string, SortedSet<string>>();
            long total = 0;
            var known = new Dictionary<Guid, string>
            {
                [TraceEventProviders.GetEventSourceGuidFromName("SparkLoad")] = "SparkLoad",
                [new Guid("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4")] = "Microsoft-Windows-DotNETRuntime",
                [new Guid("a669021c-c450-4609-a035-5af59af4df18")] = "Microsoft-Windows-DotNETRuntimeRundown",
                [new Guid("8e9f5090-2d75-4d03-8a81-e5afbf85daf1")] = "Microsoft-DotNETCore-SampleProfiler",
            };
            using (var src = new EventPipeEventSource(file))
            {
                _ = src.Dynamic; // registers manifest-based providers (EventSource) before events flow
                _ = src.Clr;
                src.AllEvents += e =>
                {
                    string p = e.ProviderName ?? "?";
                    if (known.TryGetValue(e.ProviderGuid, out string? kn)) p = kn;
                    counts.TryGetValue(p, out int c);
                    counts[p] = c + 1;
                    if (!firstMs.ContainsKey(p)) firstMs[p] = e.TimeStampRelativeMSec;
                    if (!names.TryGetValue(p, out var set)) { set = new SortedSet<string>(StringComparer.Ordinal); names[p] = set; }
                    if (set.Count < 12) set.Add(e.EventName ?? "?");
                    total++;
                };
                src.Process();
            }
            o.WriteLine("events=" + total.ToString(CultureInfo.InvariantCulture));
            foreach (var kv in counts)
                o.WriteLine("provider " + kv.Key + " events=" + kv.Value.ToString(CultureInfo.InvariantCulture) + " first_ms=" + firstMs[kv.Key].ToString("0.000", CultureInfo.InvariantCulture)
                    + " names=" + string.Join("|", names[kv.Key]));
            bool ok = true;
            foreach (string r in require.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool has = counts.TryGetValue(r, out int c) && c > 0;
                o.WriteLine("require " + r + "=" + (has ? "OK" : "MISSING"));
                if (!has) ok = false;
            }
            return ok ? 0 : 1;
        }
    }
}
