using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace SparkVS.Core;

/// <summary>
/// Route A: GC heap dump over EventPipe (the same events dotnet-gcdump uses), aggregated while streaming.
/// Only per-type (count, bytes) is kept. Nodes and edges are never stored.
/// </summary>
public static class HeapDumpEventPipe
{
    const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>
    /// Circular buffer size for the dump session in MB. 0 (default) means auto: 0.9 x the target working set, clamped to 512..4000.
    /// The heap dump emits roughly 0.6 MB of events per MB of live heap and the buffer is committed only as it fills.
    /// A fixed 1024 MB lost events on a 3 GB heap; 2047 and 4000 did not.
    /// </summary>
    public static int CircularBufferMB = 0;

    /// <summary>The buffer size actually used by the most recent Capture call.</summary>
    public static int LastCircularBufferMB;

    static int ChooseBufferMB(int pid)
    {
        if (CircularBufferMB > 0) return CircularBufferMB;
        long ws = 0;
        try { using var p = Process.GetProcessById(pid); ws = p.WorkingSet64; }
        catch { }
        long mb = (long)(ws / 1048576.0 * 0.9);
        return (int)Math.Min(4000, Math.Max(512, mb));
    }

    sealed class TypeInfo
    {
        public string Name = "";
        public ulong ModuleId;
        public ulong[] Params = Array.Empty<ulong>();
    }

    struct Agg { public long Count; public long Bytes; }

    /// <summary>
    /// Takes one live-heap snapshot of the process. EventPipe holds events written by idle threads in buffers
    /// (observed up to about 15 s for the induced GC's own events, or indefinitely), so the dump GC end event can
    /// arrive late. When no event has arrived for a while the session is stopped, which flushes everything, and the
    /// result is checked for the dump GC end.
    /// Complete means finished, no reader error, dump GC observed and zero events lost. Anything else is retried, on every
    /// failure path (losses, timeout, exception), up to maxAttempts times with a larger circular buffer each time. Each attempt
    /// forces a full GC and is recorded in HeapSnapshot.AttemptLog. When no attempt completes, the attempt with the most data is
    /// returned (a lower bound, Complete false). timeout applies to each attempt.
    /// onAttempt is called after every attempt (use it to log the forced GC as a tool event).
    /// </summary>
    public static HeapSnapshot Capture(int pid, TimeSpan timeout, Action<string>? log = null,
        int maxAttempts = SnapshotPolicy.DefaultAttempts, Action<SnapshotAttempt>? onAttempt = null)
    {
        if (maxAttempts < 1) maxAttempts = 1;
        var total = Stopwatch.StartNew();
        var attempts = new List<SnapshotAttempt>();
        HeapSnapshot? best = null;
        int baseMb = ChooseBufferMB(pid);
        for (int attempt = 0; ; attempt++)
        {
            int bufferMb = SnapshotPolicy.BufferForAttempt(baseMb, attempt);
            var one = CaptureOnce(pid, timeout, log, attempt, bufferMb);
            var rec = new SnapshotAttempt
            {
                Attempt = attempt + 1,
                BufferMB = bufferMb,
                GcPauseMs = one.GcPauseMs > 0 ? one.GcPauseMs : -1,
                EventsLost = one.EventsLost,
                Complete = one.Complete,
                ElapsedMs = one.ElapsedMs,
                Outcome = OutcomeOf(one),
            };
            attempts.Add(rec);
            try { onAttempt?.Invoke(rec); } catch { }
            if (best == null || one.Complete || one.LiveBytes > best.LiveBytes) best = one;
            if (one.Complete || attempt + 1 >= maxAttempts) break;
            log?.Invoke("snapshot attempt " + (attempt + 1) + " of " + maxAttempts + " not complete (" + rec.Outcome + "), retrying with a larger buffer");
        }
        var snap = best!;
        snap.AttemptLog = attempts;
        snap.Attempts = attempts.Count;
        snap.ElapsedMs = total.Elapsed.TotalMilliseconds;
        if (attempts.Count > 1)
            snap.Warnings.Add((snap.Complete ? "needed " : "still incomplete after ") + attempts.Count + " attempts (every attempt forced a full GC)");
        return snap;
    }

    static string OutcomeOf(HeapSnapshot s)
    {
        if (s.Complete) return "complete";
        foreach (var w in s.Warnings)
        {
            if (w.StartsWith("capture failed", StringComparison.Ordinal)) return "exception";
            if (w.StartsWith("timeout", StringComparison.Ordinal)) return "timeout";
            if (w.StartsWith("reader failed", StringComparison.Ordinal)) return "reader error";
        }
        if (s.EventsLost > 0) return "events lost";
        return "dump GC not observed";
    }

