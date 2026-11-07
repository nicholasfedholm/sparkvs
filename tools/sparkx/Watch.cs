using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using SparkVS.Core;

namespace SparkX;

/// <summary>
/// Separate EventPipe session that counts GCAllocationTick amounts per type (about one tick per 100 KB allocated).
/// Gross allocations are estimates from sampled ticks and are never live RAM.
/// </summary>
public sealed class AllocMonitor
{
    sealed class TypeInfo
    {
        public string Name = "";
        public ulong ModuleId;
        public ulong[] Params = Array.Empty<ulong>();
    }

    readonly Dictionary<ulong, long> grossByType = new();
    readonly Dictionary<ulong, TypeInfo> types = new();
    readonly Dictionary<ulong, string> modules = new();
    long totalGross;
    long lastHeap = -1;
    EventPipeSession? session;
    Thread? reader;
    string? error;

    public long TotalGross => Interlocked.Read(ref totalGross);
    public long LastHeapBytes => Interlocked.Read(ref lastHeap);
    public string? Error => error;

    public void Start(int pid)
    {
        var client = new DiagnosticsClient(pid);
        long keywords = (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Type | ClrTraceEventParser.Keywords.Loader);
        var providers = new[] { new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, keywords) };
        session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 256);
        var s = session;
        reader = new Thread(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(s.EventStream);
                var clr = new ClrTraceEventParser(source);
                var rundown = new ClrRundownTraceEventParser(source);
                clr.TypeBulkType += d =>
                {
                    lock (types)
                    {
                        for (int i = 0; i < d.Count; i++)
                        {
                            var v = d.Values(i);
                            var info = new TypeInfo { Name = v.TypeName ?? "", ModuleId = (ulong)v.ModuleID };
                            int pc = (int)v.TypeParameterCount;
                            if (pc > 0)
                            {
                                var ids = new ulong[pc];
                                for (int k = 0; k < pc; k++) ids[k] = (ulong)v.TypeParameterID(k);
                                info.Params = ids;
                            }
                            types[(ulong)v.TypeID] = info;
                        }
                    }
                };
                clr.LoaderModuleLoad += d => AddModule((ulong)d.ModuleID, d.ModuleILPath, d.ModuleNativePath);
                rundown.LoaderModuleDCStop += d => AddModule((ulong)d.ModuleID, d.ModuleILPath, d.ModuleNativePath);
                clr.GCAllocationTick += d =>
                {
                    long amount = d.AllocationAmount64;
                    Interlocked.Add(ref totalGross, amount);
                    lock (grossByType)
                    {
                        grossByType.TryGetValue((ulong)d.TypeID, out long cur);
                        grossByType[(ulong)d.TypeID] = cur + amount;
                        lock (types)
                        {
                            if (!types.ContainsKey((ulong)d.TypeID)) types[(ulong)d.TypeID] = new TypeInfo { Name = d.TypeName ?? "" };
                        }
                    }
                };
                clr.GCHeapStats += d => Interlocked.Exchange(ref lastHeap, (long)d.TotalHeapSize);
                source.Process();
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }
        })
        { IsBackground = true, Name = "sparkx alloc monitor" };
        reader.Start();
    }

    void AddModule(ulong id, string? il, string? native)
    {
        string path = !string.IsNullOrEmpty(il) ? il : (native ?? "");
        lock (modules)
        {
            if (path.Length == 0 && modules.ContainsKey(id)) return;
            modules[id] = path;
        }
    }

    /// <summary>Stops the session (flushing rundown) and waits for the reader to finish.</summary>
    public void Stop()
    {
        try { session?.Stop(); } catch { }
        reader?.Join(TimeSpan.FromSeconds(30));
        try { session?.Dispose(); } catch { }
    }

    string ModuleOf(ulong id)
    {
        lock (modules) return modules.TryGetValue(id, out var p) ? p : "";
    }

    /// <summary>Gross allocated bytes per owner id, using the same owner rule as live bytes.</summary>
    /// <summary>
    /// Allocation ticks carry the type id but the runtime may not log the module for it, so module and type-arg modules
    /// are taken from the type table of the live snapshots (same type id) when the monitor has none. Types that were never
    /// live at a snapshot and have no module stay unknown.
    /// </summary>
    public Dictionary<string, long> GrossByOwner(IModResolver r, IEnumerable<HeapSnapshot> snapshots)
    {
        var fromSnaps = new Dictionary<ulong, HeapTypeStat>();
        foreach (var sn in snapshots)
            foreach (var t in sn.Types)
                if (t.Module.Length > 0 || t.ArgModules.Count > 0) fromSnaps[t.TypeId] = t;
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (grossByType)
        lock (types)
        {
            foreach (var kv in grossByType)
            {
                var st = new HeapTypeStat { TypeId = kv.Key, Bytes = kv.Value, Count = 0 };
                if (types.TryGetValue(kv.Key, out var ti))
                {
                    st.TypeName = ti.Name;
                    st.Module = ModuleOf(ti.ModuleId);
                    var visited = new HashSet<ulong> { kv.Key };
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Collect(ti, visited, seen, st.ArgModules, 0);
                }
                if (st.Module.Length == 0 && st.ArgModules.Count == 0 && fromSnaps.TryGetValue(kv.Key, out var ss))
                {
                    st.Module = ss.Module;
                    st.ArgModules = ss.ArgModules;
                }
                var o = MemoryReport.OwnerOf(st, r, out _);
                result.TryGetValue(o.Id, out long cur);
                result[o.Id] = cur + kv.Value;
            }
        }
        return result;
    }

    void Collect(TypeInfo ti, HashSet<ulong> visited, HashSet<string> seen, List<string> result, int depth)
    {
        if (depth > 8) return;
        foreach (var pid in ti.Params)
        {
            if (!visited.Add(pid)) continue;
            if (!types.TryGetValue(pid, out var pt)) continue;
            string m = ModuleOf(pt.ModuleId);
            if (m.Length > 0 && seen.Add(m)) result.Add(m);
            Collect(pt, visited, seen, result, depth + 1);
        }
    }
}

