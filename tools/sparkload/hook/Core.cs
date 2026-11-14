using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SparkLoad.Hook
{
    // Shared state: event source, per-marker enable flags and hit counters, fault counter, patch result log.
    internal static class Core
    {
        public const string Version = "0.1.0";

        public static SparkLoadSource Src;
        public static volatile bool Ready;
        public static string RunDir;

        // Index = marker id (1..Mk.Count). On[id] false = switched off through SPARKLOAD_OFF.
        public static readonly bool[] On = new bool[Mk.Count + 1];
        public static readonly long[] Hits = new long[Mk.Count + 1];
        public static long Faults;

        private static readonly object PatchLock = new object();
        private static readonly List<string[]> PatchLog = new List<string[]>();

        public static void Fault() { Interlocked.Increment(ref Faults); }

        public static void Begin(string runDir)
        {
            RunDir = runDir;
            HookLog.SetRunDir(runDir);
            try { if (!string.IsNullOrEmpty(runDir)) AppContext.SetData("SparkLoad.RunDir", runDir); } catch { }

            string[] off = ParseOff(Environment.GetEnvironmentVariable("SPARKLOAD_OFF"));
            for (int i = 1; i <= Mk.Count; i++) On[i] = !IsOff(Mk.Names[i], off);

            Src = new SparkLoadSource();
            EmitDefinitions();
            Ready = true;
            if (off.Length > 0) HookLog.Line("off=" + string.Join(",", off));
            Installer.Start();
        }

        public static string[] ParseOff(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return Array.Empty<string>();
            var list = new List<string>();
            foreach (string p in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        // Marker name or prefix of it ("column" switches off column.pass and column.load).
        private static bool IsOff(string name, string[] off)
        {
            foreach (string o in off)
                if (name.StartsWith(o, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void EmitDefinitions()
        {
            SparkLoadSource s = Src;
            s.Hello(Environment.ProcessId, Stopwatch.GetTimestamp(), Stopwatch.Frequency, Version, RuntimeInformation.FrameworkDescription);
            for (int i = 1; i <= Mk.Count; i++) s.MarkerDef(i, Mk.Names[i]);
        }

        // Called when a session or listener attaches late: definitions and the patch results so far again.
        public static void Replay()
        {
            if (Src == null) return;
            EmitDefinitions();
            string[][] copy;
            lock (PatchLock) copy = PatchLog.ToArray();
            foreach (string[] p in copy) Src.Patch(p[0], p[1], p[2], p[3]);
        }

        public static void PatchResult(string marker, string target, string status, string detail)
        {
            lock (PatchLock) PatchLog.Add(new[] { marker, target, status, detail });
            try { Src?.Patch(marker, target, status, detail); } catch { Fault(); }
            HookLog.Line("patch " + marker + " " + target + " " + status + (string.IsNullOrEmpty(detail) ? "" : " " + detail));
        }

        // Stats at finalize end and at Playable. Also reports the targets whose assembly never loaded.
        public static void StatsAll()
        {
            try
            {
                Installer.FlushPending();
                SparkLoadSource s = Src;
                if (s == null) return;
                for (int i = 1; i <= Mk.Count; i++) s.Stats(Mk.Names[i], Interlocked.Read(ref Hits[i]));
                s.Stats("hook.faults", Interlocked.Read(ref Faults));
                ColumnThrottle.FlushStats(s);
            }
            catch { Fault(); }
        }
    }

    // Frame bookkeeping. MainGameLoop runs on one thread; the lock only guards against surprises.
    internal static class FrameStats
    {
        private static readonly object L = new object();
        private static readonly long Over50Ticks = Stopwatch.Frequency / 20;
        private static readonly long OneSecond = Stopwatch.Frequency;

        private static long aggStart, aggCount, aggSum, aggMax, aggOver;
        private static bool finalized;
        private static long framesAfter;
        private static int consecutive;
        private static bool playable;

        public static void OnFinalizeEnd()
        {
            lock (L)
            {
                finalized = true;
                framesAfter = 0;
                consecutive = 0;
                playable = false;
            }
            Core.StatsAll();
        }

        public static void OnFrame(long start, long end)
        {
            bool statsNow = false;
            lock (L)
            {
                long dur = end - start;
                if (aggCount == 0) aggStart = start;
                aggCount++;
                aggSum += dur;
                if (dur > aggMax) aggMax = dur;
                if (dur >= Over50Ticks) aggOver++;

                if (finalized)
                {
                    long idx = ++framesAfter;
                    if (idx <= 120) Emit.Frame(end, idx, dur);
                    if (!playable)
                    {
                        if (dur < Over50Ticks)
                        {
                            if (++consecutive >= 60)
                            {
                                playable = true;
                                Emit.Playable(end, idx);
                                statsNow = true;
                            }
                        }
                        else consecutive = 0;
                    }
                }

                if (end - aggStart >= OneSecond)
                {
                    Emit.FrameAgg(end, aggCount, aggSum, aggMax, aggOver);
                    aggCount = 0; aggSum = 0; aggMax = 0; aggOver = 0;
                }
            }
            if (statsNow) Core.StatsAll();
        }
    }
}