    static HeapSnapshot CaptureOnce(int pid, TimeSpan timeout, Action<string>? log, int attempt, int bufferMb)
    {
        var sw = Stopwatch.StartNew();
        double wsGb = 0;
        try { using var p0 = Process.GetProcessById(pid); wsGb = p0.WorkingSet64 / 1073741824.0; } catch { }
        double minWaitMs = (1500 + 1200 * wsGb) * (1 << attempt);
        long lastActivity = Stopwatch.GetTimestamp();
        var snap = new HeapSnapshot
        {
            Route = "A: EventPipe GC heap dump",
            Pid = pid,
            TakenUtc = DateTime.UtcNow,
        };

        var types = new Dictionary<ulong, TypeInfo>();
        var modules = new Dictionary<ulong, string>();
        var agg = new Dictionary<ulong, Agg>();

        int state = 0; // 0 waiting for dump GC, 1 inside dump GC, 2 dump GC finished
        int dumpCount = -1;
        double startMs = 0, endMs = 0;
        long nodeCount = 0;
        long heapSizeAfter = -1;
        EventPipeSession? session = null;
        int stopRequested = 0;

        void StopSession()
        {
            if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
            var s = session;
            if (s == null) return;
            try
            {
                log?.Invoke("stopping session at " + sw.ElapsedMilliseconds + " ms");
                s.Stop();
                log?.Invoke("session stop returned at " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex) { log?.Invoke("session stop failed: " + ex.Message); }
        }

        try
        {
            var client = new DiagnosticsClient(pid);
            long keywords = (long)(ClrTraceEventParser.Keywords.GCHeapSnapshot | ClrTraceEventParser.Keywords.Loader);
            var providers = new[] { new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, keywords) };
            LastCircularBufferMB = bufferMb;
            session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: bufferMb);
            log?.Invoke("event pipe session started");

            Exception? readerError = null;
            long eventsLost = 0;
            var activeSession = session;
            var reader = new Thread(() =>
            {
                try
                {
                    using var source = new EventPipeEventSource(activeSession.EventStream);
                    var clr = new ClrTraceEventParser(source);
                    var rundown = new ClrRundownTraceEventParser(source);
                    clr.TypeBulkType += d =>
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
                    };

                    clr.LoaderModuleLoad += d => AddModule(modules, (ulong)d.ModuleID, d.ModuleILPath, d.ModuleNativePath);
                    rundown.LoaderModuleDCStop += d => AddModule(modules, (ulong)d.ModuleID, d.ModuleILPath, d.ModuleNativePath);

                    clr.GCStart += d =>
                    {
                        log?.Invoke("GCStart #" + d.Count + " depth " + d.Depth + " reason " + d.Reason + " type " + d.Type + " at " + sw.ElapsedMilliseconds + " ms (event time " + d.TimeStampRelativeMSec.ToString("0.0") + ")");
                        if (state == 0 && d.Depth == 2)
                        {
                            state = 1;
                            dumpCount = d.Count;
                            startMs = d.TimeStampRelativeMSec;
                            nodeCount = 0;
                            agg.Clear();
                        }
                    };

                    clr.GCBulkNode += d =>
                    {
                        Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp());
                        if (state != 1) return;
                        int n = d.Count;
                        nodeCount += n;
                        for (int i = 0; i < n; i++)
                        {
                            var v = d.Values(i);
                            ref Agg a = ref CollectionsMarshal.GetValueRefOrAddDefault(agg, (ulong)v.TypeID, out _);
                            a.Count++;
                            a.Bytes += (long)v.Size;
                        }
                    };

                    clr.GCHeapStats += d =>
                    {
                        if (state == 1 || state == 2)
                        {
                            long total = (long)d.TotalHeapSize;
                            if (state == 1 || heapSizeAfter < 0) heapSizeAfter = total;
                        }
                    };

                    clr.GCStop += d =>
                    {
                        log?.Invoke("GCStop #" + d.Count + " depth " + d.Depth + " nodes so far " + nodeCount + " at " + sw.ElapsedMilliseconds + " ms");
                        if (state == 1 && d.Count == dumpCount)
                        {
                            if (nodeCount == 0)
                            {
                                // Not the dump GC (a natural gen2 GC began first). Keep waiting.
                                state = 0;
                                return;
                            }
                            endMs = d.TimeStampRelativeMSec;
                            state = 2;
                            Task.Run(StopSession);
                        }
                    };

                    source.Process();
                    eventsLost = source.EventsLost;
                    log?.Invoke("event stream ended at " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex)
                {
                    readerError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "SparkVS heap dump reader",
            };
            reader.Start();

            bool finished;
            while (true)
            {
                if (reader.Join(50)) { finished = true; break; }
                if (sw.Elapsed > timeout) { finished = false; break; }
                if (Volatile.Read(ref stopRequested) == 0)
                {
                    double quietMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref lastActivity)) * 1000.0 / Stopwatch.Frequency;
                    if (sw.ElapsedMilliseconds >= minWaitMs && quietMs >= 1500)
                    {
                        log?.Invoke("no dump GC end event after " + sw.ElapsedMilliseconds + " ms and " + (long)quietMs + " ms quiet: stopping the session to flush buffered events");
                        StopSession();
                    }
                }
            }
            if (!finished)
            {
                snap.Warnings.Add("timeout after " + (long)timeout.TotalSeconds + " s: heap dump incomplete");
                StopSession();
                reader.Join(TimeSpan.FromSeconds(15));
            }
            if (readerError != null) snap.Warnings.Add("reader failed: " + readerError.GetType().Name + ": " + readerError.Message);