public static class Watch
{
    sealed class Snap
    {
        public int Index;
        public double AtSeconds;
        public string Dir = "";
        public MemoryTable Table = new();
    }

    public const string GrossNote = "estimated from sampled allocation ticks (about one per 100 KB), total over the whole watch; not live RAM";

    public static int Run(Args args)
    {
        var inv = CultureInfo.InvariantCulture;
        int pid = Program.ResolvePid(args);
        if (pid < 0) return 2;
        double minutes = Math.Max(0.1, args.GetDouble("minutes", 10));
        double everyMin = args.GetDouble("snapshot-every", 0);
        int top = Math.Clamp(args.GetInt("top", 20), 1, 200);
        string outDir = args.Get("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "sparkx-watch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", inv));
        Directory.CreateDirectory(outDir);

        var resolver = ExternalModResolver.Create(pid, args.Get("data"));
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;

        var monitor = new AllocMonitor();
        try { monitor.Start(pid); }
        catch (Exception ex) { Console.Error.WriteLine("allocation monitor not started: " + ex.Message); }

        var snaps = new List<Snap>();
        var snapLock = new object();
        var snapGate = new object();
        var pending = new List<Task>();
        int snapIndex = 0;
        var clock = Stopwatch.StartNew();

        Snap TakeSnapshot(string label)
        {
            int idx = Interlocked.Increment(ref snapIndex) - 1;
            double at = clock.Elapsed.TotalSeconds;
            Console.WriteLine("snapshot " + idx.ToString(inv) + " (" + label + ") at " + at.ToString("0", inv) + " s ...");
            HeapSnapshot snap;
            // The runtime logs each type name once per Type-keyword epoch, so the alloc monitor session must not be
            // running while a dump session starts, or the dump would get no type names. Pause it, dump, restart it.
            lock (snapGate)
            {
                monitor.Stop();
                try { snap = HeapDumpEventPipe.Capture(pid, TimeSpan.FromMinutes(10)); }
                finally
                {
                    try { monitor.Start(pid); }
                    catch (Exception ex) { Console.Error.WriteLine("allocation monitor restart failed: " + ex.Message); }
                }
            }
            var table = MemoryReport.Build(snap, resolver, null, "");
            var sn = new Snap { Index = idx, AtSeconds = at, Table = table, Dir = Path.Combine(outDir, "snapshot-" + idx.ToString("00", inv)) };
            lock (snapLock) snaps.Add(sn);
            Console.WriteLine("snapshot " + idx.ToString(inv) + " done: pause " + snap.GcPauseMs.ToString("0", inv) + " ms, complete " + snap.Complete + ", events lost " + snap.EventsLost.ToString(inv));
            return sn;
        }

        // Snapshot at start runs in the background while the timeline sampling begins.
        pending.Add(Task.Run(() => TakeSnapshot("start")));

        var timeline = new StringBuilder();
        timeline.Append("utc,elapsed_s,working_set_bytes,private_bytes,gc_heap_bytes,gross_alloc_bytes_total\n");
        double totalSec = minutes * 60.0;
        double nextSnapSec = everyMin > 0 ? everyMin * 60.0 : double.MaxValue;
        double nextSample = 0;
        string timelinePath = Path.Combine(outDir, "timeline.csv");
        Console.WriteLine("watching pid " + pid.ToString(inv) + " for " + minutes.ToString("0.##", inv) + " min; Ctrl+C ends early and still writes the outputs");

        while (!cts.IsCancellationRequested)
        {
            double el = clock.Elapsed.TotalSeconds;
            if (el >= totalSec) break;
            if (el >= nextSample)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Refresh();
                    timeline.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", inv)).Append(',')
                        .Append(el.ToString("0.0", inv)).Append(',')
                        .Append(p.WorkingSet64.ToString(inv)).Append(',')
                        .Append(p.PrivateMemorySize64.ToString(inv)).Append(',')
                        .Append(monitor.LastHeapBytes >= 0 ? monitor.LastHeapBytes.ToString(inv) : "").Append(',')
                        .Append(monitor.TotalGross.ToString(inv)).Append('\n');
                    File.WriteAllText(timelinePath, timeline.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("target process gone or unreadable: " + ex.Message);
                    break;
                }
                nextSample += 5;
            }
            if (el >= nextSnapSec && el + 1 < totalSec)
            {
                nextSnapSec += everyMin * 60.0;
                pending.Add(Task.Run(() => TakeSnapshot("every " + everyMin.ToString("0.##", inv) + " min")));
            }
            cts.Token.WaitHandle.WaitOne(250);
        }

        bool early = cts.IsCancellationRequested;
        try { Task.WaitAll(pending.ToArray()); } catch (AggregateException ex) { Console.Error.WriteLine("snapshot failed: " + ex.InnerException?.Message); }

        Snap? last = null;
        try { last = TakeSnapshot("end"); }
        catch (Exception ex) { Console.Error.WriteLine("end snapshot failed: " + ex.Message); }
        File.WriteAllText(timelinePath, timeline.ToString(), new UTF8Encoding(false));

        monitor.Stop();
        Console.CancelKeyPress -= onCancel;
        List<Snap> ordered;
        lock (snapLock) ordered = snaps.OrderBy(s => s.Index).ToList();
        Dictionary<string, long>? gross = null;
        try { gross = monitor.GrossByOwner(resolver, ordered.Select(x => x.Table.Snapshot).ToList()); }
        catch (Exception ex) { Console.Error.WriteLine("gross attribution failed: " + ex.Message); }
        if (monitor.Error != null) Console.Error.WriteLine("allocation monitor error: " + monitor.Error);

        // Rebuild the last snapshot's table with the gross column so its memory.csv carries it.
        if (last != null && gross != null)
        {
            last.Table = MemoryReport.Build(last.Table.Snapshot, resolver, gross, GrossNote);
        }
        foreach (var sn in ordered)
        {
            MemoryReport.Write(sn.Dir, sn.Table, top, Program.FrontEnd);
        }

        string report = BuildReport(ordered, gross, resolver, top, minutes, early, clock.Elapsed.TotalSeconds, pid, monitor);
        File.WriteAllText(Path.Combine(outDir, "report.md"), report, new UTF8Encoding(false));
        int rows = timeline.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        Console.WriteLine("timeline rows: " + rows.ToString(inv));
        Console.WriteLine("wrote " + Path.GetFullPath(outDir) + "\\report.md, timeline.csv, snapshot-NN\\memory.*");
        return ordered.All(s => s.Table.Snapshot.Complete) ? 0 : 1;
    }

