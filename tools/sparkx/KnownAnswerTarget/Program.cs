using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace SparkxKnownAnswer;

// Known-answer payload. The same classes are copied into the Tests project and the in-process spike.
public sealed class PayloadNode
{
    public long F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14;
}

public struct PayloadCell
{
    public long A, B, C, D;
}

public static class Program
{
    const int NodeCount = 200_000;
    const int CellCount = 700_000;

    static long maxGapTicks;
    static volatile bool heartbeatRun = true;

    public static int Main(string[] args)
    {
        long ballastMb = 0;
        if (args.Length > 0 && !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ballastMb))
        {
            Console.Error.WriteLine("usage: SparkxKnownAnswer [ballastMB]");
            return 2;
        }

        object ballast = Ballast.Allocate(ballastMb);
        // Optional second arg "churn": a background thread allocates short-lived PayloadNode garbage (for gross-allocation tests).
        if (args.Length > 1 && args[1] == "churn")
            new Thread(Churn) { IsBackground = true, Name = "churn" }.Start();

        // Start the heartbeat after the ballast so the allocation phase does not pollute the stall numbers.
        var hb = new Thread(Heartbeat) { IsBackground = true, Name = "heartbeat" };
        hb.Start();

        long before = GC.GetTotalMemory(true);
        var nodes = new PayloadNode[NodeCount];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = new PayloadNode { F0 = i, F14 = i };
        var cells = new PayloadCell[CellCount];
        for (int i = 0; i < cells.Length; i++) cells[i].A = i;
        long after = GC.GetTotalMemory(true);
        long expected = after - before;
        // 16 byte object overhead (header + method table) plus 120 bytes of fields, plus the two arrays.
        long nominal = (long)NodeCount * (16 + 15 * 8) + (24 + (long)NodeCount * 8) + (24 + (long)CellCount * 32);

        Console.Out.WriteLine("{\"pid\":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"expected_bytes\":" + expected.ToString(CultureInfo.InvariantCulture)
            + ",\"nominal_bytes\":" + nominal.ToString(CultureInfo.InvariantCulture)
            + ",\"ballast_mb\":" + ballastMb.ToString(CultureInfo.InvariantCulture) + "}");
        Console.Out.Flush();
        Interlocked.Exchange(ref maxGapTicks, 0);

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            string cmd = line.Trim();
            if (cmd == "stall")
            {
                long ticks = Interlocked.Exchange(ref maxGapTicks, 0);
                double ms = ticks * 1000.0 / Stopwatch.Frequency;
                Console.Out.WriteLine("{\"max_stall_ms\":" + ms.ToString("0.0", CultureInfo.InvariantCulture) + "}");
                Console.Out.Flush();
            }
            else if (cmd == "exit")
            {
                break;
            }
        }

        heartbeatRun = false;
        GC.KeepAlive(ballast);
        GC.KeepAlive(nodes);
        GC.KeepAlive(cells);
        return 0;
    }

    static void Churn()
    {
        long sink = 0;
        while (heartbeatRun)
        {
            for (int i = 0; i < 2000; i++) sink += new PayloadNode { F0 = i }.F0;
            Thread.Sleep(1);
        }
        GC.KeepAlive(sink.ToString(CultureInfo.InvariantCulture));
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

/// <summary>Live-heap filler: many small objects (strings, small arrays, small dictionaries), System types only.</summary>
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
