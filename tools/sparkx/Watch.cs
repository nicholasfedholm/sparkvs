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
        public string Label = "";
        public string Dir = "";
        public MemoryTable Table = new();
    }

    public const double DefaultStartDelaySec = 60;
    public const double DefaultStartMaxWaitSec = 600;
    public const string TimelineHeader = "utc,elapsed_s,working_set_bytes,private_bytes,gc_heap_bytes,gross_alloc_bytes_total,marker\n";

    public const string GrossNote ="estimated from sampled allocation ticks (about one per 100 KB), total over the whole watch; not live RAM";

    public static int Run(Args args)
    {
        var inv = CultureInfo.InvariantCulture;
        int pid = Program.ResolvePid(args);
        if (pid < 0) return 2;
        double minutes = Math.Max(0.1, args.GetDouble("minutes", 10));
        double everyMin = args.GetDouble("snapshot-every", 0);
        int top = Math.Clamp(args.GetInt("top", 20), 1, 200);
        string outDir = args.Get("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "sparkx-watch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", inv));
        double startDelay = Math.Max(0, args.GetDouble("start-delay", DefaultStartDelaySec));
        double startMaxWait = Math.Max(startDelay, args.GetDouble("start-max-wait", DefaultStartMaxWaitSec));
        int maxAttempts = Math.Clamp(args.GetInt("snapshot-attempts", SnapshotPolicy.DefaultAttempts), 1, 10);
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

        var timeline = new StringBuilder();
        timeline.Append(TimelineHeader);
        var timelineLock = new object();
        string timelinePath = Path.Combine(outDir, "timeline.csv");
        var heapSamples = new List<long>();
        var wsSamples = new List<long>();
        int watchEnded = 0;

        // Marker rows carry the snapshot stamp in the trailing marker column; the numeric columns are empty.
        void AddMarker(string text)
        {
            lock (timelineLock)
            {
                timeline.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", inv)).Append(',')
                    .Append(clock.Elapsed.TotalSeconds.ToString("0.0", inv)).Append(",,,,,").Append(text).Append('\n');
                try { File.WriteAllText(timelinePath, timeline.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }

        Snap TakeSnapshot(string label, string startCondition = "")
        {
            int idx = Interlocked.Increment(ref snapIndex) - 1;
            double at = clock.Elapsed.TotalSeconds;
            Console.WriteLine("snapshot " + idx.ToString(inv) + " (" + label + ") at " + at.ToString("0", inv) + " s ...");
            AddMarker("snapshot:" + label + " begin max_attempts=" + maxAttempts.ToString(inv));
            HeapSnapshot snap;
            // The runtime logs each type name once per Type-keyword epoch, so the alloc monitor session must not be
            // running while a dump session starts, or the dump would get no type names. Pause it, dump, restart it.
            lock (snapGate)
            {
                monitor.Stop();
                try
                {
                    snap = HeapDumpEventPipe.Capture(pid, TimeSpan.FromMinutes(10), null, maxAttempts, a =>
                    {
                        Console.WriteLine("  attempt " + a.Attempt.ToString(inv) + ": " + a.Outcome + ", buffer " + a.BufferMB.ToString(inv) + " MB, events lost "
                            + a.EventsLost.ToString(inv) + ", gc pause " + (a.GcPauseMs >= 0 ? a.GcPauseMs.ToString("0", inv) + " ms" : "n/a"));
                        AddMarker("snapshot:" + label + " attempt=" + a.Attempt.ToString(inv) + " outcome=" + a.Outcome.Replace(' ', '-')
                            + " events_lost=" + a.EventsLost.ToString(inv) + " gc_pause_ms=" + (a.GcPauseMs >= 0 ? a.GcPauseMs.ToString("0", inv) : "n/a"));
                    });
                }
                finally
                {
                    try { monitor.Start(pid); }
                    catch (Exception ex) { Console.Error.WriteLine("allocation monitor restart failed: " + ex.Message); }
                }
            }
            snap.StartCondition = startCondition;
            AddMarker(SnapshotPolicy.Marker(label, snap));
            var table = MemoryReport.Build(snap, resolver, null, "");
            var sn = new Snap { Index = idx, AtSeconds = at, Label = label, Table = table, Dir = Path.Combine(outDir, "snapshot-" + idx.ToString("00", inv)) };
            lock (snapLock) snaps.Add(sn);
            Console.WriteLine("snapshot " + idx.ToString(inv) + " done: pause " + snap.GcPauseMs.ToString("0", inv) + " ms, complete " + snap.Complete + ", events lost " + snap.EventsLost.ToString(inv)
                + ", attempts " + snap.Attempts.ToString(inv));
            return sn;
        }

        // First snapshot: wait for the target to settle. At least startDelay seconds after attach AND GC heap bytes changed under 5 %
        // across the last 3 timeline samples; give up after startMaxWait seconds and take it anyway, recorded as start_condition=timeout.
        // If the watch ends first the snapshot is taken at once and also recorded as timeout.
        Snap StartSnapshot()
        {
            string cond = "stable";
            string basis = "gc_heap";
            AddMarker("start-wait:begin delay_s=" + startDelay.ToString("0.##", inv) + " max_wait_s=" + startMaxWait.ToString("0.##", inv));
            Console.WriteLine("first snapshot waits for at least " + startDelay.ToString("0.##", inv) + " s and a stable heap (gives up after " + startMaxWait.ToString("0.##", inv) + " s)");
            while (true)
            {
                double el = clock.Elapsed.TotalSeconds;
                if (Volatile.Read(ref watchEnded) != 0 || cts.IsCancellationRequested) { cond = "timeout"; basis = "watch ended"; break; }
                if (el >= startMaxWait) { cond = "timeout"; basis = "max wait reached"; break; }
                if (el >= startDelay)
                {
                    bool stable;
                    lock (timelineLock)
                    {
                        if (heapSamples.Count > 0) { stable = SnapshotPolicy.HeapStable(heapSamples); basis = "gc_heap"; }
                        else { stable = SnapshotPolicy.HeapStable(wsSamples); basis = "working_set (no GC heap data)"; }
                    }
                    if (stable) break;
                }
                cts.Token.WaitHandle.WaitOne(500);
            }
            AddMarker("start-wait:end start_condition=" + cond + " basis=" + basis.Replace(' ', '-'));
            Console.WriteLine("first snapshot start_condition=" + cond + " (" + basis + ") at " + clock.Elapsed.TotalSeconds.ToString("0", inv) + " s");
            var sn = TakeSnapshot("start", cond);
            if (cond == "timeout") sn.Table.Snapshot.Warnings.Add("start_condition=timeout: the target was not observed stable (" + basis + "); the first snapshot may not be a quiet baseline");
            return sn;
        }

        // The first snapshot runs in the background while the timeline sampling begins.
        pending.Add(Task.Run(StartSnapshot));

        double totalSec = minutes * 60.0;
        double nextSnapSec = everyMin > 0 ? everyMin * 60.0 : double.MaxValue;
        double nextSample = 0;
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
                    long heapNow = monitor.LastHeapBytes;
                    lock (timelineLock)
                    {
                        if (heapNow > 0) heapSamples.Add(heapNow);
                        wsSamples.Add(p.WorkingSet64);
                        timeline.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", inv)).Append(',')
                            .Append(el.ToString("0.0", inv)).Append(',')
                            .Append(p.WorkingSet64.ToString(inv)).Append(',')
                            .Append(p.PrivateMemorySize64.ToString(inv)).Append(',')
                            .Append(heapNow >= 0 ? heapNow.ToString(inv) : "").Append(',')
                            .Append(monitor.TotalGross.ToString(inv)).Append(",\n");
                        File.WriteAllText(timelinePath, timeline.ToString(), new UTF8Encoding(false));
                    }
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
        Volatile.Write(ref watchEnded, 1);
        try { Task.WaitAll(pending.ToArray()); } catch (AggregateException ex) { Console.Error.WriteLine("snapshot failed: " + ex.InnerException?.Message); }

        Snap? last = null;
        try { last = TakeSnapshot("end"); }
        catch (Exception ex) { Console.Error.WriteLine("end snapshot failed: " + ex.Message); }
        lock (timelineLock) File.WriteAllText(timelinePath, timeline.ToString(), new UTF8Encoding(false));

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
        int markerRows = timeline.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains(",snapshot:") || l.Contains(",start-wait:"));
        Console.WriteLine("timeline rows: " + rows.ToString(inv) + " (marker rows: " + markerRows.ToString(inv) + ")");
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

        bool anyPartial = snaps.Any(x => !x.Table.Snapshot.Complete);
        if (anyPartial)
            sb.Append("\n> PARTIAL: at least one snapshot is not complete (events lost, no dump GC end seen, or an error). Numbers marked PARTIAL come from such a snapshot and are lower bounds; a difference that involves one is not a measured change.\n");
        sb.Append("\n## Snapshots\n\n| # | label | at s | complete | events lost | attempts | start condition | dump GC pause ms | live MB | folder |\n|---:|---|---:|---|---:|---:|---|---:|---:|---|\n");
        foreach (var s in snaps)
        {
            var sn = s.Table.Snapshot;
            sb.Append("| ").Append(s.Index.ToString(inv)).Append(" | ").Append(s.Label).Append(" | ").Append(s.AtSeconds.ToString("0", inv))
                .Append(" | ").Append(sn.Complete ? "yes" : "NO").Append(" | ").Append(sn.EventsLost.ToString(inv))
                .Append(" | ").Append(sn.Attempts.ToString(inv)).Append(" | ").Append(sn.StartCondition.Length > 0 ? sn.StartCondition : "n/a")
                .Append(" | ").Append(sn.GcPauseMs.ToString("0", inv)).Append(" | ").Append(MemoryReport.Mb(s.Table.TotalLiveBytes).ToString("0.0", inv)).Append(sn.Complete ? "" : " PARTIAL")
                .Append(" | ").Append(Path.GetFileName(s.Dir)).Append(" |\n");
            foreach (var a in sn.AttemptLog)
                sb.Append("|  |  |  |  |  |  |  |  |  | attempt ").Append(a.Attempt.ToString(inv)).Append(": ").Append(a.Outcome).Append(", buffer ").Append(a.BufferMB.ToString(inv))
                    .Append(" MB, events lost ").Append(a.EventsLost.ToString(inv)).Append(", gc pause ").Append(a.GcPauseMs >= 0 ? a.GcPauseMs.ToString("0", inv) + " ms" : "n/a").Append(" |\n");
            foreach (var w in sn.Warnings) sb.Append("|  |  |  |  |  |  |  |  |  | WARNING: ").Append(w.Replace("|", "/")).Append(" |\n");
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
            bool sp = !first.Snapshot.Complete, ep = !lastT.Snapshot.Complete;
            if (sp || ep)
                sb.Append("PARTIAL (lower bound): ").Append(sp && ep ? "both the start and the end snapshot are" : sp ? "the start snapshot is" : "the end snapshot is")
                    .Append(" not complete, so the marked numbers are lower bounds and every delta here is PARTIAL.\n\n");
            sb.Append("| owner | kind | start MB | end MB | delta MB | gross alloc MB (estimated, whole watch) |\n|---|---|---:|---:|---:|---:|\n");
            int mods = 0;
            foreach (var r in rows)
            {
                if (r.Kind == OwnerKind.Mod) { if (mods >= top) continue; mods++; }
                sb.Append("| ").Append(r.Id.Replace("|", "/")).Append(" | ").Append(r.Kind.ToString().ToLowerInvariant())
                    .Append(" | ").Append(MemoryReport.Mb(r.Start).ToString("0.00", inv)).Append(sp ? " PARTIAL" : "")
                    .Append(" | ").Append(MemoryReport.Mb(r.End).ToString("0.00", inv)).Append(ep ? " PARTIAL" : "")
                    .Append(" | ").Append(MemoryReport.Mb(r.End - r.Start).ToString("+0.00;-0.00;0.00", inv)).Append(sp || ep ? " PARTIAL" : "")
                    .Append(" | ").Append(r.Gross.HasValue ? MemoryReport.Mb(r.Gross.Value).ToString("0.00", inv) : "n/a").Append(" |\n");
            }

            sb.Append("\n## Live MB by assembly, end snapshot (top 15; full list in snapshot-NN\\memory.json)").Append(ep ? " PARTIAL" : "").Append("\n\n");
            sb.Append("| bucket | owner kind | owner | live MB | live objects |\n|---|---|---|---:|---:|\n");
            foreach (var a in lastT.ByAssembly.Take(15))
                sb.Append("| ").Append(a.Bucket.Replace("|", "/")).Append(" | ").Append(a.Kind.ToString().ToLowerInvariant()).Append(" | ").Append(a.OwnerId.Replace("|", "/"))
                    .Append(" | ").Append(MemoryReport.Mb(a.Bytes).ToString("0.00", inv)).Append(ep ? " PARTIAL" : "").Append(" | ").Append(a.Count.ToString("N0", inv)).Append(" |\n");
            var unk = lastT.Rows.FirstOrDefault(x => x.Kind == OwnerKind.Unknown);
            if (unk != null)
            {
                sb.Append("\nUnknown at the end: ").Append(MemoryReport.Mb(unk.LiveBytes).ToString("0.00", inv)).Append(" MB").Append(ep ? " PARTIAL" : "")
                    .Append(". Reasons: ").Append(lastT.UnknownReasons.Count == 0 ? "none" : string.Join("; ", lastT.UnknownReasons.Select(u => u.Reason + " " + MemoryReport.Mb(u.Bytes).ToString("0.00", inv) + " MB")))
                    .Append(". Top 10 unknown types are in the end snapshot's memory.md.\n");
            }
        }
        return sb.ToString();
    }
}
