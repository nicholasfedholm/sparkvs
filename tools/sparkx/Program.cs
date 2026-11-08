using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.NETCore.Client;
using SparkVS.Core;

namespace SparkX;

/// <summary>Minimal option parser: --name value or --flag.</summary>
public sealed class Args
{
    readonly Dictionary<string, string> opts = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();

    public Args(string[] args, int start)
    {
        for (int i = start; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string key = a.Substring(2);
                int eq = key.IndexOf('=');
                if (eq >= 0) { opts[key.Substring(0, eq)] = key.Substring(eq + 1); continue; }
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) opts[key] = args[++i];
                else opts[key] = "";
            }
            else Positional.Add(a);
        }
    }

    public string? Get(string name) => opts.TryGetValue(name, out var v) ? v : null;
    public bool Has(string name) => opts.ContainsKey(name);

    public int GetInt(string name, int def)
    {
        var v = Get(name);
        return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : def;
    }

    public double GetDouble(string name, double def)
    {
        var v = Get(name);
        return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : def;
    }
}

public static class Program
{
    public const string Version = "0.1.0";
    public const string FrontEnd = "sparkx " + Version;

    public static int Main(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return argv.Length == 0 ? 2 : 0;
        }
        var args = new Args(argv, 1);
        try
        {
            switch (argv[0].ToLowerInvariant())
            {
                case "list": return List();
                case "memory": return Memory(args);
                case "watch": return Watch.Run(args);
                case "gate": return Gate.Run(args);
                default:
                    Console.Error.WriteLine("unknown command: " + argv[0]);
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sparkx failed: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("sparkx " + Version + ": per-mod live memory for a running Vintage Story process (managed heap only)");
        Console.WriteLine();
        Console.WriteLine("  sparkx list");
        Console.WriteLine("  sparkx memory [--pid N] [--out DIR] [--top N] [--data DIR] [--snapshot-attempts 3]");
        Console.WriteLine("  sparkx watch  [--pid N] [--minutes M] [--snapshot-every K] [--out DIR] [--top N] [--data DIR]");
        Console.WriteLine("                [--start-delay 60] [--start-max-wait 600] [--snapshot-attempts 3]");
        Console.WriteLine();
        Console.WriteLine("watch: the first snapshot waits --start-delay seconds after attach AND for the GC heap to change under 5 % across the last 3");
        Console.WriteLine("5-second samples, gives up after --start-max-wait seconds (start_condition=timeout). A snapshot is complete only if it");
        Console.WriteLine("finished with no events lost; otherwise it is retried up to --snapshot-attempts times (each attempt forces a full GC).");
        Console.WriteLine();
        Console.WriteLine("Without --pid exactly one Vintagestory or VintagestoryServer process must be running.");
        Console.WriteLine("The target needs a diagnostics port (the .NET default). Same user, same elevation.");
    }

    public sealed record VsProcess(int Pid, string Name, DateTime Start, long WorkingSet);

    public static List<VsProcess> FindVsProcesses()
    {
        var result = new List<VsProcess>();
        foreach (int pid in DiagnosticsClient.GetPublishedProcesses())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string n = p.ProcessName;
                if (!n.Equals("Vintagestory", StringComparison.OrdinalIgnoreCase) && !n.Equals("VintagestoryServer", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime start = DateTime.MinValue;
                try { start = p.StartTime; } catch { }
                result.Add(new VsProcess(pid, n, start, p.WorkingSet64));
            }
            catch { }
        }
        return result;
    }

    static int List()
    {
        var list = FindVsProcesses();
        if (list.Count == 0)
        {
            Console.WriteLine("no Vintagestory or VintagestoryServer process with a diagnostics port found");
            return 0;
        }
        Console.WriteLine("pid\tname\tstart\tworking_set_mb");
        foreach (var p in list)
            Console.WriteLine(p.Pid.ToString(CultureInfo.InvariantCulture) + "\t" + p.Name + "\t" + p.Start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + "\t" + MemoryReport.Mb(p.WorkingSet).ToString("0", CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>Returns the pid to use, or -1 after printing why not (caller exits 2).</summary>
    public static int ResolvePid(Args args)
    {
        string? v = args.Get("pid");
        if (v != null)
        {
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return pid;
            Console.Error.WriteLine("bad --pid: " + v);
            return -1;
        }
        var list = FindVsProcesses();
        if (list.Count == 1) return list[0].Pid;
        if (list.Count == 0) Console.Error.WriteLine("no Vintagestory or VintagestoryServer process with a diagnostics port found; pass --pid");
        else
        {
            Console.Error.WriteLine("more than one Vintagestory process found; pass --pid:");
            foreach (var p in list) Console.Error.WriteLine("  " + p.Pid.ToString(CultureInfo.InvariantCulture) + "  " + p.Name);
        }
        return -1;
    }

    static int Memory(Args args)
    {
        int pid = ResolvePid(args);
        if (pid < 0) return 2;
        int top = Math.Clamp(args.GetInt("top", 20), 1, 200);
        string outDir = args.Get("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "sparkx-memory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var resolver = ExternalModResolver.Create(pid, args.Get("data"));
        Console.WriteLine("snapshotting pid " + pid.ToString(CultureInfo.InvariantCulture) + " ...");
        int attempts = Math.Clamp(args.GetInt("snapshot-attempts", SnapshotPolicy.DefaultAttempts), 1, 10);
        var snap = HeapDumpEventPipe.Capture(pid, TimeSpan.FromMinutes(10), m => Console.WriteLine("  " + m), attempts);
        snap.StartCondition = "immediate";
        var table = MemoryReport.Build(snap, resolver, null, "");
        MemoryReport.Write(outDir, table, top, FrontEnd);
        PrintSummary(table, top);
        Console.WriteLine("wrote " + Path.GetFullPath(outDir) + "\\memory.md, memory.csv, memory.json");
        return snap.Complete ? 0 : 1;
    }

    public static void PrintSummary(MemoryTable table, int top)
    {
        var inv = CultureInfo.InvariantCulture;
        var s = table.Snapshot;
        Console.WriteLine("route: " + s.Route + ", complete: " + s.Complete + ", attempts: " + s.Attempts.ToString(inv) + ", events lost: " + s.EventsLost.ToString(inv)
            + ", dump GC pause: " + s.GcPauseMs.ToString("0.0", inv) + " ms, took " + s.ElapsedMs.ToString("0", inv) + " ms");
        Console.WriteLine("live: " + MemoryReport.Mb(table.TotalLiveBytes).ToString("0.0", inv) + " MB in " + table.TotalLiveObjects.ToString("N0", inv) + " objects");
        foreach (var w in s.Warnings) Console.WriteLine("warning: " + w);
        foreach (var row in MemoryReport.ShownRows(table, Math.Min(top, 5)))
            Console.WriteLine("  " + MemoryReport.Mb(row.LiveBytes).ToString("0.0", inv).PadLeft(9) + " MB  " + row.Id + " (" + row.Kind.ToString().ToLowerInvariant() + ")");
    }
}