    static string BuildReport(List<Snap> snaps, Dictionary<string, long>? gross, IModResolver resolver, int top,
        double minutes, bool early, double elapsedSec, int pid, AllocMonitor monitor)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# sparkx watch report\n\n");
        sb.Append("- Front end: ").Append(Program.FrontEnd).Append('\n');
        sb.Append("- Target pid: ").Append(pid.ToString(inv)).Append('\n');
        sb.Append("- Duration: ").Append(elapsedSec.ToString("0", inv)).Append(" s of ").Append((minutes * 60).ToString("0", inv)).Append(" s requested")
            .Append(early ? " (ended early)" : "").Append('\n');
        sb.Append("- Mod mapping: ").Append(resolver.Describe).Append('\n');
        sb.Append("- Gross allocations: ").Append(GrossNote).Append("; total ").Append(MemoryReport.Mb(monitor.TotalGross).ToString("0.0", inv)).Append(" MB\n\n");
        sb.Append("## Honest limits\n\n");
        for (int i = 0; i < MemoryReport.HonestLimits.Length; i++)
            sb.Append((i + 1).ToString(inv)).Append(". ").Append(MemoryReport.HonestLimits[i]).Append('\n');

        sb.Append("\n## Snapshots\n\n| # | at s | complete | events lost | dump GC pause ms | live MB | folder |\n|---:|---:|---|---:|---:|---:|---|\n");
        foreach (var s in snaps)
        {
            var sn = s.Table.Snapshot;
            sb.Append("| ").Append(s.Index.ToString(inv)).Append(" | ").Append(s.AtSeconds.ToString("0", inv))
                .Append(" | ").Append(sn.Complete ? "yes" : "NO").Append(" | ").Append(sn.EventsLost.ToString(inv))
                .Append(" | ").Append(sn.GcPauseMs.ToString("0", inv)).Append(" | ").Append(MemoryReport.Mb(s.Table.TotalLiveBytes).ToString("0.0", inv))
                .Append(" | ").Append(Path.GetFileName(s.Dir)).Append(" |\n");
            foreach (var w in sn.Warnings) sb.Append("|  |  |  |  |  |  | WARNING: ").Append(w.Replace("|", "/")).Append(" |\n");
        }

