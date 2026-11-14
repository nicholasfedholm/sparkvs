using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SparkLoad.Analyzer
{
    /// <summary>Renders loading.md and loading.csv from a Result.</summary>
    internal static class Report
    {
        public static readonly string[] CsvColumns =
        {
            "run_id", "arm", "t_start_ms", "t_end_ms", "dur_ms", "side", "thread", "phase", "subphase", "mod", "x", "z", "pass", "count", "bytes", "source", "lost"
        };

        // ---------- markdown ----------

        public static string BuildMarkdown(Result r, Result? cmp)
        {
            var d = r.D;
            var sb = new StringBuilder();
            if (d.Info.TimingVoid)
                sb.AppendLine("TIMING-VOID: this run used deep mode (sample profiler, stack walks, rundown); its timings must not be compared with default or passive runs.").AppendLine();
            sb.AppendLine("# SparkLoad loading report").AppendLine();

            Run(sb, r);
            Targets(sb, r);
            Phases(sb, r);
            Mods(sb, r);
            Columns(sb, r);
            Silences(sb, r);
            Gc(sb, r);
            Coverage(sb, r);
            LogCheck(sb, r);
            Answers(sb, r);
            if (cmp != null) Compare(sb, r, cmp);

            sb.AppendLine("## Gates").AppendLine();
            foreach (var g in r.Gates) sb.AppendLine(g.ToString());
            return Txt.Clean(sb.ToString());
        }

        private static void Table(StringBuilder sb, string[] headers, IEnumerable<string[]> rows)
        {
            sb.AppendLine("| " + string.Join(" | ", headers) + " |");
            sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
            foreach (var row in rows) sb.AppendLine("| " + string.Join(" | ", row.Select(Txt.Md)) + " |");
            sb.AppendLine();
        }

        private static string S(double? ms) => ms.HasValue ? Txt.Sec(ms.Value) : Txt.Na;

        private static void Run(StringBuilder sb, Result r)
        {
            var d = r.D;
            var i = d.Info;
            sb.AppendLine("## Run").AppendLine();
            sb.AppendLine("- run_id: " + i.RunId);
            sb.AppendLine("- arm: " + (i.Arm.Length > 0 ? i.Arm : Txt.Na));
            sb.AppendLine("- mode: " + (i.Mode.Length > 0 ? i.Mode : Txt.Na) + (i.TimingVoid ? " (timing void)" : ""));
            sb.AppendLine("- launcher_version: " + (i.LauncherVersion.Length > 0 ? i.LauncherVersion : Txt.Na));
            sb.AppendLine("- exe: " + (i.Exe.Length > 0 ? i.Exe : Txt.Na));
            sb.AppendLine("- present_mode: " + (i.PresentMode.Length > 0 ? i.PresentMode : Txt.Na));
            sb.AppendLine("- exit_reason: " + (i.ExitReason.Length > 0 ? i.ExitReason : Txt.Na));
            sb.AppendLine("- run length: " + Txt.Sec(d.EndMs) + " s");
            sb.AppendLine("- inputs: events " + (d.Ev.HasMarkers ? Txt.I(d.Ev.Count) + " (" + d.Ev.MarkerEventCount + " marker events)" : d.Ev.FilePresent ? Txt.I(d.Ev.Count) + " (no hook markers)" : "n/a")
                          + ", samples " + (d.Samples.Present ? Txt.I(d.Samples.Rows) + " rows" : "n/a")
                          + ", log lines " + (d.LogsPresent ? Txt.I(d.Logs.Count) : "n/a"));
            if (d.Ev.HasHello) sb.AppendLine("- hook: version " + d.Ev.HookVersion + ", runtime " + d.Ev.Runtime + ", pid " + d.Ev.HelloPid);
            if (d.Ev.Truncated) sb.AppendLine("- trace: TRUNCATED, kept what was read. " + d.Ev.Note);
            else if (d.Ev.Note.Length > 0) sb.AppendLine("- trace note: " + d.Ev.Note);
            sb.AppendLine();
            sb.AppendLine("Totals").AppendLine();
            Table(sb, new[] { "metric", "value" }, new[]
            {
                new[] { "launch to finalize, by log (s)", S(r.FinalizeLogMs) },
                new[] { "launch to finalize, by marker (s)", S(r.FinalizeMarkerMs) },
                new[] { "launch to playable (s)", S(r.PlayableMs) },
                new[] { "g1_delta_ms", i.G1DeltaMs.HasValue ? Txt.F3(i.G1DeltaMs.Value) : Txt.Na },
                new[] { "markers lost", d.Ev.HasMarkers ? Txt.I(r.Lost) + (r.Lost > 0 ? " (tids " + string.Join(", ", r.LostByTid.Select(kv => kv.Key + ":" + kv.Value)) + ")" : "") : Txt.Na },
                new[] { "client level finalize received (log, s)", S(r.FinalizeLogMs) },
                new[] { "client level finalize done (log, s)", S(r.FinalizeDoneLogMs) },
                new[] { "server running (log, s)", S(r.ServerRunningMs) },
            });
        }

        private static void Targets(StringBuilder sb, Result r)
        {
            sb.AppendLine("## Hook targets").AppendLine();
            if (r.Targets.Count == 0)
            {
                sb.AppendLine("n/a (no Patch or Stats events in this run)").AppendLine();
                return;
            }
            Table(sb, new[] { "marker", "target", "status", "detail", "hits", "begins seen", "flag" },
                r.Targets.Select(t => new[]
                {
                    t.Marker, t.Target, t.Status, t.Detail, t.Hits.HasValue ? Txt.I(t.Hits.Value) : Txt.Na, Txt.I(t.Begins), t.Flag,
                }));
            var zero = r.Targets.Where(t => t.Flag.Length > 0).ToList();
            sb.AppendLine(zero.Count == 0 ? "Zero-hit or inactive targets: none." : "Zero-hit or inactive targets: " + string.Join(", ", zero.Select(t => t.Marker + " [" + t.Flag + "]")) + ".").AppendLine();
        }

        private static void Phases(StringBuilder sb, Result r)
        {
            sb.AppendLine("## Phases").AppendLine();
            sb.AppendLine("### Server run phases").AppendLine();
            if (r.ServerPhases.Count == 0) sb.AppendLine("n/a (no server run phase markers or log lines)").AppendLine();
            else
            {
                double total = r.TotalRefMs;
                Table(sb, new[] { "value", "phase", "start s", "end s", "seconds", "share", "source" },
                    r.ServerPhases.Select(p => new[]
                    {
                        p.Value >= 0 ? Txt.I(p.Value) : "?", p.Name, Txt.Sec(p.T0), Txt.Sec(p.T1), Txt.Sec(p.Dur),
                        total > 0 ? Txt.Pct(100.0 * p.Dur / total) : Txt.Na, p.Source,
                    }));
                sb.AppendLine("Share is of launch to finalize (" + Txt.Sec(total) + " s). A phase lasts from its start to the next phase start.").AppendLine();
            }
            sb.AppendLine("### Phase groups (marker spans)").AppendLine();
            if (r.Groups.Count == 0) sb.AppendLine("n/a (no marker spans in this run)").AppendLine();
            else
            {
                double total = r.TotalRefMs;
                Table(sb, new[] { "side", "phase", "subphase", "spans", "wall s", "sum s", "share" },
                    r.Groups.Select(g => new[]
                    {
                        g.Side, g.Phase, g.Sub, Txt.I(g.Spans), Txt.Sec(g.WallMs), Txt.Sec(g.SumMs),
                        total > 0 ? Txt.Pct(100.0 * g.WallMs / total) : Txt.Na,
                    }));
                sb.AppendLine("Wall is the union of the group's spans, sum adds them per thread. Shares can overlap. Umbrella spans (client.boot.main, server.launch), column spans and frames are listed elsewhere.").AppendLine();
            }
        }

        private static void Mods(StringBuilder sb, Result r)
        {
            sb.AppendLine("## Top mods by phase time").AppendLine();
            foreach (var (title, list) in new[] { ("Client", r.ClientMods), ("Server", r.ServerMods) })
            {
                sb.AppendLine("### " + title).AppendLine();
                if (list.Count == 0) { sb.AppendLine("n/a (no mod.phase markers for this side)").AppendLine(); continue; }
                int rank = 0;
                Table(sb, new[] { "rank", "mod", "total s", "by phase (phase value = seconds)" },
                    list.Select(m => new[]
                    {
                        Txt.I(++rank), m.Mod, Txt.Sec(m.TotalMs),
                        string.Join("; ", m.ByPhase.Select(kv => "p" + kv.Key.ToString(CultureInfo.InvariantCulture) + "=" + Txt.Sec(kv.Value))),
                    }));
            }
        }

        private static void Columns(StringBuilder sb, Result r)
        {
            var c = r.Columns;
            sb.AppendLine("## Columns").AppendLine();
            if (!c.Any) { sb.AppendLine("n/a (no column.pass or column.load markers in this run)").AppendLine(); return; }
            sb.AppendLine("- distinct columns seen in passes: " + c.DistinctColumns);
            sb.AppendLine("- last pass seen: " + (c.Passes.Count > 0 ? Result.PassName(r.D, c.LastPass) : Txt.Na));
            sb.AppendLine("- columns reaching the last pass: " + c.ColumnsDone);
            sb.AppendLine("- pass window: " + Txt.Sec(c.WindowMs) + " s, columns per second: " + Txt.F2(c.ColumnsPerSec));
            sb.AppendLine("- columns generated: " + c.Generated + ", loaded from db: " + c.LoadedFromDb + ", unknown: " + c.UnknownLoad);
            if (c.LoadsFolded > 0 || c.HookSuppressed > 0)
                sb.AppendLine("- column.load detail: " + c.LoadsKept + " spans listed one by one (the first " + LoadFold.KeepFirst + " per column and every span of "
                    + LoadFold.SlowMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms or more), " + c.LoadsFolded + " only counted in the totals above; the hook itself left out "
                    + c.HookSuppressed + " calls over " + c.HookSuppressedColumns + " columns (Stats column.load.suppressed). Gap figures for column.load use the listed spans only.");
            sb.AppendLine();
            if (c.Passes.Count > 0)
                Table(sb, new[] { "pass", "spans", "columns", "total ms", "mean ms", "max ms" },
                    c.Passes.Select(p => new[]
                    {
                        Result.PassName(r.D, p.Pass), Txt.I(p.Spans), Txt.I(p.DistinctColumns), Txt.F1(p.SumMs), Txt.F2(p.Spans > 0 ? p.SumMs / p.Spans : 0), Txt.F1(p.MaxMs),
                    }));
            Table(sb, new[] { "column.load", "columns", "total ms", "mean ms" }, new[]
            {
                new[] { "generated", Txt.I(c.Generated), Txt.F1(c.LoadGenMs), Txt.F2(c.Generated > 0 ? c.LoadGenMs / c.Generated : 0) },
                new[] { "loaded from db", Txt.I(c.LoadedFromDb), Txt.F1(c.LoadDbMs), Txt.F2(c.LoadedFromDb > 0 ? c.LoadDbMs / c.LoadedFromDb : 0) },
                new[] { "unknown", Txt.I(c.UnknownLoad), Txt.F1(c.LoadUnkMs), Txt.F2(c.UnknownLoad > 0 ? c.LoadUnkMs / c.UnknownLoad : 0) },
            });
        }

        private static void Silences(StringBuilder sb, Result r)
        {
            sb.AppendLine("## Silences").AppendLine();
            sb.AppendLine("Every gap over 2 s between consecutive signals (hook marker events, frame events, tailed log lines; process start counts as a signal).").AppendLine();
            if (r.Silences.Count == 0) { sb.AppendLine("none").AppendLine(); return; }
            Table(sb, new[] { "start s", "end s", "gap s", "when", "top thread", "cpu s", "process io bytes", "gc pause ms", "verdict" },
                r.Silences.Select(s => new[]
                {
                    Txt.Sec(s.T0), Txt.Sec(s.T1), Txt.Sec(s.Dur), s.Pre ? "before finalize" : "after finalize",
                    s.HasSamples ? (s.TopName.Length > 0 ? s.TopName : "(unnamed)") + " tid " + s.TopTid : Txt.Na,
                    s.HasSamples ? Txt.Sec(s.TopCpuMs) : Txt.Na,
                    s.HasSamples ? Txt.F0(s.IoBytes) : Txt.Na,
                    s.HasSamples && r.D.Ev.FilePresent ? Txt.F1(s.GcPauseMs) : Txt.Na,
                    s.Verdict,
                }));
        }

        private static void Gc(StringBuilder sb, Result r)
        {
            var ev = r.D.Ev;
            sb.AppendLine("## GC").AppendLine();
            if (!ev.FilePresent) { sb.AppendLine("n/a (no trace in this run)").AppendLine(); return; }
            double totalPause = ev.Pauses.Sum(p => p.Dur);
            sb.AppendLine("- GCs: " + ev.Gcs.Count + " (gen0 " + ev.Gcs.Count(g => g.Gen == 0) + ", gen1 " + ev.Gcs.Count(g => g.Gen == 1) + ", gen2 " + ev.Gcs.Count(g => g.Gen >= 2) + ")");
            sb.AppendLine("- suspension pauses: " + ev.Pauses.Count + ", total " + Txt.F1(totalPause) + " ms, max " + Txt.F1(ev.Pauses.Count > 0 ? ev.Pauses.Max(p => p.Dur) : 0) + " ms");
            sb.AppendLine("- assembly loads: " + ev.AssemblyLoads + ", exceptions thrown: " + ev.ExceptionCount);
            if (ev.ExceptionCount > 0)
                sb.AppendLine("- top exception types: " + string.Join(", ", ev.ExceptionTypes.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key + " x" + kv.Value)));
            sb.AppendLine();
            if (ev.Pauses.Count > 0)
            {
                Table(sb, new[] { "at s", "pause ms", "gen", "reason" },
                    ev.Pauses.OrderByDescending(p => p.Dur).Take(10).OrderBy(p => p.T0).Select(p => new[]
                    {
                        Txt.Sec(p.T0), Txt.F1(p.Dur), p.Gen >= 0 ? Txt.I(p.Gen) : Txt.Na, p.Reason,
                    }));
                sb.AppendLine("Top 10 pauses by length, in time order.").AppendLine();
            }
        }

        private static void Coverage(StringBuilder sb, Result r)
        {
            var c = r.Coverage;
            sb.AppendLine("## Coverage").AppendLine();
            if (!c.Computed) { sb.AppendLine("n/a (" + c.Note + ")").AppendLine(); return; }
            sb.AppendLine("Coverage (G2): " + c.Windows + " windows of 250 ms before finalize; covered by an open marker span " + c.ByMarker +
                          ", by cpu (top thread at least 25 percent of the window) " + c.ByCpu + ", by io (at least 64 KB) " + c.ByIo +
                          ", by gc pause (at least 25 percent) " + c.ByGc + "; uncovered " + c.Uncovered + " (" + Txt.F2(c.UncoveredPct) + " percent).").AppendLine();
            sb.AppendLine("Umbrella spans (client.boot.main, server.launch) do not count as marker coverage. Limit: " + Txt.F1(Result.G2LimitPct) + " percent uncovered.").AppendLine();
        }

        private static void LogCheck(StringBuilder sb, Result r)
        {
            sb.AppendLine("## Log cross-check").AppendLine();
            if (r.G6.Count == 0)
            {
                sb.AppendLine("n/a (no server.phase markers to compare with the tailed log)").AppendLine();
                var gl6 = r.Gates.First(g => g.Id == "G6");
                sb.AppendLine("G6 " + gl6.Status).AppendLine();
                return;
            }
            Table(sb, new[] { "value", "phase", "marker s", "log s", "delta ms", "ok", "note" },
                r.G6.Select(x => new[]
                {
                    Txt.I(x.Value), x.Name, S(x.MarkerT), S(x.LogT), x.DeltaMs.HasValue ? Txt.F1(x.DeltaMs.Value) : Txt.Na, x.Ok ? "yes" : "NO", x.Note,
                }));
            var g6 = r.Gates.First(g => g.Id == "G6");
            sb.AppendLine("G6 " + g6.Status + " (limit " + Txt.F0(Result.G6LimitMs) + " ms, " + g6.Text + ")").AppendLine();
        }

        private static void Answers(StringBuilder sb, Result r)
        {
            sb.AppendLine("## Answers").AppendLine();
            // 1
            sb.AppendLine("1. Client main thread idle while the server launches");
            if (r.MainIdleMs.HasValue)
                sb.AppendLine("   - " + Txt.Sec(r.MainIdleMs.Value) + " s idle in the window " + Txt.Sec(r.IdleWindowStart) + " s to " + Txt.Sec(r.IdleWindowEnd) +
                              " s (" + Txt.Sec(r.IdleWindowEnd!.Value - r.IdleWindowStart!.Value) + " s), main thread tid " + r.MainTid + (r.MainName.Length > 0 ? " (" + r.MainName + ")" : "") +
                              ", cpu " + Txt.Sec(r.MainCpuMs) + " s.");
            else
                sb.AppendLine("   - n/a (" + (r.MainTid == null ? "no client.boot.main marker" : !r.D.Samples.Present ? "no samples" : r.IdleWindowStart == null ? "no server launch window" : "main thread has no samples") + ")");
            // 2
            sb.AppendLine("2. Between finalize and playable");
            if (r.PostGapMs.HasValue)
            {
                sb.AppendLine("   - gap " + Txt.Sec(r.PostGapMs.Value) + " s, frames rendered: " + (r.FramesRendered == true ? "yes" : "no") + " (" + r.FramesInGap + " frames in the gap), gc pause " + Txt.F1(r.GapGcPauseMs) + " ms.");
                if (r.GapThreads.Count > 0)
                    sb.AppendLine("   - top cpu threads: " + string.Join(", ", r.GapThreads.Select(t => t.Name + " " + Txt.Sec(t.CpuMs) + " s")));
                if (r.GapMarkers.Count > 0)
                    sb.AppendLine("   - marker spans open in the gap: " + string.Join(", ", r.GapMarkers.Select(m => m.Marker + " " + Txt.Sec(m.WallMs) + " s")));
                var inGap = r.Silences.Where(s => !s.Pre).ToList();
                if (inGap.Count > 0) sb.AppendLine("   - silences after finalize: " + inGap.Count + ", longest " + Txt.Sec(inGap.Max(s => s.Dur)) + " s");
            }
            else
                sb.AppendLine("   - n/a (" + (r.RefFinalizeMs == null ? "finalize time unknown" : "no Playable event") + ")");
            // 3
            sb.AppendLine("3. Seconds in the block-list wait");
            if (r.BlocklistWaitMs.HasValue)
                sb.AppendLine("   - " + Txt.Sec(r.BlocklistWaitMs.Value) + " s" + (r.BlocklistUpperBound ? " (upper bound: no server.blocklist.set marker followed the wait)" : "") + ".");
            else
                sb.AppendLine("   - n/a (no server.blocklist.wait marker)");
            sb.AppendLine();
        }

        // ---------- compare ----------

        private static void Compare(StringBuilder sb, Result r, Result c)
        {
            sb.AppendLine("## Moved bottleneck").AppendLine();
            sb.AppendLine("Run " + r.D.Info.RunId + " (arm " + r.D.Info.Arm + ") against compare run " + c.D.Info.RunId + " (arm " + c.D.Info.Arm + "). Delta = run minus compare.").AppendLine();
            if (r.D.Info.TimingVoid || c.D.Info.TimingVoid) sb.AppendLine("TIMING-VOID: at least one of the two runs is deep mode, so the deltas below are not trustworthy.").AppendLine();

            var a = r.PhaseMetrics();
            var b = c.PhaseMetrics();
            var keys = a.Keys.Union(b.Keys).ToList();
            double ta = r.TotalRefMs, tb = c.TotalRefMs;
            var rows = keys.Select(k =>
            {
                a.TryGetValue(k, out double va);
                b.TryGetValue(k, out double vb);
                return (Key: k, A: va, B: vb, Delta: va - vb, InA: a.ContainsKey(k), InB: b.ContainsKey(k));
            }).Where(x => Math.Abs(x.Delta) >= 50 || Math.Max(x.A, x.B) >= 1000).ToList();

            sb.AppendLine("- launch to finalize: run " + Txt.Sec(ta) + " s, compare " + Txt.Sec(tb) + " s, delta " + Signed(ta - tb) + " s");
            string top(Dictionary<string, double> m) => m.Count == 0 ? Txt.Na : m.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " (" + Txt.Sec(kv.Value) + " s)").First();
            sb.AppendLine("- largest phase, run: " + top(a));
            sb.AppendLine("- largest phase, compare: " + top(b));
            var changed = rows.OrderByDescending(x => Math.Abs(x.Delta)).ToList();
            if (changed.Count > 0 && Math.Abs(changed[0].Delta) >= 50)
            {
                sb.AppendLine("- largest change: " + changed[0].Key + " " + Signed(changed[0].Delta) + " s");
                string ka = a.Count > 0 ? a.OrderByDescending(kv => kv.Value).First().Key : "";
                string kb = b.Count > 0 ? b.OrderByDescending(kv => kv.Value).First().Key : "";
                sb.AppendLine("- bottleneck: " + (ka == kb ? "same phase (" + ka + ")" : "moved from " + kb + " to " + ka));
            }
            else sb.AppendLine("- largest change: none over 0.05 s");
            sb.AppendLine();

            if (changed.Count > 0)
                Table(sb, new[] { "phase", "run s", "compare s", "delta s", "run share", "compare share" },
                    changed.Take(15).Select(x => new[]
                    {
                        x.Key, x.InA ? Txt.Sec(x.A) : Txt.Na, x.InB ? Txt.Sec(x.B) : Txt.Na, Signed(x.Delta),
                        x.InA && ta > 0 ? Txt.Pct(100.0 * x.A / ta) : Txt.Na, x.InB && tb > 0 ? Txt.Pct(100.0 * x.B / tb) : Txt.Na,
                    }));

            string F(double? v) => v.HasValue ? Txt.Sec(v.Value) : Txt.Na;
            string D(double? x, double? y) => x.HasValue && y.HasValue ? Signed(x.Value - y.Value) : Txt.Na;
            Table(sb, new[] { "answer", "run s", "compare s", "delta s" }, new[]
            {
                new[] { "client main idle during server launch", F(r.MainIdleMs), F(c.MainIdleMs), D(r.MainIdleMs, c.MainIdleMs) },
                new[] { "finalize to playable", F(r.PostGapMs), F(c.PostGapMs), D(r.PostGapMs, c.PostGapMs) },
                new[] { "block-list wait", F(r.BlocklistWaitMs), F(c.BlocklistWaitMs), D(r.BlocklistWaitMs, c.BlocklistWaitMs) },
            });
        }

        private static string Signed(double ms)
        {
            string s = Txt.Sec(Math.Abs(ms));
            return (ms < 0 ? "-" : "+") + s;
        }

        // ---------- csv ----------

        public static string BuildCsv(Result r)
        {
            var d = r.D;
            var rows = new List<(double T0, int Ord, string Line)>();
            int ord = 0;
            string lostTotal = r.D.Ev.HasMarkers ? Txt.I(r.Lost) : "";

            string Row(double t0, double t1, string side, string thread, string phase, string sub, string mod, string x, string z, string pass, string count, string bytes, string source, string lost) =>
                string.Join(",", new[]
                {
                    Txt.Csv(d.Info.RunId), Txt.Csv(d.Info.Arm), Txt.F3(t0), Txt.F3(t1), Txt.F3(Math.Max(0, t1 - t0)),
                    Txt.Csv(side), Txt.Csv(thread), Txt.Csv(phase), Txt.Csv(sub), Txt.Csv(mod), x, z, pass, count, bytes, Txt.Csv(source), lost,
                });

            string first = Row(0, d.EndMs, "", "", "process", "", "", "", "", "", "", "", d.Ev.HasMarkers ? "marker" : "log", lostTotal);

            foreach (var s in d.Spans)
            {
                if (s.Marker == "server.phase" || s.Marker == "frame") continue;
                var c = Result.Classify(s, d);
                string tn = r.ThreadName(s.Tid);
                string thread = (tn.Length > 0 ? tn : "") + "#" + s.Tid.ToString(CultureInfo.InvariantCulture);
                bool col = s.Marker == "column.pass" || s.Marker == "column.load";
                string sub = c.Sub;
                if (s.Open) sub = (sub.Length > 0 ? sub + " " : "") + "(open)";
                rows.Add((s.T0, ord++, Row(s.T0, s.T1, c.Side, thread, c.Phase, sub, c.Mod,
                    col ? Txt.I(s.A) : "", col ? Txt.I(s.B) : "", s.Marker == "column.pass" ? Txt.I(s.C) : "", "", "", "marker", s.Lost > 0 ? Txt.I(s.Lost) : "")));
            }
            if (r.PhasesFromMarkers)
                foreach (var p in r.ServerPhases)
                    rows.Add((p.T0, ord++, Row(p.T0, p.T1, "server", "", "server.phase", p.Name, "", "", "", "", "", "", "marker", "")));
            foreach (var p in r.LogPhases)
                rows.Add((p.T0, ord++, Row(p.T0, p.T1, "server", "", "server.phase", p.Name, "", "", "", "", "", "", "log", "")));
            if (r.FinalizeLogMs.HasValue && r.FinalizeDoneLogMs.HasValue && r.FinalizeDoneLogMs.Value >= r.FinalizeLogMs.Value)
                rows.Add((r.FinalizeLogMs.Value, ord++, Row(r.FinalizeLogMs.Value, r.FinalizeDoneLogMs.Value, "client", "", "client.finalize", "log", "", "", "", "", "", "", "log", "")));
            if (r.PostGapMs.HasValue)
                rows.Add((r.RefFinalizeMs!.Value, ord++, Row(r.RefFinalizeMs.Value, r.PlayableMs!.Value, "client", "", "postfinalize.gap", "to playable", "", "", "", "", Txt.I(r.FramesInGap ?? 0), "", "marker", "")));
            foreach (var p in d.Ev.Pauses)
                rows.Add((p.T0, ord++, Row(p.T0, p.T1, "process", "", "gc", p.Gen >= 0 ? "pause gen" + p.Gen : "pause", "", "", "", "", "", "", "marker", "")));
            foreach (var s in r.Silences)
                rows.Add((s.T0, ord++, Row(s.T0, s.T1, "", s.HasSamples ? (s.TopName.Length > 0 ? s.TopName : "") + "#" + s.TopTid : "", "silence", s.Verdict, "", "", "", "", "", s.HasSamples ? Txt.F0(s.IoBytes) : "", s.HasSamples ? "sample" : "log", "")));

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", CsvColumns));
            sb.AppendLine(first);
            foreach (var row in rows.OrderBy(x => x.T0).ThenBy(x => x.Ord)) sb.AppendLine(row.Line);
            return sb.ToString();
        }
    }
}
