using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SparkVS.Core;

namespace SparkX;

/// <summary>Hidden command: cross-process known-answer gate against SparkxKnownAnswer.</summary>
public static class Gate
{
    sealed class GateResolver : IModResolver
    {
        readonly IModResolver inner;
        public GateResolver(IModResolver inner) { this.inner = inner; }
        public string Describe => "gate: module SparkxKnownAnswer is the known-answer mod; otherwise " + inner.Describe;
        public Owner Resolve(string module)
        {
            if (module.IndexOf("SparkxKnownAnswer", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Owner(OwnerKind.Mod, "known-answer", "SparkxKnownAnswer");
            return inner.Resolve(module);
        }
    }

    static string? FindTarget(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        string baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "SparkxKnownAnswer.exe", "SparkxKnownAnswer.dll" })
        {
            string p = Path.Combine(baseDir, name);
            if (File.Exists(p)) return p;
        }
        // Dev layout: tools/sparkx/bin/<cfg>/net10.0/ -> tools/sparkx/KnownAnswerTarget/bin/<cfg>/net10.0/
        string? dir = baseDir.TrimEnd('\\', '/');
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string kat = Path.Combine(dir, "KnownAnswerTarget", "bin");
            if (Directory.Exists(kat))
            {
                foreach (var f in Directory.EnumerateFiles(kat, "SparkxKnownAnswer.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)) return f;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static int Run(Args args)
    {
        var inv = CultureInfo.InvariantCulture;
        int ballast = args.GetInt("ballast", 0);
        string? target = FindTarget(args.Get("target"));
        if (target == null)
        {
            Console.Error.WriteLine("gate: SparkxKnownAnswer not found next to sparkx; pass --target PATH");
            return 2;
        }

        var psi = new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(target);
        }
        psi.ArgumentList.Add(ballast.ToString(inv));

        using var proc = Process.Start(psi);
        if (proc == null) { Console.Error.WriteLine("gate: could not start the target"); return 2; }
        try
        {
            string? ReadLineTimeout(TimeSpan t)
            {
                var task = proc.StandardOutput.ReadLineAsync();
                return task.Wait(t) ? task.Result : null;
            }

            Console.WriteLine("gate: target pid " + proc.Id.ToString(inv) + ", ballast " + ballast.ToString(inv) + " MB, waiting for payload line ...");
            string? first = ReadLineTimeout(TimeSpan.FromMinutes(10));
            if (first == null) { Console.Error.WriteLine("gate: no payload line from the target"); return 1; }
            long expected, nominal;
            int tpid;
            using (var doc = JsonDocument.Parse(first))
            {
                expected = doc.RootElement.GetProperty("expected_bytes").GetInt64();
                nominal = doc.RootElement.GetProperty("nominal_bytes").GetInt64();
                tpid = doc.RootElement.GetProperty("pid").GetInt32();
            }
            Console.WriteLine("gate: target ready, expected " + expected.ToString(inv) + " bytes (nominal " + nominal.ToString(inv) + ")");

            System.Threading.Thread.Sleep(2000);
            double StallRead()
            {
                proc.StandardInput.WriteLine("stall");
                proc.StandardInput.Flush();
                string? l = ReadLineTimeout(TimeSpan.FromSeconds(30));
                if (l == null) return -1;
                using var d = JsonDocument.Parse(l);
                return d.RootElement.GetProperty("max_stall_ms").GetDouble();
            }
            StallRead(); // reset the target's heartbeat max so only the snapshot window is measured

            var resolver = new GateResolver(ExternalModResolver.Create(tpid, args.Get("data")));
            var snap = HeapDumpEventPipe.Capture(tpid, TimeSpan.FromMinutes(10));
            double stall = StallRead();
            var table = MemoryReport.Build(snap, resolver, null, "");
            long attributed = table.Rows.Where(r => r.Id == "known-answer").Select(r => r.LiveBytes).FirstOrDefault();
            double err = expected > 0 ? 100.0 * (attributed - expected) / expected : double.NaN;

            bool accuracy = snap.Complete && snap.EventsLost == 0 && Math.Abs(err) <= 20.0;
            bool freeze = snap.GcPauseMs <= 5000 && stall >= 0 && stall <= 5000;
            bool pass = accuracy && freeze;

            Console.WriteLine("---- sparkx gate result ----");
            Console.WriteLine("route:            " + snap.Route);
            Console.WriteLine("ballast MB:       " + ballast.ToString(inv));
            Console.WriteLine("expected bytes:   " + expected.ToString(inv) + " (" + MemoryReport.Mb(expected).ToString("0.0", inv) + " MB)");
            Console.WriteLine("attributed bytes: " + attributed.ToString(inv) + " (" + MemoryReport.Mb(attributed).ToString("0.0", inv) + " MB)");
            Console.WriteLine("error:            " + err.ToString("0.00", inv) + " % (limit 20 %): " + (accuracy ? "ok" : "FAIL"));
            Console.WriteLine("live bytes total: " + snap.LiveBytes.ToString(inv) + " (" + MemoryReport.Mb(snap.LiveBytes).ToString("0.0", inv) + " MB), objects " + snap.LiveObjects.ToString(inv));
            Console.WriteLine("dump GC pause ms: " + snap.GcPauseMs.ToString("0.0", inv));
            Console.WriteLine("target max stall: " + stall.ToString("0.0", inv) + " ms (limit 5000 ms with pause: " + (freeze ? "ok" : "FAIL") + ")");
            Console.WriteLine("events lost:      " + snap.EventsLost.ToString(inv));
            Console.WriteLine("complete:         " + snap.Complete + ", attempts " + snap.Attempts.ToString(inv) + ", start_condition " + (snap.StartCondition.Length > 0 ? snap.StartCondition : "n/a") + ", snapshot took " + snap.ElapsedMs.ToString("0", inv) + " ms");
            foreach (var a in snap.AttemptLog) Console.WriteLine("attempt " + a.Attempt.ToString(inv) + ":        " + a.Outcome + ", buffer " + a.BufferMB.ToString(inv) + " MB, events lost " + a.EventsLost.ToString(inv) + ", gc pause " + (a.GcPauseMs >= 0 ? a.GcPauseMs.ToString("0.0", inv) + " ms" : "n/a"));
            foreach (var w in snap.Warnings) Console.WriteLine("warning:          " + w);
            Console.WriteLine(pass ? "GATE PASS" : "GATE FAIL");
            return pass ? 0 : 1;
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }
}
