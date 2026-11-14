using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace SparkLoad.Analyzer
{
    /// <summary>
    /// Step 1: raw.nettrace to events.jsonl. SparkLoad events through the dynamic parser, GC, loader and
    /// exception events through ClrTraceEventParser. A truncated trace writes what was read and sets a note.
    /// </summary>
    internal static class TraceConverter
    {
        private sealed class Raw
        {
            public double T;            // ms since t0 (valid when HasT)
            public bool HasT;
            public long Qpc;
            public bool HasQpc;
            public double Rel;          // trace relative msec, for runtime-time events
            public string Src = "";
            public string Name = "";
            public List<KeyValuePair<string, object?>> F = new List<KeyValuePair<string, object?>>();
            public int Order;
        }

        // SPEC section 3 event schema: id, name, fields (name, isString).
        private static readonly (int Id, string Name, (string F, bool Str)[] Fields)[] Schema =
        {
            (1, "Hello", new[] { ("pid", false), ("qpc", false), ("qpcFreq", false), ("hookVersion", true), ("runtime", true) }),
            (2, "MarkerDef", new[] { ("markerId", false), ("name", true) }),
            (3, "Begin", new[] { ("markerId", false), ("tid", false), ("seq", false), ("qpc", false), ("a", false), ("b", false), ("c", false), ("s", true) }),
            (4, "End", new[] { ("markerId", false), ("tid", false), ("seq", false), ("qpc", false), ("a", false), ("b", false), ("c", false), ("s", true) }),
            (5, "Thread", new[] { ("tid", false), ("managedId", false), ("name", true) }),
            (6, "Patch", new[] { ("marker", true), ("target", true), ("status", true), ("detail", true) }),
            (7, "Frame", new[] { ("tid", false), ("seq", false), ("qpc", false), ("index", false), ("durTicks", false) }),
            (8, "FrameAgg", new[] { ("tid", false), ("seq", false), ("qpc", false), ("count", false), ("sumTicks", false), ("maxTicks", false), ("over50", false) }),
            (9, "Playable", new[] { ("tid", false), ("seq", false), ("qpc", false), ("frameIndex", false) }),
            (10, "Stats", new[] { ("marker", true), ("hits", false) }),
        };

        // Sorted runs of at most this many events are spilled to temp files, then merged k-way by (T, Order).
        // Peak memory of step 1 is one run (about 1M events) plus one read buffer per run, whatever the trace size.
        public const int MaxEventsPerRun = 1_000_000;
        // A run is also cut when its encoded payload reaches this many bytes.
        public const long MaxBytesPerRun = 256L << 20;

        public static void Convert(string nettracePath, RunInfo run, string eventsPath, string metaPath, TextWriter log)
        {
            Convert(nettracePath, run, eventsPath, metaPath, log, MaxEventsPerRun);
        }

        internal static void Convert(string nettracePath, RunInfo run, string eventsPath, string metaPath, TextWriter log, int maxEventsPerRun)
        {
            bool truncated = false;
            string note = "";
            int order = 0;
            long traceBytes = 0;
            DateTime sessionStartUtc = DateTime.MinValue;
            try { traceBytes = new FileInfo(nettracePath).Length; } catch (Exception) { }

            // SparkLoad events carry the hook's QPC: convert with t0 and the qpc frequency. The frequency is normally in
            // run.json; otherwise it comes from the first Hello (a short pre-pass that stops at that event).
            double freq = run.QpcFreq;
            if (freq <= 0) freq = HelloFreq(nettracePath);

            // Offset between trace time and run time (ms): learned from the SparkLoad events that carry a payload QPC.
            // The payload QPC is read just before the event is written, so (T - Rel) is largest for the event with the
            // least write latency. The Hello event alone is biased by first-call JIT cost, so take the maximum over
            // all QPC-carrying SparkLoad events (Hello included). Tracked while streaming.
            double? offset = null;
            bool sawHello = false;
            long count = 0;

            string spillDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(eventsPath)) ?? ".", "events.spill");
            using var spill = new Spill(spillDir, maxEventsPerRun);
            var opts = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };
            using var scratch = new MemoryStream();
            using var sw = new Utf8JsonWriter(scratch, opts);

            void Take(Raw r)
            {
                count++;
                if (freq > 0)
                {
                    if (r.HasQpc && run.T0Qpc > 0) { r.T = (r.Qpc - run.T0Qpc) * 1000.0 / freq; r.HasT = true; }
                    if (r.Name == "Frame") Add(r, "dur_ms", FieldLong(r, "durTicks") * 1000.0 / freq);
                    else if (r.Name == "FrameAgg")
                    {
                        Add(r, "sum_ms", FieldLong(r, "sumTicks") * 1000.0 / freq);
                        Add(r, "max_ms", FieldLong(r, "maxTicks") * 1000.0 / freq);
                    }
                }
                if (r.Src == "sparkload" && r.HasT)
                {
                    double o = r.T - r.Rel;
                    if (offset == null || o > offset.Value) offset = o;
                    if (r.Name == "Hello") sawHello = true;
                }
                // body of the JSON object without its braces: "src":..,"name":..,"fields":{..}
                scratch.SetLength(0);
                sw.Reset(scratch);
                WriteBody(sw, r);
                sw.Flush();
                byte[] b = scratch.GetBuffer();
                try { spill.Add(!r.HasT, r.HasT ? r.T : r.Rel, r.Order, b, 1, (int)scratch.Length - 2); }
                catch (Exception ex) { throw new SpillException(ex); }
            }

            try
            {
                using var fs = new FileStream(nettracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var src = new EventPipeEventSource(fs);
                var clr = new ClrTraceEventParser(src);

                src.Dynamic.All += e =>
                {
                    if (!string.Equals(e.ProviderName, "SparkLoad", StringComparison.Ordinal)) return;
                    var r = ReadSparkLoad(e, ref order);
                    if (r != null) Take(r);
                };
                clr.GCStart += e =>
                {
                    var r = Runtime(e, "gc", "GCStart", ref order);
                    Add(r, "gc", e.Count); Add(r, "gen", e.Depth); Add(r, "reason", e.Reason.ToString()); Add(r, "type", e.Type.ToString());
                    Take(r);
                };
                clr.GCStop += e =>
                {
                    var r = Runtime(e, "gc", "GCEnd", ref order);
                    Add(r, "gc", e.Count); Add(r, "gen", e.Depth);
                    Take(r);
                };
                clr.GCSuspendEEStart += e =>
                {
                    var r = Runtime(e, "gc", "GCSuspendBegin", ref order);
                    Add(r, "reason", e.Reason.ToString()); Add(r, "gc", e.Count);
                    Take(r);
                };
                clr.GCRestartEEStop += e =>
                {
                    Take(Runtime(e, "gc", "GCRestartEnd", ref order));
                };
                clr.LoaderAssemblyLoad += e =>
                {
                    var r = Runtime(e, "loader", "AssemblyLoad", ref order);
                    Add(r, "assembly", e.FullyQualifiedAssemblyName); Add(r, "id", (long)e.AssemblyID);
                    Take(r);
                };
                clr.ExceptionStart += e =>
                {
                    var r = Runtime(e, "exception", "ExceptionThrown", ref order);
                    Add(r, "type", e.ExceptionType); Add(r, "message", Txt.Cut(e.ExceptionMessage, 300)); Add(r, "hresult", e.ExceptionHRESULT);
                    Take(r);
                };

                try { sessionStartUtc = src.SessionStartTime.ToUniversalTime(); } catch (Exception) { }
                src.Process();
            }
            catch (SpillException sx)
            {
                throw new IOException("step 1 spill failed: " + sx.InnerException?.Message, sx.InnerException);
            }
            catch (Exception ex)
            {
                truncated = true;
                note = "trace read stopped early: " + ex.GetType().Name + ": " + Txt.Clean(Txt.Cut(ex.Message, 200));
                log.WriteLine("analyzer: " + note + " (kept " + count + " events)");
            }
            if (string.Equals(run.TraceEnd, "size_cap", StringComparison.Ordinal))
            {
                truncated = true;
                note += (note.Length > 0 ? "; " : "") + "trace stopped at the size cap (--max-trace-mb), later events are not recorded";
            }

            string offsetSrc = "none";
            if (offset != null) offsetSrc = sawHello ? "hello" : "first-event";
            if (offset == null && run.T0UtcFileTime > 0 && sessionStartUtc != DateTime.MinValue)
            {
                double sessionMs = (sessionStartUtc.ToFileTimeUtc() - run.T0UtcFileTime) / 10000.0;
                offset = sessionMs;
                offsetSrc = "filetime";
            }
            long dropped = 0;
            if (!offset.HasValue) dropped = spill.CountRel; // runtime-time events without a time offset
            double off = offset ?? 0;

            long written = 0;
            var runs = spill.Finish(dropped > 0);
            int spilledRuns = spill.RunsWritten;
            try
            {
                using var os = new FileStream(eventsPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                var heap = new RunHeap(runs.Count);
                foreach (var rn in runs)
                    if (rn.Advance(off)) heap.Push(rn);
                    else rn.Dispose();
                using var ms = new MemoryStream();
                using var tw = new Utf8JsonWriter(ms, opts);
                while (heap.Count > 0)
                {
                    var rn = heap.Top;
                    ms.SetLength(0);
                    tw.Reset(ms);
                    tw.WriteStartObject();
                    tw.WriteNumber("t_ms", Math.Round(rn.T, 3));
                    tw.Flush();
                    ms.WriteByte((byte)',');
                    ms.Write(rn.Buf, rn.Off, rn.Len);
                    ms.WriteByte((byte)'}');
                    ms.WriteByte((byte)'\n');
                    os.Write(ms.GetBuffer(), 0, (int)ms.Length);
                    written++;
                    if (rn.Advance(off)) heap.SiftTop();
                    else { heap.Pop(); rn.Dispose(); }
                }
            }
            finally
            {
                foreach (var rn in runs) rn.Dispose();
            }
            spill.DeleteFiles();
            if (dropped > 0) note += (note.Length > 0 ? "; " : "") + dropped + " events dropped (no time offset)";

            using (var mf = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var w = new Utf8JsonWriter(mf, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteBoolean("truncated", truncated);
                w.WriteString("note", Txt.Clean(note));
                w.WriteNumber("events", written);
                w.WriteNumber("trace_bytes", traceBytes);
                w.WriteString("offset_source", offsetSrc);
                w.WriteEndObject();
            }
            log.WriteLine("analyzer: step 1 wrote " + written + " events to events.jsonl (offset " + offsetSrc + (truncated ? ", TRUNCATED trace" : "") + ", " + spilledRuns + " spilled run files)");
        }

        private sealed class SpillException : Exception
        {
            public SpillException(Exception inner) : base(inner.Message, inner) { }
        }

        // Frequency from the first Hello event, for runs whose run.json has no qpc_freq.
        private static double HelloFreq(string nettracePath)
        {
            double freq = 0;
            try
            {
                using var fs = new FileStream(nettracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var src = new EventPipeEventSource(fs);
                int ord = 0;
                src.Dynamic.All += e =>
                {
                    if (freq > 0 || !string.Equals(e.ProviderName, "SparkLoad", StringComparison.Ordinal)) return;
                    var r = ReadSparkLoad(e, ref ord);
                    if (r != null && r.Name == "Hello")
                    {
                        freq = FieldLong(r, "qpcFreq");
                        src.StopProcessing();
                    }
                };
                src.Process();
            }
            catch (Exception) { }
            return freq;
        }

        // Writes `"src":..,"name":..,"fields":{..}` (an object whose braces the caller strips).
        private static void WriteBody(Utf8JsonWriter w, Raw r)
        {
            w.WriteStartObject();
            w.WriteString("src", r.Src);
            w.WriteString("name", r.Name);
            w.WriteStartObject("fields");
            foreach (var kv in r.F)
            {
                switch (kv.Value)
                {
                    case null: w.WriteNull(kv.Key); break;
                    case string s: w.WriteString(kv.Key, Txt.Clean(s)); break;
                    case long l: w.WriteNumber(kv.Key, l); break;
                    case int i: w.WriteNumber(kv.Key, i); break;
                    case double d: w.WriteNumber(kv.Key, Math.Round(d, 3)); break;
                    case bool b: w.WriteBoolean(kv.Key, b); break;
                    default: w.WriteString(kv.Key, Txt.Clean(kv.Value.ToString())); break;
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        // ---- external merge sort ----

        /// <summary>One sorted source of encoded events. Rel runs sort by (Rel, Order) and get the offset added at merge time.</summary>
        private abstract class Run : IDisposable
        {
            public bool IsRel;
            public double T;
            public int Order;
            public byte[] Buf = Array.Empty<byte>();
            public int Off, Len;
            protected double Key;

            protected abstract bool ReadNext();

            public bool Advance(double offset)
            {
                if (!ReadNext()) return false;
                T = IsRel ? Key + offset : Key;
                return true;
            }

            public virtual void Dispose() { }
        }

        private struct Item
        {
            public double Key;
            public int Order;
            public int Off, Len;
        }

        private sealed class MemRun : Run
        {
            readonly Item[] _items;
            readonly int _n;
            int _i;

            public MemRun(List<Item> items, byte[] arena, bool rel)
            {
                _items = items.ToArray();
                _n = _items.Length;
                Buf = arena;
                IsRel = rel;
            }

            protected override bool ReadNext()
            {
                if (_i >= _n) return false;
                var it = _items[_i++];
                Key = it.Key; Order = it.Order; Off = it.Off; Len = it.Len;
                return true;
            }
        }

        private sealed class FileRun : Run
        {
            readonly FileStream _fs;
            readonly BinaryReader _br;

            public FileRun(string path, bool rel)
            {
                _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                _br = new BinaryReader(_fs);
                IsRel = rel;
                Buf = new byte[1024];
            }

            protected override bool ReadNext()
            {
                if (_fs.Position >= _fs.Length) return false;
                Key = _br.ReadDouble();
                Order = _br.ReadInt32();
                int len = _br.ReadInt32();
                if (Buf.Length < len) Buf = new byte[Math.Max(len, Buf.Length * 2)];
                int got = 0;
                while (got < len)
                {
                    int n = _fs.Read(Buf, got, len - got);
                    if (n <= 0) throw new EndOfStreamException("spill run is truncated");
                    got += n;
                }
                Off = 0; Len = len;
                return true;
            }

            public override void Dispose()
            {
                try { _br.Dispose(); } catch (Exception) { }
                try { _fs.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>Array min-heap of runs by (T, Order).</summary>
        private sealed class RunHeap
        {
            readonly Run[] _a;
            public int Count;

            public RunHeap(int cap) { _a = new Run[Math.Max(1, cap)]; }

            public Run Top => _a[0];

            static bool Less(Run x, Run y) => x.T < y.T || (x.T == y.T && x.Order < y.Order);

            public void Push(Run r)
            {
                int i = Count++;
                _a[i] = r;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (!Less(_a[i], _a[p])) break;
                    (_a[i], _a[p]) = (_a[p], _a[i]);
                    i = p;
                }
            }

            public void SiftTop() => Down(0);

            public void Pop()
            {
                _a[0] = _a[--Count];
                _a[Count] = null!;
                if (Count > 0) Down(0);
            }

            void Down(int i)
            {
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < Count && Less(_a[l], _a[m])) m = l;
                    if (r < Count && Less(_a[r], _a[m])) m = r;
                    if (m == i) return;
                    (_a[i], _a[m]) = (_a[m], _a[i]);
                    i = m;
                }
            }
        }

        private sealed class Spill : IDisposable
        {
            readonly string _dir;
            readonly int _max;
            MemoryStream _arena = new MemoryStream(1 << 20);
            readonly List<Item> _abs = new List<Item>();   // T known: key = T
            readonly List<Item> _rel = new List<Item>();   // runtime-time events: key = Rel
            readonly List<(string Path, bool Rel)> _files = new List<(string, bool)>();
            public long CountRel;
            public int RunsWritten => _files.Count;

            public Spill(string dir, int maxEvents) { _dir = dir; _max = Math.Max(1, maxEvents); }

            public void Add(bool rel, double key, int order, byte[] src, int off, int len)
            {
                int o = (int)_arena.Length;
                _arena.Write(src, off, len);
                (rel ? _rel : _abs).Add(new Item { Key = key, Order = order, Off = o, Len = len });
                if (rel) CountRel++;
                if (_abs.Count + _rel.Count >= _max || _arena.Length >= MaxBytesPerRun) FlushRun();
            }

            static int Cmp(Item x, Item y)
            {
                int c = x.Key.CompareTo(y.Key);
                return c != 0 ? c : x.Order.CompareTo(y.Order);
            }

            void WriteFile(List<Item> items, bool rel)
            {
                if (items.Count == 0) return;
                items.Sort(Cmp);
                Directory.CreateDirectory(_dir);
                string path = Path.Combine(_dir, "run" + _files.Count.ToString(CultureInfo.InvariantCulture) + (rel ? ".rel" : ".abs"));
                byte[] buf = _arena.GetBuffer();
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var bw = new BinaryWriter(fs))
                {
                    foreach (var it in items)
                    {
                        bw.Write(it.Key);
                        bw.Write(it.Order);
                        bw.Write(it.Len);
                        bw.Write(buf, it.Off, it.Len);
                    }
                }
                _files.Add((path, rel));
                items.Clear();
            }

            void FlushRun()
            {
                WriteFile(_abs, false);
                WriteFile(_rel, true);
                _arena.SetLength(0);
            }

            /// <summary>Sorted runs ready to merge. Everything in one batch stays in memory (no temp files).
            /// skipRel drops the runtime-time events (no time offset).</summary>
            public List<Run> Finish(bool skipRel)
            {
                var runs = new List<Run>();
                if (_files.Count == 0)
                {
                    _abs.Sort(Cmp);
                    byte[] arena = _arena.GetBuffer();
                    if (_abs.Count > 0) runs.Add(new MemRun(_abs, arena, false));
                    if (!skipRel && _rel.Count > 0) { _rel.Sort(Cmp); runs.Add(new MemRun(_rel, arena, true)); }
                    return runs;
                }
                FlushRun();
                _arena = new MemoryStream();
                foreach (var f in _files)
                {
                    if (f.Rel && skipRel) continue;
                    runs.Add(new FileRun(f.Path, f.Rel));
                }
                return runs;
            }

            public void DeleteFiles()
            {
                foreach (var f in _files) { try { File.Delete(f.Path); } catch (Exception) { } }
                _files.Clear();
                try { if (Directory.Exists(_dir)) Directory.Delete(_dir); } catch (Exception) { }
            }

            public void Dispose() { DeleteFiles(); }
        }

        private static void Add(Raw r, string name, object? v) => r.F.Add(new KeyValuePair<string, object?>(name, v));

        private static Raw Runtime(TraceEvent e, string src, string name, ref int order)
        {
            return new Raw { Src = src, Name = name, Rel = e.TimeStampRelativeMSec, Order = ++order };
        }

        private static Raw? ReadSparkLoad(TraceEvent e, ref int order)
        {
            int id = (int)e.ID;
            var sc = Schema[0];
            bool found = false;
            foreach (var s in Schema)
            {
                if (s.Id == id) { sc = s; found = true; break; }
            }
            if (!found)
            {
                // Fall back to the event name when the id is not the SPEC id.
                foreach (var s in Schema)
                {
                    if (string.Equals(s.Name, e.EventName, StringComparison.OrdinalIgnoreCase)) { sc = s; found = true; break; }
                }
            }
            if (!found) return null;

            var r = new Raw { Src = "sparkload", Name = sc.Name, Rel = e.TimeStampRelativeMSec, Order = ++order };
            string[] names = e.PayloadNames ?? Array.Empty<string>();
            long qpc = 0;
            bool hasQpc = false;
            for (int i = 0; i < sc.Fields.Length; i++)
            {
                var (fname, isStr) = sc.Fields[i];
                object? v = null;
                int idx = Array.FindIndex(names, n => string.Equals(n, fname, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 && i < names.Length) idx = i;
                if (idx >= 0)
                {
                    try { v = e.PayloadValue(idx); } catch (Exception) { v = null; }
                }
                if (isStr)
                {
                    r.F.Add(new KeyValuePair<string, object?>(fname, v == null ? "" : v.ToString() ?? ""));
                }
                else
                {
                    long l = ToLong(v);
                    r.F.Add(new KeyValuePair<string, object?>(fname, l));
                    if (fname == "qpc") { qpc = l; hasQpc = true; }
                }
            }
            r.Name = sc.Name;
            r.Qpc = qpc;
            r.HasQpc = hasQpc;
            return r;
        }

        private static long ToLong(object? v)
        {
            switch (v)
            {
                case null: return 0;
                case long l: return l;
                case int i: return i;
                case uint ui: return ui;
                case ulong ul: return unchecked((long)ul);
                case short s: return s;
                case ushort us: return us;
                case byte b: return b;
                case sbyte sb: return sb;
                case double d: return (long)d;
                case float f: return (long)f;
                case bool bo: return bo ? 1 : 0;
                case string str: return long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out long p) ? p : 0;
                default:
                    try { return System.Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
            }
        }

        private static long FieldLong(Raw r, string name)
        {
            foreach (var kv in r.F)
                if (kv.Key == name) return ToLong(kv.Value);
            return 0;
        }
    }
}
