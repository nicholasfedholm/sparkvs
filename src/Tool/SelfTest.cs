using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SparkVS.Core;

/// <summary>
/// Runs the real capture and report pipeline on a known workload: a 60 fps loop on this thread
/// with a 200 ms block every 3 s inside a named method. Checks the report names that method.
/// </summary>
static class SelfTest
{
    static double sink;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Spin(double ms)
    {
        long end = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        double x = sink + 1;
        while (Stopwatch.GetTimestamp() < end) for (int i = 0; i < 500; i++) x += Math.Sqrt(x + i);
        sink = x;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void FakeRenderPass() => Spin(4);
    [MethodImpl(MethodImplOptions.NoInlining)] static void FakeSpikeBlock() => Spin(200);

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        const int seconds = 12;
        var frames = new FrameLog(4096, 50);
        var capture = new CaptureSession(Path.Combine(dir, ReportBuilder.TraceFile));
        capture.Start();
        frames.Reset(Stopwatch.GetTimestamp());
        var clock = Stopwatch.StartNew();
        int n = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var frame = Stopwatch.GetTimestamp();
            FakeRenderPass();
            if (++n % 180 == 0) FakeSpikeBlock();
            frames.Record(Stopwatch.GetTimestamp());
            double left = 16.6 - (Stopwatch.GetTimestamp() - frame) * 1000.0 / Stopwatch.Frequency;
            if (left > 1) Thread.Sleep((int)left);
        }
        var stop = Stopwatch.StartNew();
        capture.Stop(120000);
        Console.WriteLine($"stopMs={stop.ElapsedMilliseconds} traceBytes={capture.TraceBytes} readerAllocBytes={capture.ReaderBytesAllocated} readerError={capture.ReaderError?.Message ?? "none"}");

        var meta = new CaptureMeta
        {
            Label = "selftest",
            StartQpc = capture.StartQpc,
            EndQpc = capture.EndQpc,
            QpcFrequency = Stopwatch.Frequency,
            MainThreadOsId = Os.CurrentThreadId(),
            FrameCount = frames.Frames,
            NettraceBytes = capture.TraceBytes,
            SamplerBytesAllocated = capture.ReaderBytesAllocated,
            FrameHookBytesAllocated = frames.AllocatedBytes,
            GameAssemblies = new() { "sparkvs-report" },
        };
        for (int i = 0; i < frames.SpikeCount; i++) meta.Spikes.Add(frames.SpikeAt(i));
        File.WriteAllText(Path.Combine(dir, ReportBuilder.MetaFile), JsonSerializer.Serialize(meta));

        var convert = Stopwatch.StartNew();
        var report = ReportBuilder.Run(dir);
        var main = report.Threads.First(t => t.Role == "client-main");
        Console.WriteLine($"reportMs={convert.ElapsedMilliseconds} samples={report.TotalSamples} main={report.MainThreadSamples} mainResolved={report.MainResolvedPct:F1}% meanIntervalMs={report.MainMeanIntervalMs:F2} threadMs={main.DurationMs:F0}");
        foreach (var m in main.Methods.Take(4)) Console.WriteLine($"  self={m.SelfPct:F1}% total={m.TotalPct:F1}% {Analyzer.ShortName(m.Method)} [{m.Module}] -> {m.Owner}");
        foreach (var s in report.Spikes.Take(4)) Console.WriteLine($"  spike at={s.AtSec:F1}s ms={s.DurationMs:F0} inFrame={s.SamplesInFrame} dominant={s.DominantMethod} stackHasBlock={s.NearestStack.Contains("FakeSpikeBlock")}");

        int bad = 0;
        void Gate(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) bad++; }
        Gate("main thread found by OS id", report.MainThreadSamples > 0);
        Gate("stack capture >= 90%", report.MainResolvedPct >= 90);
        Gate("sample clock lines up with capture", Math.Abs(main.DurationMs / 1000 - seconds) < 2);
        Gate("spikes recorded", report.Spikes.Count >= 2);
        Gate("spike stacks name the blocking method", report.Spikes.Count > 0 && report.Spikes.All(s => s.NearestStack.Contains("FakeSpikeBlock") && s.SamplesInFrame > 0));
        Gate("render pass visible", main.Methods.Any(m => m.Method.Contains("FakeRenderPass")));
        Gate("frame hook zero alloc", frames.AllocatedBytes == 0);
        Gate("reader alloc under 256 KB", capture.ReaderBytesAllocated < 256 * 1024);
        foreach (var f in new[] { "summary.json", "summary.md", "profile.speedscope.json" }) Gate(f + " exists", File.Exists(Path.Combine(dir, f)));
        Console.WriteLine($"selftest failed={bad}");
        return bad == 0 ? 0 : 1;
    }
}