            snap.EventsLost = eventsLost;
            snap.Complete = SnapshotPolicy.IsComplete(finished, readerError != null, state, eventsLost);
            if (state != 2 && finished && readerError == null)
                snap.Warnings.Add("the dump GC was not observed to complete");
            if (eventsLost > 0)
                snap.Warnings.Add("events lost: " + eventsLost + "; per-type numbers are a lower bound and the snapshot is NOT complete");
            if (state == 2)
            {
                snap.GcPauseMs = Math.Max(0, endMs - startMs);
                snap.GcHeapSizeAfterBytes = heapSizeAfter;
            }

            foreach (var kv in agg)
            {
                var st = new HeapTypeStat { TypeId = kv.Key, Count = kv.Value.Count, Bytes = kv.Value.Bytes };
                if (types.TryGetValue(kv.Key, out var ti))
                {
                    st.TypeName = ti.Name;
                    st.Module = ModuleOf(modules, ti.ModuleId);
                    var visited = new HashSet<ulong> { kv.Key };
                    var seenMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectArgModules(types, modules, ti, visited, seenMods, st.ArgModules, 0);
                }
                else
                {
                    st.TypeName = "<type 0x" + kv.Key.ToString("x") + ">";
                }
                snap.Types.Add(st);
                snap.LiveObjects += st.Count;
                snap.LiveBytes += st.Bytes;
            }
            snap.Types.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            int unnamed = 0;
            foreach (var t in snap.Types) if (t.TypeName.StartsWith("<type 0x", StringComparison.Ordinal)) unnamed++;
            if (unnamed > 0) snap.Warnings.Add(unnamed + " types had no type-name event");
        }
        catch (Exception ex)
        {
            snap.Complete = false;
            snap.Warnings.Add("capture failed: " + ex.GetType().Name + ": " + ex.Message);
            StopSession();
        }
        finally
        {
            StopSession();
            try { session?.Dispose(); } catch { }
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            p.Refresh();
            snap.ProcessName = p.ProcessName;
            snap.WorkingSetBytes = p.WorkingSet64;
            snap.PrivateBytes = p.PrivateMemorySize64;
        }
        catch (Exception ex)
        {
            snap.Warnings.Add("process totals unavailable: " + ex.Message);
        }

        sw.Stop();
        snap.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return snap;
    }

    static void AddModule(Dictionary<ulong, string> modules, ulong id, string? il, string? native)
    {
        string path = !string.IsNullOrEmpty(il) ? il : (native ?? "");
        if (path.Length == 0 && modules.ContainsKey(id)) return;
        modules[id] = path;
    }

    static string ModuleOf(Dictionary<ulong, string> modules, ulong id)
        => modules.TryGetValue(id, out var p) ? p : "";

    static void CollectArgModules(Dictionary<ulong, TypeInfo> types, Dictionary<ulong, string> modules,
        TypeInfo ti, HashSet<ulong> visited, HashSet<string> seenMods, List<string> result, int depth)
    {
        if (depth > 8) return;
        foreach (var pid in ti.Params)
        {
            if (!visited.Add(pid)) continue;
            if (!types.TryGetValue(pid, out var pt)) continue;
            string m = ModuleOf(modules, pt.ModuleId);
            if (m.Length > 0 && seenMods.Add(m)) result.Add(m);
            CollectArgModules(types, modules, pt, visited, seenMods, result, depth + 1);
        }
    }
}
