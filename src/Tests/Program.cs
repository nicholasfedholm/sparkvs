using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SparkVS;
using SparkVS.Core;

int passed = 0, failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) passed++;
    else { failed++; Console.WriteLine($"FAIL {name} {detail}"); }
}
void Near(string name, double got, double want, double tol = 0.001) => Check(name, Math.Abs(got - want) <= tol, $"got {got} want {want}");

// Synthetic capture: main thread 1, 10 ms apart. root > render > modA.Draw 6 samples,
// root > render 2 samples, root > render > modA.Draw > System.List.Sort 2 samples.
var set = new SampleSet();
int F(string m, string a) { set.Frames.Add(new FrameInfo { Method = m, Module = a }); return set.Frames.Count - 1; }
int root = F("Game.Root()", "VintagestoryLib"), render = F("Game.Render()", "VintagestoryLib");
int draw = F("ModA.Draw()", "ModAsm"), sort = F("System.List.Sort()", "System.Private.CoreLib");
int dyn = F("DMD<Game.Foo>", ""), other = F("Who.Knows()", "MysteryAsm"), wait = F("System.Threading.Monitor.Wait()", "System.Private.CoreLib");
set.Stacks.Add(new[] { draw, render, root });
set.Stacks.Add(new[] { render, root });
set.Stacks.Add(new[] { sort, draw, render, root });
set.Stacks.Add(new[] { wait, root });
double t = 0;
void S(int thread, int stack, int n, bool ext = false)
{
    for (int i = 0; i < n; i++, t += 10) set.Samples.Add(new Sample { ThreadId = thread, TimeMs = t, StackId = stack, External = ext });
}
S(1, 0, 6); S(1, 1, 2, true); S(1, 2, 2);
t = 0; S(2, 3, 5);

var meta = new CaptureMeta
{
    Label = "synthetic",
    StartQpc = 0,
    EndQpc = Stopwatch.Frequency / 10,
    QpcFrequency = Stopwatch.Frequency,
    MainThreadOsId = 1,
    AssemblyToMod = new() { ["ModAsm"] = "moda" },
    GameAssemblies = new() { "VintagestoryLib" },
    Spikes = new() { new SpikeRecord { StartQpc = Stopwatch.Frequency * 15 / 1000, DurationMs = 21, Gen0 = 1 } },
    FrameCount = 10,
};
var report = Analyzer.Build(set, meta);
var main = report.Threads.First(x => x.Role == "client-main");

// Aggregation maths
Check("main samples", main.Samples == 10);
Near("thread total is 100 ms", main.DurationMs, 100);
Near("draw self", main.Methods.First(m => m.Method.StartsWith("ModA.Draw")).SelfMs, 60);
Near("draw total", main.Methods.First(m => m.Method.StartsWith("ModA.Draw")).TotalMs, 80);
Near("render self", main.Methods.First(m => m.Method.StartsWith("Game.Render")).SelfMs, 20);
Near("root total is everything", main.Methods.First(m => m.Method.StartsWith("Game.Root")).TotalPct, 100);
Near("self sums to total", main.Methods.Sum(m => m.SelfMs), 100);
Near("external pct", main.ExternalPct, 20);
Check("self ordering", main.Methods[0].Method.StartsWith("ModA.Draw"));
Check("paths root first", main.TopPaths[0].Path == "Game.Root > Game.Render > ModA.Draw", main.TopPaths[0].Path);
Near("path pct", main.TopPaths[0].Pct, 60);

// Recursion must not double count total time
var rec = new SampleSet();
rec.Frames.Add(new FrameInfo { Method = "R.Rec()", Module = "ModAsm" });
rec.Stacks.Add(new[] { 0, 0, 0 });
rec.Samples.Add(new Sample { ThreadId = 1, TimeMs = 0, StackId = 0 });
rec.Samples.Add(new Sample { ThreadId = 1, TimeMs = 10, StackId = 0 });
var recReport = Analyzer.Build(rec, new CaptureMeta { MainThreadOsId = 1 });
Near("recursion total", recReport.Threads[0].Methods[0].TotalPct, 100);

// Attribution
var attr = new Attribution(meta.AssemblyToMod, meta.GameAssemblies);
Check("mod frame", attr.OwnerOf(set.Frames[draw]) == "moda");
Check("mod lookup ignores case", attr.OwnerOf(new FrameInfo { Method = "x", Module = "MODASM" }) == "moda");
Check("game frame", attr.OwnerOf(set.Frames[root]) == Attribution.Game);
Check("runtime frame", attr.OwnerOf(set.Frames[sort]) == Attribution.Runtime);
Check("dynamic is not guessed", attr.OwnerOf(set.Frames[dyn]) == Attribution.Patched);
Check("unknown assembly stays unknown", attr.OwnerOf(set.Frames[other]) == Attribution.Unknown);
Check("similar name is not a mod", attr.OwnerOf(new FrameInfo { Method = "x", Module = "ModAsm2" }) == Attribution.Unknown);
Check("runtime leaf bills caller", attr.OwnerOfStack(set.Stacks[2], set.Frames) == "moda");
Check("runtime only bills runtime", attr.OwnerOfStack(new[] { sort }, set.Frames) == Attribution.Runtime);
Near("mod self includes runtime callee", main.ByMod.First(g => g.Name == "moda").SelfMs, 80);
Near("game self", main.ByMod.First(g => g.Name == Attribution.Game).SelfMs, 20);
Near("game inclusive", main.ByMod.First(g => g.Name == Attribution.Game).InclusiveMs, 100);
Near("assembly self", main.ByAssembly.First(g => g.Name == "ModAsm").SelfMs, 60);

// Idle and stats
Check("wait thread is idle", report.Threads.First(x => x.OsThreadId == 2).IdlePct > 99);
Check("capture stats", report.TotalSamples == 15 && report.SamplesWithResolvedStack == 15);
Near("main resolved", report.MainResolvedPct, 100);

// Spikes: frame 15..36 ms holds samples at 20 and 30 ms
Check("spike count", report.Spikes.Count == 1);
Check("samples in spike", report.Spikes[0].SamplesInFrame == 2, report.Spikes[0].SamplesInFrame.ToString());
Check("spike dominant", report.Spikes[0].DominantMethod == "ModA.Draw", report.Spikes[0].DominantMethod);
Check("spike owner", report.Spikes[0].DominantOwner == "moda");
Check("spike gc", report.Spikes[0].Gen0 == 1);

// Speedscope against the fixture
using (var ms = new MemoryStream())
{
    Speedscope.Write(ms, set, report, "fixture");
    using var doc = JsonDocument.Parse(ms.ToArray());
    var d = doc.RootElement;
    Check("schema", d.GetProperty("$schema").GetString() == "https://www.speedscope.app/file-format-schema.json");
    int frameCount = d.GetProperty("shared").GetProperty("frames").GetArrayLength();
    Check("frames", frameCount == set.Frames.Count);
    var profiles = d.GetProperty("profiles");
    Check("profiles", profiles.GetArrayLength() == 2);
    var p = profiles[0];
    Check("type", p.GetProperty("type").GetString() == "sampled");
    Check("unit", p.GetProperty("unit").GetString() == "milliseconds");
    var samples = p.GetProperty("samples");
    var weights = p.GetProperty("weights");
    Check("runs merged", samples.GetArrayLength() == 3 && weights.GetArrayLength() == 3);
    Near("weights sum", weights.EnumerateArray().Sum(w => w.GetDouble()), 100);
    Near("end minus start", p.GetProperty("endValue").GetDouble() - p.GetProperty("startValue").GetDouble(), 100);
    Check("root first", samples[0][0].GetInt32() == root);
    Check("leaf last", samples[0][samples[0].GetArrayLength() - 1].GetInt32() == draw);
    Check("indexes in range", samples.EnumerateArray().All(s => s.EnumerateArray().All(i => i.GetInt32() < frameCount)));
}

// Summary round trip and markdown shape
var rt = SummaryWriter.FromJson(SummaryWriter.ToJson(report))!;
Check("json round trip", rt.Threads.Count == report.Threads.Count && rt.Threads[0].Methods[0].Method == report.Threads[0].Methods[0].Method);
var md = SummaryWriter.ToMarkdown(report);
Check("md sections", md.Contains("top 15 methods") && md.Contains("by owning mod") && md.Contains("by assembly") && md.Contains("top call paths") && md.Contains("Frames over") && md.Contains("Capture overhead"));

// Compare
var cmpA = new Report { DurationSec = 10, Threads = { new ThreadReport { Role = "client-main", Methods = { new MethodRow { Method = "X.A()", TotalMs = 100, SelfMs = 10 } } } } };
var cmpB = new Report { DurationSec = 5, Threads = { new ThreadReport { Role = "client-main", Methods = { new MethodRow { Method = "X.A()", TotalMs = 100, SelfMs = 10 }, new MethodRow { Method = "X.B()", TotalMs = 5, SelfMs = 5 } } } } };
var delta = SummaryWriter.Compare(cmpA, cmpB, "client-main", true, 5);
Check("compare normalises by duration", delta[0].Key.EndsWith("X.A") && Math.Abs(delta[0].A - 10) < 1e-6 && Math.Abs(delta[0].B - 20) < 1e-6);
Check("compare keeps one sided rows", delta.Count == 2 && delta[1].A == 0);

// Frame log: spikes, GC deltas, and no allocation over many frames
var log = new FrameLog(4, 50);
long tick = Stopwatch.Frequency / 1000;
log.Reset(0);
log.Record(16 * tick);
log.Record(100 * tick);
Check("one spike", log.SpikeCount == 1);
Near("spike ms", log.SpikeAt(0).DurationMs, 84, 0.01);
Check("spike start", log.SpikeAt(0).StartQpc == 16 * tick);
for (int i = 0; i < 6; i++) log.Record((200 + i * 100) * tick);
Check("full buffer drops", log.SpikeCount == 4 && log.Dropped == 3, $"{log.SpikeCount}/{log.Dropped}");
var zero = new FrameLog(1024, 50);
zero.Reset(0);
long stamp = 0;
long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 2_000_000; i++) { stamp += tick * (i % 1000 == 0 ? 60 : 16); zero.Record(stamp); }
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check("frame log zero alloc", allocated == 0, allocated.ToString());
Check("frame log self count zero", zero.AllocatedBytes == 0, zero.AllocatedBytes.ToString());

// Bundle writers: frames.jsonl, frames-summary.json, tick.jsonl, heap.jsonl, gpu-memory.jsonl, stubs
{
    string dir = Path.Combine(Path.GetTempPath(), "sparkvs-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    long f = Stopwatch.Frequency, ms1 = f / 1000;
    var clock = new BundleClock(0, DateTime.Now, f);
    var fl = new FrameLog(16, 50, 8);
    fl.Reset(0);
    long ft = 0;
    for (int i = 1; i <= 20; i++) { ft += (i == 12 ? 80 : 10) * ms1; fl.Record(ft); }
    Check("ring retains capacity", fl.RetainedFrames == 8 && fl.DroppedOldest == 12, $"{fl.RetainedFrames}/{fl.DroppedOldest}");
    var fs = BundleWriter.WriteFrames(dir, fl, clock, 50, Array.Empty<SparkVS.Core.SpikeRecord>(), 0);
    var lines = File.ReadAllLines(Path.Combine(dir, "frames.jsonl"));
    Check("frames.jsonl header plus rows", lines.Length == 1 + 8, lines.Length.ToString());
    var hdr = JsonDocument.Parse(lines[0]).RootElement;
    Check("frames header schema_version", hdr.GetProperty("schema_version").GetInt32() == 1);
    var row0 = JsonDocument.Parse(lines[1]).RootElement;
    Check("frames row fields", row0.TryGetProperty("ts_local", out _) && row0.TryGetProperty("t_ms", out _) && row0.TryGetProperty("frame_ms", out _));
    Check("frames row t_ms ordering", JsonDocument.Parse(lines[8]).RootElement.GetProperty("t_ms").GetDouble() > row0.GetProperty("t_ms").GetDouble());
    Check("frames-summary written", File.Exists(Path.Combine(dir, "frames-summary.json")));
    var fsum = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "frames-summary.json"))).RootElement;
    Check("frames-summary schema_version", fsum.GetProperty("schema_version").GetInt32() == 1);
    Check("frames over-50 list", fsum.GetProperty("over_threshold_frames").GetArrayLength() == 0 || fsum.GetProperty("over_threshold_frames").GetArrayLength() == 1);

    var stat = BundleWriter.ComputeStats(Enumerable.Range(1, 100).Select(i => (float)i).ToArray(), fl, 50, 0);
    Near("p50 nearest rank", stat.P50FrameMs, 50, 0.01);
    Near("p95 nearest rank", stat.P95FrameMs, 95, 0.01);
    Near("p99 nearest rank", stat.P99FrameMs, 99, 0.01);
    Near("mean", stat.MeanFrameMs, 50.5, 0.01);
    Near("1% low is fps of slowest 1%", stat.OnePercentLowFps, 10, 0.01);

    // Compact switch: 600k frames at the projected object size would exceed 50 MB
    var big = new FrameLog(4, 50, 600_000);
    big.Reset(0);
    long bt = 0;
    for (int i = 0; i < 600_000; i++) { bt += 10 * ms1; big.Record(bt); }
    string bdir = Path.Combine(dir, "big"); Directory.CreateDirectory(bdir);
    BundleWriter.WriteFrames(bdir, big, clock, 50, Array.Empty<SparkVS.Core.SpikeRecord>(), 0);
    var bl = File.ReadLines(Path.Combine(bdir, "frames.jsonl")).Take(2).ToArray();
    Check("compact switch", JsonDocument.Parse(bl[0]).RootElement.GetProperty("format").GetString() == "compact_number_arrays" && bl[1].StartsWith("["));
    Check("compact file under 50 MB", new FileInfo(Path.Combine(bdir, "frames.jsonl")).Length < 50_000_000, new FileInfo(Path.Combine(bdir, "frames.jsonl")).Length.ToString());

    // Tick log: disabled when empty, rows when populated
    string tdir = Path.Combine(dir, "tick"); Directory.CreateDirectory(tdir);
    var ts0 = BundleWriter.WriteTick(tdir, null, clock, "probe missing");
    var tl0 = File.ReadAllLines(Path.Combine(tdir, "tick.jsonl"));
    Check("tick disabled stub", !ts0.Enabled && tl0.Length == 1 && tl0[0].Contains("\"enabled\":false") && tl0[0].Contains("probe missing"), tl0[0]);
    var tk = new TickLog(8);
    tk.Begin();
    for (int i = 1; i <= 12; i++) tk.Record(i * 50 * ms1, 3 + i % 2);
    tk.End();
    var ts1 = BundleWriter.WriteTick(tdir, tk, clock, null);
    var tl1 = File.ReadAllLines(Path.Combine(tdir, "tick.jsonl"));
    Check("tick ring keeps newest", ts1.Enabled && ts1.TickCount == 8 && tl1.Length == 9, $"{ts1.TickCount}/{tl1.Length}");
    Check("tick row mspt", JsonDocument.Parse(tl1[1]).RootElement.GetProperty("mspt_ms").GetInt32() >= 3);
    Near("tick interval", ts1.MeanIntervalMs, 50, 0.5);

    // Heap recorder and 1 Hz sampler (real GC APIs, real background thread)
    var hr = new HeapRecorder(clock);
    var sampler = new PeriodicSampler("test heap", BundleClock.FromStamp(Stopwatch.GetTimestamp()), hr.Sample, null, null, 100);
    sampler.Start();
    Thread.Sleep(450);
    sampler.Stop();
    var hs = hr.Write(dir, sampler.LastError);
    var hl = File.ReadAllLines(Path.Combine(dir, "heap.jsonl"));
    Check("heap samples collected", hl.Length >= 1 + 3, hl.Length.ToString());
    var hrow = JsonDocument.Parse(hl[1]).RootElement;
    Check("heap row fields", hrow.TryGetProperty("total_memory_bytes", out _) && hrow.TryGetProperty("gen0_count", out _) && hrow.TryGetProperty("gen2_count", out _) && hrow.TryGetProperty("ts_local", out _) && hrow.TryGetProperty("t_ms", out _));
    Check("sampler no errors", sampler.Errors == 0, sampler.LastError ?? "");

    // GPU memory reader: DXGI/PDH on this machine. Either real numbers or an honest reason.
    var gr = new GpuRecorder(clock);
    gr.Setup(Environment.ProcessId);
    gr.Sample(0);
    gr.Sample(1000);
    gr.Teardown();
    var vs = gr.Write(dir);
    var gl = File.ReadAllLines(Path.Combine(dir, "gpu-memory.jsonl"));
    Check("gpu file written", gl.Length >= 1, gl.Length.ToString());
    Check("gpu real or reason", (vs.Enabled && gl.Length >= 3) || (!vs.Enabled && !string.IsNullOrEmpty(vs.Reason)), $"enabled={vs.Enabled} reason={vs.Reason}");

    // DXGI switched off (GpuMemoryDxgi=false): the file must say so, with the reason, and report PDH only
    var gr2 = new GpuRecorder(clock, false);
    gr2.Setup(Environment.ProcessId);
    gr2.Sample(0);
    gr2.Teardown();
    var vs2 = gr2.Write(dir);
    var gl2 = File.ReadAllLines(Path.Combine(dir, "gpu-memory.jsonl"));
    var gh2 = JsonDocument.Parse(gl2[0]).RootElement;
    if (vs2.Enabled)
    {
        Check("gpu dxgi off header says so with reason", gh2.GetProperty("dxgi_enabled").GetBoolean() == false && gh2.GetProperty("dxgi_off_reason").GetString()!.Contains("GpuMemoryDxgi") && gh2.GetProperty("pdh_note").GetString()!.Contains("one reading"));
        Check("gpu dxgi off rows are labelled pdh", JsonDocument.Parse(gl2[1]).RootElement.GetProperty("source").GetString() == "pdh");
    }
    else Check("gpu dxgi off without pdh still gives a reason", !string.IsNullOrEmpty(vs2.Reason), vs2.Reason ?? "");
    var gh1 = JsonDocument.Parse(gl[0]).RootElement;
    if (vs.Enabled) Check("gpu dxgi on header says pdh is not independent", gh1.GetProperty("dxgi_enabled").GetBoolean() && !gh1.GetProperty("pdh_independent_of_runner_pdh_csv").GetBoolean());

    BundleWriter.WriteStubs(dir);
    foreach (var stub in BundleWriter.Stubs)
    {
        var sj = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, stub.File))).RootElement;
        Check("stub " + stub.File, !sj.GetProperty("enabled").GetBoolean() && sj.GetProperty("reason").GetString()!.Length > 0 && sj.GetProperty("schema_version").GetInt32() == 1);
    }
    try { Directory.Delete(dir, true); } catch { }
}


