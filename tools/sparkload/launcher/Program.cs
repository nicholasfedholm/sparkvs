using System;
using System.Collections.Generic;
using System.IO;
using SparkLoad.Launch;

namespace SparkLoad
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Native.AttachParentConsole();
            if (args.Length == 0)
            {
                Err("sparkload: missing command (run, analyze, preflight)");
                return 2;
            }
            try
            {
                switch (args[0])
                {
                    case "run":
                        return CmdRun(args);
                    case "analyze":
                        return CmdAnalyze(args);
                    case "preflight":
                        return CmdPreflight(args);
                    case "tracecheck":
                        return TraceCheck.Run(Slice(args, 1), Console.Out);
                    case "sampler-selftest":
                        return SamplerSelfTest.Run(Console.Out);
                    default:
                        Err("sparkload: unknown command " + args[0]);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Err("sparkload: unexpected failure: " + ex);
                return 1;
            }
        }

        static string[] Slice(string[] a, int from)
        {
            var l = new List<string>();
            for (int i = from; i < a.Length; i++) l.Add(a[i]);
            return l.ToArray();
        }

        static void Err(string s)
        {
            try { Console.Error.WriteLine(s); } catch { }
        }

        static int CmdRun(string[] args)
        {
            var o = new RunOptions();
            string? bad = RunOptions.Parse(args, 1, o);
            if (bad != null)
            {
                Err("sparkload run: " + bad);
                return 2;
            }
            using var log = new RunLog();
            return Launcher.Run(o, log);
        }

        static int CmdAnalyze(string[] args)
        {
            string? run = null, compare = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--run" && i + 1 < args.Length) run = args[++i];
                else if (args[i] == "--compare" && i + 1 < args.Length) compare = args[++i];
                else { Err("sparkload analyze: unknown option " + args[i]); return 2; }
            }
            if (run == null) { Err("sparkload analyze: --run is required"); return 2; }
            if (!Directory.Exists(run)) { Err("sparkload analyze: run folder not found"); return 2; }
            try
            {
                int rc = SparkLoad.Analyzer.AnalyzerMain.Run(Path.GetFullPath(run), compare == null ? null : Path.GetFullPath(compare), Console.Out);
                return rc == 0 ? 0 : 6;
            }
            catch (Exception ex)
            {
                Err("sparkload analyze: failed: " + ex.Message);
                return 6;
            }
        }

        static int CmdPreflight(string[] args)
        {
            string names = "Vintagestory,Optimum";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--guard-names" && i + 1 < args.Length) names = args[++i];
                else { Err("sparkload preflight: unknown option " + args[i]); return 2; }
            }
            return Guard.Preflight(RunOptions.SplitGuard(names), s => Console.Out.WriteLine(s));
        }
    }
}
