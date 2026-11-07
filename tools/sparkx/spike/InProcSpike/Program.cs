using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using SparkVS.Core;

namespace InProcSpike;

// Known-answer payload (same shape as tools/sparkx/KnownAnswerTarget, lives in assembly InProcSpike).
public sealed class PayloadNode
{
    public long F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14;
}

public struct PayloadCell
{
    public long A, B, C, D;
}

sealed class SpikeResolver : IModResolver
{
    public string Describe => "in-process spike: InProcSpike.dll is the known-answer mod, System.* and Microsoft.* are shared";

    public Owner Resolve(string module)
    {
        string f = Path.GetFileName(module.Replace('/', '\\'));
        if (f.Equals("InProcSpike.dll", StringComparison.OrdinalIgnoreCase) || f.Equals("InProcSpike", StringComparison.OrdinalIgnoreCase))
            return new Owner(OwnerKind.Mod, "known-answer", "InProcSpike");
        if (f.StartsWith("System", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || f.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) || f.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            return new Owner(OwnerKind.Shared, MemoryReport.SharedId, MemoryReport.SharedId);
        return new Owner(OwnerKind.Unknown, MemoryReport.UnknownId, MemoryReport.UnknownId);
    }
}

public static class Program
{
    const int NodeCount = 200_000;
    const int CellCount = 700_000;

    static long maxGapTicks;
    static volatile bool heartbeatRun = true;

    public static int Main(string[] args)
    {
        var inv = CultureInfo.InvariantCulture;
        long ballastMb = 0;
        if (args.Length > 0) long.TryParse(args[0], NumberStyles.Integer, inv, out ballastMb);
        if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, inv, out int buf)) HeapDumpEventPipe.CircularBufferMB = buf;

        var swAlloc = Stopwatch.StartNew();
        object ballast = Ballast.Allocate(ballastMb);
        Console.WriteLine("ballast " + ballastMb.ToString(inv) + " MB allocated in " + swAlloc.ElapsedMilliseconds.ToString(inv) + " ms");

        var hb = new Thread(Heartbeat) { IsBackground = true, Name = "heartbeat" };
        hb.Start();

        long before = GC.GetTotalMemory(true);
        var nodes = new PayloadNode[NodeCount];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = new PayloadNode { F0 = i, F14 = i };
        var cells = new PayloadCell[CellCount];
        for (int i = 0; i < cells.Length; i++) cells[i].A = i;
        long after = GC.GetTotalMemory(true);
        long expected = after - before;
        Console.WriteLine("expected bytes " + expected.ToString(inv) + " (" + (expected / 1048576.0).ToString("0.0", inv) + " MB)");

        Thread.Sleep(1000);
        Interlocked.Exchange(ref maxGapTicks, 0);

        HeapSnapshot? snap = null;
        var t = new Thread(() => snap = HeapDumpEventPipe.Capture(Environment.ProcessId, TimeSpan.FromMinutes(5), m => Console.WriteLine("log: " + m)))
        {
            IsBackground = true,
            Name = "spike capture",
        };
        var sw = Stopwatch.StartNew();
        t.Start();
        t.Join();
        sw.Stop();
        double maxStall = Interlocked.Read(ref maxGapTicks) * 1000.0 / Stopwatch.Frequency;

        if (snap == null)
        {
            Console.WriteLine("RESULT FAIL: no snapshot");
            return 1;
        }

        var table = MemoryReport.Build(snap, new SpikeResolver(), null, "");
        long attributed = 0;
        foreach (var r in table.Rows) if (r.Id == "known-answer") attributed = r.LiveBytes;
        double err = expected > 0 ? 100.0 * (attributed - expected) / expected : 0;

        Console.WriteLine("route: " + snap.Route);
        Console.WriteLine("complete: " + snap.Complete);
        Console.WriteLine("events lost: " + snap.EventsLost.ToString(inv));
        Console.WriteLine("live bytes: " + snap.LiveBytes.ToString(inv) + " (" + (snap.LiveBytes / 1048576.0).ToString("0.0", inv) + " MB), live objects " + snap.LiveObjects.ToString(inv));
        Console.WriteLine("attributed bytes (InProcSpike): " + attributed.ToString(inv) + " vs expected " + expected.ToString(inv)
            + " (error " + err.ToString("0.00", inv) + " %)");
        Console.WriteLine("dump GC pause ms: " + snap.GcPauseMs.ToString("0.0", inv));
        Console.WriteLine("max heartbeat stall ms: " + maxStall.ToString("0.0", inv));
        using (var self = Process.GetCurrentProcess())
            Console.WriteLine("peak working set MB: " + (self.PeakWorkingSet64 / 1048576.0).ToString("0", inv) + ", circular buffer MB: " + HeapDumpEventPipe.LastCircularBufferMB.ToString(inv));
        Console.WriteLine("elapsed ms: " + snap.ElapsedMs.ToString("0", inv) + " (wall " + sw.ElapsedMilliseconds.ToString(inv) + ")");
        foreach (var w in snap.Warnings) Console.WriteLine("warning: " + w);
        bool ok = snap.Complete && snap.EventsLost == 0 && Math.Abs(err) <= 20 && snap.GcPauseMs <= 5000 && maxStall <= 5000;
        Console.WriteLine(ok ? "SPIKE PASS" : "SPIKE FAIL");

        heartbeatRun = false;
        GC.KeepAlive(ballast);
        GC.KeepAlive(nodes);
        GC.KeepAlive(cells);
        return ok ? 0 : 1;
    }

    static void Heartbeat()
    {
        long last = Stopwatch.GetTimestamp();
        while (heartbeatRun)
        {
            Thread.Sleep(1);
            long now = Stopwatch.GetTimestamp();
            long gap = now - last;
            last = now;
            long cur = Interlocked.Read(ref maxGapTicks);
            if (gap > cur) Interlocked.CompareExchange(ref maxGapTicks, gap, cur);
        }
    }
}

public static class Ballast
{
    public static object Allocate(long targetMb)
    {
        var chunks = new List<object[]>();
        if (targetMb <= 0) return chunks;
        long target = targetMb * 1024L * 1024L;
        long start = GC.GetTotalMemory(true);
        var rng = new Random(12345);
        int round = 0;
        while (true)
        {
            var chunk = new object[2048];
            for (int i = 0; i < chunk.Length; i++)
            {
                switch (round++ & 7)
                {
                    case 0:
                    case 1:
                    case 2:
                        chunk[i] = new string((char)('a' + (i & 15)), 8 + rng.Next(40));
                        break;
                    case 3:
                        chunk[i] = new object[] { (long)i, "x" + (i & 255).ToString(CultureInfo.InvariantCulture), (object)(i & 1023) };
                        break;
                    case 4:
                        chunk[i] = new int[4 + (i & 15)];
                        break;
                    case 5:
                        chunk[i] = new List<string> { "a", "b" };
                        break;
                    case 6:
                        {
                            var d = new Dictionary<int, string>(4);
                            d[1] = "one"; d[2] = "two"; d[i] = "i";
                            chunk[i] = d;
                            break;
                        }
                    default:
                        chunk[i] = new byte[32 + rng.Next(96)];
                        break;
                }
            }
            chunks.Add(chunk);
            if ((chunks.Count & 63) == 0 && GC.GetTotalMemory(false) - start >= target) break;
        }
        GC.Collect();
        return chunks;
    }
}