// ---------------------------------------------------------------- step 3: allocations by mod
{
    // GCAllocationTick payload v4: u32 amount, u32 kind, u16 clr id, u64 amount64, pointer type id, UTF-16 type name, heap index, address, object size
    byte[] Payload(long bytes, uint kind, string type)
    {
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        w.Write((uint)bytes); w.Write(kind); w.Write((ushort)0); w.Write((ulong)bytes); w.Write(0xABCDUL);
        w.Write(System.Text.Encoding.Unicode.GetBytes(type)); w.Write((ushort)0);
        w.Write(0u); w.Write(0UL); w.Write(0UL);
        return ms2.ToArray();
    }
    Check("tick parse ok", AllocationTick.TryParse(Payload(102400, 0, "System.String"), out long tb, out bool tl, out string tn) && tb == 102400 && !tl && tn == "System.String", $"{tb} {tl} {tn}");
    Check("tick parse large", AllocationTick.TryParse(Payload(90000, 1, "Foo.Big[]"), out _, out bool tl2, out _) && tl2);
    Check("tick parse bytes use 64 bit amount", AllocationTick.TryParse(Payload(5_000_000_000, 0, "X"), out long tb3, out _, out _) && tb3 == 5_000_000_000, tb3.ToString());
    Check("tick parse short payload is rejected", !AllocationTick.TryParse(new byte[4], out _, out _, out _));

    // Synthetic ticks on the fixture frames: draw -> moda, root -> game, other -> unknown, runtime only -> runtime
    var aset = new SampleSet();
    aset.Frames.AddRange(set.Frames);
    aset.Stacks.AddRange(set.Stacks);
    aset.Stacks.Add(new[] { other, root });         // stack 4: unknown owner (MysteryAsm), billed unknown
    aset.Stacks.Add(new[] { sort });                // stack 5: runtime frames only
    aset.TypeNames.AddRange(new[] { "System.String", "ModA.Particle[]", "Game.Chunk" });
    void Tick(int stack, long bytes, int type, bool large = false) => aset.Allocs.Add(new AllocSample { ThreadId = 1, TimeMs = aset.Allocs.Count, StackId = stack, Bytes = bytes, TypeId = type, Large = large });
    for (int i = 0; i < 6; i++) Tick(0, 100000, 1);     // modA.Draw: 600000
    Tick(2, 100000, 0);                                  // modA via runtime callee: 100000 string
    Tick(1, 100000, 2);                                  // game render: 100000
    Tick(4, 100000, 2, true);                            // unknown assembly: 100000, large
    Tick(5, 50000, 0);                                   // runtime frames only: 50000
    var ameta = new CaptureMeta { Label = "alloc", AllocationSampling = true, AssemblyToMod = new() { ["ModAsm"] = "moda" }, GameAssemblies = new() { "VintagestoryLib" }, QpcFrequency = Stopwatch.Frequency, EndQpc = Stopwatch.Frequency * 2, GcAllocatedBytesStart = 1000, GcAllocatedBytesEnd = 1000 + 1_000_000 };
    var aj = AllocationReport.Build(aset, ameta);
    Check("alloc enabled", aj["enabled"]!.GetValue<bool>());
    Check("alloc schema_version", aj["schema_version"]!.GetValue<int>() == 1);
    Check("alloc total", aj["total_allocated_bytes"]!.GetValue<long>() == 950000, aj["total_allocated_bytes"]!.ToJsonString());
    Check("alloc sampling rate stated", aj["sampling"]!["sampling_rate"]!.GetValue<string>().Contains("100 KB"));
    var owners = aj["by_owner"]!.AsArray();
    var modaNode = owners.First(o => o!["owner"]!.GetValue<string>() == "moda")!;
    Check("alloc moda bytes", modaNode["allocated_bytes"]!.GetValue<long>() == 700000, modaNode["allocated_bytes"]!.ToJsonString());
    Check("alloc moda kind", modaNode["kind"]!.GetValue<string>() == "mod");
    Check("alloc moda top type", modaNode["top_types"]![0]!["type"]!.GetValue<string>() == "ModA.Particle[]");
    Check("alloc moda top method", modaNode["top_methods"]![0]!["method"]!.GetValue<string>().StartsWith("ModA.Draw"), modaNode["top_methods"]![0]!["method"]!.ToJsonString());
    Check("alloc runtime callee billed to its mod caller", modaNode["ticks"]!.GetValue<int>() == 7);
    Check("alloc unknown bucket", aj["unknown_bucket"]!["allocated_bytes"]!.GetValue<long>() == 100000);
    Check("alloc game bucket", owners.First(o => o!["owner"]!.GetValue<string>() == Attribution.Game)!["allocated_bytes"]!.GetValue<long>() == 100000);
    Check("alloc runtime bucket", owners.First(o => o!["owner"]!.GetValue<string>() == Attribution.Runtime)!["allocated_bytes"]!.GetValue<long>() == 50000);
    Check("alloc large object bytes", aj["large_object_heap_bytes"]!.GetValue<long>() == 100000);
    Near("alloc cross check percent", aj["cross_check"]!["estimate_vs_gc_delta_percent"]!.GetValue<double>(), -5.0, 0.01);
    Check("alloc owner sum is total", owners.Sum(o => o!["allocated_bytes"]!.GetValue<long>()) == aj["total_allocated_bytes"]!.GetValue<long>());
    Check("alloc markdown has the owner", AllocationReport.ToMarkdown(aj).Contains("moda"));
    ameta.AllocationSampling = false;
    var aoff = AllocationReport.Build(aset, ameta);
    Check("alloc off is disabled with reason", !aoff["enabled"]!.GetValue<bool>() && aoff["reason"]!.GetValue<string>().Length > 0);
    ameta.AllocationSampling = true;
    var aempty = AllocationReport.Build(new SampleSet(), ameta);
    Check("alloc on with no ticks is disabled with reason", !aempty["enabled"]!.GetValue<bool>() && aempty["reason"]!.GetValue<string>().Contains("no GCAllocationTick"));

    // End to end: a real in-process EventPipe session with allocation ticks, then the real loader and report
    string adir = Path.Combine(Path.GetTempPath(), "sparkvs-alloc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(adir);
    string trace = Path.Combine(adir, "capture.nettrace");
    var cs = new CaptureSession(trace, true);
    cs.Start();
    var keep = new List<byte[]>();
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 1500)
    {
        for (int i = 0; i < 200; i++) keep.Add(new byte[2000]);
        if (keep.Count > 20000) keep.Clear();
        Thread.Sleep(1);
    }
    cs.Stop(60000);
    var emeta = new CaptureMeta { Label = "e2e", AllocationSampling = true, StartQpc = cs.StartQpc, EndQpc = cs.EndQpc, QpcFrequency = Stopwatch.Frequency, GameAssemblies = new() { "sparkvs-tests" }, MainThreadOsId = 0 };
    var eset = NettraceLoader.Load(trace, emeta, null);
    Check("e2e ticks decoded", eset.Allocs.Count > 0, $"allocs={eset.Allocs.Count} err={cs.ReaderError?.Message}");
    long tickSum = eset.Allocs.Sum(a => a.Bytes);
    Check("e2e ticks carry stacks", eset.Allocs.Any(a => eset.Stacks[a.StackId].Length > 0), "no stacks");
    Check("e2e byte[] type seen", eset.TypeNames.Any(n => n.Contains("System.Byte[]") || n.Contains("Byte[]")), string.Join(",", eset.TypeNames.Take(5)));
    emeta.GcAllocatedBytesStart = 0; emeta.GcAllocatedBytesEnd = tickSum;
    var ej = AllocationReport.Build(eset, emeta);
    Check("e2e report enabled and total matches ticks", ej["enabled"]!.GetValue<bool>() && ej["total_allocated_bytes"]!.GetValue<long>() == tickSum);
    Check("e2e owner by_owner non empty", ej["by_owner"]!.AsArray().Count > 0);
    // Same load with sampling off: the session must produce no allocation ticks (config switch is real)
    string trace2 = Path.Combine(adir, "off.nettrace");
    var csOff = new CaptureSession(trace2, false);
    csOff.Start();
    var sw2 = Stopwatch.StartNew();
    while (sw2.ElapsedMilliseconds < 800)
    {
        for (int i = 0; i < 200; i++) keep.Add(new byte[2000]);
        if (keep.Count > 20000) keep.Clear();
        Thread.Sleep(1);
    }
    csOff.Stop(60000);
    var offSet = NettraceLoader.Load(trace2, new CaptureMeta { StartQpc = csOff.StartQpc, EndQpc = csOff.EndQpc, QpcFrequency = Stopwatch.Frequency }, null);
    Check("e2e sampling off gives no allocation ticks", offSet.Allocs.Count == 0, offSet.Allocs.Count.ToString());
    Console.WriteLine($"e2e allocation ticks={eset.Allocs.Count} bytes={tickSum} types={eset.TypeNames.Count} trace={new FileInfo(trace).Length}");
    try { Directory.Delete(adir, true); } catch { }
}

// ---------------------------------------------------------------- step 4: chunk pipeline
{
    Check("stage ids match table", ChunkStages.All.Length == ChunkStages.Count && ChunkStages.All.Select((d, i) => d.Id == i).All(x => x));
    Check("stage names unique", ChunkStages.All.Select(d => d.Name).Distinct().Count() == ChunkStages.Count);
    var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\g\VintagestoryLib.dll", @"C:\g\Lib\Newtonsoft.Json.dll", @"C:\g\Mods\VSEssentials.dll" };
    Check("assembly shipped in game root, Lib or Mods is the game's", Attribution.ShipsWithGame("VintagestoryLib", @"C:\g", shipped.Contains) && Attribution.ShipsWithGame("Newtonsoft.Json", @"C:\g", shipped.Contains) && Attribution.ShipsWithGame("VSEssentials", @"C:\g", shipped.Contains));
    Check("assembly that does not ship in the game folder stays unknown", !Attribution.ShipsWithGame("P5WorldgenEngine", @"C:\g", shipped.Contains) && !Attribution.ShipsWithGame("", @"C:\g", shipped.Contains));
    Check("PopulateChunk pins its parameter type (two overloads at runtime)", ChunkHookTargets.All.Any(h => h.Method == "PopulateChunk" && h.ParamTypes != null && h.ParamTypes.Length == 1 && h.ParamTypes[0] == "Vintagestory.Server.ChunkColumnLoadRequest"));
    Check("every stage has a hook target", ChunkStages.All.All(d => ChunkHookTargets.All.Any(h => h.Stages.Contains(d.Id))));
    Check("pass mapping", ChunkTimers.WorldgenStageForPass(1) == ChunkStages.WorldgenTerrain && ChunkTimers.WorldgenStageForPass(5) == ChunkStages.WorldgenPreDone && ChunkTimers.WorldgenStageForPass(9) == ChunkStages.WorldgenOtherPass);

    // Idle: Start returns 0 and Stop records nothing
    Check("timer idle start is 0", ChunkTimers.Start() == 0);
    ChunkTimers.Stop(ChunkStages.WorldgenPopulate, 0);

    ChunkTimers.Begin();
    // Warm up, then 200000 start/stop pairs must not allocate on this thread
    for (int i = 0; i < 1000; i++) ChunkTimers.Stop(ChunkStages.StreamReadDb, ChunkTimers.Start());
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 200000; i++)
    {
        long st = ChunkTimers.Start();
        ChunkTimers.Stop(ChunkStages.StreamReadDb, st);
        ChunkTimers.AddUpload(10, 15, 400);
    }
    long timerAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Check("timers allocate nothing per call", timerAlloc == 0, timerAlloc + " bytes over 200000 calls");
    var stats0 = ChunkTimers.End();
    Check("timer idle after end", ChunkTimers.Start() == 0);
    Check("timer recorded all calls", stats0[ChunkStages.StreamReadDb].Count == 201000, stats0[ChunkStages.StreamReadDb].Count.ToString());
    Check("upload totals", ChunkTimers.UploadTotals() == (200000, 2_000_000, 3_000_000, 80_000_000));

    // Exact stats: 100 samples of 1..100 ms
    var st1 = new StageStats(1000);   // 1000 ticks per second, so ticks == ms
    for (int i = 1; i <= 100; i++) st1.Record(i);
    Near("stats total", st1.TotalMs, 5050, 0.01);
    Near("stats max", st1.MaxMs, 100, 0.01);
    Near("stats p95 nearest rank", st1.P95Ms(out int used), 95, 0.01);
    Check("stats p95 used all", used == 100);

    // Writer: installed stage with calls, installed stage with no calls, failed hook
    var hooks = new List<HookInfo>
    {
        new() { Stages = new[] { ChunkStages.StreamReadDb, ChunkStages.StreamReadMiss }, Target = "GameDatabase.GetChunk", Installed = true, Note = "n" },
        new() { Stages = new[] { ChunkStages.WorldgenPopulate }, Target = "PopulateChunk", Installed = false, FailureReason = "type not found in this fork" },
    };
    hooks[0].OtherPatches.Add(("watersheds", "prefix", "Watersheds.Patch.Pre"));
    var cstats = new StageStats[ChunkStages.Count];
    for (int i = 0; i < cstats.Length; i++) cstats[i] = new StageStats(1000);
    for (int i = 1; i <= 100; i++) cstats[ChunkStages.StreamReadDb].Record(i);
    var cclock = BundleClock.FromStamp(Stopwatch.GetTimestamp());
    var queue = new QueueRecorder(cclock, () => new QueueSnapshot(7, 3));
    queue.Sample(0); queue.Sample(1000);
    var cj = ChunkPipelineWriter.Build(cclock, 5000, cstats, (4, 100, 150, 4000), hooks, queue.ToJson("test", null), "1 of 2 hooks installed");
    var cst = cj["stages"]!.AsObject();
    Check("chunk stage with calls", cst["stream_read_db"]!["calls"]!.GetValue<long>() == 100 && cst["stream_read_db"]!["p95_ms"]!.GetValue<double>() > 94);
    Check("chunk installed stage without calls is null with reason", cst["stream_read_db_miss"]!["calls"]!.GetValue<long>() == 0 && cst["stream_read_db_miss"]!["total_ms"] == null && cst["stream_read_db_miss"]!["reason"]!.GetValue<string>().Contains("no calls"));
    Check("chunk failed hook is null with its reason", cst["worldgen_populate_column"]!["calls"] == null && cst["worldgen_populate_column"]!["total_ms"] == null && cst["worldgen_populate_column"]!["reason"]!.GetValue<string>() == "type not found in this fork");
    Check("chunk never hooked stage is null with reason", cst["upload_chunk"]!["total_ms"] == null && cst["upload_chunk"]!["reason"]!.GetValue<string>().Length > 0);
    Check("chunk no zero for unhooked", cst.All(kv => kv.Value!["hooked"]!.GetValue<bool>() || kv.Value["calls"] == null));
    Check("chunk hook_notes carry other patches", cj["hook_notes"]![0]!["other_patches"]![0]!["owner"]!.GetValue<string>() == "watersheds");
    Check("chunk runtime db snapshot present", cj["runtime_db_snapshot"]!.AsArray().Count >= 5);
    Check("chunk upload fields", cj["upload"]!["mesh_parts_uploaded"]!.GetValue<long>() == 4 && cj["upload"]!["bytes_uploaded_estimated"]!.GetValue<long>() == 4000);
    Check("chunk queue series", cj["queue_depth"]!["samples"]!.GetValue<int>() == 2 && cj["queue_depth"]!["series"]![1]!["t_ms"]!.GetValue<double>() == 1000 && cj["queue_depth"]!["series"]![0]!["ts_local"]!.GetValue<string>().Contains("T"));
    Check("chunk schema_version", cj["schema_version"]!.GetValue<int>() == 1);
    Check("chunk names units", cst["stream_read_db"]!.AsObject().ContainsKey("total_ms") && cst["stream_read_db"]!.AsObject().ContainsKey("p95_ms"));
    var qoff = new QueueRecorder(cclock, () => null).ToJson("t", "counters missing");
    Check("queue with no samples is disabled with reason", !qoff["enabled"]!.GetValue<bool>() && qoff["reason"]!.GetValue<string>() == "counters missing");
    Check("chunk disabled form", !ChunkPipelineWriter.Disabled("off")["enabled"]!.GetValue<bool>());

    // Hook targets resolve against the game's own assemblies (Optimum install). Skipped, not failed, if the install is absent.
    string optimum = Environment.GetEnvironmentVariable("VINTAGE_STORY") is { Length: > 0 } vsEnv ? vsEnv : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Optimum");
    if (File.Exists(Path.Combine(optimum, "VintagestoryLib.dll")))
    {
        System.Reflection.Assembly? Resolve(object? s, ResolveEventArgs a)
        {
            string n = new System.Reflection.AssemblyName(a.Name).Name + ".dll";
            foreach (var d in new[] { optimum, Path.Combine(optimum, "Lib") })
            {
                string f = Path.Combine(d, n);
                if (File.Exists(f)) return System.Reflection.Assembly.LoadFrom(f);
            }
            return null;
        }
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            var lib = System.Reflection.Assembly.LoadFrom(Path.Combine(optimum, "VintagestoryLib.dll"));
            var api = System.Reflection.Assembly.LoadFrom(Path.Combine(optimum, "VintagestoryAPI.dll"));
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var h in ChunkHookTargets.All)
            {
                var type = lib.GetType(h.TypeName) ?? api.GetType(h.TypeName);
                Check("resolve type " + h.TypeName, type != null);
                if (type == null) continue;
                var ms3 = type.GetMethods(any).Where(m => m.Name == h.Method && m.GetParameters().Length == h.ParamCount).ToList();
                if (h.ParamTypes != null)
                {
                    var pinned = ms3.Where(m => m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(h.ParamTypes)).ToList();
                    Check("pinned parameter types pick exactly one overload " + h.Target, pinned.Count == 1, "matches=" + pinned.Count);
                    if (pinned.Count == 1) ms3 = pinned;
                }
                Check("resolve unique method " + h.Target, ms3.Count == 1, "matches=" + ms3.Count);
                if (ms3.Count != 1) continue;
                var names = ms3[0].GetParameters().Select(p => p.Name).ToList();
                Check("param names " + h.Target, h.RequiredParams.All(r => names.Contains(r)), string.Join(",", names));
                if (h.ReturnType != null) Check("return type " + h.Target, ms3[0].ReturnType.Name == h.ReturnType, ms3[0].ReturnType.Name);
            }
            var rs = lib.GetType("Vintagestory.Client.RuntimeStats");
            Check("RuntimeStats counters", rs?.GetField("chunksAwaitingTesselation")?.FieldType == typeof(int) && rs?.GetField("chunksAwaitingPooling")?.FieldType == typeof(int));
            var ml = lib.GetType("Vintagestory.Common.ModLoader")?.GetMethods(any).Where(m => m.Name == "TryRunModPhase" && m.GetParameters().Length == 4).ToList();
            Check("TryRunModPhase resolves", ml?.Count == 1 && ml[0].GetParameters()[3].ParameterType.Name == "ModRunPhase");
            var phases = lib.GetType("Vintagestory.Common.ModRunPhase");
            Check("ModRunPhase names", phases != null && new[] { "Pre", "Start", "AssetsLoaded", "AssetsFinalize", "Normal" }.All(n => Enum.GetNames(phases).Contains(n)));
        }
        catch (Exception ex)
        {
            Console.WriteLine("SKIP game resolution test: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
    }
    else Console.WriteLine("SKIP game resolution test: Optimum install not found");
}

