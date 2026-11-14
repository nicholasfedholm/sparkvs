using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SparkLoad.Analyzer
{
    internal sealed class RunInfo
    {
        public string RunId = "", Arm = "", Mode = "", Exe = "", DataPath = "", LauncherVersion = "", ExitReason = "", PresentMode = "", TraceEnd = "";
        public bool TimingVoid;
        public long T0Qpc, QpcFreq, T0UtcFileTime, ChildPid;
        public double? G1DeltaMs, FinalizeLogMs, EndMs;

        public static RunInfo Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new InvalidDataException("run.json is not an object");
            var i = new RunInfo
            {
                RunId = J.Str(r, "run_id"),
                Arm = J.Str(r, "arm"),
                Mode = J.Str(r, "mode"),
                Exe = J.Str(r, "exe"),
                DataPath = J.Str(r, "data_path"),
                LauncherVersion = J.Str(r, "launcher_version"),
                ExitReason = J.Str(r, "exit_reason"),
                PresentMode = J.Str(r, "present_mode"),
                TraceEnd = J.Str(r, "trace_end"),
                TimingVoid = J.Bool(r, "timing_void"),
                T0Qpc = J.Long(r, "t0_qpc") ?? 0,
                QpcFreq = J.Long(r, "qpc_freq") ?? 0,
                T0UtcFileTime = J.Long(r, "t0_utc_filetime") ?? 0,
                ChildPid = J.Long(r, "child_pid") ?? 0,
                G1DeltaMs = J.Dbl(r, "g1_delta_ms"),
                FinalizeLogMs = J.Dbl(r, "finalize_log_ms"),
                EndMs = J.Dbl(r, "end_ms"),
            };
            if (i.RunId.Length == 0) i.RunId = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "") ?? "";
            return i;
        }
    }

    internal static class J
    {
        public static string Str(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Null => "",
                JsonValueKind.Undefined => "",
                _ => v.ToString(),
            };
        }

        public static bool Bool(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.String) return string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        public static long? Long(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt64(out long l)) return l;
                return (long)v.GetDouble();
            }
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)) return s;
            return null;
        }

        public static double? Dbl(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String && Num.TryD(v.GetString(), out double d)) return d;
            return null;
        }
    }

    internal sealed class LogLine
    {
        public double T;
        public string File = "";
        public long Offset;
        public string Text = "";
    }

    /// <summary>Step function of a cumulative counter sampled at ticks.</summary>
    internal sealed class Series
    {
        public readonly List<double> T = new List<double>();
        public readonly List<double> V = new List<double>();
        public double Base;
        public int Pid;
        public string Name = "";

        public double At(double t)
        {
            int lo = 0, hi = T.Count - 1, res = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (T[mid] <= t) { res = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return res < 0 ? Base : V[res];
        }

        public double Delta(double a, double b) => Math.Max(0, At(b) - At(a));
    }

    internal sealed class SampleSet
    {
        public bool Present;
        public double MinT = double.MaxValue, MaxT;
        public readonly Dictionary<int, Series> Cpu = new Dictionary<int, Series>();
        public readonly Dictionary<long, Series> ProcIo = new Dictionary<long, Series>();
        public readonly Dictionary<long, Series> ProcCpu = new Dictionary<long, Series>();
        public int Rows;

        public string NameOf(int tid) => Cpu.TryGetValue(tid, out var s) ? s.Name : "";

        public double IoDelta(double a, double b)
        {
            double sum = 0;
            foreach (var s in ProcIo.Values) sum += s.Delta(a, b);
            return sum;
        }

        public static SampleSet Load(string path, TextWriter log)
        {
            var set = new SampleSet();
            if (!File.Exists(path)) return set;
            try
            {
                int iT = -1, iPid = -1, iKind = -1, iTid = -1, iName = -1, iCpu = -1, iRd = -1, iWr = -1, iOt = -1;
                bool header = true;
                foreach (var row in CsvReader.Rows(path))
                {
                    if (header)
                    {
                        header = false;
                        for (int k = 0; k < row.Count; k++)
                        {
                            switch (row[k].Trim())
                            {
                                case "t_ms": iT = k; break;
                                case "pid": iPid = k; break;
                                case "kind": iKind = k; break;
                                case "tid": iTid = k; break;
                                case "name": iName = k; break;
                                case "cpu_ms": iCpu = k; break;
                                case "read_bytes": iRd = k; break;
                                case "write_bytes": iWr = k; break;
                                case "other_bytes": iOt = k; break;
                            }
                        }
                        if (iT < 0 || iKind < 0 || iCpu < 0) throw new InvalidDataException("samples.csv header lacks t_ms/kind/cpu_ms");
                        continue;
                    }
                    string G(int k) => k >= 0 && k < row.Count ? row[k] : "";
                    if (!Num.TryD(G(iT), out double t)) continue;
                    double cpu = Num.D(G(iCpu));
                    long pid = Num.L(G(iPid));
                    string kind = G(iKind).Trim();
                    set.Rows++;
                    if (t < set.MinT) set.MinT = t;
                    if (t > set.MaxT) set.MaxT = t;
                    if (kind == "P")
                    {
                        if (!set.ProcIo.TryGetValue(pid, out var io)) { io = new Series(); set.ProcIo[pid] = io; }
                        if (!set.ProcCpu.TryGetValue(pid, out var pc)) { pc = new Series(); set.ProcCpu[pid] = pc; }
                        io.T.Add(t); io.V.Add(Num.D(G(iRd)) + Num.D(G(iWr)) + Num.D(G(iOt)));
                        pc.T.Add(t); pc.V.Add(cpu);
                    }
                    else if (kind == "T")
                    {
                        if (!int.TryParse(G(iTid), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tid)) continue;
                        if (!set.Cpu.TryGetValue(tid, out var s)) { s = new Series { Pid = (int)pid }; set.Cpu[tid] = s; }
                        s.T.Add(t); s.V.Add(cpu);
                        string nm = G(iName);
                        if (nm.Length > 0) s.Name = nm;
                    }
                }
                set.Present = set.Rows > 0;
                if (set.Present)
                {
                    // Threads first seen on the very first tick pre-exist: their first cumulative value is a baseline.
                    foreach (var s in set.Cpu.Values)
                        s.Base = s.T.Count > 0 && s.T[0] <= set.MinT + 1e-6 ? s.V[0] : 0;
                    foreach (var s in set.ProcIo.Values) s.Base = s.V.Count > 0 ? s.V[0] : 0;
                    foreach (var s in set.ProcCpu.Values) s.Base = s.V.Count > 0 ? s.V[0] : 0;
                }
            }
            catch (Exception ex)
            {
                log.WriteLine("analyzer: samples.csv unreadable, treated as absent: " + Txt.Clean(ex.Message));
                return new SampleSet();
            }
            return set;
        }
    }

    internal sealed class MarkerEv
    {
        public double T;
        public bool Begin;
        public string Marker = "";
        public int MarkerId;
        public int Tid;
        public long Seq;
        public long A, B, C;
        public string S = "";
        public int Order;
    }

    internal sealed class Span
    {
        public string Marker = "";
        public int Tid;
        public double T0, T1;
        public bool Open;
        public long A, B, C;
        public string S = "";
        public long SeqB, SeqE;
        public int Lost;
        internal bool Keep; // column.load streaming fold: this span is kept in the listing

        public double Dur => Math.Max(0, T1 - T0);
    }

    /// <summary>
    /// Distinct seq numbers per thread without keeping them: a bitmap per thread in 64K-seq chunks (sparse), plus the
    /// maximum and the count. Lost markers = max - count, and the number present inside any seq range.
    /// </summary>
    internal sealed class SeqTracker
    {
        private sealed class PerTid
        {
            public long Max, Count;
            public readonly Dictionary<long, ulong[]> Chunks = new Dictionary<long, ulong[]>();
            public long LastId = -1;
            public ulong[]? Last;
        }

        private readonly Dictionary<int, PerTid> _t = new Dictionary<int, PerTid>();

        public void Add(int tid, long seq)
        {
            if (seq <= 0) return;
            if (!_t.TryGetValue(tid, out var p)) { p = new PerTid(); _t[tid] = p; }
            long id = seq >> 16;
            ulong[]? c;
            if (id == p.LastId) c = p.Last;
            else
            {
                if (!p.Chunks.TryGetValue(id, out c)) { c = new ulong[1024]; p.Chunks[id] = c; }
                p.LastId = id; p.Last = c;
            }
            int bit = (int)(seq & 0xFFFF);
            ulong m = 1UL << (bit & 63);
            ref ulong w = ref c![bit >> 6];
            if ((w & m) == 0) { w |= m; p.Count++; }
            if (seq > p.Max) p.Max = seq;
        }

        /// <summary>(tid, max seq, distinct count) for every tid, ordered by tid.</summary>
        public List<(int Tid, long Max, long Count)> Totals() =>
            _t.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Max, kv.Value.Count)).ToList();

        /// <summary>Distinct seq numbers of the tid inside [lo, hi].</summary>
        public long CountIn(int tid, long lo, long hi)
        {
            if (lo > hi || lo <= 0 || !_t.TryGetValue(tid, out var p)) return 0;
            long total = 0;
            for (long id = lo >> 16; id <= (hi >> 16); id++)
            {
                if (!p.Chunks.TryGetValue(id, out var c)) continue;
                int from = id == (lo >> 16) ? (int)(lo & 0xFFFF) : 0;
                int to = id == (hi >> 16) ? (int)(hi & 0xFFFF) : 0xFFFF;
                for (int wi = from >> 6; wi <= (to >> 6); wi++)
                {
                    ulong w = c[wi];
                    int lb = wi == (from >> 6) ? from & 63 : 0;
                    int hb = wi == (to >> 6) ? to & 63 : 63;
                    if (lb > 0) w &= ~0UL << lb;
                    if (hb < 63) w &= (1UL << (hb + 1)) - 1;
                    total += System.Numerics.BitOperations.PopCount(w);
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Totals of the column.load spans that were not kept in the listing (see EventData.Load): a column is loaded tens of
    /// thousands of times, so only the first KeepFirst calls per column and every span of SlowMs or more stay individual.
    /// </summary>
    internal sealed class LoadFold
    {
        public const int KeepFirst = 4;
        public const double SlowMs = 1.0;
        public long Db, Gen, Unk;
        public double DbMs, GenMs, UnkMs;
        public long Total => Db + Gen + Unk;
        public bool[] Windows = new bool[1024]; // 250 ms windows touched by folded spans

        public void Add(Span s)
        {
            double d = s.Dur;
            if (s.C == 1) { Db++; DbMs += d; }
            else if (s.C == 0) { Gen++; GenMs += d; }
            else { Unk++; UnkMs += d; }
            const int cap = 1 << 22;
            int a = (int)Math.Floor(s.T0 / Result.WindowMs);
            int b = (int)Math.Floor(Math.Max(s.T0, s.T1 - 1e-6) / Result.WindowMs);
            if (a < 0) a = 0;
            if (b >= cap) b = cap - 1;
            if (b < a) return;
            if (b >= Windows.Length) Array.Resize(ref Windows, Math.Min(cap, Math.Max(b + 1, Windows.Length * 2)));
            for (int w = a; w <= b; w++) Windows[w] = true;
        }
    }

    internal sealed class PatchEv { public string Marker = "", Target = "", Status = "", Detail = ""; public double T; }
    internal sealed class StatEv { public string Marker = ""; public long Hits; public double T; }
    internal sealed class FrameEv { public double T; public int Tid; public long Seq; public long Index; public double DurMs; }
    internal sealed class FrameAggEv { public double T; public int Tid; public long Seq; public long Count; public double SumMs, MaxMs; public long Over50; }
    internal sealed class PlayableEv { public double T; public int Tid; public long Seq; public long FrameIndex; }
    internal sealed class GcInfo { public long Count; public int Gen; public string Reason = ""; public double T0, T1; public bool Closed; }
    internal sealed class PauseInfo { public double T0, T1; public string Reason = ""; public int Gen = -1; public double Dur => Math.Max(0, T1 - T0); }

    internal sealed class EventData
    {
        public bool FilePresent;
        public bool Truncated;
        public string Note = "";
        public int Count;
        public double MaxT;
        public bool HasHello;
        public double HelloT;
        public string Runtime = "", HookVersion = "";
        public long HelloPid;
        public readonly List<MarkerEv> Markers = new List<MarkerEv>();
        public readonly Dictionary<int, string> ThreadNames = new Dictionary<int, string>();
        public readonly List<PatchEv> Patches = new List<PatchEv>();
        public readonly List<StatEv> Stats = new List<StatEv>();
        public readonly List<FrameEv> Frames = new List<FrameEv>();
        public readonly List<FrameAggEv> FrameAggs = new List<FrameAggEv>();
        public readonly List<PlayableEv> Playables = new List<PlayableEv>();
        public readonly List<GcInfo> Gcs = new List<GcInfo>();
        public readonly List<PauseInfo> Pauses = new List<PauseInfo>();
        public long ExceptionCount;
        public readonly Dictionary<string, long> ExceptionTypes = new Dictionary<string, long>();
        public int AssemblyLoads;
        /// <summary>Every Begin/End event seen (also the folded column.load ones).</summary>
        public long MarkerEventCount;
        /// <summary>Begin events of the folded markers, by marker name (their Begins are not in Markers).</summary>
        public readonly Dictionary<string, long> FoldedBegins = new Dictionary<string, long>();
        public readonly SeqTracker Seqs = new SeqTracker();
        /// <summary>Times of all marker events, thinned: points between neighbours at most SilenceGapMs apart are dropped, which keeps every silence.</summary>
        public readonly List<double> MarkerSignal = new List<double>();
        public readonly List<Span> RetainedLoadSpans = new List<Span>();
        public readonly LoadFold Fold = new LoadFold();
        /// <summary>Hook-side suppressed column.load calls: last Stats value per (x, z).</summary>
        public readonly Dictionary<(long X, long Z), long> SuppressedByColumn = new Dictionary<(long, long), long>();
        public readonly Dictionary<int, string> MarkerNames = new Dictionary<int, string>();

        /// <summary>True when any hook marker (Begin/End) exists: the run has marker data.</summary>
        public bool HasMarkers => MarkerEventCount > 0;

        public const string SuppressedPrefix = "column.load.suppressed ";

        // Marker time signal for the silence scan. Events arrive in time order. When the previous-but-one point is
        // within SilenceGapMs of the new one, the previous point cannot start or end a silence, so it is replaced.
        private static void AddSignal(List<double> l, double t)
        {
            int n = l.Count;
            if (n >= 2 && t - l[n - 2] <= Result.SilenceGapMs) l[n - 1] = t;
            else l.Add(t);
        }

        // Streaming pairing of column.load Begin/End per thread. A span stays individual when it is among the first
        // LoadFold.KeepFirst of its column (by Begin order) or when it lasts LoadFold.SlowMs or more. Every other span
        // is only counted (class by c, duration sum, 250 ms windows touched).
        private static void FoldLoad(EventData ev, Dictionary<int, Stack<Span>> stacks, Dictionary<(long, long), int> seen,
            bool begin, double t, int tid, long seq, long a, long b, long c, string s)
        {
            if (!stacks.TryGetValue(tid, out var st)) { st = new Stack<Span>(); stacks[tid] = st; }
            if (begin)
            {
                ev.FoldedBegins["column.load"] = (ev.FoldedBegins.TryGetValue("column.load", out long nb) ? nb : 0) + 1;
                var key = (a, b);
                seen.TryGetValue(key, out int n);
                seen[key] = ++n;
                st.Push(new Span { Marker = "column.load", Tid = tid, T0 = t, T1 = t, Open = true, A = a, B = b, C = c, S = s, SeqB = seq, Keep = n <= LoadFold.KeepFirst });
                return;
            }
            if (st.Count == 0) return;
            var sp = st.Pop();
            sp.T1 = Math.Max(t, sp.T0);
            sp.Open = false;
            sp.SeqE = seq;
            if (s.Length > 0 && sp.S.Length == 0) sp.S = s;
            if (sp.Keep || sp.Dur >= LoadFold.SlowMs) ev.RetainedLoadSpans.Add(sp);
            else ev.Fold.Add(sp);
        }

        public static EventData Load(string eventsPath, string metaPath, TextWriter log)
        {
            var ev = new EventData();
            if (!File.Exists(eventsPath)) return ev;
            ev.FilePresent = true;
            int bad = 0;
            GcInfo? openGc = null;
            double? openSuspend = null;
            string suspendReason = "";
            int order = 0;
            int loadMarkerId = -1;
            var loadStacks = new Dictionary<int, Stack<Span>>();
            var loadSeen = new Dictionary<(long, long), int>();
            using (var fs = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        double t = J.Dbl(r, "t_ms") ?? 0;
                        string src = J.Str(r, "src");
                        string name = J.Str(r, "name");
                        JsonElement f = r.TryGetProperty("fields", out var fe) ? fe : default;
                        ev.Count++;
                        if (t > ev.MaxT) ev.MaxT = t;
                        order++;
                        if (src == "sparkload")
                        {
                            switch (name)
                            {
                                case "Hello":
                                    if (!ev.HasHello)
                                    {
                                        ev.HasHello = true; ev.HelloT = t;
                                        ev.Runtime = J.Str(f, "runtime"); ev.HookVersion = J.Str(f, "hookVersion");
                                        ev.HelloPid = J.Long(f, "pid") ?? 0;
                                    }
                                    break;
                                case "MarkerDef":
                                    {
                                        int mid = (int)(J.Long(f, "markerId") ?? -1);
                                        string mname = J.Str(f, "name");
                                        ev.MarkerNames[mid] = mname;
                                        if (mname == "column.load") loadMarkerId = mid;
                                        break;
                                    }
                                case "Begin":
                                case "End":
                                    {
                                        bool isBegin = name == "Begin";
                                        string mk = J.Str(f, "marker");
                                        int mid = (int)(J.Long(f, "markerId") ?? -1);
                                        int tid = (int)(J.Long(f, "tid") ?? 0);
                                        long seq = J.Long(f, "seq") ?? 0;
                                        long ma = J.Long(f, "a") ?? 0, mb = J.Long(f, "b") ?? 0, mc = J.Long(f, "c") ?? 0;
                                        string ms = J.Str(f, "s");
                                        ev.MarkerEventCount++;
                                        ev.Seqs.Add(tid, seq);
                                        AddSignal(ev.MarkerSignal, t);
                                        if (mk == "column.load" || (mk.Length == 0 && loadMarkerId >= 0 && mid == loadMarkerId))
                                        {
                                            FoldLoad(ev, loadStacks, loadSeen, isBegin, t, tid, seq, ma, mb, mc, ms);
                                            break;
                                        }
                                        ev.Markers.Add(new MarkerEv
                                        {
                                            T = t, Begin = isBegin, Marker = mk, MarkerId = mid, Tid = tid,
                                            Seq = seq, A = ma, B = mb, C = mc, S = ms, Order = order,
                                        });
                                        break;
                                    }
                                case "Thread":
                                    ev.ThreadNames[(int)(J.Long(f, "tid") ?? 0)] = J.Str(f, "name");
                                    break;
                                case "Patch":
                                    ev.Patches.Add(new PatchEv { T = t, Marker = J.Str(f, "marker"), Target = J.Str(f, "target"), Status = J.Str(f, "status"), Detail = J.Str(f, "detail") });
                                    break;
                                case "Stats":
                                    {
                                        string smk = J.Str(f, "marker");
                                        long shits = J.Long(f, "hits") ?? 0;
                                        if (smk.StartsWith(SuppressedPrefix, StringComparison.Ordinal))
                                        {
                                            // "column.load.suppressed <x> <z>": the hook's own count of calls it did not emit
                                            var parts = smk.Substring(SuppressedPrefix.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sx)
                                                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sz))
                                                ev.SuppressedByColumn[(sx, sz)] = shits;
                                            break;
                                        }
                                        ev.Stats.Add(new StatEv { T = t, Marker = smk, Hits = shits });
                                        break;
                                    }
                                case "Frame":
                                    {
                                        double dur = J.Dbl(f, "dur_ms") ?? 0;
                                        var fe2 = new FrameEv { T = t, Tid = (int)(J.Long(f, "tid") ?? 0), Seq = J.Long(f, "seq") ?? 0, Index = J.Long(f, "index") ?? 0, DurMs = dur };
                                        ev.Frames.Add(fe2);
                                        ev.Seqs.Add(fe2.Tid, fe2.Seq);
                                        break;
                                    }
                                case "FrameAgg":
                                    {
                                        var fa = new FrameAggEv
                                        {
                                            T = t, Tid = (int)(J.Long(f, "tid") ?? 0), Seq = J.Long(f, "seq") ?? 0, Count = J.Long(f, "count") ?? 0,
                                            SumMs = J.Dbl(f, "sum_ms") ?? 0, MaxMs = J.Dbl(f, "max_ms") ?? 0, Over50 = J.Long(f, "over50") ?? 0,
                                        };
                                        ev.FrameAggs.Add(fa);
                                        ev.Seqs.Add(fa.Tid, fa.Seq);
                                        break;
                                    }
                                case "Playable":
                                    {
                                        var pl = new PlayableEv { T = t, Tid = (int)(J.Long(f, "tid") ?? 0), Seq = J.Long(f, "seq") ?? 0, FrameIndex = J.Long(f, "frameIndex") ?? 0 };
                                        ev.Playables.Add(pl);
                                        ev.Seqs.Add(pl.Tid, pl.Seq);
                                        break;
                                    }
                            }
                        }
                        else if (src == "gc")
                        {
                            switch (name)
                            {
                                case "GCStart":
                                    openGc = new GcInfo { Count = J.Long(f, "gc") ?? 0, Gen = (int)(J.Long(f, "gen") ?? 0), Reason = J.Str(f, "reason"), T0 = t, T1 = t };
                                    ev.Gcs.Add(openGc);
                                    break;
                                case "GCEnd":
                                    {
                                        long cnt = J.Long(f, "gc") ?? -1;
                                        var g = openGc != null && (cnt < 0 || openGc.Count == cnt) ? openGc : ev.Gcs.LastOrDefault(x => !x.Closed && x.Count == cnt);
                                        if (g != null) { g.T1 = t; g.Closed = true; if (ReferenceEquals(g, openGc)) openGc = null; }
                                        break;
                                    }
                                case "GCSuspendBegin":
                                    openSuspend = t; suspendReason = J.Str(f, "reason");
                                    break;
                                case "GCRestartEnd":
                                    if (openSuspend.HasValue)
                                    {
                                        ev.Pauses.Add(new PauseInfo { T0 = openSuspend.Value, T1 = t, Reason = suspendReason });
                                        openSuspend = null;
                                    }
                                    break;
                            }
                        }
                        else if (src == "loader")
                        {
                            if (name == "AssemblyLoad") ev.AssemblyLoads++;
                        }
                        else if (src == "exception")
                        {
                            ev.ExceptionCount++;
                            string et = J.Str(f, "type");
                            ev.ExceptionTypes[et] = ev.ExceptionTypes.TryGetValue(et, out long ec) ? ec + 1 : 1;
                        }
                    }
                    catch (JsonException) { bad++; }
                }
            }
            // column.load spans still open at the end: kept ones stay open spans, the rest count with T1 = end of trace.
            foreach (var st in loadStacks.Values)
                foreach (var s in st)
                {
                    if (s.Keep) { ev.RetainedLoadSpans.Add(s); continue; }
                    s.T1 = Math.Max(s.T0, ev.MaxT);
                    s.Open = false;
                    ev.Fold.Add(s);
                }
            foreach (var m in ev.Markers)
                if (m.Marker.Length == 0 && ev.MarkerNames.TryGetValue(m.MarkerId, out var nm)) m.Marker = nm;
            // Attribute each pause to the GC that started inside it (or the closest GC after its start).
            foreach (var p in ev.Pauses)
            {
                var g = ev.Gcs.FirstOrDefault(x => x.T0 >= p.T0 - 1 && x.T0 <= p.T1 + 1);
                if (g != null) p.Gen = g.Gen;
            }
            if (bad > 0) log.WriteLine("analyzer: skipped " + bad + " malformed events.jsonl lines");
            if (File.Exists(metaPath))
            {
                try
                {
                    using var md = JsonDocument.Parse(File.ReadAllText(metaPath));
                    ev.Truncated = J.Bool(md.RootElement, "truncated");
                    ev.Note = J.Str(md.RootElement, "note");
                }
                catch (Exception) { }
            }
            return ev;
        }
    }

    /// <summary>Everything one analysis needs, loaded from a run folder.</summary>
    internal sealed class RunData
    {
        public string Dir = "";
        public RunInfo Info = new RunInfo();
        public SampleSet Samples = new SampleSet();
        public List<LogLine> Logs = new List<LogLine>();
        public bool LogsPresent;
        public EventData Ev = new EventData();
        public Dictionary<int, string> PhaseNames = RunPhases.Defaults();
        public Dictionary<int, string> PassNames = new Dictionary<int, string>();
        public List<Span> Spans = new List<Span>();
        public double EndMs;

        public static RunData Load(string runDir, TextWriter log, bool convertTrace)
        {
            var d = new RunData { Dir = runDir };
            string runJson = Path.Combine(runDir, "run.json");
            if (!File.Exists(runJson)) throw new FileNotFoundException("run.json not found in " + runDir);
            d.Info = RunInfo.Load(runJson);
            d.PhaseNames = RunPhases.Load(runDir, log);
            d.PassNames = RunPhases.LoadPasses(runDir, log);

            string nettrace = Path.Combine(runDir, "raw.nettrace");
            string events = Path.Combine(runDir, "events.jsonl");
            string meta = Path.Combine(runDir, "events.meta.json");
            if (convertTrace && File.Exists(nettrace))
            {
                try { TraceConverter.Convert(nettrace, d.Info, events, meta, log); }
                catch (Exception ex) { log.WriteLine("analyzer: trace conversion failed: " + Txt.Clean(ex.Message)); }
            }
            d.Ev = EventData.Load(events, meta, log);
            d.Samples = SampleSet.Load(Path.Combine(runDir, "samples.csv"), log);
            d.LoadLogs(Path.Combine(runDir, "logtail.csv"), log);
            d.BuildSpans();
            double end = d.Info.EndMs ?? 0;
            end = Math.Max(end, d.Ev.MaxT);
            if (d.Logs.Count > 0) end = Math.Max(end, d.Logs[d.Logs.Count - 1].T);
            if (d.Samples.Present) end = Math.Max(end, d.Samples.MaxT);
            d.EndMs = end;
            return d;
        }

        private void LoadLogs(string path, TextWriter log)
        {
            if (!File.Exists(path)) return;
            try
            {
                int iT = -1, iFile = -1, iOff = -1, iText = -1;
                bool header = true;
                foreach (var row in CsvReader.Rows(path))
                {
                    if (header)
                    {
                        header = false;
                        for (int k = 0; k < row.Count; k++)
                        {
                            switch (row[k].Trim())
                            {
                                case "t_ms": iT = k; break;
                                case "file": iFile = k; break;
                                case "offset": iOff = k; break;
                                case "text": iText = k; break;
                            }
                        }
                        if (iT < 0 || iText < 0) throw new InvalidDataException("logtail.csv header lacks t_ms/text");
                        continue;
                    }
                    if (iT >= row.Count || !Num.TryD(row[iT], out double t)) continue;
                    Logs.Add(new LogLine
                    {
                        T = t,
                        File = iFile >= 0 && iFile < row.Count ? row[iFile] : "",
                        Offset = iOff >= 0 && iOff < row.Count ? Num.L(row[iOff]) : 0,
                        Text = iText < row.Count ? row[iText] : "",
                    });
                }
                LogsPresent = true;
                Logs = Logs.OrderBy(l => l.T).ToList();
            }
            catch (Exception ex)
            {
                log.WriteLine("analyzer: logtail.csv unreadable, treated as absent: " + Txt.Clean(ex.Message));
                Logs = new List<LogLine>();
                LogsPresent = false;
            }
        }

        private void BuildSpans()
        {
            var byTid = Ev.Markers.GroupBy(m => m.Tid);
            double maxT = Ev.MaxT;
            foreach (var g in byTid)
            {
                var list = g.OrderBy(m => m.Seq > 0 ? m.Seq : long.MaxValue).ThenBy(m => m.Order).ToList();
                bool anySeq = list.Any(m => m.Seq > 0);
                if (!anySeq) list = g.OrderBy(m => m.T).ThenBy(m => m.Order).ToList();
                var stacks = new Dictionary<string, Stack<Span>>();
                foreach (var m in list)
                {
                    if (!stacks.TryGetValue(m.Marker, out var st)) { st = new Stack<Span>(); stacks[m.Marker] = st; }
                    if (m.Begin)
                    {
                        var s = new Span { Marker = m.Marker, Tid = m.Tid, T0 = m.T, T1 = m.T, Open = true, A = m.A, B = m.B, C = m.C, S = m.S, SeqB = m.Seq };
                        st.Push(s);
                        Spans.Add(s);
                    }
                    else if (st.Count > 0)
                    {
                        var s = st.Pop();
                        s.T1 = Math.Max(m.T, s.T0);
                        s.Open = false;
                        s.SeqE = m.Seq;
                        if (m.S.Length > 0 && s.S.Length == 0) s.S = m.S;
                    }
                }
            }
            Spans.AddRange(Ev.RetainedLoadSpans);
            foreach (var s in Spans) if (s.Open) s.T1 = Math.Max(s.T0, maxT);
            // Lost markers per span: missing seq numbers inside [SeqB, SeqE].
            foreach (var s in Spans)
            {
                if (s.SeqB <= 0 || s.SeqE <= 0 || s.SeqE < s.SeqB) continue;
                long present = Ev.Seqs.CountIn(s.Tid, s.SeqB, s.SeqE);
                s.Lost = (int)Math.Max(0, (s.SeqE - s.SeqB + 1) - present);
            }
            Spans = Spans.OrderBy(s => s.T0).ThenBy(s => s.T1).ToList();
        }
    }
}
