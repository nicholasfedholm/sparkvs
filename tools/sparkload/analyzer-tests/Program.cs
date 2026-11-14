using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SparkLoad.Analyzer;

namespace SparkLoad.AnalyzerTests
{
    internal static class Program
    {
        private static int _passed, _failed;
        private static string _runs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "runs", "synthetic"));
        private static readonly List<string> Notes = new List<string>();

        private static void Check(bool ok, string name, string? detail = null)
        {
            if (ok) { _passed++; return; }
            _failed++;
            Console.WriteLine("FAIL: " + name + (detail != null ? " :: " + detail : ""));
        }

        private static (int Exit, string Log) RunAnalyzer(string dir, string? cmp = null)
        {
            var sw = new StringWriter();
            int rc = AnalyzerMain.Run(dir, cmp, sw);
            return (rc, sw.ToString());
        }

        private static string Gate(string md, string id)
        {
            int i = md.LastIndexOf("## Gates", StringComparison.Ordinal);
            if (i < 0) return "";
            foreach (var l in md.Substring(i).Split('\n'))
                if (l.TrimEnd().StartsWith(id + " ", StringComparison.Ordinal)) return l.TrimEnd();
            return "";
        }

        private static readonly char[] BadDashes = { '\u2014', '\u2013' };

        private static void NoDashes(string label, string path)
        {
            if (!File.Exists(path)) { Check(false, label + " exists for dash check"); return; }
            string text = File.ReadAllText(path);
            Check(text.IndexOfAny(BadDashes) < 0, label + " has no em or en dash");
        }

        private static string Dir(string name) => Path.Combine(_runs, name);

        private static int Main(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--runs") _runs = args[i + 1];
            Directory.CreateDirectory(_runs);
            try
            {
                TestA();
                TestB();
                TestC();
                TestOverrides();
                TestCompare();
                TestRealTrace();
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL: unhandled exception " + ex);
            }
            foreach (var n in Notes) Console.WriteLine("note: " + n);
            Console.WriteLine("tests passed=" + _passed + " failed=" + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // (a) full default-arm run.
        private static void TestA()
        {
            var sc = new Scenario { Name = "synthetic-a" };
            var syn = new Synth(sc);
            string dir = Dir("a");
            syn.Write(dir);
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, "a: exit 0", log);
            string mdPath = Path.Combine(dir, "loading.md"), csvPath = Path.Combine(dir, "loading.csv");
            Check(File.Exists(mdPath) && File.Exists(csvPath), "a: loading.md and loading.csv written");
            if (!File.Exists(mdPath) || !File.Exists(csvPath)) return;
            string md = File.ReadAllText(mdPath);

            // CSV shape.
            var rows = CsvReader.Rows(csvPath).ToList();
            Check(string.Join(",", rows[0]) == string.Join(",", Analyzer_Columns()), "a: csv header exact", string.Join(",", rows[0]));
            Check(rows.Count > 1 && rows[1][7] == "process" && rows[1][2] == "0.000", "a: first row is process at 0", rows.Count > 1 ? string.Join(",", rows[1]) : "");
            Check(rows.All(r => r.Count == 17), "a: every csv row has 17 columns");
            int passRows = rows.Skip(1).Count(r => r[7] == "column.pass");
            Check(passRows == Synth.Columns * 6, "a: 294 column.pass rows", passRows.ToString());
            int logPhaseRows = rows.Skip(1).Count(r => r[7] == "server.phase" && r[15] == "log");
            Check(logPhaseRows == 8, "a: 8 log-derived server.phase rows", logPhaseRows.ToString());
            int markerPhaseRows = rows.Skip(1).Count(r => r[7] == "server.phase" && r[15] == "marker");
            Check(markerPhaseRows == 9, "a: 9 marker-derived server.phase rows", markerPhaseRows.ToString());
            var gamePre = rows.Skip(1).FirstOrDefault(r => r[7] == "server.phase" && r[8] == "LoadGamePre" && r[15] == "marker");
            Check(gamePre != null && Math.Abs(Num.D(gamePre[4]) - 10000) < 1, "a: LoadGamePre marker row lasts 10 s", gamePre == null ? "missing" : gamePre[4]);
            Check(rows.Skip(1).Any(r => r[7] == "silence" && r[15] == "sample"), "a: silence row from samples");
            Check(rows.Skip(1).Any(r => r[7] == "gc"), "a: gc pause rows");
            var mod = rows.Skip(1).FirstOrDefault(r => r[7] == "mod.phase" && r[9] == "clientmod3");
            Check(mod != null && mod[5] == "client", "a: mod.phase row carries mod and side", mod == null ? "missing" : string.Join(",", mod));
            var colRow = rows.Skip(1).FirstOrDefault(r => r[7] == "column.pass" && r[10] == "3" && r[11] == "2" && r[12] == "4");
            Check(colRow != null, "a: column.pass row has x,z,pass");

            // md headings.
            foreach (var h in new[] { "# SparkLoad loading report", "## Run", "## Hook targets", "## Phases", "## Top mods by phase time", "## Columns", "## Silences", "## GC", "## Coverage", "## Log cross-check", "## Answers", "## Gates" })
                Check(md.Contains(h + "\n") || md.Contains(h + "\r\n"), "a: heading '" + h + "'");
            Check(!md.Contains("## Moved bottleneck"), "a: no compare section without --compare");
            Check(md.Contains("TIMING-VOID") == false, "a: no timing-void line on default arm");
            var lastLines = md.TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToList();
            Check(lastLines.Count > 0 && lastLines[lastLines.Count - 1].StartsWith("G11 ", StringComparison.Ordinal), "a: md ends with the G11 gate line");
            int gi = lastLines.FindLastIndex(l => l == "## Gates");
            Check(gi >= 0 && lastLines.Skip(gi + 1).Where(l => l.Length > 0).Count() == 5, "a: Gates block holds five lines");

            // Gates.
            Check(Gate(md, "G1") == "G1 g1_delta_ms=12.500 PASS", "a: G1 line", Gate(md, "G1"));
            Check(Gate(md, "G2") == "G2 uncovered_pct=0.83 silences_over_2s=1 unnamed=0 PASS", "a: G2 line", Gate(md, "G2"));
            Check(Gate(md, "G3") == "G3 zero_hit_targets=none lost=0 columns_done=49 PASS", "a: G3 line", Gate(md, "G3"));
            Check(Gate(md, "G6") == "G6 max_delta_ms=350.0 phases=8 PASS", "a: G6 line", Gate(md, "G6"));
            Check(Gate(md, "G11") == "G11 main_idle_s=47.50 postfinalize_gap_s=4.34 frames_in_gap=90 blocklist_wait_s=3.01 ANSWERED", "a: G11 line", Gate(md, "G11"));
            Check(syn.FramesInGap == 90, "a: generator frame count matches expectation", syn.FramesInGap.ToString());

            // Content of sections.
            Check(md.Contains("SoundLoader"), "a: silence names the thread");
            Check(md.Contains("cpu thread SoundLoader"), "a: silence verdict names the cpu thread");
            Check(md.Contains("| 80.00 | 86.00 | 6.00 |"), "a: 6 s silence row", "");
            Check(md.Contains("| 120.0 |") || md.Contains("120.0 |"), "a: GC pause of 120 ms listed");
            Check(md.Contains("columns reaching the last pass: 49"), "a: 49 columns reach the last pass");
            Check(md.Contains("columns generated: 39, loaded from db: 10"), "a: generated vs loaded columns", "");
            Check(md.Contains("clientmod11"), "a: top client mods listed");
            Check(md.Contains("servermod7"), "a: top server mods listed");
            Check(md.Contains("server.remap.items"), "a: hook target table lists server.remap.items");
            Check(md.Contains("Zero-hit or inactive targets: none."), "a: zero-hit statement");
            Check(md.Contains("launch to finalize, by log (s) | 120.00"), "a: launch to finalize by log", "");
            Check(md.Contains("launch to finalize, by marker (s) | 121.00"), "a: launch to finalize by marker", "");
            Check(md.Contains("launch to playable (s) | 124.34"), "a: launch to playable", "");
            Check(md.Contains("frames rendered: yes (90 frames in the gap)"), "a: frames rendered in gap");
            Check(md.Contains("Coverage (G2): 480 windows"), "a: coverage line", "");
            NoDashes("a loading.md", mdPath);
            NoDashes("a loading.csv", csvPath);
            Check(File.Exists(Path.Combine(dir, "events.jsonl")), "a: events.jsonl still present");
        }

        private static string[] Analyzer_Columns() => new[] { "run_id", "arm", "t_start_ms", "t_end_ms", "dur_ms", "side", "thread", "phase", "subphase", "mod", "x", "z", "pass", "count", "bytes", "source", "lost" };

        // (b) seq gap and zero-hit target.
        private static void TestB()
        {
            var sc = new Scenario { Name = "synthetic-b", DropPair = true, ZeroHit = true };
            string dir = Dir("b");
            new Synth(sc).Write(dir);
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, "b: exit 0", log);
            string md = File.ReadAllText(Path.Combine(dir, "loading.md"));
            string g3 = Gate(md, "G3");
            Check(g3 == "G3 zero_hit_targets=server.remap.items lost=2 columns_done=49 FAIL", "b: G3 FAIL names the target and lost=2", g3);
            Check(md.Contains("server.remap.items [ZERO-HIT]"), "b: zero-hit flagged in the target table");
            Check(Gate(md, "G1") == "G1 g1_delta_ms=12.500 PASS", "b: G1 unaffected");
            Check(Gate(md, "G6").EndsWith("PASS", StringComparison.Ordinal), "b: G6 unaffected", Gate(md, "G6"));
            var rows = CsvReader.Rows(Path.Combine(dir, "loading.csv")).ToList();
            Check(rows[1][16] == "2", "b: process row carries lost=2", rows[1][16]);
            Check(md.Contains("markers lost | 2 (tids 201:2)"), "b: totals show lost per tid", "");
        }

        // (c) passive: run.json and logtail.csv only.
        private static void TestC()
        {
            var sc = new Scenario { Name = "synthetic-c", Arm = "V", Mode = "passive", Passive = true, G1 = 8 };
            var syn = new Synth(sc);
            string dir = Dir("c");
            syn.Write(dir);
            Check(!File.Exists(Path.Combine(dir, "samples.csv")) && !File.Exists(Path.Combine(dir, "events.jsonl")) && !File.Exists(Path.Combine(dir, "raw.nettrace")), "c: only run.json and logtail.csv exist");
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, "c: exit 0", log);
            string md = File.ReadAllText(Path.Combine(dir, "loading.md"));
            Check(Gate(md, "G1") == "G1 g1_delta_ms=8.000 PASS", "c: G1", Gate(md, "G1"));
            Check(Gate(md, "G2") == "G2 uncovered_pct=n/a silences_over_2s=1 unnamed=n/a FAIL", "c: G2 n/a", Gate(md, "G2"));
            Check(Gate(md, "G3") == "G3 zero_hit_targets=n/a lost=n/a columns_done=n/a FAIL", "c: G3 n/a", Gate(md, "G3"));
            Check(Gate(md, "G6") == "G6 max_delta_ms=n/a phases=0 FAIL", "c: G6 n/a", Gate(md, "G6"));
            Check(Gate(md, "G11") == "G11 main_idle_s=n/a postfinalize_gap_s=n/a frames_in_gap=n/a blocklist_wait_s=n/a MISSING", "c: G11 n/a", Gate(md, "G11"));
            var rows = CsvReader.Rows(Path.Combine(dir, "loading.csv")).ToList();
            Check(rows[1][7] == "process" && rows[1][2] == "0.000", "c: first row is process at 0");
            int phases = rows.Skip(1).Count(r => r[7] == "server.phase" && r[15] == "log");
            Check(phases == 8, "c: phases come from log lines", phases.ToString());
            Check(rows.Skip(1).All(r => r[15] != "marker" || r[7] == "process"), "c: no marker rows beyond the process row");
            Check(md.Contains("| 3 | LoadAssets |"), "c: server phase table built from logs", "");
            Check(md.Contains("n/a (no samples)"), "c: silence attribution is n/a without samples");
            Check(md.Contains("## Hook targets") && md.Contains("n/a (no Patch or Stats events in this run)"), "c: hook targets n/a");
            Check(!md.Contains("## Moved bottleneck"), "c: no compare section");
            NoDashes("c loading.md", Path.Combine(dir, "loading.md"));
            NoDashes("c loading.csv", Path.Combine(dir, "loading.csv"));
        }

        // phases.json and passes.json overrides.
        private static void TestOverrides()
        {
            var sc = new Scenario { Name = "synthetic-ovr" };
            string dir = Dir("ovr");
            new Synth(sc).Write(dir);
            File.WriteAllText(Path.Combine(dir, "phases.json"), "{\"0\":\"Start\",\"1\":\"Initialization\",\"2\":\"Configuration\",\"3\":\"LoadAssets\",\"4\":\"AssetsFinalize\",\"5\":\"LoadGamePreX\",\"6\":\"GameReady\",\"7\":\"WorldReady\",\"8\":\"RunGame\"}");
            File.WriteAllText(Path.Combine(dir, "passes.json"), "[{\"value\":6,\"name\":\"Done\"},{\"value\":1,\"name\":\"Terrain\"}]");
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, "ovr: exit 0", log);
            string md = File.ReadAllText(Path.Combine(dir, "loading.md"));
            Check(md.Contains("LoadGamePreX"), "ovr: phases.json override used");
            Check(Gate(md, "G6").EndsWith("FAIL", StringComparison.Ordinal), "ovr: renamed phase no longer matches its log line", Gate(md, "G6"));
            Check(md.Contains("Done(6)") && md.Contains("Terrain(1)"), "ovr: passes.json names used");
            Check(md.Contains("last pass seen: Done(6)"), "ovr: last pass named");
        }

        // --compare: a variant with one phase longer.
        private static void TestCompare()
        {
            string a = Dir("a");
            var s1 = new Scenario { Name = "synthetic-a-shift5", Shift = 5000 };
            string d1 = Dir("a-shift5");
            new Synth(s1).Write(d1);
            var (rc, log) = RunAnalyzer(d1, a);
            Check(rc == 0, "cmp1: exit 0", log);
            string md = File.ReadAllText(Path.Combine(d1, "loading.md"));
            Check(md.Contains("## Moved bottleneck"), "cmp1: moved bottleneck section present");
            Check(md.Contains("- largest change: server.phase LoadGamePre +5.00 s"), "cmp1: diff names LoadGamePre +5.00 s", Extract(md, "- largest change"));
            Check(md.Contains("- bottleneck: same phase (server.phase LoadAssets)"), "cmp1: bottleneck unchanged", Extract(md, "- bottleneck"));
            Check(md.Contains("| server.phase LoadGamePre | 15.00 | 10.00 | +5.00 |"), "cmp1: table row for LoadGamePre", "");
            Check(md.IndexOf("## Moved bottleneck", StringComparison.Ordinal) < md.IndexOf("## Gates", StringComparison.Ordinal), "cmp1: compare section before gates");
            string g11 = Gate(md, "G11");
            Check(g11.Contains("main_idle_s=52.25"), "cmp1: shifted run main idle", g11);
            NoDashes("cmp1 loading.md", Path.Combine(d1, "loading.md"));

            // Second variant: the longer phase becomes the largest, so the bottleneck moves.
            var s2 = new Scenario { Name = "synthetic-a-shift12", Shift = 12000 };
            string d2 = Dir("a-shift12");
            new Synth(s2).Write(d2);
            var (rc2, log2) = RunAnalyzer(d2, a);
            Check(rc2 == 0, "cmp2: exit 0", log2);
            string md2 = File.ReadAllText(Path.Combine(d2, "loading.md"));
            Check(md2.Contains("- largest change: server.phase LoadGamePre +12.00 s"), "cmp2: diff names LoadGamePre +12.00 s", Extract(md2, "- largest change"));
            Check(md2.Contains("- bottleneck: moved from server.phase LoadAssets to server.phase LoadGamePre"), "cmp2: bottleneck moved", Extract(md2, "- bottleneck"));

            // Compare run that cannot be read: analysis of the main run still succeeds.
            var (rc3, log3) = RunAnalyzer(d1, Path.Combine(_runs, "does-not-exist"));
            Check(rc3 == 0 && log3.Contains("compare run unreadable"), "cmp3: unreadable compare run skips the diff", log3);

            // Unreadable main run: non-zero.
            string empty = Dir("empty");
            Directory.CreateDirectory(empty);
            foreach (var f in Directory.GetFiles(empty)) File.Delete(f);
            var (rc4, _) = RunAnalyzer(empty);
            Check(rc4 != 0, "cmp4: missing run.json returns non-zero");
        }

        private static string Extract(string md, string prefix) =>
            md.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?.TrimEnd() ?? "(missing)";

        // Real EventPipe trace produced with dotnet-trace against tracegen.
        private static void TestRealTrace()
        {
            string trace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-trace.exe");
            string gen = Path.Combine(AppContext.BaseDirectory, "tracegen", "tracegen.exe");
            if (!File.Exists(trace) || !File.Exists(gen))
            {
                Notes.Add("real-trace test SKIPPED (dotnet-trace or tracegen.exe missing: " + trace + " | " + gen + ")");
                return;
            }
            string dir = Dir("real");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            string nettrace = Path.Combine(dir, "raw.nettrace");
            long t0 = Stopwatch.GetTimestamp();
            var psi = new ProcessStartInfo(trace)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = dir,
            };
            foreach (var a in new[] { "collect", "--providers", "SparkLoad,Microsoft-Windows-DotNETRuntime:0x8009:4", "--output", nettrace, "--", gen })
                psi.ArgumentList.Add(a);
            string toolOut;
            using (var p = Process.Start(psi)!)
            {
                p.StandardInput.Close();
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch (Exception) { } Check(false, "real: dotnet-trace finished in 120 s"); return; }
                toolOut = so.Result + se.Result;
                Check(p.ExitCode == 0, "real: dotnet-trace exit 0", toolOut);
            }
            Check(File.Exists(nettrace) && new FileInfo(nettrace).Length > 1000, "real: raw.nettrace written");
            File.WriteAllText(Path.Combine(dir, "run.json"),
                "{\"run_id\":\"real-trace\",\"arm\":\"O\",\"mode\":\"default\",\"timing_void\":false,\"t0_qpc\":" + t0 + ",\"qpc_freq\":" + Stopwatch.Frequency +
                ",\"g1_delta_ms\":3.5,\"end_ms\":60000,\"launcher_version\":\"test\"}");
            var (rc, log) = RunAnalyzer(dir);
            Check(rc == 0, "real: analyzer exit 0", log);
            string evPath = Path.Combine(dir, "events.jsonl");
            Check(File.Exists(evPath), "real: events.jsonl written");
            if (!File.Exists(evPath)) return;
            var events = File.ReadAllLines(evPath).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
            string S(JsonElement e, string k) => e.GetProperty(k).GetString() ?? "";
            var sparks = events.Where(e => S(e, "src") == "sparkload").ToList();
            int Count(string name) => sparks.Count(e => S(e, "name") == name);
            Check(Count("Hello") == 1, "real: 1 Hello", Count("Hello").ToString());
            Check(Count("MarkerDef") == 4, "real: 4 MarkerDef", Count("MarkerDef").ToString());
            Check(Count("Begin") == 9 && Count("End") == 9, "real: 9 Begin and 9 End", Count("Begin") + "/" + Count("End"));
            Check(Count("Thread") == 2 && Count("Patch") == 2 && Count("Frame") == 3 && Count("FrameAgg") == 1 && Count("Playable") == 1 && Count("Stats") == 2, "real: Thread, Patch, Frame, FrameAgg, Playable, Stats counts");
            double T(JsonElement e) => e.GetProperty("t_ms").GetDouble();
            var hello = sparks.First(e => S(e, "name") == "Hello");
            Check(T(hello) > 0 && T(hello) < 60000, "real: Hello time is within the first minute after t0", T(hello).ToString(CultureInfo.InvariantCulture));
            Check(hello.GetProperty("fields").GetProperty("hookVersion").GetString() == "test-1.0", "real: Hello string payload");
            var colBegin = sparks.Where(e => S(e, "name") == "Begin" && e.GetProperty("fields").GetProperty("markerId").GetInt32() == 3).ToList();
            Check(colBegin.Count == 3 && colBegin.All(e => e.GetProperty("fields").GetProperty("a").GetInt64() == 3 && e.GetProperty("fields").GetProperty("b").GetInt64() == 4), "real: column.pass payload a,b", colBegin.Count.ToString());
            Check(colBegin.Select(e => e.GetProperty("fields").GetProperty("c").GetInt64()).OrderBy(x => x).SequenceEqual(new long[] { 1, 2, 3 }), "real: column.pass passes 1..3");
            var gcm = sparks.Where(e => S(e, "name") == "Begin" && e.GetProperty("fields").GetProperty("s").GetString() == "gcmarker|client").ToList();
            var gce = sparks.Where(e => S(e, "name") == "End" && e.GetProperty("fields").GetProperty("s").GetString() == "gcmarker|client").ToList();
            Check(gcm.Count == 1 && gce.Count == 1, "real: gcmarker begin and end present");
            var gcs = events.Where(e => S(e, "src") == "gc").ToList();
            var gcStart = gcs.Where(e => S(e, "name") == "GCStart").ToList();
            Check(gcStart.Count >= 1 && gcs.Any(e => S(e, "name") == "GCEnd") && gcs.Any(e => S(e, "name") == "GCSuspendBegin") && gcs.Any(e => S(e, "name") == "GCRestartEnd"), "real: GC start, end, suspend, restart events");
            if (gcm.Count == 1 && gce.Count == 1 && gcStart.Count >= 1)
            {
                var inWin = gcStart.Where(e => T(e) > T(gcm[0]) && T(e) < T(gce[0])).ToList();
                Check(inWin.Count >= 1, "real: induced GC maps inside the marker window (time offset check)",
                    "marker " + T(gcm[0]).ToString(CultureInfo.InvariantCulture) + ".." + T(gce[0]).ToString(CultureInfo.InvariantCulture) + " gc " + T(gcStart[0]).ToString(CultureInfo.InvariantCulture));
                Check(inWin.Count >= 1 && inWin[0].GetProperty("fields").GetProperty("gen").GetInt32() == 2, "real: induced GC is gen 2");
            }
            var exc = events.Where(e => S(e, "src") == "exception" && e.GetProperty("fields").GetProperty("type").GetString() == "System.InvalidOperationException").ToList();
            var modB = sparks.Where(e => S(e, "name") == "Begin" && e.GetProperty("fields").GetProperty("s").GetString() == "testmod|server").ToList();
            var modE = sparks.Where(e => S(e, "name") == "End" && e.GetProperty("fields").GetProperty("s").GetString() == "testmod|server").ToList();
            Check(exc.Count >= 1 && modB.Count == 1 && modE.Count == 1 && T(exc[0]) > T(modB[0]) && T(exc[0]) < T(modE[0]), "real: exception falls inside its marker span");
            var loads = events.Where(e => S(e, "src") == "loader").ToList();
            Check(loads.Count >= 1 && loads.Any(e => (e.GetProperty("fields").GetProperty("assembly").GetString() ?? "").Contains("System.Private.Xml.Linq")), "real: loader events include System.Private.Xml.Linq");
            var ts = events.Select(T).ToList();
            Check(ts.SequenceEqual(ts.OrderBy(x => x)), "real: events.jsonl is time ordered");

            // Step 2 on the real trace.
            string md = File.ReadAllText(Path.Combine(dir, "loading.md"));
            Check(md.Contains("server.phase [NOT-ACTIVE]"), "real: mismatched patch flagged NOT-ACTIVE");
            Check(Gate(md, "G3").Contains("zero_hit_targets=server.phase(mismatch)"), "real: G3 names the inactive target", Gate(md, "G3"));
            Check(Gate(md, "G1") == "G1 g1_delta_ms=3.500 PASS", "real: G1", Gate(md, "G1"));
            Check(md.Contains("test-1.0") && md.Contains("hook: version"), "real: header shows hook version");
            NoDashes("real loading.md", Path.Combine(dir, "loading.md"));
            NoDashes("real events.jsonl", evPath);

            // Truncated traces: cut the file at several points. The tail of a nettrace is mostly rundown data, so a cut
            // late in the file may lose no SparkLoad events; every cut must still analyze with exit 0 and a truncation flag,
            // and the early cuts must lose events.
            var counts = new List<string>();
            bool anyFewer = false;
            foreach (double frac in new[] { 0.6, 0.4, 0.25, 0.15 })
            {
                string tdir = Dir("real-trunc-" + ((int)(frac * 100)).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(tdir);
                foreach (var f in Directory.GetFiles(tdir)) File.Delete(f);
                byte[] all = File.ReadAllBytes(nettrace);
                File.WriteAllBytes(Path.Combine(tdir, "raw.nettrace"), all.Take((int)(all.Length * frac)).ToArray());
                File.Copy(Path.Combine(dir, "run.json"), Path.Combine(tdir, "run.json"));
                var (rct, logt) = RunAnalyzer(tdir);
                string tag = "trunc" + ((int)(frac * 100)).ToString(CultureInfo.InvariantCulture) + ": ";
                Check(rct == 0, tag + "analyzer exit 0 on a truncated trace", logt);
                string tev = Path.Combine(tdir, "events.jsonl");
                Check(File.Exists(tev), tag + "events.jsonl written");
                int tn = File.Exists(tev) ? File.ReadAllLines(tev).Length : -1;
                Check(tn >= 0 && tn <= events.Count, tag + "not more events than the full trace", tn + " vs " + events.Count);
                if (tn >= 0 && tn < events.Count) anyFewer = true;
                string meta = File.Exists(Path.Combine(tdir, "events.meta.json")) ? File.ReadAllText(Path.Combine(tdir, "events.meta.json")) : "";
                Check(meta.Contains("\"truncated\": true"), tag + "events.meta.json records truncation", meta);
                string tmd = File.Exists(Path.Combine(tdir, "loading.md")) ? File.ReadAllText(Path.Combine(tdir, "loading.md")) : "";
                Check(tmd.Contains("TRUNCATED"), tag + "loading.md notes the truncation");
                Check(File.Exists(Path.Combine(tdir, "loading.csv")), tag + "loading.csv written");
                counts.Add(((int)(frac * 100)).ToString(CultureInfo.InvariantCulture) + "%=" + tn);
            }
            Check(anyFewer, "trunc: an early cut loses events", string.Join(" ", counts));
            Notes.Add("real-trace test RAN: full trace " + events.Count + " events, truncated cuts " + string.Join(" ", counts));
        }
    }
}