// ---------------------------------------------------------------- step 5: patch map, load phases, asset inventory
{
    var snap = new PatchSnapshot { Side = "client", TakenLocal = DateTime.Now };
    PatchedMethod PM(string name, params (string o, string k)[] ps) { var m = new PatchedMethod { Method = name }; foreach (var (o, k) in ps) m.Patches.Add(new PatchEntry(o, k, "P." + o + k)); return m; }
    snap.Methods.Add(PM("A.Shared()", ("optimum", "prefix"), ("rivers", "postfix"), ("rivers", "transpiler")));
    snap.Methods.Add(PM("B.Solo()", ("optimum", "prefix")));
    snap.Methods.Add(PM("C.Three()", ("a", "prefix"), ("b", "prefix"), ("c", "finalizer")));
    var ss = PatchMapBuilder.Summarise(snap);
    Check("patch totals", ss["patched_methods_total"]!.GetValue<int>() == 3 && ss["patches_total"]!.GetValue<int>() == 7 && ss["owners_total"]!.GetValue<int>() == 5);
    Check("patch kinds", ss["patches_by_kind"]!["prefix"]!.GetValue<int>() == 4 && ss["patches_by_kind"]!["postfix"]!.GetValue<int>() == 1 && ss["patches_by_kind"]!["transpiler"]!.GetValue<int>() == 1 && ss["patches_by_kind"]!["finalizer"]!.GetValue<int>() == 1);
    Check("multi owner count", ss["methods_patched_by_multiple_owners"]!.GetValue<int>() == 2);
    var firstMulti = ss["multi_owner_methods"]![0]!;
    Check("multi owner order and owners", firstMulti["method"]!.GetValue<string>() == "C.Three()" && firstMulti["owner_count"]!.GetValue<int>() == 3);
    var riv = ss["multi_owner_methods"]!.AsArray().First(m => m!["method"]!.GetValue<string>() == "A.Shared()")!["owners"]!.AsArray().First(o => o!["owner"]!.GetValue<string>() == "rivers")!;
    Check("owner kinds listed", riv["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).OrderBy(x => x).SequenceEqual(new[] { "postfix", "transpiler" }));
    var pmap = PatchMapBuilder.Build(snap, null, null, "no server here");
    Check("patch map sides", pmap["sides"]!["client"]!["available"]!.GetValue<bool>() && !pmap["sides"]!["server"]!["available"]!.GetValue<bool>() && pmap["sides"]!["server"]!["reason"]!.GetValue<string>() == "no server here");
    Check("patch map none is disabled with reason", !PatchMapBuilder.Build(null, null, "c", "s")["enabled"]!.GetValue<bool>());
    Check("patch snapshot failure is reported", !PatchMapBuilder.Summarise(new PatchSnapshot { FailureReason = "boom" })["available"]!.GetValue<bool>());
    Check("patch headline", PatchMapBuilder.Headline(pmap)["client"]!["patches_total"]!.GetValue<int>() == 7 && PatchMapBuilder.Headline(pmap)["server"] == null);

    // Load phases
    var lpl = new LoadPhaseLog();
    lpl.MarkInstalled();
    long f = Stopwatch.Frequency;
    lpl.Add("moda", "StartPre", "client", 0, f / 100);
    lpl.Add("moda", "Start", "client", f / 100, f / 100 + f / 50);
    lpl.Add("moda", "StartClientSide", "client", f / 2, f / 2 + f / 10);
    lpl.Add("moda", "Start", "server", f / 10, f / 10 + f / 100);
    lpl.Add("moda", "StartServerSide", "server", f, f + f / 100);
    var lj = lpl.ToJson(new[] { "moda", "modb" });
    var ma = lj["per_mod"]!["moda"]!;
    Near("load StartPre ms", ma["client"]!["StartPre_ms"]!.GetValue<double>(), 10, 0.01);
    Near("load Start client ms", ma["client"]!["Start_ms"]!.GetValue<double>(), 20, 0.01);
    Near("load StartClientSide ms", ma["client"]!["StartClientSide_ms"]!.GetValue<double>(), 100, 0.01);
    Check("load missing phase is null with reason", ma["client"]!["AssetsLoaded_ms"] == null && ma["client"]!["AssetsLoaded_reason"]!.GetValue<string>().Length > 0);
    Check("load server has no client phase key", ma["server"]!.AsObject().ContainsKey("StartServerSide_ms") && !ma["server"]!.AsObject().ContainsKey("StartClientSide_ms"));
    Check("load mod without rows is listed with null total", lj["per_mod"]!["modb"]!["total_ms"] == null);
    Near("load total boot ms", lj["total_boot_ms"]!.GetValue<double>(), 1010, 0.5);
    Near("load mod total", ma["total_ms"]!.GetValue<double>(), 10 + 20 + 100 + 10 + 10, 0.01);
    var lpOff = new LoadPhaseLog();
    lpOff.MarkFailed("hook missing");
    Check("load phases hook not installed is disabled with reason", !lpOff.ToJson(Array.Empty<string>())["enabled"]!.GetValue<bool>() && lpOff.ToJson(Array.Empty<string>())["reason"]!.GetValue<string>() == "hook missing");
    var lpClientOnly = new LoadPhaseLog();
    lpClientOnly.MarkInstalled();
    lpClientOnly.Add("m", "Start", "client", 0, f / 100);
    Check("load absent side is null with reason", lpClientOnly.ToJson(new[] { "m" })["per_mod"]!["m"]!["server"] == null && lpClientOnly.ToJson(new[] { "m" })["per_mod"]!["m"]!["server_reason"]!.GetValue<string>().Length > 0);

    // Asset inventory copy
    string idir = Path.Combine(Path.GetTempPath(), "sparkvs-inv-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(idir);
    File.WriteAllText(Path.Combine(idir, "old.json"), "{\"mods\":1}");
    File.SetLastWriteTime(Path.Combine(idir, "old.json"), DateTime.Now.AddDays(-3));
    File.WriteAllText(Path.Combine(idir, "new.json"), "{\"mods\":2}");
    var ai = AssetInventoryCopy.Build(idir, DateTime.Now);
    Check("inventory newest copied", ai["enabled"]!.GetValue<bool>() && ai["inventory"]!["mods"]!.GetValue<int>() == 2 && ai["source_path"]!.GetValue<string>().EndsWith("new.json"));
    Check("inventory labelled static with timestamp", ai["label"]!.GetValue<string>() == "static" && ai["source_modified_local"]!.GetValue<string>().Contains("T"));
    Check("inventory missing folder is disabled with reason", !AssetInventoryCopy.Build(Path.Combine(idir, "nope"), DateTime.Now)["enabled"]!.GetValue<bool>());
    File.Delete(Path.Combine(idir, "old.json")); File.Delete(Path.Combine(idir, "new.json"));
    Check("inventory empty folder is disabled with reason", AssetInventoryCopy.Build(idir, DateTime.Now)["reason"]!.GetValue<string>().Contains("no .json"));
    try { Directory.Delete(idir, true); } catch { }

    // Report step reads the bundle files and lists what was and was not collected
    string rdir = Path.Combine(Path.GetTempPath(), "sparkvs-rep-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(rdir);
    BundleWriter.WriteJson(Path.Combine(rdir, "chunk-pipeline.json"), cj_forReport());
    BundleWriter.WriteJson(Path.Combine(rdir, "patch-map.json"), pmap);
    BundleWriter.WriteJson(Path.Combine(rdir, "asset-inventory.json"), AssetInventoryCopy.Build(null, DateTime.Now));
    BundleWriter.WriteStubs(rdir);
    meta.Bundle = new BundleSummary { StartLocal = "t", SparkVsVersion = "test" };
    var breport = Analyzer.Build(set, meta);
    var hl = JsonDocument.Parse(ReportBuilder.HeadlineJson(breport, AllocationReport.Disabled("x"), rdir)).RootElement;
    var nc = hl.GetProperty("not_collected").EnumerateArray().Select(e => e.GetString()).ToList();
    Check("headline lists disabled files", nc.Contains("asset-inventory.json") && nc.Contains("vram-by-mod.json") && nc.Contains("gpu-phases.json") && nc.Contains("allocations.json"), string.Join(",", nc));
    Check("headline does not list collected files", !nc.Contains("chunk-pipeline.json") && !nc.Contains("patch-map.json"));
    Check("headline has chunk and patch sections", hl.GetProperty("sections").TryGetProperty("chunk_pipeline", out _) && hl.GetProperty("sections").TryGetProperty("patch_map", out _));
    Check("deferred stubs carry the plan reason", hl.GetProperty("not_collected_reasons").GetProperty("vram-by-mod.json").GetString()!.Contains("deferred, see capture-stack-audit/PLAN.md"));
    var extraMd = ExtraSections.Markdown(rdir);
    Check("markdown has chunk table and patch lines", extraMd.Contains("| worldgen_populate_column |") && extraMd.Contains("client: 3 patched methods"));
    BundleWriter.WritePlaceholdersForMissing(rdir, "writer failed");
    Check("placeholders only for missing", JsonDocument.Parse(File.ReadAllText(Path.Combine(rdir, "allocations.json"))).RootElement.GetProperty("reason").GetString() == "writer failed" && JsonDocument.Parse(File.ReadAllText(Path.Combine(rdir, "patch-map.json"))).RootElement.GetProperty("enabled").GetBoolean());
    try { Directory.Delete(rdir, true); } catch { }

    JsonObject cj_forReport()
    {
        var hs = new List<HookInfo> { new() { Stages = new[] { ChunkStages.StreamReadDb }, Target = "x", Installed = true } };
        var st = new StageStats[ChunkStages.Count];
        for (int i = 0; i < st.Length; i++) st[i] = new StageStats(1000);
        st[ChunkStages.StreamReadDb].Record(5);
        var ck = BundleClock.FromStamp(Stopwatch.GetTimestamp());
        return ChunkPipelineWriter.Build(ck, 1000, st, (0, 0, 0, 0), hs, new QueueRecorder(ck, () => null).ToJson("t", "none"), "1 of 1");
    }
}

// ---------------------------------------------------------------- GPU time by render phase
{
    const long Sec = 1_000_000_000L;
    GpuPhaseRecorder NewRec(FakeGpuTimer ft, int cap = 1000) => new GpuPhaseRecorder(ft, cap);
    // Frame i: boundary times in ms after the frame base; the ui interval is uiMs.
    void RunFrame(GpuPhaseRecorder rec, FakeGpuTimer ft, long i, double uiMs, bool shadow = true, bool skipOpaqueEnd = false, long stamp = 0)
    {
        long b = i * Sec;
        long Ms(double v) => b + (long)(v * 1_000_000);
        ft.Now = Ms(0); rec.BeginFrame(stamp);
        if (shadow)
        {
            ft.Now = Ms(1); rec.Mark(GpuBoundary.ShadowStart);
            ft.Now = Ms(2); rec.Mark(GpuBoundary.ShadowFarDone);
            ft.Now = Ms(3); rec.Mark(GpuBoundary.ShadowNearDone);
        }
        ft.Now = Ms(4); rec.Mark(GpuBoundary.OpaqueStart);
        ft.Now = Ms(9); if (!skipOpaqueEnd) rec.Mark(GpuBoundary.OpaqueEnd);
        ft.Now = Ms(11); rec.Mark(GpuBoundary.AfterOitEnd);
        ft.Now = Ms(14); rec.Mark(GpuBoundary.AfterPostEnd);
        ft.Now = Ms(15); rec.Mark(GpuBoundary.AfterBlitEnd);
        ft.Now = Ms(15 + uiMs); rec.Mark(GpuBoundary.DoneEnd);
    }

    // One frame: phases are the differences between the right boundaries, unattributed is the rest
    var ft1 = new FakeGpuTimer();
    var r1 = NewRec(ft1);
    r1.Begin(0, 0);
    for (int i = 0; i < 3; i++) RunFrame(r1, ft1, i, 1);
    Check("gpu ring has not read before f+3", r1.FramesMeasured == 0);
    RunFrame(r1, ft1, 3, 1);
    Check("gpu ring reads frame f at frame f+3", r1.FramesMeasured == 1 && r1.DroppedNotReady == 0, $"{r1.FramesMeasured}/{r1.DroppedNotReady}");
    Near("gpu frame ms", r1.Stats(GpuPhase.Frame).Mean, 16);
    Near("gpu shadow ms", r1.Stats(GpuPhase.Shadow).Mean, 2);
    Near("gpu opaque ms", r1.Stats(GpuPhase.Opaque).Mean, 5);
    Near("gpu oit ms", r1.Stats(GpuPhase.Oit).Mean, 2);
    Near("gpu post ms", r1.Stats(GpuPhase.Post).Mean, 3);
    Near("gpu final ms", r1.Stats(GpuPhase.Final).Mean, 1);
    Near("gpu ui ms", r1.Stats(GpuPhase.Ui).Mean, 1);
    Near("gpu unattributed ms", r1.Stats(GpuPhase.Unattributed).Mean, 2);
    Check("gpu timer never read an unavailable result", ft1.ResultBeforeReady == 0);

    // Percentiles: ui = 1..100 ms over 100 frames
    var ft2 = new FakeGpuTimer();
    var r2 = NewRec(ft2);
    r2.Begin(0, 0);
    for (int i = 0; i < 103; i++) RunFrame(r2, ft2, i, i + 1);
    var us = r2.Stats(GpuPhase.Ui);
    Check("gpu percentile count", us.Count == 100 && r2.FramesMeasured == 100, $"{us.Count}/{r2.FramesMeasured}");
    Near("gpu mean", us.Mean, 50.5);
    Near("gpu p50", us.P50, 50);
    Near("gpu p95", us.P95, 95);
    Near("gpu p99", us.P99, 99);
    Near("gpu max", us.Max, 100);

    // Not ready: frame 5 is dropped without a stall, its slot is reused by frame 9 and later frames still read
    var ft3 = new FakeGpuTimer { NotReady = v => v / Sec == 5 };
    var r3 = NewRec(ft3);
    r3.Begin(0, 0);
    for (int i = 0; i < 20; i++) RunFrame(r3, ft3, i, 1);
    Check("gpu not ready dropped and counted", r3.DroppedNotReady == 1 && r3.FramesMeasured == 16, $"{r3.DroppedNotReady}/{r3.FramesMeasured}");
    Check("gpu not ready never read", ft3.ResultBeforeReady == 0);

    // Incomplete frame (a boundary never fired) and frames with shadows off
    var ft4 = new FakeGpuTimer();
    var r4 = NewRec(ft4);
    r4.Begin(0, 0);
    for (int i = 0; i < 10; i++) RunFrame(r4, ft4, i, 1, shadow: i % 2 == 0, skipOpaqueEnd: i == 3);
    Check("gpu incomplete counted", r4.Incomplete == 1 && r4.FramesMeasured == 6, $"{r4.Incomplete}/{r4.FramesMeasured}");
    Check("gpu shadow count only where shadows ran", r4.SampleCount(GpuPhase.Shadow) == 4 && r4.SampleCount(GpuPhase.Opaque) == 6, $"{r4.SampleCount(GpuPhase.Shadow)}/{r4.SampleCount(GpuPhase.Opaque)}");

    // Inactive recorder issues nothing
    var ft5 = new FakeGpuTimer();
    var r5 = NewRec(ft5);
    for (int i = 0; i < 10; i++) RunFrame(r5, ft5, i, 1);
    Check("gpu inactive issues no queries", ft5.Stamps == 0 && r5.FramesIssued == 0);

    // Overflow and zero allocation on the hot path (BeginFrame, Mark, aggregation)
    var ft6 = new FakeGpuTimer();
    var r6 = NewRec(ft6, 500);
    r6.Begin(0, 0);
    for (int i = 0; i < 50; i++) RunFrame(r6, ft6, i, 1);   // warm up
    long gb = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 50; i < 3050; i++) RunFrame(r6, ft6, i, 1);
    long gAlloc = GC.GetAllocatedBytesForCurrentThread() - gb;
    Check("gpu phases zero alloc", gAlloc == 0, gAlloc.ToString());
    Check("gpu overflow counted", r6.Overflow > 2000 && r6.SampleCount(GpuPhase.Frame) == 500, $"{r6.Overflow}/{r6.SampleCount(GpuPhase.Frame)}");

    // A/B schedule: off block first, queries only in on blocks
    long freq = Stopwatch.Frequency;
    Check("gpu ab block schedule", !GpuPhaseRecorder.BlockIsOn(0, 0, 2 * freq) && !GpuPhaseRecorder.BlockIsOn(2 * freq - 1, 0, 2 * freq) && GpuPhaseRecorder.BlockIsOn(2 * freq, 0, 2 * freq) && !GpuPhaseRecorder.BlockIsOn(4 * freq, 0, 2 * freq) && GpuPhaseRecorder.BlockIsOn(7, 0, 0));
    var ft7 = new FakeGpuTimer();
    var r7 = NewRec(ft7);
    r7.Begin(0, 2 * freq);
    long frameTicks = freq / 100;   // 100 fps, 8 s => 4 blocks
    for (int i = 0; i < 800; i++) RunFrame(r7, ft7, i, 1, stamp: i * frameTicks);
    Check("gpu ab issues in on blocks only", r7.FramesIssued == 400, r7.FramesIssued.ToString());

    // A/B bookkeeping from the frame log: 8 s, 2 s blocks, off 5 ms frames, on 5.5 ms frames, first second of each block dropped
    long offTicks = (long)(0.005 * freq), onTicks = (long)(0.0055 * freq);
    var abLog = new FrameLog(4, 50, 10000);
    abLog.Reset(0);
    long at = 0;
    while (at < 8 * freq) { at += (at / (2 * freq)) % 2 == 1 ? onTicks : offTicks; abLog.Record(at); }
    var ab = GpuPhaseAb.Compute(abLog, 0, 8 * freq, 2.0);
    Check("gpu ab four blocks alternate", ab.Blocks.Count == 4 && !ab.Blocks[0].On && ab.Blocks[1].On && !ab.Blocks[2].On && ab.Blocks[3].On && ab.BlocksOff == 2 && ab.BlocksOn == 2);
    Near("gpu ab fps off", ab.MeanFpsOff, freq / (double)offTicks, 0.5);
    Near("gpu ab fps on", ab.MeanFpsOn, freq / (double)onTicks, 0.5);
    Near("gpu ab percent cost", ab.PercentCost ?? -1, (1.0 / offTicks - 1.0 / onTicks) / (1.0 / offTicks) * 100, 0.3);
    Check("gpu ab drops the first second", ab.Blocks[0].Frames > 150 && ab.Blocks[0].Frames < 210, ab.Blocks[0].Frames.ToString());
    Near("gpu ab block end ms", ab.Blocks[3].EndMs, 8000, 0.5);
    var abShort = GpuPhaseAb.Compute(abLog, 0, 8 * freq, 0.5);   // blocks shorter than the 1 s drop have no frames
    Check("gpu ab short blocks have no fps", abShort.PercentCost == null && abShort.BlocksOff == 0);
    var abJson = ab.ToJson();
    Check("gpu ab json shape", abJson["blocks"]!.AsArray().Count == 4 && abJson["blocks"]![0]!["state"]!.GetValue<string>() == "off" && abJson["note"]!.GetValue<string>().Contains("present in both") && abJson["percent_cost"] != null);

    // gpu-phases.json shape, disabled form, summary pieces
    var gi = new GpuInfo { Vendor = "TestVendor", Renderer = "TestGpu", TimerBits = 64 };
    var gdoc = GpuPhasesWriter.Build(r2, gi, ab);
    Check("gpu json top level", gdoc["label"]!.GetValue<string>() == "experimental" && gdoc["enabled"]!.GetValue<bool>() && gdoc["gl_vendor"]!.GetValue<string>() == "TestVendor" && gdoc["gl_renderer"]!.GetValue<string>() == "TestGpu" && gdoc["timer_bits"]!.GetValue<int>() == 64);
    Check("gpu json counters", gdoc["frames_measured"]!.GetValue<long>() == 100 && gdoc["frames_dropped_not_ready"] != null && gdoc["frames_incomplete"] != null && gdoc["overflow"] != null && gdoc["overhead_ab"] is JsonObject);
    Check("gpu json phases", GpuPhase.Names.All(n => gdoc["phases"]![n] is JsonObject p && new[] { "count", "mean_ms", "p50_ms", "p95_ms", "p99_ms", "max_ms" }.All(k => p[k] != null) && gdoc["boundaries"]![n] != null));
    Check("gpu json note", gdoc["note"]!.GetValue<string>().Contains("excluded permanently"));
    Check("gpu json without ab is null", GpuPhasesWriter.Build(r2, gi, null)["overhead_ab"] == null);
    var gdis = GpuPhasesWriter.Disabled("no timer queries", gi);
    Check("gpu json disabled form", !gdis["enabled"]!.GetValue<bool>() && gdis["reason"]!.GetValue<string>() == "no timer queries" && gdis["label"]!.GetValue<string>() == "experimental" && gdis["timer_bits"]!.GetValue<int>() == 64);
    string gdir = Path.Combine(Path.GetTempPath(), "sparkvs-gpu-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(gdir);
    BundleWriter.WriteJson(Path.Combine(gdir, "gpu-phases.json"), gdoc);
    var gmd = ExtraSections.Markdown(gdir);
    Check("gpu markdown section", gmd.Contains("## GPU time by render phase [experimental]") && gmd.Contains("| opaque |") && gmd.Contains("In-launch A/B"), gmd.Length.ToString());
    var ghl = ExtraSections.Headline("gpu-phases.json", gdoc);
    Check("gpu headline", ghl["label"]!.GetValue<string>() == "experimental" && ghl["phases"]!["opaque"]!["p95_ms"] != null && ghl["frames_measured"]!.GetValue<long>() == 100);
    BundleWriter.WriteJson(Path.Combine(gdir, "gpu-phases.json"), gdis);
    Check("gpu markdown disabled", ExtraSections.Markdown(gdir).Contains("Not collected: no timer queries"));
    File.Delete(Path.Combine(gdir, "gpu-phases.json"));
    BundleWriter.WritePlaceholdersForMissing(gdir, "writer failed");
    Check("gpu placeholder when writer missing", JsonDocument.Parse(File.ReadAllText(Path.Combine(gdir, "gpu-phases.json"))).RootElement.GetProperty("enabled").GetBoolean() == false);
    Check("gpu is no longer a deferred stub", BundleWriter.Stubs.All(s => s.File != "gpu-phases.json") && BundleWriter.Stubs.Any(s => s.File == "vram-by-mod.json"));
    try { Directory.Delete(gdir, true); } catch { }
}

// ---------------------------------------------------------------- VRAM gate: DXGI CurrentUsage vs PDH Local Usage
{
    const long Mib = 1048576;
    GpuReading Row(long dxgi, long? local, long? dedicated, string source = "dxgi") =>
        new() { DedicatedBytes = dxgi, Source = source, Adapter = "t", PdhLocalBytes = local, PdhDedicatedBytes = dedicated, PdhNonLocalBytes = 5 };
    List<GpuReading> Rows(int n, Func<int, GpuReading> f) => Enumerable.Range(0, n).Select(f).ToList();

    // Pass: DXGI within 1 percent of Local, Dedicated much higher (demoted). Only the last 60 of 100 rows count.
    var pass = VramGate.Compute(Rows(100, i => i < 40 ? Row(9000 * Mib, 100 * Mib, 9000 * Mib) : Row(1005 * Mib, 1000 * Mib, 1600 * Mib)));
    Check("vram gate pass", pass["pass"]!.GetValue<bool>() && pass["settled_rows"]!.GetValue<int>() == 60 && pass["paired_rows"]!.GetValue<int>() == 60);
    Near("vram gate diff pct", pass["diff_pct"]!.GetValue<double>(), 0.5, 0.001);
    Check("vram gate medians", pass["median_dxgi_bytes"]!.GetValue<long>() == 1005 * Mib && pass["median_pdh_local_bytes"]!.GetValue<long>() == 1000 * Mib && pass["median_pdh_dedicated_bytes"]!.GetValue<long>() == 1600 * Mib);
    Check("vram gate demoted bytes", pass["demoted_or_nonresident_bytes"]!.GetValue<long>() == 600 * Mib);
    Near("vram gate dedicated pct is informational", pass["dxgi_vs_pdh_dedicated_pct"]!.GetValue<double>(), (1005.0 - 1600) / 1600 * 100, 0.01);
    Check("vram gate fields", pass["pair"]!.GetValue<string>() == "dxgi_local_current_usage vs pdh_local_usage" && pass["bar_pct"]!.GetValue<double>() == 10);

    // Fail: DXGI 20 percent under Local
    var fail = VramGate.Compute(Rows(80, _ => Row(800 * Mib, 1000 * Mib, 1000 * Mib)));
    Check("vram gate fail", fail["pass"]!.GetValue<bool>() == false);
    Near("vram gate fail diff", fail["diff_pct"]!.GetValue<double>(), -20, 0.001);

    // Boundary: exactly 10 percent passes, just over fails
    Check("vram gate bar inclusive", VramGate.Compute(Rows(5, _ => Row(1100, 1000, 1000)))["pass"]!.GetValue<bool>() && !VramGate.Compute(Rows(5, _ => Row(1101, 1000, 1000)))["pass"]!.GetValue<bool>());

    // Fewer than 60 rows: all rows, count reported
    var few = VramGate.Compute(Rows(7, _ => Row(1000, 1000, 1500)));
    Check("vram gate fewer than 60 rows", few["settled_rows"]!.GetValue<int>() == 7 && few["pass"]!.GetValue<bool>());

    // PDH Local missing: pass null with reason, dedicated info still reported
    var noLocal = VramGate.Compute(Rows(10, _ => Row(1000, null, 1500)));
    Check("vram gate pdh local missing", noLocal["pass"] == null && noLocal["reason"]!.GetValue<string>().Contains("Local Usage") && noLocal["median_pdh_dedicated_bytes"] != null);

    // DXGI not the source (switched off): pass null with reason
    var off = VramGate.Compute(Rows(10, _ => Row(1000, 1000, 1500, "pdh")));
    Check("vram gate dxgi off", off["pass"] == null && off["reason"]!.GetValue<string>().Contains("DXGI"));
    Check("vram gate no rows", VramGate.Compute(new List<GpuReading>())["pass"] == null);
    Near("vram median even", VramGate.Median(new double[] { 4, 1, 3, 2 }), 2.5);
    Check("vram describe", VramGate.Describe(pass).Contains("PASS") && VramGate.Describe(fail).Contains("FAIL") && VramGate.Describe(noLocal).Contains("not evaluated") && VramGate.Describe(pass).Contains("committed"));

    // Written file: rows carry the new fields, the header carries the gate. DXGI is off in the test recorder, so the gate is null with a reason
    string vdir = Path.Combine(Path.GetTempPath(), "sparkvs-vg-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vdir);
    var vclock = BundleClock.FromStamp(Stopwatch.GetTimestamp());
    var vr = new GpuRecorder(vclock, false);
    vr.Setup(Environment.ProcessId);
    vr.Sample(0); vr.Sample(1000);
    vr.Teardown();
    var vst = vr.Write(vdir);
    if (vst.Enabled)
    {
        var vl = File.ReadAllLines(Path.Combine(vdir, "gpu-memory.jsonl"));
        var vh = JsonDocument.Parse(vl[0]).RootElement;
        var vrow = JsonDocument.Parse(vl[1]).RootElement;
        Check("vram row has local and nonlocal fields", vrow.TryGetProperty("pdh_local_bytes", out _) && vrow.TryGetProperty("pdh_nonlocal_bytes", out _));
        Check("vram header has gate and note", vh.TryGetProperty("vram_gate", out var vg) && vg.TryGetProperty("pair", out _) && vh.GetProperty("vram_gate_note").GetString()!.Contains("interop") && vh.GetProperty("pdh_independent_of_runner_pdh_csv").GetBoolean() == false);
        Check("vram stats carry the gate", vst.VramGate != null && vst.VramGate["pass"] == null);
    }
    Check("vram default off reason says switched off in config", GpuRecorder.DefaultDxgiOffReason.Contains("switched off in config") && GpuRecorder.DefaultDxgiOffReason.Contains("GpuMemoryDxgi"));
    try { Directory.Delete(vdir, true); } catch { }
}

// Memory report: the three files, the honest limits, and the exact CSV header (literal copies of DESIGN-memory.md).
{
    var msnap = new HeapSnapshot
    {
        Route = "A: test", Pid = 1, ProcessName = "test", TakenUtc = DateTime.UtcNow, Complete = true, LiveBytes = 3000, LiveObjects = 30,
        Types =
        {
            new HeapTypeStat { TypeId = 1, TypeName = "ModA.Thing", Module = "C:\\mods\\moda\\ModA.dll", Count = 10, Bytes = 1000 },
            new HeapTypeStat { TypeId = 2, TypeName = "System.String", Module = "System.Private.CoreLib.dll", Count = 10, Bytes = 1500 },
            new HeapTypeStat { TypeId = 3, TypeName = "Mystery.Blob", Module = "MysteryAsm", Count = 10, Bytes = 500 },
        },
    };
    var mtable = MemoryReport.Build(msnap, new TestResolver("ModA", "moda"), null, "not measured");
    string mdir = Path.Combine(Path.GetTempPath(), "sparkvs-mem-" + Guid.NewGuid().ToString("N"));
    MemoryReport.Write(mdir, mtable, 20, "SparkVS tests");
    Check("memory report writes md, csv and json", File.Exists(Path.Combine(mdir, "memory.md")) && File.Exists(Path.Combine(mdir, "memory.csv")) && File.Exists(Path.Combine(mdir, "memory.json")));
    string mmd = File.ReadAllText(Path.Combine(mdir, "memory.md"));
    Check("memory.md has the four honest limits", mmd.Contains("Live managed heap only, attributed by object type to owning assembly to owning mod. Game, VintagestoryAPI, System.*, Microsoft.*, and shared Harmony go to system/shared. Unmapped goes to unknown. Nothing is guessed.")
        && mmd.Contains("Content-only mods (JSON, no DLL) hold their data inside game types, so their footprint lands in system/shared.")
        && mmd.Contains("Native and unmanaged memory, including GPU / VRAM, is not per-mod separable and is not in this table (see the capture's gpu-memory output).")
        && mmd.Contains("Gross allocations are not live RAM. The two columns are separate and never merged."));
    var mcsv = File.ReadAllLines(Path.Combine(mdir, "memory.csv"));
    Check("memory csv header matches the spec exactly", mcsv[0] == "rank,owner_kind,owner_id,owner_name,live_bytes,live_mb,live_objects,pct_of_live_heap,gross_alloc_bytes,top_types", mcsv[0]);
    Check("memory csv rows: mod first, then shared and unknown", mcsv.Length == 4 && mcsv[1].Split(',')[2] == "system/shared" && mcsv.Any(l => l.Contains(",moda,")) && mcsv.Any(l => l.Contains(",unknown,")), string.Join("|", mcsv));
    string mcdir = Path.Combine(mdir, "csvonly");
    MemoryReport.WriteCsvOnly(mcdir, mtable);
    Check("WriteCsvOnly writes only memory.csv", Directory.GetFiles(mcdir).Length == 1 && File.Exists(Path.Combine(mcdir, "memory.csv")));
    try { Directory.Delete(mdir, true); } catch { }
}

// Memory known-answer, in process: allocate the payload, measure expected with GC.GetTotalMemory, snapshot our own pid.
{
    long kBefore = GC.GetTotalMemory(true);
    var kNodes = new PayloadNode[200_000];
    for (int i = 0; i < kNodes.Length; i++) kNodes[i] = new PayloadNode { F0 = i, F14 = i };
    var kCells = new PayloadCell[700_000];
    for (int i = 0; i < kCells.Length; i++) kCells[i].A = i;
    long kExpected = GC.GetTotalMemory(true) - kBefore;
    HeapSnapshot? ksnap = null;
    Exception? kerr = null;
    var kthread = new Thread(() =>
    {
        try { ksnap = HeapDumpEventPipe.Capture(Environment.ProcessId, TimeSpan.FromMinutes(2), null); }
        catch (Exception ex) { kerr = ex; }
    }) { IsBackground = true, Name = "memory known-answer" };
    kthread.Start();
    kthread.Join();
    long kAttributed = 0;
    double kErrPct = 100;
    if (ksnap != null)
    {
        var ktable = MemoryReport.Build(ksnap, new TestResolver("sparkvs-tests", "known-answer"), null, "");
        foreach (var r in ktable.Rows) if (r.Id == "known-answer") kAttributed = r.LiveBytes;
        kErrPct = kExpected > 0 ? 100.0 * (kAttributed - kExpected) / kExpected : 100;
        Console.WriteLine($"memory known-answer: expected={kExpected} attributed={kAttributed} error={kErrPct:0.00}% pause={ksnap.GcPauseMs:0.0} ms complete={ksnap.Complete} lost={ksnap.EventsLost}");
    }
    Check("memory known-answer in-process", ksnap != null && ksnap.Complete && Math.Abs(kErrPct) <= 20, kerr?.Message ?? $"expected {kExpected} attributed {kAttributed} error {kErrPct:0.00}%");
    GC.KeepAlive(kNodes);
    GC.KeepAlive(kCells);
}

MemoryEngineTests.Run(Check);

// ===== BEGIN capture-fixes worker A tests: entity census, events log, config migration, durations =====
{
    // Census: counts, domain rollup, densest chunk
    var ents = new List<(string, double, double, double)>
    {
        ("game:wolf-male", 1, 100, 1), ("game:wolf-male", 5, 100, 5), ("game:wolf-male", 40, 100, 40),
        ("lop:trex", -1, 50, -1), ("lop:trex", -2, 50, -3), ("lop:trex", -3, 51, -3),
        ("bare", 0, 0, 0),
    };
    var cen = EntityCensus.Build(ents, "server");
    Check("census total", cen.Total == 7, cen.Total.ToString());
    Check("census by_code order", cen.ByCode[0].Key == "game:wolf-male" && cen.ByCode[0].Value == 3 && cen.ByCode[1].Key == "lop:trex" && cen.ByCode[2].Key == "bare", string.Join(",", cen.ByCode.Select(k => k.Key + "=" + k.Value)));
    Check("census domain rollup", cen.ByDomain.Count == 2 && cen.ByDomain[0].Key == "game" && cen.ByDomain[0].Value == 4 && cen.ByDomain[1].Key == "lop" && cen.ByDomain[1].Value == 3, string.Join(",", cen.ByDomain.Select(k => k.Key + "=" + k.Value)));
    Check("census domain none is game", EntityCensus.DomainOf("bare") == "game" && EntityCensus.DomainOf("lop:trex") == "lop");
    Check("census densest chunk (negative coords floor)", cen.Densest != null && cen.Densest.Cx == -1 && cen.Densest.Cy == 1 && cen.Densest.Cz == -1 && cen.Densest.Count == 3, cen.Densest == null ? "null" : $"{cen.Densest.Cx},{cen.Densest.Cy},{cen.Densest.Cz} n={cen.Densest.Count}");
    Check("census densest block coords", cen.Densest!.BlockX == -32 && cen.Densest.BlockZ == -32);
    Check("census densest top codes", cen.Densest.TopCodes.Count == 1 && cen.Densest.TopCodes[0].Key == "lop:trex" && cen.Densest.TopCodes[0].Value == 3);
    Check("census empty input", EntityCensus.Build(new List<(string, double, double, double)>(), "client").Densest == null);
    var tops = EntityCensus.Build(Enumerable.Range(0, 20).Select(i => ($"game:e{i:00}", 1.0, 1.0, 1.0)), "server");
    Check("census top_codes capped at 5", tops.Densest!.TopCodes.Count == 5 && tops.Densest.Count == 20);
    cen.Source = "test"; cen.TMs = 12.34; cen.DurationMs = 1.5; cen.TsLocal = "2026-11-08T00:00:00.000+00:00";
    var cenJson = cen.ToJson(2);
    var back = EntityCensus.FromJson(JsonNode.Parse(cenJson.ToJsonString()));
    Check("census json round trip", back != null && back.Total == 7 && back.ByCode.Count == 3 && back.Densest!.Count == 3 && back.Side == "server" && back.DurationMs == 1.5);
    Check("census json keys", cenJson["by_code"] != null && cenJson["by_domain"] != null && cenJson["densest_chunk"]?["top_codes"] != null && cenJson["duration_ms"] != null && cenJson["side"]?.GetValue<string>() == "server");
    Check("census disabled placeholder reads as null", EntityCensus.FromJson(EntityCensus.Disabled(2, "x")) == null);

    // Diff
    var endEnts = new List<(string, double, double, double)>
    {
        ("game:wolf-male", 1, 100, 1), ("game:wolf-male", 5, 100, 5),
        ("lop:trex", -1, 50, -1), ("lop:trex", -2, 50, -3), ("lop:trex", -3, 51, -3),
        ("lop:raptor", 0, 0, 0), ("lop:raptor", 1, 0, 1),
    };
    var diff = EntityCensus.Diff(cen, EntityCensus.Build(endEnts, "server"));
    Check("diff total delta", diff.TotalDelta == 0 && diff.StartTotal == 7 && diff.EndTotal == 7);
    Check("diff code order by abs delta", diff.ByCode.Count == 3 && diff.ByCode[0].Key == "lop:raptor" && diff.ByCode[0].Delta == 2 && diff.ByCode[1].Delta == -1, string.Join(",", diff.ByCode.Select(d => d.Key + ":" + d.Delta)));
    Check("diff appeared and vanished", diff.Appeared.SequenceEqual(new[] { "lop:raptor" }) && diff.Vanished.SequenceEqual(new[] { "bare" }), string.Join("|", diff.Appeared) + "/" + string.Join("|", diff.Vanished));
    Check("diff domain deltas", diff.ByDomain.Count == 2 && diff.ByDomain[0].Key == "game" && diff.ByDomain[0].Delta == -2 && diff.ByDomain[1].Key == "lop" && diff.ByDomain[1].Delta == 2, string.Join(",", diff.ByDomain.Select(d => d.Key + ":" + d.Delta)));
    Check("diff markdown names changes", diff.Markdown(10).Contains("lop:raptor") && diff.ToJson(10)["top_code_changes"]!.AsArray().Count == 3);
    var same = EntityCensus.Diff(cen, cen);
    Check("diff of identical censuses is empty", same.ByCode.Count == 0 && same.TotalDelta == 0 && same.Appeared.Count == 0);

    // Events: bucketing and flush
    var evClock = new BundleClock(0, new DateTime(2026, 11, 8, 12, 0, 0), Stopwatch.Frequency);
    var ev = new EventLog(evClock);
    for (int i = 0; i < 3; i++) ev.ChunkLoaded();
    ev.ChunkUnloaded();
    ev.ClientChunkReceived(); ev.ClientChunkReceived();
    ev.Ignored();
    Check("events flush writes a row for a non-empty second", ev.Flush(1000) && ev.RowCount == 1);
    Check("events flush skips an empty second", !ev.Flush(2000) && ev.RowCount == 1);
    var chunkRow = JsonNode.Parse(ev.Lines()[0])!.AsObject();
    Check("events chunk row shape", chunkRow["type"]!.GetValue<string>() == "chunks" && chunkRow["source"]!.GetValue<string>() == "game" && chunkRow["loaded"]!.GetValue<int>() == 3 && chunkRow["unloaded"]!.GetValue<int>() == 1 && chunkRow["client_received"]!.GetValue<int>() == 2 && chunkRow["t_ms"]!.GetValue<double>() == 1000 && chunkRow["ts_local"]!.GetValue<string>().StartsWith("2026-11-08T12:00:01"));
    Check("events handler overhead counted", ev.HandlerCalls == 7 && ev.HandlerNs >= 0, ev.HandlerCalls.ToString());
    ev.ChunkLoaded();
    Check("events flush resets the bucket", ev.Flush(3000) && JsonNode.Parse(ev.Lines()[1])!["loaded"]!.GetValue<int>() == 1 && JsonNode.Parse(ev.Lines()[1])!["unloaded"]!.GetValue<int>() == 0);
    ev.ToolAt(500, "capture_start", ("mode", "server"));
    ev.ToolAt(2500, "census_end", ("duration_ms", 1.25), ("entity_total", 42L));
    ev.Gc(2800, 1, 3);
    ev.Teleport(2900, new TeleportInfo { Player = "Nick", FromX = 0, FromY = 80, FromZ = 0, ToX = 500, ToY = 80, ToZ = 0, Distance = 500 });
    var lines = ev.Lines();
    var t0s = lines.Select(l => JsonNode.Parse(l)!["t_ms"]!.GetValue<double>()).ToList();
    Check("events lines sorted by t_ms", t0s.SequenceEqual(t0s.OrderBy(x => x)), string.Join(",", t0s));
    var gcRow = lines.Select(l => JsonNode.Parse(l)!.AsObject()).First(o => o["type"]!.GetValue<string>() == "gc");
    Check("events gc row", gcRow["source"]!.GetValue<string>() == "runtime" && gcRow["gen2_count_delta"]!.GetValue<int>() == 1 && gcRow["gen1_count_delta"]!.GetValue<int>() == 3);
    var tpRow = lines.Select(l => JsonNode.Parse(l)!.AsObject()).First(o => o["type"]!.GetValue<string>() == "teleport");
    Check("events teleport row", tpRow["player"]!.GetValue<string>() == "Nick" && tpRow["distance"]!.GetValue<double>() == 500 && tpRow["from"]!.AsArray().Count == 3 && tpRow["to"]![0]!.GetValue<double>() == 500 && tpRow["source"]!.GetValue<string>() == "game");
    var toolRow = lines.Select(l => JsonNode.Parse(l)!.AsObject()).First(o => o["type"]!.GetValue<string>() == "census_end");
    Check("events tool row", toolRow["source"]!.GetValue<string>() == "tool" && toolRow["entity_total"]!.GetValue<long>() == 42 && toolRow["duration_ms"]!.GetValue<double>() == 1.25);

    // Events: thread safety of the counters
    var evPar = new EventLog(evClock);
    Parallel.For(0, 4, _ => { for (int i = 0; i < 25000; i++) evPar.ChunkLoaded(); });
    evPar.Flush(1000);
    Check("events counters lose nothing across threads", JsonNode.Parse(evPar.Lines()[0])!["loaded"]!.GetValue<int>() == 100000 && evPar.HandlerCalls == 100000);

    // Events: file round trip and summary
    string evDir = Path.Combine(Path.GetTempPath(), "sparkvs-evtest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(evDir);
    ev.Write(Path.Combine(evDir, ExtraSections.EventsFile));
    var evFile = File.ReadAllLines(Path.Combine(evDir, ExtraSections.EventsFile));
    Check("events file has header then rows", evFile.Length == ev.RowCount + 1 && JsonNode.Parse(evFile[0])!["type"]!.GetValue<string>() == "header" && JsonNode.Parse(evFile[0])!["series"]!.GetValue<string>() == "events");
    var evSum = EventLog.Summarize(Path.Combine(evDir, ExtraSections.EventsFile));
    Check("events summary", evSum.Present && evSum.Loaded == 4 && evSum.Unloaded == 1 && evSum.ClientReceived == 2 && evSum.Teleports.Count == 1 && evSum.Gen2Collections == 1 && evSum.ToolRows.Count == 2, $"{evSum.Loaded} {evSum.Unloaded} {evSum.ClientReceived} {evSum.Teleports.Count} {evSum.Gen2Collections} {evSum.ToolRows.Count}");

    // Teleport threshold and gc watcher
    var td = new TeleportDetector();
    Check("teleport first sighting is silent", td.Observe("a", 0, 0, 0, 0) == null);
    Check("teleport exactly 64 blocks is not a jump", td.Observe("a", 64, 0, 0, 0) == null);
    var jump = td.Observe("a", 64 + 65, 0, 0, 0);
    Check("teleport over 64 blocks", jump != null && Math.Abs(jump.Distance - 65) < 1e-9 && jump.FromX == 64 && jump.ToX == 129);
    Check("teleport small steps never fire", Enumerable.Range(0, 10).All(i => td.Observe("a", 129 + i * 60.0, 0, 0, 0) == null));
    var dimJump = td.Observe("a", 129 + 9 * 60.0, 0, 0, 1);
    Check("teleport dimension change fires", dimJump != null && dimJump.FromDimension == 0 && dimJump.ToDimension == 1);
    Check("teleport 3d distance counts height", new TeleportDetector().Observe("b", 0, 0, 0, 0) == null);
    var td3 = new TeleportDetector();
    td3.Observe("p", 0, 0, 0, 0);
    Check("teleport vertical drop over threshold", td3.Observe("p", 0, 100, 0, 0) != null);
    td3.Retain(new List<string>());
    Check("teleport rejoin is a first sighting", td3.Observe("p", 5000, 0, 0, 0) == null);
    var gw = new GcWatcher();
    Check("gc watcher baseline", !gw.Observe(5, 2, out _, out _));
    Check("gc watcher gen1 only is quiet", !gw.Observe(9, 2, out int g1a, out int g2a) && g1a == 4 && g2a == 0);
    Check("gc watcher gen2 rise", gw.Observe(11, 3, out int g1b, out int g2b) && g1b == 2 && g2b == 1);
    try { Directory.Delete(evDir, true); } catch (IOException) { }

    // Census and events sections of a bundle folder, server summary
    string secDir = Path.Combine(Path.GetTempPath(), "sparkvs-sectest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(secDir);
    BundleWriter.WriteJson(Path.Combine(secDir, ExtraSections.CensusStartFile), cen.ToJson(2));
    BundleWriter.WriteJson(Path.Combine(secDir, ExtraSections.CensusEndFile), EntityCensus.Build(endEnts, "server").ToJson(2));
    ev.Write(Path.Combine(secDir, ExtraSections.EventsFile));
    var metaNode = new JsonObject
    {
        ["mode"] = "server", ["label"] = "t", ["game_version"] = "1.22.7", ["started_local"] = "x", ["duration_sec"] = 5, ["requested_seconds"] = 5, ["entity_source"] = "test",
        [ExtraSections.OverheadKey] = new JsonObject { ["event_handler_calls"] = 7, ["event_handler_ns"] = 700, ["event_handler_ns_per_call"] = 100.0, ["census_start_ms"] = 0.5, ["census_end_ms"] = 0.6, ["events_sampler_ticks"] = 6, ["events_sampler_errors"] = 0 },
        ["bundle"] = JsonSerializer.SerializeToNode(new BundleSummary()),
    };
    BundleWriter.WriteJson(Path.Combine(secDir, ReportBuilder.MetaFile), metaNode);
    var sdiff = ExtraSections.LoadCensusDiff(secDir, out string? sreason, out _, out _);
    Check("sections load census diff", sdiff != null && sreason == null && sdiff.ByCode.Count == 3);
    string serverMd = ExtraSections.ServerSummaryMarkdown(secDir);
    Check("server summary has census, events and overhead", serverMd.Contains("## Entity census") && serverMd.Contains("lop:raptor") && serverMd.Contains("## Events") && serverMd.Contains("Overhead of the event and census collection") && serverMd.Contains("700 ns"), serverMd.Length.ToString());
    var emptyDir = Path.Combine(secDir, "empty");
    Directory.CreateDirectory(emptyDir);
    Check("sections degrade when files are absent", ExtraSections.CensusEventsMarkdown(emptyDir).Contains("Not collected") && ExtraSections.LoadCensusDiff(emptyDir, out string? absentReason, out _, out _) == null && absentReason != null);
    try { Directory.Delete(secDir, true); } catch (IOException) { }

    // Config: migration, durations, ring size, bad file name
    var oldCfg = new SparkVSConfig { ConfigSchema = 0, DefaultDurationSec = 120, MaxDurationSec = 300 };
    Check("config migrates old defaults", oldCfg.Migrate(out string mig1) && oldCfg.DefaultDurationSec == 600 && oldCfg.MaxDurationSec == 1800 && oldCfg.ConfigSchema == 2 && mig1.Contains("raised"), mig1);
    var custom = new SparkVSConfig { ConfigSchema = 0, DefaultDurationSec = 90, MaxDurationSec = 300 };
    Check("config keeps customized values", custom.Migrate(out string mig2) && custom.DefaultDurationSec == 90 && custom.MaxDurationSec == 300 && custom.ConfigSchema == 2 && mig2.Contains("kept"), mig2);
    var customMax = new SparkVSConfig { ConfigSchema = 1, DefaultDurationSec = 120, MaxDurationSec = 900 };
    Check("config keeps a customized max even with default length", customMax.Migrate(out _) && customMax.DefaultDurationSec == 120 && customMax.MaxDurationSec == 900);
    var current = SparkVSConfig.CreateDefault();
    Check("config new default is schema 2, 600/1800", current.ConfigSchema == 2 && current.DefaultDurationSec == 600 && current.MaxDurationSec == 1800 && !current.Migrate(out _));
    Check("config file without ConfigSchema deserializes to 0", JsonSerializer.Deserialize<SparkVSConfig>("{\"DefaultDurationSec\":120,\"MaxDurationSec\":300}")!.ConfigSchema == 0);
    Check("config hard cap is 7200", SparkVSConfig.HardMaxSeconds == 7200);
    Check("frame ring covers 3500 fps for the capture", new[] { 1, 60, 300, 600, 1800 }.All(s => SparkVSConfig.FrameRingCapacityFor(s) >= 3500L * s || SparkVSConfig.FrameRingCapacityFor(s) == (1 << 23)));
    Check("frame ring capped at 8M entries", SparkVSConfig.FrameRingCapacityFor(7200) == (1 << 23) && SparkVSConfig.FrameRingCapacityFor(1800) == (1 << 23) && SparkVSConfig.FrameRingCapacityFor(1) == (1 << 16));
    Check("bad config name", SparkVSConfig.BadFileName(@"C:\x\SparkVS.json", new DateTime(2026, 11, 8, 13, 5, 9)) == @"C:\x\SparkVS.json.bad-20261108130509");
}
// ===== END capture-fixes worker A tests =====

// ===== BEGIN capture-fixes worker C tests: entity tick timing accumulation =====
{
    var propsA = new object(); var propsB = new object(); var propsA2 = new object();
    var acc = new SparkVS.Core.EntityTickAccumulator();
    Check("tick acc: first thread owns", acc.AcceptsThread(7) && acc.AcceptsThread(7) && !acc.AcceptsThread(8) && acc.DroppedOffThread == 1);
    acc.Add(propsA, 100); acc.Add(propsA, 300); acc.Add(propsB, 50); acc.Add(propsA2, 1000);
    Check("tick acc: totals", acc.CallsTotal == 4 && acc.TicksTotal == 1450 && acc.KeyCount == 3);
    (string, string) Res(object k) => k == propsB ? ("game:pig-wild-female", "game") : ("game:chicken-hen", "game");
    // QPC frequency 1e6 means 1 tick = 1 us, 1000 ticks = 1 ms.
    var file = SparkVS.Core.EntityTickReport.Build(acc, Res, 1_000_000, 42.5, 10, "T.M", "n");
    var rows = file["rows"]!.AsArray();
    Check("tick table: two merged rows, hens first", rows.Count == 2 && rows[0]!["code"]!.GetValue<string>() == "game:chicken-hen" && rows[1]!["code"]!.GetValue<string>() == "game:pig-wild-female");
    Check("tick table: hen numbers", rows[0]!["calls"]!.GetValue<long>() == 3 && Math.Abs(rows[0]!["total_ms"]!.GetValue<double>() - 1.4) < 1e-9 && Math.Abs(rows[0]!["max_us"]!.GetValue<double>() - 1000) < 1e-9 && Math.Abs(rows[0]!["mean_us"]!.GetValue<double>() - 466.667) < 1e-3);
    Check("tick table: share sums to 100", Math.Abs(rows.Sum(r => r!["share_pct"]!.GetValue<double>()) - 100) < 0.02 && Math.Abs(rows[1]!["share_pct"]!.GetValue<double>() - 3.45) < 0.01);
    Check("tick table: totals and overhead", file["wrapped_calls_total"]!.GetValue<long>() == 4 && file["wrapper_overhead_estimate_ns"]!.GetValue<double>() == 42.5 && file["note"]!.GetValue<string>().Contains("physics worker threads"));
    acc.Freeze(); acc.Add(propsA, 5);
    Check("tick acc: frozen add dropped", acc.CallsTotal == 4 && acc.DroppedFrozen == 1);
    var accNull = new SparkVS.Core.EntityTickAccumulator();
    accNull.Add(null, 3);
    Check("tick acc: null key allowed", accNull.CallsTotal == 1);
    var off = SparkVS.Core.EntityTickReport.Disabled("why");
    Check("tick disabled form + markdown", off["enabled"]!.GetValue<bool>() == false && SparkVS.Core.EntityTickReport.Markdown(off).Contains("Not collected: why"));
    var tickMd = SparkVS.Core.EntityTickReport.Markdown(file, 1);
    Check("tick markdown: top-N rows, section title", tickMd.Contains("## Entity tick time") && tickMd.Contains("chicken-hen") && !tickMd.Contains("pig-wild-female"));
    double ns = SparkVS.Core.EntityTickAccumulator.Calibrate(20000);
    Check("tick calibration plausible", ns > 0 && ns < 5000);
}
// ===== END capture-fixes worker C tests =====

// ---------------------------------------------------------------- dedicated server parity (0.4.0)
{
    // SPARKVS_AUTOSTART parsing, shared by the client and the server path
    Check("autostart blank is none", AutostartSpec.TryParse(null, out _) == AutostartOutcome.None && AutostartSpec.TryParse("  ", out _) == AutostartOutcome.None);
    Check("autostart basic", AutostartSpec.TryParse("lbl;5;60", out var spA) == AutostartOutcome.Ok && spA!.Label == "lbl" && spA.DelaySeconds == 5 && spA.DurationSeconds == 60 && spA.Mark == null && !spA.Bundle && !spA.Report);
    Check("autostart report flag", AutostartSpec.TryParse("lbl;5;60;report", out var spB) == AutostartOutcome.Ok && spB!.Report && !spB.Bundle);
    Check("autostart bundle implies report", AutostartSpec.TryParse("lbl;5;60;bundle", out var spC) == AutostartOutcome.Ok && spC!.Bundle && spC.Report);
    Check("autostart flag list with spaces and unknown flag", AutostartSpec.TryParse("lbl;5;60; report , bundle ,nonsense", out var spD) == AutostartOutcome.Ok && spD!.Bundle && spD.Report);
    Check("autostart mark delay", AutostartSpec.TryParse("lbl;mark:go now;30;bundle", out var spE) == AutostartOutcome.Ok && spE!.Mark == "go now" && spE.DelaySeconds == 0 && spE.DurationSeconds == 30 && spE.Bundle);
    Check("autostart malformed", AutostartSpec.TryParse("x", out _) == AutostartOutcome.Malformed
        && AutostartSpec.TryParse("a;b;c", out _) == AutostartOutcome.Malformed
        && AutostartSpec.TryParse("a;5;x", out _) == AutostartOutcome.Malformed
        && AutostartSpec.TryParse("a;mark:x", out _) == AutostartOutcome.Malformed);

    // Output folders: the same rule for both sides, rooted at the data path
    string spData = Path.Combine(Path.GetTempPath(), "vsdata");
    Check("root plain default", CapturePaths.Root("", "", false, spData) == Path.Combine(spData, "ModData", "sparkvs"));
    Check("root bundle default has captures subfolder", CapturePaths.Root("", "", true, spData) == Path.Combine(spData, "ModData", "sparkvs", "captures"));
    Check("root output folder wins for plain and bundle without bundle folder", CapturePaths.Root("", "out", false, spData) == "out" && CapturePaths.Root("", "out", true, spData) == "out");
    Check("root bundle folder only for bundles", CapturePaths.Root("b", "out", true, spData) == "b" && CapturePaths.Root("b", "out", false, spData) == "out" && CapturePaths.Root("b", "", false, spData) == Path.Combine(spData, "ModData", "sparkvs"));
    var spNow = new DateTime(2026, 11, 11, 10, 15, 30);
    Check("capture name", CapturePaths.CaptureName(spNow, "") == "20261111-101530" && CapturePaths.CaptureName(spNow, "my label/1") == "20261111-101530-my_label_1");
    Check("memory dir", CapturePaths.MemoryDir(spData, spNow) == Path.Combine(spData, "ModData", "sparkvs", "memory-20261111-101530"));
    Check("asset inventory dir", CapturePaths.AssetInventoryDir("", spData) == Path.Combine(spData, "ModData", "sparkvs", "asset-inventory") && CapturePaths.AssetInventoryDir("x", spData) == "x");

    // Side selection: only a dedicated server without a client instance in the process captures on the server side
    Check("server capture active only when dedicated and no client", CaptureSide.ServerCaptureActive(true, false)
        && !CaptureSide.ServerCaptureActive(true, true) && !CaptureSide.ServerCaptureActive(false, false) && !CaptureSide.ServerCaptureActive(false, true));
    Check("main role by side", CaptureSide.MainRole(null) == "client-main" && CaptureSide.MainRole("client") == "client-main" && CaptureSide.MainRole("server") == "server-main");
    Check("client only file list", CaptureSide.ClientOnlyFiles.Contains("frames.jsonl") && CaptureSide.ClientOnlyFiles.Contains("gpu-memory.jsonl") && !CaptureSide.ServerWriterFiles.Contains("gpu-phases.json"));

    // The autostart guard fires once, even from many threads
    var spOnce = new OnceFlag();
    int spFires = 0;
    var spThreads = Enumerable.Range(0, 8).Select(_ => new Thread(() => { if (spOnce.TryFire()) Interlocked.Increment(ref spFires); })).ToList();
    spThreads.ForEach(x => x.Start());
    spThreads.ForEach(x => x.Join());
    Check("once flag fires once", spFires == 1 && spOnce.HasFired && !spOnce.TryFire());

    // Tick view on a dedicated server: work time and tick period, or period only when the work time source is missing
    string spDir = Path.Combine(Path.GetTempPath(), "sparkvs-srv-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(spDir);
    var spClock = BundleClock.FromStamp(Stopwatch.GetTimestamp());
    long spMs = Stopwatch.Frequency / 1000;
    var spTicks = new TickLog(64);
    spTicks.Begin();
    for (int i = 1; i <= 40; i++) spTicks.Record(i * 50 * spMs, 4 + i % 3);
    spTicks.End();
    var spStats = BundleWriter.WriteTick(spDir, spTicks, spClock, null, true);
    var spHeader = JsonDocument.Parse(File.ReadLines(Path.Combine(spDir, "tick.jsonl")).First()).RootElement;
    Check("server tick stats", spStats.Enabled && spStats.TickCount == 40 && spStats.MsptSource == "ServerMain.StatsCollector tickTimes" && spStats.MeanMsptMs >= 4 && spStats.MaxMsptMs <= 6);
    Near("server tick rate is 20 TPS for a 50 ms period", spStats.MeanTps ?? 0, 20, 0.05);
    Check("server tick interval percentiles", Math.Abs((spStats.P95IntervalMs ?? 0) - 50) < 0.5 && Math.Abs((spStats.MaxIntervalMs ?? 0) - 50) < 0.5);
    Check("server tick header names the side", spHeader.GetProperty("side").GetString() == "server" && spHeader.GetProperty("description").GetString()!.Contains("Dedicated server"));

    var spNoMspt = new TickLog(16);
    spNoMspt.Begin();
    for (int i = 1; i <= 10; i++) spNoMspt.Record(i * 25 * spMs, TickLog.NoMspt);
    spNoMspt.End();
    string spDir2 = Path.Combine(spDir, "nomspt");
    Directory.CreateDirectory(spDir2);
    var spStats2 = BundleWriter.WriteTick(spDir2, spNoMspt, spClock, "StatsCollector missing", true);
    var spRows = File.ReadAllLines(Path.Combine(spDir2, "tick.jsonl"));
    Check("period only tick stats", spStats2.Enabled && spStats2.MsptSource!.StartsWith("unavailable") && spStats2.Reason == "StatsCollector missing" && spStats2.MaxMsptMs == 0);
    Near("period only tick rate is 40 TPS for 25 ms", spStats2.MeanTps ?? 0, 40, 0.1);
    Check("period only rows carry null mspt", JsonDocument.Parse(spRows[2]).RootElement.GetProperty("mspt_ms").ValueKind == JsonValueKind.Null && spRows[0].Contains("unavailable"));

    // The client tick output is unchanged: no server-only keys
    var spClientTick = BundleWriter.WriteTick(spDir, spTicks, spClock, null);
    string spClientJson = JsonSerializer.Serialize(spClientTick);
    Check("client tick stats have no server keys", spClientTick.MeanTps == null && !spClientJson.Contains("mean_tps") && !spClientJson.Contains("mspt_source"));

    // Reports on the server side: role, scope, no frame or VRAM sections, client-only files named as not collected
    var spSet = new SampleSet();
    spSet.Frames.Add(new FrameInfo { Method = "Vintagestory.Server.ServerMain.Process()", Module = "VintagestoryLib" });
    spSet.Frames.Add(new FrameInfo { Method = "ModA.Tick()", Module = "ModAsm" });
    spSet.Stacks.Add(new[] { 1, 0 });
    for (int i = 0; i < 10; i++) spSet.Samples.Add(new Sample { ThreadId = 7, TimeMs = i * 10, StackId = 0 });
    CaptureMeta SpMeta(int mainId) => new()
    {
        Label = "srv", Side = "server", StartQpc = 0, EndQpc = Stopwatch.Frequency / 10, QpcFrequency = Stopwatch.Frequency,
        MainThreadOsId = mainId, ServerThreadOsId = mainId, AssemblyToMod = new() { ["ModAsm"] = "moda" }, GameAssemblies = new() { "VintagestoryLib" },
        Bundle = new BundleSummary { StartLocal = "t", SparkVsVersion = "test", Side = "server", Frames = null, Vram = null, Tick = spStats, Heap = new HeapStats { Enabled = true, Samples = 3 } },
    };
    var spReport = Analyzer.Build(spSet, SpMeta(7));
    Check("server report main role", spReport.Side == "server" && spReport.Threads[0].Role == "server-main" && spReport.MainThreadSamples == 10 && spReport.ScopeNote.StartsWith("Dedicated server"));
    var spReportUnknownId = Analyzer.Build(spSet, SpMeta(0));
    Check("server main thread found by its ServerMain stack when the id is unknown", spReportUnknownId.MainThreadSamples == 10 && Analyzer.FindMain(spReportUnknownId)?.Role == "server");
    string spMd = SummaryWriter.ToMarkdown(spReport, true);
    Check("server markdown has no client sections", !spMd.Contains("## Frames") && !spMd.Contains("VRAM") && !spMd.Contains("Fps in the seconds") && spMd.Contains("## Server main thread: top 15 methods") && spMd.Contains("Server tick, dedicated server") && spMd.Contains("Client only, omitted from a server bundle"));
    Check("server markdown tick rate line", spMd.Contains("Tick rate: 20.00 TPS"), spMd);
    var spHead = JsonDocument.Parse(ReportBuilder.HeadlineJson(spReport, AllocationReport.Disabled("x"), spDir)).RootElement;
    var spSections = spHead.GetProperty("sections");
    var spNc = spHead.GetProperty("not_collected").EnumerateArray().Select(e => e.GetString()).ToList();
    Check("server headline omits frames and vram sections", !spSections.TryGetProperty("frames", out _) && !spSections.TryGetProperty("vram_dedicated", out _) && spSections.TryGetProperty("tick", out _) && spSections.TryGetProperty("heap", out _) && spHead.GetProperty("side").GetString() == "server");
    Check("server headline names client only files", CaptureSide.ClientOnlyFiles.All(f => spNc.Contains(f)) && spHead.GetProperty("not_collected_reasons").GetProperty("frames.jsonl").GetString()!.StartsWith("client only"));
    Check("server top owners come from the server main thread", spSections.GetProperty("cpu_sampling").GetProperty("main_thread_samples").GetInt32() == 10);
    string spCpuJson = SummaryWriter.ToJson(spReport);
    Check("server cpu summary has no frames or vram bundle blocks", !spCpuJson.Contains("\"frames\"") && !spCpuJson.Contains("\"vram\"") && spCpuJson.Contains("\"side\": \"server\""));
    string spDir3 = Path.Combine(spDir, "ph");
    Directory.CreateDirectory(spDir3);
    BundleWriter.WritePlaceholdersForMissing(spDir3, "r", CaptureSide.ServerWriterFiles);
    Check("server placeholders written for server files only", File.Exists(Path.Combine(spDir3, "allocations.json")) && File.Exists(Path.Combine(spDir3, "chunk-pipeline.json")) && !File.Exists(Path.Combine(spDir3, "gpu-phases.json")));
    BundleWriter.WriteJson(Path.Combine(spDir3, "chunk-pipeline.json"), new JsonObject { ["enabled"] = false, ["reason"] = "x" });
    Check("server extra markdown has no gpu section", ExtraSections.Markdown(spDir3, true).Length < ExtraSections.Markdown(spDir3, false).Length);

    // The client capture files do not change: no side key anywhere unless the capture was on a server
    var spClientMeta = new CaptureMeta { Label = "c", Bundle = new BundleSummary() };
    string spClientMetaJson = JsonSerializer.Serialize(spClientMeta);
    Check("client meta and bundle have no side key", !spClientMetaJson.Contains("\"Side\"") && !spClientMetaJson.Contains("\"side\"") && spClientMetaJson.Contains("\"frames\"") && spClientMetaJson.Contains("\"vram\""));
    var spServerMetaJson = JsonSerializer.Serialize(SpMeta(7));
    var spMetaBack = JsonSerializer.Deserialize<CaptureMeta>(spServerMetaJson)!;
    Check("server meta round trips with its side", spMetaBack.Side == "server" && spMetaBack.Bundle!.Side == "server" && !spServerMetaJson.Contains("\"frames\"") && !spServerMetaJson.Contains("\"vram\""));
    Check("client report json has no side key", !SummaryWriter.ToJson(Analyzer.Build(set, new CaptureMeta { MainThreadOsId = 1, QpcFrequency = Stopwatch.Frequency, EndQpc = Stopwatch.Frequency })).Contains("\"side\""));

    try { Directory.Delete(spDir, true); } catch { }
}

// EventStream module: modes, row format, clock alignment, sinks, re-entrancy, summary, wiring into the bundle files
{
    EventStreamHub.Reset();
    string evDir = Path.Combine(Path.GetTempPath(), "sparkvs-ev-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(evDir);
    string[] EvReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr0 = new StreamReader(fs);
        var all = new List<string>();
        string? ln;
        while ((ln = sr0.ReadLine()) != null) all.Add(ln);
        return all.ToArray();
    }
    var evClock = new BundleClock(1000, new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Local), 1000);   // 1 kHz counter, origin at stamp 1000

    // Modes and policy
    bool evValid;
    Check("mode parse off", EventStreamModes.Parse("off", out evValid) == EventStreamMode.Off && evValid);
    Check("mode parse is case and space insensitive", EventStreamModes.Parse(" ALWAYS ", out evValid) == EventStreamMode.Always && evValid);
    Check("mode parse capture", EventStreamModes.Parse("capture", out evValid) == EventStreamMode.Capture && evValid);
    Check("mode parse null is the default and valid", EventStreamModes.Parse(null, out evValid) == EventStreamMode.Capture && evValid);
    Check("mode parse unknown falls back to capture and is flagged", EventStreamModes.Parse("bogus", out evValid) == EventStreamMode.Capture && !evValid);
    Check("mode parse empty is flagged", EventStreamModes.Parse("", out evValid) == EventStreamMode.Capture && !evValid);
    Check("mode names round trip", new[] { EventStreamMode.Off, EventStreamMode.Capture, EventStreamMode.Always }.All(m => EventStreamModes.Parse(EventStreamModes.Name(m), out _) == m));
    Check("policy subscribe", !EventStreamPolicy.Subscribe(EventStreamMode.Off) && EventStreamPolicy.Subscribe(EventStreamMode.Capture) && EventStreamPolicy.Subscribe(EventStreamMode.Always));
    Check("policy patch hurt", !EventStreamPolicy.PatchHurt(EventStreamMode.Off, true) && !EventStreamPolicy.PatchHurt(EventStreamMode.Capture, false) && EventStreamPolicy.PatchHurt(EventStreamMode.Capture, true) && EventStreamPolicy.PatchHurt(EventStreamMode.Always, true) && !EventStreamPolicy.PatchHurt(EventStreamMode.Always, false));
    Check("policy session file only in always", !EventStreamPolicy.SessionFile(EventStreamMode.Off) && !EventStreamPolicy.SessionFile(EventStreamMode.Capture) && EventStreamPolicy.SessionFile(EventStreamMode.Always));
    Check("session path follows plain captures: data path default", EventStreamPaths.SessionFile("", "D:\\data", new DateTime(2026, 11, 13, 12, 0, 5)) == Path.Combine("D:\\data", "ModData", "sparkvs", "events", "events-20261113-120005.jsonl"));
    Check("session path follows plain captures: output folder", EventStreamPaths.SessionFile("X:\\out", "D:\\data", new DateTime(2026, 11, 13, 12, 0, 5)) == Path.Combine("X:\\out", "events", "events-20261113-120005.jsonl"));

    // Row format
    var evFull = new EventRow
    {
        Type = EventRow.Hurt,
        Entity = new EventEntity("game:wolf-\"male\"", 42, "game", "Wolf \\ <male>\n\u00e9"),
        Pos = new[] { 1.54, 2, -3.21 },
        Source = "Entity", DmgType = "PiercingAttack", Damage = 4.567,
        Attacker = new EventAttacker("game:player", 7),
    };
    string evLine = EventLine.Build(evFull, 12.34567, "2026-11-13T12:00:00.012+00:00");
    using (var evDoc = JsonDocument.Parse(evLine))
    {
        var r = evDoc.RootElement;
        Check("row t_ms three decimals", evLine.Contains("\"t_ms\":12.346,"), evLine);
        Check("row type and side", r.GetProperty("type").GetString() == "hurt" && r.GetProperty("side").GetString() == "server");
        Check("row entity fields", r.GetProperty("entity").GetProperty("code").GetString() == "game:wolf-\"male\"" && r.GetProperty("entity").GetProperty("id").GetInt64() == 42 && r.GetProperty("entity").GetProperty("domain").GetString() == "game");
        Check("row escaping round trips", r.GetProperty("entity").GetProperty("name").GetString() == "Wolf \\ <male>\n\u00e9");
        Check("row pos one decimal", evLine.Contains("\"pos\":[1.5,2.0,-3.2]"), evLine);
        Check("row damage two decimals", evLine.Contains("\"damage\":4.57"), evLine);
        Check("row source dmg_type attacker", r.GetProperty("source").GetString() == "Entity" && r.GetProperty("dmg_type").GetString() == "PiercingAttack" && r.GetProperty("attacker").GetProperty("id").GetInt64() == 7 && r.GetProperty("attacker").GetProperty("code").GetString() == "game:player");
        Check("row without applicable fields omits them", !r.TryGetProperty("level", out _) && !r.TryGetProperty("message", out _) && !r.TryGetProperty("player", out _) && !r.TryGetProperty("reason", out _));
    }
    string evNote = EventLine.Build(new EventRow { Type = EventRow.Note, Player = "console", Text = "hello" }, 1, "x");
    Check("note row omits entity pos damage attacker", !evNote.Contains("\"entity\"") && !evNote.Contains("\"pos\"") && !evNote.Contains("\"damage\"") && !evNote.Contains("\"attacker\"") && evNote.Contains("\"player\":\"console\"") && evNote.Contains("\"text\":\"hello\""), evNote);
    string evBadNum = EventLine.Build(new EventRow { Type = EventRow.Hurt, Damage = double.NaN, Pos = new[] { double.NaN, 0, 0 } }, 0, "x");
    Check("non finite numbers are left out so the line stays valid json", !evBadNum.Contains("NaN") && JsonDocument.Parse(evBadNum).RootElement.GetProperty("type").GetString() == "hurt", evBadNum);
    var evOldCulture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        string deLine = EventLine.Build(new EventRow { Type = EventRow.Hurt, Damage = 1.5, Pos = new[] { 1.5, 2.5, 3.5 } }, 0.5, "x");
        Check("invariant decimals under a comma culture", deLine.Contains("\"damage\":1.50") && deLine.Contains("\"t_ms\":0.500") && JsonDocument.Parse(deLine).RootElement.GetProperty("damage").GetDouble() == 1.5, deLine);
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = evOldCulture; }
    var evLogRow = EventLine.Build(new EventRow { Type = EventRow.Log, Level = "warning", Message = "bad {0} \"x\"" }, 0, "x");
    Check("log row has level and message", JsonDocument.Parse(evLogRow).RootElement.GetProperty("message").GetString() == "bad {0} \"x\"" && evLogRow.Contains("\"level\":\"warning\""));

    // Chat classification
    var evCmd = EventRow.FromChat("Nick", "/tp 100 64 100 secret");
    Check("slash line is a command row with the first token only", evCmd != null && evCmd.Type == "command" && evCmd.Text == "/tp" && evCmd.Player == "Nick");
    Check("bare command keeps itself", EventRow.FromChat("Nick", "/time")?.Text == "/time");
    var evChat = EventRow.FromChat("Nick", "hello there");
    Check("plain line is a chat row", evChat != null && evChat.Type == "chat" && evChat.Text == "hello there");
    Check("blank chat is skipped", EventRow.FromChat("Nick", "   ") == null && EventRow.FromChat("Nick", null) == null);
    Check("chat without a player name gets a placeholder", EventRow.FromChat(null, "hi")?.Player == "?");

    // Mode gating: nothing open, nothing written
    Check("record with no sinks does nothing", EventStreamHub.Record(new EventRow { Type = EventRow.Spawn }, 2000) == 0 && !EventStreamHub.Active);
    string evNone = Path.Combine(evDir, "none");
    Directory.CreateDirectory(evNone);
    Check("end capture without begin writes no file and returns 0", EventStreamHub.EndCapture(evNone) == 0 && !File.Exists(Path.Combine(evNone, "gameevents.jsonl")));
    Check("hub starts detached", !EventStreamHub.Attached && !EventStreamHub.CaptureOpen && !EventStreamHub.SessionOpen);

    // Capture sink: file layout and clock alignment
    EventStreamHub.BeginCapture(evClock);
    Check("capture open makes the hub active", EventStreamHub.Active && EventStreamHub.CaptureOpen);
    long[] evStamps = { 1000, 1500, 2345, 61000 };
    for (int i = 0; i < evStamps.Length; i++)
        EventStreamHub.Record(new EventRow { Type = i % 2 == 0 ? EventRow.Spawn : EventRow.Death, Entity = new EventEntity("game:wolf-male", 100 + i, "game", "Wolf"), Pos = new[] { 1.0, 2.0, 3.0 } }, evStamps[i]);
    string evCapDir = Path.Combine(evDir, "cap");
    Directory.CreateDirectory(evCapDir);
    int evWritten = EventStreamHub.EndCapture(evCapDir);
    string evFile = Path.Combine(evCapDir, "gameevents.jsonl");
    var evLines = File.ReadAllLines(evFile);
    Check("end capture returns the row count and writes the file", evWritten == 4 && File.Exists(evFile) && evLines.Length == 6, evLines.Length.ToString());
    Check("capture closed after end", !EventStreamHub.Active);
    using (var h = JsonDocument.Parse(evLines[0]))
        Check("first line is the game_events header", h.RootElement.GetProperty("type").GetString() == "header" && h.RootElement.GetProperty("series").GetString() == "game_events" && h.RootElement.GetProperty("start_qpc").GetInt64() == 1000 && h.RootElement.GetProperty("qpc_frequency_hz").GetInt64() == 1000 && h.RootElement.GetProperty("start_local").GetString() == evClock.StartIso);
    using (var f = JsonDocument.Parse(evLines[^1]))
        Check("last line is the footer", f.RootElement.GetProperty("type").GetString() == "footer" && f.RootElement.GetProperty("rows").GetInt32() == 4 && f.RootElement.GetProperty("dropped").GetInt32() == 0);
    bool evAligned = true;
    for (int i = 0; i < evStamps.Length; i++)
    {
        using var rd = JsonDocument.Parse(evLines[i + 1]);
        double want = evClock.MsSince(evStamps[i]);
        evAligned &= Math.Abs(rd.RootElement.GetProperty("t_ms").GetDouble() - want) < 0.0006 && rd.RootElement.GetProperty("ts_local").GetString() == evClock.IsoAt(want);
    }
    Check("t_ms equals MsSince(qpc) and ts_local equals IsoAt for every row", evAligned);
    Near("row at the clock origin is 0 ms", evClock.MsSince(1000), 0);
    using (var r2 = JsonDocument.Parse(evLines[3]))
        Near("1345 counts at 1 kHz is 1345 ms", r2.RootElement.GetProperty("t_ms").GetDouble(), 1345.0);

    // Two sinks at once, session file written row by row
    string evSession = Path.Combine(evDir, "sess", "events-test.jsonl");
    var evSessClock = new BundleClock(5000, new DateTime(2026, 11, 13, 13, 0, 0, DateTimeKind.Local), 1000);
    Check("start session opens the file", EventStreamHub.StartSession(evSession, evSessClock) && File.Exists(evSession) && EventStreamHub.SessionOpen && EventStreamHub.SessionPath == evSession);
    Check("second session start is refused", !EventStreamHub.StartSession(Path.Combine(evDir, "sess", "second.jsonl"), evSessClock));
    EventStreamHub.BeginCapture(evClock);
    Check("one record reaches both sinks", EventStreamHub.Record(new EventRow { Type = EventRow.Spawn, Entity = new EventEntity("game:a", 1, "game", "A") }, 5100) == 2);
    var evLive = EventSummary.FromJsonl(evSession);   // file still open: rows are already on disk
    Check("session rows are flushed per row", evLive.Rows == 1 && evLive.ByType["spawn"] == 1);
    using (var sr = JsonDocument.Parse(EvReadLines(evSession)[1]))
        Check("session rows use the session clock", Math.Abs(sr.RootElement.GetProperty("t_ms").GetDouble() - 100.0) < 0.0006 && sr.RootElement.GetProperty("ts_local").GetString() == evSessClock.IsoAt(100.0));
    string evBothDir = Path.Combine(evDir, "both");
    Directory.CreateDirectory(evBothDir);
    EventStreamHub.EndCapture(evBothDir);
    using (var cr = JsonDocument.Parse(File.ReadAllLines(Path.Combine(evBothDir, "gameevents.jsonl"))[1]))
        Check("capture copy uses the capture clock, not the session clock", Math.Abs(cr.RootElement.GetProperty("t_ms").GetDouble() - 4100.0) < 0.0006 && cr.RootElement.GetProperty("ts_local").GetString() == evClock.IsoAt(4100.0));
    Check("session stays open after the capture ended", EventStreamHub.SessionOpen && EventStreamHub.Active);
    int evSessRows = EventStreamHub.StopSession();
    var evSessLines = EvReadLines(evSession);
    Check("stop session returns rows and writes the footer", evSessRows == 1 && evSessLines.Length == 3 && evSessLines[^1].Contains("\"type\":\"footer\"") && evSessLines[^1].Contains("\"rows\":1"));
    Check("stop session twice is -1 and the hub is idle", EventStreamHub.StopSession() == -1 && !EventStreamHub.Active);

    // Cap and dropped
    Check("row cap is 200000", EventStreamSink.RowCap == 200_000);
    var evSmall = new EventStreamSink(evClock, null, 3);
    int evAccepted = 0;
    for (int i = 0; i < 5; i++) if (evSmall.Add(new EventRow { Type = EventRow.Spawn }, 1000 + i)) evAccepted++;
    Check("cap keeps the first rows and counts the rest as dropped", evAccepted == 3 && evSmall.Rows == 3 && evSmall.Dropped == 2 && evSmall.Lines().Count == 3);
    string evCapFile = Path.Combine(evDir, "capped.jsonl");
    Check("capture sink footer carries rows and dropped", evSmall.WriteCapture(evCapFile) == 3 && File.ReadAllLines(evCapFile)[^1] == "{\"type\":\"footer\",\"rows\":3,\"dropped\":2}");
    Check("summary reads the dropped count from the footer", EventSummary.FromJsonl(evCapFile).Dropped == 2 && EventSummary.FromJsonl(evCapFile).Rows == 3);
    Check("closed sink takes nothing", !evSmall.Add(new EventRow { Type = EventRow.Spawn }, 1000));

    // Re-entrancy guard
    EventStreamHub.Reset();
    EventStreamHub.BeginCapture(evClock);
    EventStreamHub.Probe = row => { if (row.Message == "outer") EventStreamHub.Record(new EventRow { Type = EventRow.Log, Level = "warning", Message = "inner" }, 1100); };
    int evOuter = EventStreamHub.Record(new EventRow { Type = EventRow.Log, Level = "error", Message = "outer" }, 1050);
    EventStreamHub.Probe = null;
    Check("a record made while recording is ignored", evOuter == 1 && EventStreamHub.Status().Contains("1 rows"), EventStreamHub.Status());
    Check("the guard resets afterwards", EventStreamHub.Record(new EventRow { Type = EventRow.Log, Level = "warning", Message = "after" }, 1200) == 1 && EventStreamHub.Status().Contains("2 rows"));
    bool evOtherThread = false;
    EventStreamHub.Probe = row =>
    {
        if (row.Message != "trigger") return;
        // another thread is not inside Record, so this thread's guard does not block it
        var th = new Thread(() => evOtherThread = EventStreamHub.Record(new EventRow { Type = EventRow.Log, Message = "other" }, 1300) == 1);
        th.Start(); th.Join();
    };
    EventStreamHub.Record(new EventRow { Type = EventRow.Log, Message = "trigger" }, 1250);
    EventStreamHub.Probe = null;
    Check("the guard is per thread", evOtherThread);
    EventStreamHub.Reset();

    // Commands and their reply texts
    var evOff = EventStreamCommands.Start(EventStreamMode.Off, () => "unused");
    Check("off start says it is off", !evOff.Ok && evOff.Text == EventStreamCommands.OffMessage && evOff.Text.Contains("off in SparkVS.json"));
    Check("off stop and note say it is off", EventStreamCommands.Stop(EventStreamMode.Off).Text == EventStreamCommands.OffMessage && EventStreamCommands.Note(EventStreamMode.Off, "x", "y", 1).Text == EventStreamCommands.OffMessage);
    var evCapStart = EventStreamCommands.Start(EventStreamMode.Capture, () => "unused");
    Check("capture start is a no-op with the bound message", evCapStart.Ok && evCapStart.Text.Contains("event logging is bound to SparkVS captures (.sparkvs start / /sparkvs start)") && !EventStreamHub.Active);
    Check("capture stop is a no-op with the bound message", EventStreamCommands.Stop(EventStreamMode.Capture).Text.Contains(EventStreamCommands.BoundMessage));
    var evStatusCap = EventStreamCommands.Status(EventStreamMode.Capture, true, false);
    Check("status names mode, hurt state, sinks and the binding", evStatusCap.Text.Contains("mode capture") && evStatusCap.Text.Contains("HurtLog on, hurt patch not live") && evStatusCap.Text.Contains("no sink open") && evStatusCap.Text.Contains(EventStreamCommands.BoundMessage), evStatusCap.Text);
    Check("status in always mode has no binding sentence", !EventStreamCommands.Status(EventStreamMode.Always, false, false).Text.Contains(EventStreamCommands.BoundMessage) && EventStreamCommands.Status(EventStreamMode.Always, false, true).Text.Contains("HurtLog off, hurt patch live"));
    var evNoteNone = EventStreamCommands.Note(EventStreamMode.Capture, "x", "y", 1);
    Check("note with nothing open is not recorded", !evNoteNone.Ok && evNoteNone.Text.Contains("not recorded: no capture or session open"));
    string evCmdSession = Path.Combine(evDir, "cmd", "events-cmd.jsonl");
    var evStart = EventStreamCommands.Start(EventStreamMode.Always, () => evCmdSession);
    Check("always start opens the session", evStart.Ok && evStart.Text.Contains(evCmdSession) && EventStreamHub.SessionOpen);
    Check("always start twice is refused", !EventStreamCommands.Start(EventStreamMode.Always, () => Path.Combine(evDir, "cmd", "again.jsonl")).Ok);
    string evCmdText = EventStreamCommands.Note(EventStreamMode.Always, null, "marker one", Stopwatch.GetTimestamp()).Ok ? string.Join("|", EvReadLines(evCmdSession)) : "";
    Check("note goes into the session with a console author", evCmdText.Contains("\"player\":\"console\"") && evCmdText.Contains("marker one"), evCmdText);
    var evStop = EventStreamCommands.Stop(EventStreamMode.Always);
    Check("always stop reports the rows", evStop.Ok && evStop.Text.Contains("1 rows") && !EventStreamHub.Active, evStop.Text);
    Check("always stop with no session is an error", !EventStreamCommands.Stop(EventStreamMode.Always).Ok);
    EventStreamHub.Reset();

    // Summary
    EventStreamHub.BeginCapture(evClock);
    void EvRow(string type, string code, long stamp) => EventStreamHub.Record(new EventRow { Type = type, Entity = new EventEntity(code, 1, "game", "n") }, stamp);
    for (int i = 0; i < 3; i++) EvRow(EventRow.Hurt, "game:wolf-male", 2000 + i);
    EvRow(EventRow.Hurt, "game:drifter-normal", 2010);
    for (int i = 0; i < 2; i++) EvRow(EventRow.Hurt, "game:bear", 2020 + i);
    for (int i = 0; i < 2; i++) EvRow(EventRow.Death, "game:drifter-normal", 3000 + i);
    EvRow(EventRow.Death, "game:wolf-male", 3010);
    EvRow(EventRow.Spawn, "game:chicken", 1500);
    EventStreamHub.Record(new EventRow { Type = EventRow.Log, Level = "error", Message = "boom" }, 4000);
    string evSumDir = Path.Combine(evDir, "sum");
    Directory.CreateDirectory(evSumDir);
    EventStreamHub.EndCapture(evSumDir);
    var evSum = EventSummary.FromJsonl(Path.Combine(evSumDir, "gameevents.jsonl"));
    Check("summary rows and types", evSum.Rows == 11 && evSum.ByType["hurt"] == 6 && evSum.ByType["death"] == 3 && evSum.ByType["spawn"] == 1 && evSum.ByType["log"] == 1 && evSum.Dropped == 0, string.Join(",", evSum.ByType));
    Check("top hurts ordered by count then code", evSum.TopHurts.Select(kv => kv.Key + "=" + kv.Value).SequenceEqual(new[] { "game:wolf-male=3", "game:bear=2", "game:drifter-normal=1" }), string.Join(",", evSum.TopHurts));
    Check("top deaths ordered by count", evSum.TopDeaths.Select(kv => kv.Key + "=" + kv.Value).SequenceEqual(new[] { "game:drifter-normal=2", "game:wolf-male=1" }), string.Join(",", evSum.TopDeaths));
    Near("first t_ms", evSum.FirstMs ?? -1, 500);
    Near("last t_ms", evSum.LastMs ?? -1, 3000);
    string evMd = evSum.Markdown();
    Check("markdown heading is exactly ## Game events at the start", evMd.StartsWith("## Game events\r\n") || evMd.StartsWith("## Game events\n"), evMd);
    Check("markdown carries types and tops", evMd.Contains("| hurt | 6 |") && evMd.Contains("| game:wolf-male | 3 |") && evMd.Contains("Top entities by deaths"));
    var evJson = evSum.ToJson();
    Check("summary json shape", evJson["rows"]!.GetValue<int>() == 11 && evJson["by_type"]!["hurt"]!.GetValue<int>() == 6 && evJson["top_hurts"]![0]!["entity"]!.GetValue<string>() == "game:wolf-male" && evJson["detail_file"]!.GetValue<string>() == "gameevents.jsonl" && evJson["dropped"]!.GetValue<int>() == 0);
    EventStreamHub.BeginCapture(evClock);
    for (int i = 0; i < 14; i++) EvRow(EventRow.Hurt, "game:e" + i.ToString("00"), 2000 + i);
    string evTopDir = Path.Combine(evDir, "top");
    Directory.CreateDirectory(evTopDir);
    EventStreamHub.EndCapture(evTopDir);
    Check("top lists are cut at 10", EventSummary.FromJsonl(Path.Combine(evTopDir, "gameevents.jsonl")).TopHurts.Count == 10);
    EventStreamHub.BeginCapture(evClock);
    string evEmptyDir = Path.Combine(evDir, "empty");
    Directory.CreateDirectory(evEmptyDir);
    Check("an empty capture still writes a header and footer file", EventStreamHub.EndCapture(evEmptyDir) == 0 && File.ReadAllLines(Path.Combine(evEmptyDir, "gameevents.jsonl")).Length == 2);
    var evEmptySum = EventSummary.FromJsonl(Path.Combine(evEmptyDir, "gameevents.jsonl"));
    Check("empty summary has no first or last and says so", evEmptySum.Rows == 0 && evEmptySum.FirstMs == null && evEmptySum.Markdown().Contains("No events were recorded") && evEmptySum.ToJson()["first_t_ms"] == null);

    // Wiring into the bundle summary
    var evMeta = new CaptureMeta { MainThreadOsId = 1, QpcFrequency = Stopwatch.Frequency, EndQpc = Stopwatch.Frequency, Bundle = new BundleSummary { StartLocal = "t", SparkVsVersion = "test" } };
    var evReport = Analyzer.Build(set, evMeta);
    string evNoFilesDir = Path.Combine(evDir, "nofile");
    Directory.CreateDirectory(evNoFilesDir);
    var evHeadNo = JsonDocument.Parse(ReportBuilder.HeadlineJson(evReport, AllocationReport.Disabled("x"), evNoFilesDir)).RootElement;
    Check("summary.json has no game_events key without the file", !evHeadNo.GetProperty("sections").TryGetProperty("game_events", out _) && !evHeadNo.GetProperty("not_collected").EnumerateArray().Any(e => e.GetString() == "gameevents.jsonl"));
    string evMdNo = ExtraSections.Markdown(evNoFilesDir);
    Check("markdown has no Game events section without the file", !evMdNo.Contains("## Game events"));
    File.Copy(Path.Combine(evSumDir, "gameevents.jsonl"), Path.Combine(evNoFilesDir, "gameevents.jsonl"));
    var evHeadYes = JsonDocument.Parse(ReportBuilder.HeadlineJson(evReport, AllocationReport.Disabled("x"), evNoFilesDir)).RootElement;
    Check("summary.json gets the game_events section with the file", evHeadYes.GetProperty("sections").GetProperty("game_events").GetProperty("rows").GetInt32() == 11 && evHeadYes.GetProperty("sections").GetProperty("game_events").GetProperty("by_type").GetProperty("death").GetInt32() == 3);
    string evMdYes = ExtraSections.Markdown(evNoFilesDir);
    int evIdx = evMdYes.IndexOf("## Game events", StringComparison.Ordinal);
    Check("Game events section is appended last", evIdx > evMdYes.IndexOf("## Asset inventory", StringComparison.Ordinal) && evMdYes.IndexOf("\n## ", evIdx + 3, StringComparison.Ordinal) < 0, evMdYes);
    Check("existing sections keep their names and order", evMdYes.IndexOf("## Chunk pipeline", StringComparison.Ordinal) < evMdYes.IndexOf("## Harmony patch map", StringComparison.Ordinal) && evMdYes.IndexOf("## Harmony patch map", StringComparison.Ordinal) < evMdYes.IndexOf("## Asset inventory", StringComparison.Ordinal));
    Check("gameevents.jsonl is not in any placeholder or stub list", !BundleWriter.WriterFiles.Contains("gameevents.jsonl") && !CaptureSide.ServerWriterFiles.Contains("gameevents.jsonl") && !CaptureSide.ClientOnlyFiles.Contains("gameevents.jsonl") && !BundleWriter.Stubs.Any(s => s.File == "gameevents.jsonl"));
    string evPhDir = Path.Combine(evDir, "ph");
    Directory.CreateDirectory(evPhDir);
    BundleWriter.WritePlaceholdersForMissing(evPhDir, "r");
    BundleWriter.WriteStubs(evPhDir);
    Check("placeholders and stubs never create gameevents.jsonl", !File.Exists(Path.Combine(evPhDir, "gameevents.jsonl")));

    EventStreamHub.Reset();
    try { Directory.Delete(evDir, true); } catch { }
}

Console.WriteLine($"tests passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;

sealed class FakeGpuTimer : IGpuTimer
{
    public long Now;
    public int Stamps, ResultBeforeReady;
    public Func<long, bool>? NotReady;
    readonly Dictionary<int, long> values = new();
    int nextId = 1;
    public void GenQueries(int[] ids) { for (int i = 0; i < ids.Length; i++) ids[i] = nextId++; }
    public void Stamp(int id) { values[id] = Now; Stamps++; }
    public bool IsAvailable(int id) => !(NotReady?.Invoke(values[id]) ?? false);
    public long Result(int id)
    {
        if (NotReady?.Invoke(values[id]) ?? false) ResultBeforeReady++;
        return values[id];
    }
}

// Known-answer payload (same shape as tools/sparkx/KnownAnswerTarget); lives in the sparkvs-tests assembly.
public sealed class PayloadNode
{
    public long F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14;
}

public struct PayloadCell
{
    public long A, B, C, D;
}

sealed class TestResolver : IModResolver
{
    readonly string assembly, id;
    public TestResolver(string assembly, string id) { this.assembly = assembly; this.id = id; }
    public string Describe => "test resolver: " + assembly + " is the mod " + id;

    public Owner Resolve(string module)
    {
        string f = Path.GetFileNameWithoutExtension(module.Replace('/', '\\'));
        if (f.Equals(assembly, StringComparison.OrdinalIgnoreCase)) return new Owner(OwnerKind.Mod, id, assembly);
        if (f.StartsWith("System", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || f.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) || f.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            return new Owner(OwnerKind.Shared, MemoryReport.SharedId, MemoryReport.SharedId);
        return new Owner(OwnerKind.Unknown, MemoryReport.UnknownId, MemoryReport.UnknownId);
    }
}