        if (snaps.Count >= 1)
        {
            var first = snaps[0].Table;
            var lastT = snaps[snaps.Count - 1].Table;
            var startMap = first.Rows.ToDictionary(r => r.Kind + "|" + r.Id, r => r);
            var endMap = lastT.Rows.ToDictionary(r => r.Kind + "|" + r.Id, r => r);
            var keys = startMap.Keys.Union(endMap.Keys).ToList();
            var rows = keys.Select(k =>
            {
                startMap.TryGetValue(k, out var a);
                endMap.TryGetValue(k, out var b);
                var any = b ?? a!;
                long g = 0;
                bool hasG = gross != null && gross.TryGetValue(any.Id, out g);
                return new { any.Id, any.Name, any.Kind, Start = a?.LiveBytes ?? 0, End = b?.LiveBytes ?? 0, Gross = hasG ? g : (long?)null };
            }).OrderByDescending(r => r.End).ThenByDescending(r => r.Start).ToList();

            sb.Append("\n## Live MB per owner, start vs end (snapshot ").Append(snaps[0].Index.ToString(inv)).Append(" to ")
                .Append(snaps[snaps.Count - 1].Index.ToString(inv)).Append(")\n\n");
            sb.Append("| owner | kind | start MB | end MB | delta MB | gross alloc MB (estimated, whole watch) |\n|---|---|---:|---:|---:|---:|\n");
            int mods = 0;
            foreach (var r in rows)
            {
                if (r.Kind == OwnerKind.Mod) { if (mods >= top) continue; mods++; }
                sb.Append("| ").Append(r.Id.Replace("|", "/")).Append(" | ").Append(r.Kind.ToString().ToLowerInvariant())
                    .Append(" | ").Append(MemoryReport.Mb(r.Start).ToString("0.00", inv))
                    .Append(" | ").Append(MemoryReport.Mb(r.End).ToString("0.00", inv))
                    .Append(" | ").Append(MemoryReport.Mb(r.End - r.Start).ToString("+0.00;-0.00;0.00", inv))
                    .Append(" | ").Append(r.Gross.HasValue ? MemoryReport.Mb(r.Gross.Value).ToString("0.00", inv) : "n/a").Append(" |\n");
            }
        }
        return sb.ToString();
    }
}
