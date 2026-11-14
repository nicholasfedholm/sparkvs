using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SparkLoad.Analyzer
{
    internal sealed class TargetRow
    {
        public string Marker = "", Target = "", Status = "", Detail = "", Flag = "";
        public long? Hits;
        public long Begins;
    }

    internal sealed class ServerPhaseRow
    {
        public long Value;
        public string Name = "";
        public double T0, T1;
        public string Source = "";
        public double Dur => Math.Max(0, T1 - T0);
    }

    internal sealed class GroupRow
    {
        public string Side = "", Phase = "", Sub = "";
        public int Spans;
        public double WallMs, SumMs;
    }

    internal sealed class ModRow
    {
        public string Side = "", Mod = "";
        public double TotalMs;
        public readonly SortedDictionary<long, double> ByPhase = new SortedDictionary<long, double>();
    }

    internal sealed class PassRow
    {
        public long Pass;
        public int Spans;
        public double SumMs, MaxMs;
        public int DistinctColumns;
    }

    internal sealed class ColumnReport
    {
        public bool Any;
        public int DistinctColumns, ColumnsDone;
        public long LastPass;
        public double WindowMs, ColumnsPerSec;
        public readonly List<PassRow> Passes = new List<PassRow>();
        public int LoadedFromDb, Generated, UnknownLoad;
        public double LoadDbMs, LoadGenMs, LoadUnkMs;
        public long LoadsKept, LoadsFolded;                 // column.load spans listed one by one, and only counted
        public long HookSuppressed;                         // calls the hook did not emit (Stats column.load.suppressed)
        public int HookSuppressedColumns;
    }

    internal sealed class Silence
    {
        public double T0, T1;
        public bool Pre;
        public bool HasSamples;
        public int TopTid;
        public string TopName = "";
        public double TopCpuMs, IoBytes, GcPauseMs;
        public string Verdict = "";
        public bool Named;
        public double Dur => T1 - T0;
    }

    internal sealed class CoverageResult
    {
        public bool Computed;
        public int Windows, ByMarker, ByCpu, ByIo, ByGc, Uncovered;
        public double UncoveredPct;
        public string Note = "";
    }

    internal sealed class G6Row
    {
        public long Value;
        public string Name = "";
        public double? MarkerT, LogT;
        public double? DeltaMs;
        public string Note = "";
        public bool Ok;
    }

    internal sealed class GateLine
    {
        public string Id = "";
        public string Text = "";
        public bool Pass;
        public string Status = "";
        public override string ToString() => Id + " " + Text + " " + Status;
    }

    /// <summary>All derived numbers for one run. Built once, rendered by Report.</summary>
    internal sealed class Result
    {
        public const double SilenceGapMs = 2000;
        public const double WindowMs = 250;
        public const double CpuShare = 0.25;
        public const double IoBytesWindow = 64 * 1024;
        public const int ExpectedColumns = 49;
        public const double G6LimitMs = 1100;
        public const double G1LimitMs = 50;
        public const double G2LimitPct = 5.0;

        public static readonly string[] Umbrella = { "client.boot.main", "server.launch" };

        public RunData D = null!;
        public double? FinalizeLogMs, FinalizeDoneLogMs, FinalizeMarkerMs, PlayableMs, ServerRunningMs, RefFinalizeMs;
        public int Lost;
        public readonly Dictionary<int, int> LostByTid = new Dictionary<int, int>();
        public List<TargetRow> Targets = new List<TargetRow>();
        public bool HasStats;
        public List<ServerPhaseRow> ServerPhases = new List<ServerPhaseRow>();
        public bool PhasesFromMarkers;
        public List<ServerPhaseRow> LogPhases = new List<ServerPhaseRow>();
        public List<GroupRow> Groups = new List<GroupRow>();
        public List<ModRow> ClientMods = new List<ModRow>();
        public List<ModRow> ServerMods = new List<ModRow>();
        public ColumnReport Columns = new ColumnReport();
        public List<Silence> Silences = new List<Silence>();
        public CoverageResult Coverage = new CoverageResult();
        public List<G6Row> G6 = new List<G6Row>();
        public List<GateLine> Gates = new List<GateLine>();

        // G11
        public int? MainTid;
        public string MainName = "";
        public double? IdleWindowStart, IdleWindowEnd, MainCpuMs, MainIdleMs;
        public double? PostGapMs;
        public int? FramesInGap;
        public bool? FramesRendered;
        public List<(string Name, double CpuMs)> GapThreads = new List<(string, double)>();
        public List<(string Marker, double WallMs)> GapMarkers = new List<(string, double)>();
        public double GapGcPauseMs;
        public double? BlocklistWaitMs;
        public bool BlocklistUpperBound;

        public double TotalRefMs => RefFinalizeMs ?? D.EndMs;

        // ---------- helpers ----------

        public static (string Side, string Phase, string Sub, string Mod) Classify(Span s, RunData d)
        {
            string n = s.Marker;
            switch (n)
            {
                case "server.phase": return ("server", "server.phase", RunPhases.Name(d.PhaseNames, s.A), "");
                case "mod.phase":
                    {
                        int bar = s.S.LastIndexOf('|');
                        string id = bar >= 0 ? s.S.Substring(0, bar) : s.S;
                        string side = bar >= 0 ? s.S.Substring(bar + 1) : "";
                        return (side, "mod.phase", "p" + s.A.ToString(CultureInfo.InvariantCulture), id);
                    }
                case "column.pass": return ("server", "column.pass", PassName(d, s.C), "");
                case "column.load": return ("server", "column.load", s.C == 1 ? "db" : s.C == 0 ? "generated" : "unknown", "");
                case "frame": return ("client", "frame", "", "");
            }
            var parts = n.Split('.');
            string sd = parts[0] == "client" || parts[0] == "server" ? parts[0] : "";
            if (parts.Length >= 3) return (sd, string.Join(".", parts.Take(parts.Length - 1)), parts[parts.Length - 1], "");
            return (sd, n, "", "");
        }

        public static string PassName(RunData d, long pass) =>
            d.PassNames.TryGetValue((int)pass, out var nm) ? nm + "(" + pass.ToString(CultureInfo.InvariantCulture) + ")" : pass.ToString(CultureInfo.InvariantCulture);

        public static double UnionMs(IEnumerable<(double A, double B)> intervals)
        {
            double total = 0, curA = 0, curB = 0;
            bool have = false;
            foreach (var (a, b) in intervals.OrderBy(x => x.A))
            {
                if (!have) { curA = a; curB = b; have = true; }
                else if (a <= curB) { if (b > curB) curB = b; }
                else { total += curB - curA; curA = a; curB = b; }
            }
            if (have) total += curB - curA;
            return total;
        }

        private LogLine? FindLog(string fileKey, string contains) =>
            D.Logs.FirstOrDefault(l => l.File.IndexOf(fileKey, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                       l.Text.IndexOf(contains, StringComparison.Ordinal) >= 0);

        public string ThreadName(int tid)
        {
            string n = D.Samples.NameOf(tid);
            if (n.Length == 0 && D.Ev.ThreadNames.TryGetValue(tid, out var m)) n = m;
            return n;
        }

        public double GcPauseIn(double a, double b)
        {
            double sum = 0;
            foreach (var p in D.Ev.Pauses)
            {
                double lo = Math.Max(a, p.T0), hi = Math.Min(b, p.T1);
                if (hi > lo) sum += hi - lo;
            }
            return sum;
        }

        // ---------- build ----------

        public static Result Build(RunData d)
        {
            var r = new Result { D = d };
            r.BuildTotals();
            r.BuildLost();
            r.BuildTargets();
            r.BuildServerPhases();
            r.BuildGroups();
            r.BuildMods();
            r.BuildColumns();
            r.BuildSilences();
            r.BuildCoverage();
            r.BuildG6();
            r.BuildG11();
            r.BuildGates();
            return r;
        }

        private void BuildTotals()
        {
            FinalizeLogMs = D.Info.FinalizeLogMs ?? FindLog("client-main", "Received level finalize")?.T;
            FinalizeDoneLogMs = FindLog("client-debug", "Done level finalize")?.T;
            var handle = D.Spans.Where(s => s.Marker == "client.finalize.handle" && !s.Open).ToList();
            if (handle.Count > 0) FinalizeMarkerMs = handle.Max(s => s.T1);
            else
            {
                var trig = D.Spans.Where(s => s.Marker == "client.finalize.trigger" && !s.Open).ToList();
                if (trig.Count > 0) FinalizeMarkerMs = trig.Max(s => s.T1);
            }
            if (D.Ev.Playables.Count > 0) PlayableMs = D.Ev.Playables.Min(p => p.T);
            ServerRunningMs = FindLog("server-main", "Singleplayer Server now running!")?.T;
            RefFinalizeMs = FinalizeLogMs ?? FinalizeMarkerMs;
        }

        private void BuildLost()
        {
            foreach (var t in D.Ev.Seqs.Totals())
            {
                if (t.Count == 0) continue;
                int lost = (int)(t.Max - t.Count);
                if (lost > 0) { LostByTid[t.Tid] = lost; Lost += lost; }
            }
        }

        private void BuildTargets()
        {
            var ev = D.Ev;
            HasStats = ev.Stats.Count > 0;
            var hits = new Dictionary<string, long>();
            foreach (var s in ev.Stats)
                hits[s.Marker] = hits.TryGetValue(s.Marker, out var h) ? Math.Max(h, s.Hits) : s.Hits;
            var begins = ev.Markers.Where(m => m.Begin).GroupBy(m => m.Marker).ToDictionary(g => g.Key, g => (long)g.Count());
            foreach (var fb in ev.FoldedBegins) begins[fb.Key] = (begins.TryGetValue(fb.Key, out var b0) ? b0 : 0) + fb.Value;
            var rows = new Dictionary<string, TargetRow>();
            foreach (var p in ev.Patches)
                rows[p.Marker] = new TargetRow { Marker = p.Marker, Target = p.Target, Status = p.Status, Detail = p.Detail };
            foreach (var k in hits.Keys)
                if (!rows.ContainsKey(k)) rows[k] = new TargetRow { Marker = k, Status = "n/a" };
            foreach (var row in rows.Values)
            {
                row.Begins = begins.TryGetValue(row.Marker, out var b) ? b : 0;
                if (hits.TryGetValue(row.Marker, out var h)) row.Hits = h;
                long eff = row.Hits ?? row.Begins;
                string st = row.Status.ToLowerInvariant();
                if (st == "ok" && eff == 0) row.Flag = "ZERO-HIT";
                else if (st == "missing" || st == "mismatch" || st == "error") row.Flag = "NOT-ACTIVE";
            }
            Targets = rows.Values.OrderBy(t => t.Marker, StringComparer.Ordinal).ToList();
        }

        private void BuildServerPhases()
        {
            // From markers: one Begin per EnterRunPhase call; a phase lasts until the next server.phase Begin.
            var sp = D.Spans.Where(s => s.Marker == "server.phase").OrderBy(s => s.T0).ToList();
            for (int i = 0; i < sp.Count; i++)
            {
                var row = new ServerPhaseRow { Value = sp[i].A, Name = RunPhases.Name(D.PhaseNames, sp[i].A), T0 = sp[i].T0, Source = "marker" };
                if (i + 1 < sp.Count) row.T1 = sp[i + 1].T0;
                else row.T1 = ServerRunningMs.HasValue && ServerRunningMs.Value >= row.T0 ? ServerRunningMs.Value : sp[i].T1;
                ServerPhases.Add(row);
            }
            PhasesFromMarkers = ServerPhases.Count > 0;

            // From logs.
            var rx = new Regex(@"Entering runphase\s+(\w+)", RegexOptions.CultureInvariant);
            var seen = new HashSet<string>();
            var names = D.PhaseNames.ToDictionary(kv => kv.Value, kv => (long)kv.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var l in D.Logs)
            {
                if (l.File.IndexOf("server-main", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var m = rx.Match(l.Text);
                if (!m.Success) continue;
                string nm = m.Groups[1].Value;
                if (!seen.Add(nm)) continue;
                LogPhases.Add(new ServerPhaseRow { Name = nm, Value = names.TryGetValue(nm, out var v) ? v : -1, T0 = l.T, Source = "log" });
            }
            for (int i = 0; i < LogPhases.Count; i++)
            {
                if (i + 1 < LogPhases.Count) LogPhases[i].T1 = LogPhases[i + 1].T0;
                else LogPhases[i].T1 = ServerRunningMs.HasValue && ServerRunningMs.Value >= LogPhases[i].T0 ? ServerRunningMs.Value : LogPhases[i].T0;
            }
            if (!PhasesFromMarkers) ServerPhases = LogPhases;
        }

        private void BuildGroups()
        {
            var skip = new HashSet<string> { "server.phase", "column.pass", "column.load", "frame" };
            var groups = new Dictionary<(string, string, string), List<Span>>();
            foreach (var s in D.Spans)
            {
                if (skip.Contains(s.Marker) || Array.IndexOf(Umbrella, s.Marker) >= 0) continue;
                var c = Classify(s, D);
                var key = (c.Side, c.Phase, c.Sub);
                if (!groups.TryGetValue(key, out var l)) { l = new List<Span>(); groups[key] = l; }
                l.Add(s);
            }
            foreach (var kv in groups)
                Groups.Add(new GroupRow
                {
                    Side = kv.Key.Item1, Phase = kv.Key.Item2, Sub = kv.Key.Item3, Spans = kv.Value.Count,
                    WallMs = UnionMs(kv.Value.Select(s => (s.T0, s.T1))), SumMs = kv.Value.Sum(s => s.Dur),
                });
            Groups = Groups.OrderByDescending(g => g.WallMs).ThenBy(g => g.Phase, StringComparer.Ordinal).ToList();
        }

        private void BuildMods()
        {
            var mods = new Dictionary<(string, string), ModRow>();
            foreach (var s in D.Spans.Where(x => x.Marker == "mod.phase"))
            {
                var c = Classify(s, D);
                var key = (c.Side, c.Mod);
                if (!mods.TryGetValue(key, out var row)) { row = new ModRow { Side = c.Side, Mod = c.Mod }; mods[key] = row; }
                row.TotalMs += s.Dur;
                row.ByPhase[s.A] = (row.ByPhase.TryGetValue(s.A, out var v) ? v : 0) + s.Dur;
            }
            ClientMods = mods.Values.Where(m => m.Side == "client").OrderByDescending(m => m.TotalMs).Take(15).ToList();
            ServerMods = mods.Values.Where(m => m.Side == "server").OrderByDescending(m => m.TotalMs).Take(15).ToList();
        }

        private void BuildColumns()
        {
            var cr = Columns;
            var pass = D.Spans.Where(s => s.Marker == "column.pass").ToList();
            var load = D.Spans.Where(s => s.Marker == "column.load").ToList();
            var fold = D.Ev.Fold;
            cr.Any = pass.Count > 0 || load.Count > 0 || fold.Total > 0;
            cr.LoadsKept = load.Count;
            cr.LoadsFolded = fold.Total;
            cr.LoadedFromDb += (int)fold.Db; cr.LoadDbMs += fold.DbMs;
            cr.Generated += (int)fold.Gen; cr.LoadGenMs += fold.GenMs;
            cr.UnknownLoad += (int)fold.Unk; cr.LoadUnkMs += fold.UnkMs;
            cr.HookSuppressedColumns = D.Ev.SuppressedByColumn.Count;
            foreach (var sv in D.Ev.SuppressedByColumn.Values) cr.HookSuppressed += sv;
            if (pass.Count > 0)
            {
                cr.DistinctColumns = pass.Select(s => (s.A, s.B)).Distinct().Count();
                var closed = pass.Where(s => !s.Open).ToList();
                if (closed.Count > 0)
                {
                    cr.LastPass = closed.Max(s => s.C);
                    cr.ColumnsDone = closed.Where(s => s.C == cr.LastPass).Select(s => (s.A, s.B)).Distinct().Count();
                }
                foreach (var g in pass.GroupBy(s => s.C).OrderBy(g => g.Key))
                    cr.Passes.Add(new PassRow
                    {
                        Pass = g.Key, Spans = g.Count(), SumMs = g.Sum(s => s.Dur), MaxMs = g.Max(s => s.Dur),
                        DistinctColumns = g.Select(s => (s.A, s.B)).Distinct().Count(),
                    });
                cr.WindowMs = pass.Max(s => s.T1) - pass.Min(s => s.T0);
                cr.ColumnsPerSec = cr.WindowMs > 0 ? cr.ColumnsDone / (cr.WindowMs / 1000.0) : 0;
            }
            foreach (var s in load)
            {
                if (s.C == 1) { cr.LoadedFromDb++; cr.LoadDbMs += s.Dur; }
                else if (s.C == 0) { cr.Generated++; cr.LoadGenMs += s.Dur; }
                else { cr.UnknownLoad++; cr.LoadUnkMs += s.Dur; }
            }
        }

        private void BuildSilences()
        {
            var ev = D.Ev;
            var sig = new List<double> { 0 };
            sig.AddRange(ev.MarkerSignal);
            sig.AddRange(ev.Frames.Select(f => f.T));
            sig.AddRange(ev.FrameAggs.Select(f => f.T));
            sig.AddRange(ev.Playables.Select(f => f.T));
            sig.AddRange(D.Logs.Select(l => l.T));
            sig.Sort();
            double? fin = RefFinalizeMs;
            for (int i = 1; i < sig.Count; i++)
            {
                double gap = sig[i] - sig[i - 1];
                if (gap <= SilenceGapMs) continue;
                var s = new Silence { T0 = sig[i - 1], T1 = sig[i], Pre = !fin.HasValue || sig[i - 1] < fin.Value, HasSamples = D.Samples.Present };
                Attribute(s);
                Silences.Add(s);
            }
        }

        private void Attribute(Silence s)
        {
            if (!s.HasSamples) { s.Verdict = "n/a (no samples)"; return; }
            double best = -1;
            int bestTid = 0;
            foreach (var kv in D.Samples.Cpu)
            {
                double dlt = kv.Value.Delta(s.T0, s.T1);
                if (dlt > best) { best = dlt; bestTid = kv.Key; }
            }
            s.TopTid = bestTid;
            s.TopCpuMs = Math.Max(0, best);
            s.TopName = best >= 0 ? ThreadName(bestTid) : "";
            s.IoBytes = D.Samples.IoDelta(s.T0, s.T1);
            s.GcPauseMs = GcPauseIn(s.T0, s.T1);
            double dur = s.Dur;
            var parts = new List<string>();
            bool cpuHit = best > 0 && s.TopCpuMs >= CpuShare * dur;
            if (cpuHit)
            {
                if (s.TopName.Length > 0) { parts.Add("cpu thread " + s.TopName); s.Named = true; }
                else parts.Add("cpu unnamed thread " + s.TopTid.ToString(CultureInfo.InvariantCulture));
            }
            if (s.IoBytes >= IoBytesWindow) parts.Add("io");
            if (s.GcPauseMs >= CpuShare * dur) parts.Add("gc");
            s.Verdict = parts.Count > 0 ? string.Join(" + ", parts) : "idle (no cpu, io or gc)";
        }

        private void BuildCoverage()
        {
            var c = Coverage;
            double? fin = RefFinalizeMs;
            if (!D.Ev.HasMarkers || !D.Samples.Present || !fin.HasValue || fin.Value < WindowMs)
            {
                c.Computed = false;
                c.Note = !D.Ev.HasMarkers ? "no markers in this run" : !D.Samples.Present ? "no samples.csv" : "finalize time unknown";
                return;
            }
            int n = (int)Math.Floor(fin.Value / WindowMs);
            c.Windows = n;
            var byMarker = new bool[n];
            foreach (var s in D.Spans)
            {
                if (Array.IndexOf(Umbrella, s.Marker) >= 0) continue;
                int a = (int)Math.Floor(s.T0 / WindowMs);
                int b = (int)Math.Floor(Math.Max(s.T0, s.T1 - 1e-6) / WindowMs);
                for (int w = Math.Max(0, a); w <= Math.Min(n - 1, b); w++) byMarker[w] = true;
            }
            var fw = D.Ev.Fold.Windows;
            for (int w = 0; w < n && w < fw.Length; w++) if (fw[w]) byMarker[w] = true;
            for (int w = 0; w < n; w++)
            {
                double w0 = w * WindowMs, w1 = w0 + WindowMs;
                bool m = byMarker[w], cpu = false, io = false, gc = false;
                if (!m)
                {
                    foreach (var s in D.Samples.Cpu.Values)
                        if (s.Delta(w0, w1) >= CpuShare * WindowMs) { cpu = true; break; }
                    if (!cpu) io = D.Samples.IoDelta(w0, w1) >= IoBytesWindow;
                    if (!cpu && !io) gc = GcPauseIn(w0, w1) >= CpuShare * WindowMs;
                }
                if (m) c.ByMarker++;
                else if (cpu) c.ByCpu++;
                else if (io) c.ByIo++;
                else if (gc) c.ByGc++;
                else c.Uncovered++;
            }
            c.UncoveredPct = n > 0 ? 100.0 * c.Uncovered / n : 0;
            c.Computed = true;
        }

        private void BuildG6()
        {
            var markers = D.Spans.Where(s => s.Marker == "server.phase").OrderBy(s => s.T0).ToList();
            var logPhases = LogPhases;
            var usedLog = new HashSet<int>();
            foreach (var m in markers)
            {
                string name = RunPhases.Name(D.PhaseNames, m.A);
                var row = new G6Row { Value = m.A, Name = name, MarkerT = m.T0 };
                if (m.A == 0) { row.Note = "no log line expected"; row.Ok = true; G6.Add(row); continue; }
                int idx = logPhases.FindIndex(l => !usedLog.Contains(logPhases.IndexOf(l)) && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) { row.Note = "no log line"; G6.Add(row); continue; }
                usedLog.Add(idx);
                row.LogT = logPhases[idx].T0;
                row.DeltaMs = m.T0 - logPhases[idx].T0;
                row.Ok = Math.Abs(row.DeltaMs.Value) <= G6LimitMs;
                G6.Add(row);
            }
            for (int i = 0; i < logPhases.Count; i++)
            {
                if (usedLog.Contains(i)) continue;
                if (markers.Count == 0) continue; // passive run: nothing to compare against
                G6.Add(new G6Row { Value = logPhases[i].Value, Name = logPhases[i].Name, LogT = logPhases[i].T0, Note = "no marker", Ok = false });
            }
        }

        private void BuildG11()
        {
            var main = D.Spans.Where(s => s.Marker == "client.boot.main").OrderBy(s => s.T0).FirstOrDefault();
            if (main != null) { MainTid = main.Tid; MainName = ThreadName(main.Tid); }
            var launch = D.Spans.Where(s => s.Marker == "server.launch").OrderBy(s => s.T0).FirstOrDefault();
            var sphases = D.Spans.Where(s => s.Marker == "server.phase").OrderBy(s => s.T0).ToList();
            double? start = launch?.T0 ?? (sphases.Count > 0 ? sphases[0].T0 : (double?)null);
            var run = sphases.FirstOrDefault(s => s.A == RunPhases.RunGameValue);
            double? end = run?.T0;
            if (start.HasValue && end.HasValue && end.Value > start.Value)
            {
                IdleWindowStart = start; IdleWindowEnd = end;
                if (MainTid.HasValue && D.Samples.Present && D.Samples.Cpu.TryGetValue(MainTid.Value, out var ser))
                {
                    MainCpuMs = ser.Delta(start.Value, end.Value);
                    MainIdleMs = (end.Value - start.Value) - MainCpuMs;
                }
            }

            // Post-finalize gap.
            double? fin = RefFinalizeMs;
            if (fin.HasValue && PlayableMs.HasValue && PlayableMs.Value >= fin.Value)
            {
                double a = fin.Value, b = PlayableMs.Value;
                PostGapMs = b - a;
                int frames = D.Ev.Frames.Count(f => f.T >= a && f.T <= b);
                frames += (int)D.Ev.FrameAggs.Where(f => f.T >= a && f.T <= b).Sum(f => f.Count);
                FramesInGap = frames;
                FramesRendered = frames > 0;
                if (D.Samples.Present)
                    GapThreads = D.Samples.Cpu.Select(kv => (Name: DisplayName(kv.Key), Cpu: kv.Value.Delta(a, b)))
                        .Where(x => x.Cpu > 0).OrderByDescending(x => x.Cpu).Take(5).ToList();
                GapMarkers = D.Spans.Where(s => s.Marker != "frame" && Array.IndexOf(Umbrella, s.Marker) < 0 && s.T1 > a && s.T0 < b)
                    .GroupBy(s => s.Marker)
                    .Select(g => (g.Key, UnionMs(g.Select(s => (Math.Max(a, s.T0), Math.Min(b, s.T1))))))
                    .OrderByDescending(x => x.Item2).Take(8).ToList();
                GapGcPauseMs = GcPauseIn(a, b);
            }

            // Block-list wait.
            var waits = D.Spans.Where(s => s.Marker == "server.blocklist.wait").OrderBy(s => s.T0).ToList();
            var sets = D.Spans.Where(s => s.Marker == "server.blocklist.set" && !s.Open).OrderBy(s => s.T1).ToList();
            if (waits.Count > 0)
            {
                double sum = 0;
                bool upper = false;
                foreach (var w in waits)
                {
                    var set = sets.FirstOrDefault(s => s.T1 >= w.T0);
                    if (set != null) sum += Math.Max(0, set.T1 - w.T0);
                    else { sum += w.Dur; upper = true; }
                }
                BlocklistWaitMs = sum;
                BlocklistUpperBound = upper;
            }
            else if (D.Ev.Stats.Any(s => s.Marker == "server.blocklist.wait") && D.Ev.Stats.Where(s => s.Marker == "server.blocklist.wait").Max(s => s.Hits) == 0)
            {
                BlocklistWaitMs = 0;
            }
        }

        private string DisplayName(int tid)
        {
            string n = ThreadName(tid);
            return n.Length > 0 ? n + " (tid " + tid.ToString(CultureInfo.InvariantCulture) + ")" : "tid " + tid.ToString(CultureInfo.InvariantCulture);
        }

        private void BuildGates()
        {
            // G1
            var g1 = new GateLine { Id = "G1" };
            if (D.Info.G1DeltaMs.HasValue)
            {
                g1.Text = "g1_delta_ms=" + Txt.F3(D.Info.G1DeltaMs.Value);
                g1.Pass = Math.Abs(D.Info.G1DeltaMs.Value) <= G1LimitMs;
            }
            else { g1.Text = "g1_delta_ms=n/a"; g1.Pass = false; }
            g1.Status = g1.Pass ? "PASS" : "FAIL";
            Gates.Add(g1);

            // G2
            var g2 = new GateLine { Id = "G2" };
            var pre = Silences.Where(s => s.Pre).ToList();
            string sil = D.LogsPresent || D.Ev.HasMarkers ? Txt.I(pre.Count) : "n/a";
            string unnamed = D.Samples.Present && D.Ev.HasMarkers ? Txt.I(pre.Count(s => !s.Named)) : "n/a";
            string unc = Coverage.Computed ? Txt.F2(Coverage.UncoveredPct) : "n/a";
            g2.Text = "uncovered_pct=" + unc + " silences_over_2s=" + sil + " unnamed=" + unnamed;
            g2.Pass = Coverage.Computed && Coverage.UncoveredPct <= G2LimitPct && unnamed == "0";
            g2.Status = g2.Pass ? "PASS" : "FAIL";
            Gates.Add(g2);

            // G3
            var g3 = new GateLine { Id = "G3" };
            if (D.Ev.HasMarkers)
            {
                var bad = Targets.Where(t => t.Flag.Length > 0).Select(t => t.Flag == "ZERO-HIT" ? t.Marker : t.Marker + "(" + t.Status + ")").ToList();
                string zl = bad.Count == 0 ? "none" : string.Join(",", bad);
                g3.Text = "zero_hit_targets=" + zl + " lost=" + Txt.I(Lost) + " columns_done=" + Txt.I(Columns.ColumnsDone);
                g3.Pass = bad.Count == 0 && Lost == 0 && Columns.ColumnsDone >= ExpectedColumns;
            }
            else { g3.Text = "zero_hit_targets=n/a lost=n/a columns_done=n/a"; g3.Pass = false; }
            g3.Status = g3.Pass ? "PASS" : "FAIL";
            Gates.Add(g3);

            // G6
            var g6 = new GateLine { Id = "G6" };
            var matched = G6.Where(x => x.DeltaMs.HasValue).ToList();
            if (D.Spans.Any(s => s.Marker == "server.phase") && D.LogsPresent)
            {
                double mx = matched.Count > 0 ? matched.Max(x => Math.Abs(x.DeltaMs!.Value)) : 0;
                g6.Text = "max_delta_ms=" + (matched.Count > 0 ? Txt.F1(mx) : "n/a") + " phases=" + Txt.I(matched.Count);
                g6.Pass = matched.Count > 0 && G6.All(x => x.Ok);
            }
            else { g6.Text = "max_delta_ms=n/a phases=0"; g6.Pass = false; }
            g6.Status = g6.Pass ? "PASS" : "FAIL";
            Gates.Add(g6);

            // G11
            var g11 = new GateLine { Id = "G11" };
            string idle = MainIdleMs.HasValue ? Txt.F2(MainIdleMs.Value / 1000.0) : "n/a";
            string gap = PostGapMs.HasValue ? Txt.F2(PostGapMs.Value / 1000.0) : "n/a";
            string fr = FramesInGap.HasValue ? Txt.I(FramesInGap.Value) : "n/a";
            string bl = BlocklistWaitMs.HasValue ? Txt.F2(BlocklistWaitMs.Value / 1000.0) : "n/a";
            g11.Text = "main_idle_s=" + idle + " postfinalize_gap_s=" + gap + " frames_in_gap=" + fr + " blocklist_wait_s=" + bl;
            g11.Pass = MainIdleMs.HasValue && PostGapMs.HasValue && FramesInGap.HasValue && BlocklistWaitMs.HasValue;
            g11.Status = g11.Pass ? "ANSWERED" : "MISSING";
            Gates.Add(g11);
        }

        // ---------- comparison metrics ----------

        /// <summary>Named durations (ms) used for the moved-bottleneck diff. Totals and answers are kept apart.</summary>
        public Dictionary<string, double> PhaseMetrics()
        {
            var m = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in ServerPhases)
                m["server.phase " + p.Name] = (m.TryGetValue("server.phase " + p.Name, out var v) ? v : 0) + p.Dur;
            foreach (var g in Groups)
                m[(g.Side.Length > 0 && !g.Phase.StartsWith(g.Side, StringComparison.Ordinal) ? g.Side + " " : "") + g.Phase + (g.Sub.Length > 0 ? "/" + g.Sub : "")] = g.WallMs;
            if (FinalizeLogMs.HasValue && FinalizeDoneLogMs.HasValue && FinalizeDoneLogMs.Value >= FinalizeLogMs.Value)
                m["client level finalize (log)"] = FinalizeDoneLogMs.Value - FinalizeLogMs.Value;
            return m;
        }
    }
}
