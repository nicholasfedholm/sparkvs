using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SparkVS.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SparkVS;

public sealed class SparkVSSystem : ModSystem, IRenderer
{
    const int HardMaxSeconds = 300;
    const int BaselineSeconds = 3;
    const int SpikeCapacity = 8192;
    const int FrameRingCapacity = 1 << 20;   // about 3500 fps for 300 s; 13 MB, allocated once
    const int TickRingCapacity = 1 << 18;
    const string EtwEnv = "SPARKVS_ETW";
    const string AutostartEnv = "SPARKVS_AUTOSTART";    // label;delaySec;durationSec[;report]
    const string RateEnv = "DOTNET_EventPipeThreadSamplingRate";
    const string GpuAbEnv = "SPARKVS_GPUPHASES_AB";      // seconds per A/B block, overrides GpuPhasesAbBlockSeconds

    enum State { Idle, Baseline, Capturing, Stopping }

    // Both sides of a singleplayer game run in this process, so the server side leaves its thread id here.
    static volatile int serverThreadId;

    ICoreClientAPI? capi;
    SparkVSConfig config = new();
    long serverListener;

    // Everything below is touched by OnRenderFrame every frame of a capture, and allocates nothing there.
    volatile State state = State.Idle;
    FrameLog frames = new(1, 50);
    long phaseEndStamp;
    long phaseStartStamp;
    double baselineFps;
    bool rendererOn;

    // Capture setup, used off the hot path.
    CaptureSession? capture;
    string captureDir = "";
    string lastCaptureDir = "";
    string label = "";
    int durationSeconds;
    int mainThreadId;
    int gen0, gen1, gen2;
    bool autoReport;
    volatile bool reportRunning;
    volatile bool memoryRunning;

    // Bundle captures only (.sparkvs capture, or autostart with the bundle flag).
    bool bundleMode;
    BundleClock? clock;
    FrameLog? bundleFrames;
    // Static: client and server run separate instances of this system in one process. The client starts the capture, the server thread records.
    static readonly TickLog tickLog = new(TickRingCapacity);
    static ServerTickProbe? tickProbe;
    static string? tickProbeFailure;
    HeapRecorder? heap;
    PeriodicSampler? heapSampler;
    GpuRecorder? gpu;
    PeriodicSampler? gpuSampler;
    JsonObject? startState, endState, ctxWorld, ctxGraphics, ctxVersions;
    JsonArray? ctxDisabled;
    List<ModEntry>? ctxMods;
    readonly List<string> bundleErrors = new();
    DateTime captureEndLocal;
    long serverTickListener;

    // Chunk pipeline timers and queue series (bundle captures only).
    bool chunkTimersOn;
    StageStats[]? chunkStats;
    double chunkEndMs;
    QueueRecorder? queueRecorder;
    PeriodicSampler? queueSampler;
    string? queueFailure;
    bool allocSamplingOn;
    long gcAllocStart;

    // GPU time by render phase (bundle captures only). The recorder is built once on the render thread and reused; the markers are registered per capture.
    GpuPhaseRecorder? gpuPhases;
    GpuInfo? gpuInfo;
    string? gpuPhasesFailure;
    List<GpuPhaseMarker>? gpuMarkers;
    bool gpuMarkersOn;
    double gpuAbSeconds;
    long gpuStartStamp, gpuEndStamp;

    public double RenderOrder => 0;
    public int RenderRange => 0;

    /// <summary>Runs before the first mod phase, so the load phase hook sees every phase after this mod is constructed.</summary>
    public SparkVSSystem() { LoadPhaseHook.InstallOnce(); }

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void StartServerSide(ICoreServerAPI api)
    {
        ServerInfo.Api = api;
        if (LoadPhaseHook.FlagInConfigFile("ChunkPipeline")) ChunkPipelineHooks.Install("server", api.Logger);
        serverListener = api.Event.RegisterGameTickListener(_ =>
        {
            serverThreadId = Os.CurrentThreadId();
            ReadServerInfo(api);
            if (LoadPhaseHook.FlagInConfigFile("PatchMap")) PatchMapCollector.TakeServer();
            api.Event.UnregisterGameTickListener(serverListener);
        }, 1000);

        // Per tick work time for the bundle. Idle (one volatile read) unless a bundle capture is running.
        tickProbe = ServerTickProbe.Create(api.World, out string reason);
        tickProbeFailure = tickProbe == null ? reason : null;
        if (tickProbe != null)
            serverTickListener = api.Event.RegisterGameTickListener(_ => OnServerTick(), 1);
    }

    void OnServerTick()
    {
        if (!tickLog.Active) return;
        var probe = tickProbe;
        if (probe != null && probe.TryRead(out int mspt)) tickLog.Record(Stopwatch.GetTimestamp(), mspt);
    }

    static void ReadServerInfo(ICoreServerAPI api)
    {
        try { ServerInfo.WorldName = System.IO.Path.GetFileNameWithoutExtension(api.WorldManager.CurrentWorldFilepath); } catch { }
        try { ServerInfo.Seed = api.World.Seed; } catch { }
        try { ServerInfo.MapSize = new[] { api.WorldManager.MapSizeX, api.WorldManager.MapSizeY, api.WorldManager.MapSizeZ }; } catch { }
        try
        {
            var values = new Dictionary<string, string?>();
            foreach (var key in new[] { "worldHeight", "landcover", "oceanscale", "upheavelCommonness", "landformScale", "spawnRadius", "worldWidth", "worldLength" })
                values[key] = api.World.Config?.GetAsString(key, null!);
            ServerInfo.WorldConfig = values;
        }
        catch { }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        config = LoadConfig(api);
        if (Mod?.Info?.Version is string modVersion) BuildInfo.SparkVsVersion = modVersion;
        if (config.ChunkPipeline) ChunkPipelineHooks.Install("client", api.Logger);

        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create("sparkvs")
            .WithDescription("SparkVS sampling profiler")
            .BeginSubCommand("start")
                .WithDescription("Start a capture: .sparkvs start [seconds] [label]")
                .WithArgs(parsers.OptionalInt("seconds", -1), parsers.OptionalWord("label"))
                .HandleWith(args => OnStart((int)args[0], (string?)args[1] ?? "", false, false))
            .EndSubCommand()
            .BeginSubCommand("capture")
                .WithDescription("Run a full capture bundle and report it: .sparkvs capture <seconds> <label>. Writes a folder under the configured bundle folder.")
                .WithArgs(parsers.OptionalInt("seconds", -1), parsers.OptionalWord("label"))
                .HandleWith(args => OnStart((int)args[0], (string?)args[1] ?? "", true, true))
            .EndSubCommand()
            .BeginSubCommand("stop")
                .WithDescription("Stop the running capture")
                .HandleWith(_ => OnStop())
            .EndSubCommand()
            .BeginSubCommand("report")
                .WithDescription("Build summary.md, summary.json and the Speedscope file for the last capture")
                .HandleWith(_ => OnReport())
            .EndSubCommand()
            .BeginSubCommand("memory")
                .WithDescription("Take a live managed heap snapshot now and write memory.md/csv/json per mod under ModData/sparkvs/memory-<time>. Pauses the game for about 3.5 s per 3 GB of heap.")
                .HandleWith(_ => OnMemory())
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Show capture state")
                .HandleWith(_ => TextCommandResult.Success($"SparkVS {state}{(memoryRunning ? ", memory snapshot running" : "")}. Last capture: {(lastCaptureDir.Length > 0 ? lastCaptureDir : "none")}"))
            .EndSubCommand();

        api.Event.LevelFinalize += ScheduleAutostart;
    }

    static SparkVSConfig LoadConfig(ICoreAPI api)
    {
        const string file = "SparkVS.json";
        SparkVSConfig? loaded = null;
        try { loaded = api.LoadModConfig<SparkVSConfig>(file); }
        catch (Exception ex) { api.Logger.Warning("SparkVS: config unreadable, using defaults: " + ex.Message); }
        if (loaded == null)
        {
            loaded = new SparkVSConfig();
            api.StoreModConfig(loaded, file);
        }
        loaded.MaxDurationSec = Math.Clamp(loaded.MaxDurationSec, 1, HardMaxSeconds);
        loaded.MemoryTopN = Math.Clamp(loaded.MemoryTopN, 1, 200);
        return loaded;
    }

    void ScheduleAutostart()
    {
        var spec = Environment.GetEnvironmentVariable(AutostartEnv);
        if (string.IsNullOrWhiteSpace(spec) || capi == null) return;
        var parts = spec.Split(';');
        int delay = 0;
        bool waitForMark = parts.Length >= 3 && parts[1].StartsWith("mark:", StringComparison.Ordinal);
        if (parts.Length < 3
            || (!waitForMark && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out delay))
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            capi.Logger.Warning("SparkVS: ignoring malformed " + AutostartEnv);
            return;
        }
        // Flags are comma separated and optional: report (build the report after the capture), bundle (write the full bundle; implies report).
        var flags = parts.Length > 3 ? parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
        bool wantBundle = flags.Contains("bundle", StringComparer.Ordinal);
        bool wantReport = wantBundle || flags.Contains("report", StringComparer.Ordinal);
        void Begin()
        {
            var result = OnStart(seconds, parts[0], wantBundle, wantReport);
            capi.Logger.Notification("SparkVS autostart: " + result.StatusMessage);
        }
        if (waitForMark) WaitForBenchMark(parts[1].Substring(5), Begin);
        else capi.Event.RegisterCallback(_ => Begin(), Math.Max(delay, 1) * 1000);
    }

    /// <summary>Bench runs only: lines a capture up with the director's measure window by watching its log.</summary>
    void WaitForBenchMark(string mark, Action begin)
    {
        var dir = Environment.GetEnvironmentVariable("VSBENCH_OUT");
        if (string.IsNullOrEmpty(dir) || capi == null)
        {
            capi?.Logger.Warning("SparkVS: autostart mark needs VSBENCH_OUT");
            return;
        }
        string path = Path.Combine(dir, "director.jsonl");
        long listener = 0;
        listener = capi.Event.RegisterGameTickListener(_ =>
        {
            if (!File.Exists(path)) return;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (!reader.ReadToEnd().Contains(mark, StringComparison.Ordinal)) return;
            }
            catch (IOException) { return; }
            capi.Event.UnregisterGameTickListener(listener);
            begin();
        }, 500);
    }

    TextCommandResult OnStart(int seconds, string requestedLabel, bool bundle, bool report)
    {
        if (capi == null) return TextCommandResult.Error("Client not ready");
        if (state != State.Idle) return TextCommandResult.Error("SparkVS is already " + state);
        if (reportRunning) return TextCommandResult.Error("A report is still building");
        if (memoryRunning) return TextCommandResult.Error("A memory snapshot is still running");

        durationSeconds = Math.Clamp(seconds > 0 ? seconds : config.DefaultDurationSec, 1, config.MaxDurationSec);
        label = requestedLabel;
        bundleMode = bundle;
        autoReport = report;
        string root = bundle && config.BundleFolder.Length > 0 ? config.BundleFolder
            : config.OutputFolder.Length > 0 ? config.OutputFolder
            : bundle ? Path.Combine(GamePaths.DataPath, "ModData", "sparkvs", "captures")
            : Path.Combine(GamePaths.DataPath, "ModData", "sparkvs");
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (label.Length > 0 ? "-" + Sanitize(label) : "");
        captureDir = Path.Combine(root, name);
        Directory.CreateDirectory(captureDir);
        mainThreadId = Os.CurrentThreadId();

        if (bundle) frames = bundleFrames ??= new FrameLog(SpikeCapacity, config.SpikeThresholdMs, FrameRingCapacity);
        else frames = new FrameLog(SpikeCapacity, config.SpikeThresholdMs);
        phaseStartStamp = Stopwatch.GetTimestamp();
        frames.Reset(phaseStartStamp);
        phaseEndStamp = phaseStartStamp + BaselineSeconds * Stopwatch.Frequency;
        state = State.Baseline;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "sparkvs");
        rendererOn = true;

        return TextCommandResult.Success($"SparkVS: {BaselineSeconds} s fps baseline, then capturing {durationSeconds} s. {RateNote()}");
    }

    string RateNote()
    {
        var knob = Environment.GetEnvironmentVariable(RateEnv) ?? "";
        string wanted = config.IntervalMs.ToString("X", CultureInfo.InvariantCulture);
        if (knob.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return $"Interval {config.IntervalMs} ms from launch setting.";
        if (knob.Length > 0) return $"Launch setting {RateEnv}={knob} (hex ms) overrides the configured {config.IntervalMs} ms.";
        return $"This game was launched without {RateEnv}={wanted}, so the runtime samples at about 1 ms, which costs more. It can only be set at launch.";
    }

    static string Sanitize(string text)
    {
        var chars = text.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (state != State.Baseline && state != State.Capturing) return;
        long now = Stopwatch.GetTimestamp();
        frames.Record(now);
        if (now < phaseEndStamp) return;

        if (state == State.Baseline) BeginSession(now);
        else if (state == State.Capturing) BeginStop(now);
    }

    void BeginSession(long now)
    {
        baselineFps = frames.Frames / ((now - phaseStartStamp) / (double)Stopwatch.Frequency);
        gen0 = GC.CollectionCount(0);
        gen1 = GC.CollectionCount(1);
        gen2 = GC.CollectionCount(2);

        allocSamplingOn = bundleMode && config.AllocationSampling;
        gcAllocStart = GC.GetTotalAllocatedBytes(false);
        capture = new CaptureSession(Path.Combine(captureDir, ReportBuilder.TraceFile), allocSamplingOn);
        try
        {
            capture.Start();
        }
        catch (Exception ex)
        {
            state = State.Idle;
            RemoveRendererLater();
            capi?.Logger.Error("SparkVS: could not open the trace session: " + ex);
            capi?.ShowChatMessage("SparkVS: could not open the trace session: " + ex.Message);
            return;
        }
        if (bundleMode) StartBundle();
        phaseStartStamp = Stopwatch.GetTimestamp();
        phaseEndStamp = phaseStartStamp + durationSeconds * Stopwatch.Frequency;
        frames.Reset(phaseStartStamp);
        state = State.Capturing;
    }

    void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            bundleErrors.Add(what + ": " + ex.GetType().Name + ": " + ex.Message);
            capi?.Logger.Warning("SparkVS bundle: " + what + " failed: " + ex.Message);
        }
    }

    /// <summary>Main thread, once per bundle capture, before the frame clock starts: time origin, context snapshots, 1 Hz samplers, tick log.</summary>
    void StartBundle()
    {
        bundleErrors.Clear();
        startState = endState = ctxWorld = ctxGraphics = ctxVersions = null;
        ctxDisabled = null;
        ctxMods = null;
        clock = BundleClock.FromStamp(capture!.StartQpc);
        var c = clock;
        var api = capi!;
        Guard("start state", () => startState = ContextCollector.State(api, c));
        Guard("world", () => ctxWorld = ContextCollector.World(api));
        Guard("graphics settings", () => ctxGraphics = ContextCollector.Graphics(api));
        Guard("versions", () => ctxVersions = ContextCollector.Versions(api, BuildInfo.SparkVsVersion));
        Guard("mod list", () => ctxMods = ContextCollector.ModList(api));
        Guard("disabled mods", () => ctxDisabled = ContextCollector.DisabledModIds());

        var heapRecorder = heap = new HeapRecorder(c);
        var hs = heapSampler = new PeriodicSampler("SparkVS heap 1Hz", c, heapRecorder.Sample);
        var recorder = gpu = new GpuRecorder(c, config.GpuMemoryDxgi);
        var gs = gpuSampler = new PeriodicSampler("SparkVS gpu-memory 1Hz", c, recorder.Sample, () => recorder.Setup(Environment.ProcessId), recorder.Teardown);
        Guard("heap sampler", () => hs.Start());
        Guard("gpu sampler", () => gs.Start());
        tickProbe?.Reset();
        tickLog.Begin();
        SetupGpuPhases();
        chunkStats = null;
        queueRecorder = null;
        queueSampler = null;
        queueFailure = null;
        if (config.ChunkPipeline && ChunkPipelineHooks.Entries.Any(e => e.Info.Installed))
        {
            var reader = ChunkPipelineHooks.CreateQueueReader(out queueFailure);
            if (reader != null)
            {
                var qr = queueRecorder = new QueueRecorder(c, reader);
                var qs = queueSampler = new PeriodicSampler("SparkVS chunk queues 1Hz", c, qr.Sample);
                Guard("queue sampler", () => qs.Start());
            }
            ChunkTimers.Begin();
            chunkTimersOn = true;
        }
    }

    /// <summary>
    /// Render thread, at bundle start. Probes timer queries and builds the recorder (GL calls), then registers the markers on the main thread task queue:
    /// this runs inside the Before stage loop, and inserting a renderer at the front of that list mid loop would run the current renderer twice.
    /// </summary>
    void SetupGpuPhases()
    {
        gpuPhasesFailure = null;
        gpuStartStamp = gpuEndStamp = 0;
        if (!config.GpuPhases) { gpuPhasesFailure = "GpuPhases is false in SparkVS.json"; return; }
        Guard("gpu phases setup", () =>
        {
            gpuInfo ??= OpenGlTimer.Probe(out string reason) ?? Failed(reason);
            if (gpuInfo == null) return;
            gpuPhases ??= new GpuPhaseRecorder(new OpenGlTimer());
            gpuAbSeconds = config.GpuPhasesAbBlockSeconds;
            if (double.TryParse(Environment.GetEnvironmentVariable(GpuAbEnv), NumberStyles.Float, CultureInfo.InvariantCulture, out double fromEnv) && fromEnv >= 0) gpuAbSeconds = fromEnv;
            gpuAbSeconds = Math.Max(0, gpuAbSeconds);
            capi?.Event.EnqueueMainThreadTask(RegisterGpuMarkers, "sparkvs");
        });
        if (gpuPhases == null && gpuPhasesFailure == null) gpuPhasesFailure = "GPU phase setup failed, see collection_errors in context.json";
    }

    GpuInfo? Failed(string reason)
    {
        gpuPhasesFailure = reason;
        return null;
    }

    /// <summary>Main thread, outside the render loops.</summary>
    void RegisterGpuMarkers()
    {
        if (capi == null || gpuPhases == null || gpuMarkersOn || state != State.Capturing) return;
        gpuMarkers = GpuPhaseMarker.CreateAll(gpuPhases);
        foreach (var m in gpuMarkers) capi.Event.RegisterRenderer(m, m.Stage, "sparkvs-gpu");
        gpuMarkersOn = true;
        gpuStartStamp = Stopwatch.GetTimestamp();
        gpuPhases.Begin(gpuStartStamp, (long)(gpuAbSeconds * Stopwatch.Frequency));
    }

    void RemoveGpuMarkers()
    {
        if (!gpuMarkersOn || gpuMarkers == null) return;
        foreach (var m in gpuMarkers) capi?.Event.UnregisterRenderer(m, m.Stage);
        gpuMarkersOn = false;
    }

    /// <summary>The gpu-phases.json content for this capture. Called from the finish thread, after the markers stopped.</summary>
    JsonObject BuildGpuPhases()
    {
        if (gpuPhasesFailure != null) return GpuPhasesWriter.Disabled(gpuPhasesFailure, gpuInfo);
        var rec = gpuPhases;
        if (rec == null || gpuStartStamp == 0) return GpuPhasesWriter.Disabled("the capture ended before the GPU phase markers were registered", gpuInfo);
        if (rec.FramesMeasured == 0)
            return GpuPhasesWriter.Disabled($"no frame was measured: {rec.FramesIssued} frames issued, {rec.DroppedNotReady} not ready, {rec.Incomplete} incomplete", gpuInfo);
        GpuAbResult? ab = gpuAbSeconds > 0 ? GpuPhaseAb.Compute(frames, gpuStartStamp, gpuEndStamp, gpuAbSeconds) : null;
        return GpuPhasesWriter.Build(rec, gpuInfo!, ab);
    }

    /// <summary>Stops the chunk timers at the end of the capture window. Allocation free; safe to call twice.</summary>
    void EndChunkTimers()
    {
        if (!chunkTimersOn || clock == null) return;
        chunkEndMs = clock.MsSince(Stopwatch.GetTimestamp());
        chunkStats = ChunkTimers.End();
        chunkTimersOn = false;
    }

    /// <summary>Called at the end of the capture window, from the render hook.</summary>
    void EndBundleState()
    {
        captureEndLocal = DateTime.Now;
        gpuPhases?.End();
        if (gpuEndStamp == 0) gpuEndStamp = Stopwatch.GetTimestamp();
        EndChunkTimers();
        if (clock != null && capi != null) Guard("end state", () => endState = ContextCollector.State(capi, clock));
    }

    void StopBundleSamplers()
    {
        tickLog.End();
        EndChunkTimers();
        Guard("queue sampler stop", () => queueSampler?.Stop());
        Guard("heap sampler stop", () => heapSampler?.Stop());
        Guard("gpu sampler stop", () => gpuSampler?.Stop());
    }

    TextCommandResult OnStop()
    {
        if (state == State.Baseline)
        {
            state = State.Idle;
            RemoveRenderer();
            return TextCommandResult.Success("SparkVS: stopped before the capture began.");
        }
        if (state != State.Capturing) return TextCommandResult.Error("SparkVS is not capturing");
        BeginStop(Stopwatch.GetTimestamp());
        return TextCommandResult.Success("SparkVS: stopping, the rundown can take a few seconds.");
    }

    void BeginStop(long now)
    {
        state = State.Stopping;
        RemoveRendererLater();
        if (bundleMode) EndBundleState();
        double captureFps = frames.Frames / ((now - phaseStartStamp) / (double)Stopwatch.Frequency);
        var meta = BuildMeta(captureFps);
        var session = capture!;
        var dir = captureDir;
        // The resolver reads the mod list and loaded assemblies, so it is built here on the main thread.
        InProcModResolver? memoryResolver = null;
        if (bundleMode && config.MemorySnapshotOnCaptureEnd && capi != null)
            Guard("memory resolver", () => memoryResolver = InProcModResolver.Build(capi));
        var thread = new Thread(() => FinishCapture(session, meta, dir, memoryResolver))
        {
            IsBackground = true,
            Name = "SparkVS finish",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>This can run inside the game's renderer loop, so the list change waits until that loop is done.</summary>
    void RemoveRendererLater()
    {
        capi?.Event.EnqueueMainThreadTask(RemoveRenderer, "sparkvs");
    }

    void RemoveRenderer()
    {
        if (rendererOn) capi?.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        rendererOn = false;
        RemoveGpuMarkers();
    }

    CaptureMeta BuildMeta(double captureFps)
    {
        var meta = new CaptureMeta
        {
            Label = label,
            World = capi?.World?.SavegameIdentifier ?? "",
            GameVersion = GameVersion.OverallVersion,
            StartedLocal = DateTime.Now.AddSeconds(-durationSeconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            QpcFrequency = Stopwatch.Frequency,
            MainThreadOsId = mainThreadId,
            ServerThreadOsId = serverThreadId,
            IntegratedServer = capi?.IsSinglePlayer ?? false,
            SamplingRateKnob = Environment.GetEnvironmentVariable(RateEnv) ?? "",
            SpikeThresholdMs = config.SpikeThresholdMs,
            FrameCount = frames.Frames,
            BaselineFps = baselineFps,
            CaptureFps = captureFps,
            Gen0 = GC.CollectionCount(0) - gen0,
            Gen1 = GC.CollectionCount(1) - gen1,
            Gen2 = GC.CollectionCount(2) - gen2,
            FrameHookBytesAllocated = frames.AllocatedBytes,
            SpikesDropped = frames.Dropped,
            AllocationSampling = allocSamplingOn,
            GcAllocatedBytesStart = gcAllocStart,
            GcAllocatedBytesEnd = GC.GetTotalAllocatedBytes(false),
        };
        for (int i = 0; i < frames.SpikeCount; i++) meta.Spikes.Add(frames.SpikeAt(i));
        if (capi != null) FillAttribution(capi, meta);
        return meta;
    }

    /// <summary>
    /// Names a mod only from what the game loaded: the assembly of a mod's ModSystem, or an assembly file
    /// inside that mod's unpack folder. An assembly under the game folder is the game. Anything else stays unknown.
    /// </summary>
    static void FillAttribution(ICoreAPI api, CaptureMeta meta)
    {
        string gameDir = NormalizeDir(AppContext.BaseDirectory);
        var folders = new List<(string Dir, string ModId)>();
        foreach (var mod in api.ModLoader.Mods)
        {
            foreach (var system in mod.Systems)
            {
                var assembly = system.GetType().Assembly;
                if (!IsUnder(SafeLocation(assembly), gameDir)) meta.AssemblyToMod[assembly.GetName().Name ?? ""] = mod.Info.ModID;
            }
            // FolderPath is on ModContainer, not on the public Mod type.
            if (mod.GetType().GetField("FolderPath")?.GetValue(mod) is string folder && folder.Length > 0)
                folders.Add((NormalizeDir(folder), mod.Info.ModID));
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string location = SafeLocation(assembly);
            string name = assembly.GetName().Name ?? "";
            if (name.Length == 0) continue;
            if (location.Length > 0 && IsUnder(location, gameDir))
            {
                meta.GameAssemblies.Add(name);
                continue;
            }
            if (meta.AssemblyToMod.ContainsKey(name)) continue;
            bool owned = false;
            if (location.Length > 0)
                foreach (var (dir, modId) in folders)
                {
                    if (IsUnder(location, dir))
                    {
                        meta.AssemblyToMod[name] = modId;
                        owned = true;
                        break;
                    }
                }
            // Optimum loads its patched VintagestoryLib, VintagestoryAPI and built-in mod assemblies from memory or another folder, so
            // Location is empty or outside the game folder. A loaded assembly whose file ships in the game folder is still the game's;
            // anything else stays unknown.
            if (!owned && !assembly.IsDynamic && Attribution.ShipsWithGame(name, gameDir, File.Exists)) meta.GameAssemblies.Add(name);
        }
    }

    static string SafeLocation(Assembly assembly)
    {
        try { return assembly.IsDynamic ? "" : assembly.Location; }
        catch (NotSupportedException) { return ""; }
    }

    static string NormalizeDir(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    static bool IsUnder(string file, string dir) => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);

    void FinishCapture(CaptureSession session, CaptureMeta meta, string dir, IModResolver? memoryResolver)
    {
        string message;
        if (bundleMode) StopBundleSamplers();
        try
        {
            session.Stop(120000);
            meta.StartQpc = session.StartQpc;
            meta.EndQpc = session.EndQpc;
            meta.SamplerBytesAllocated = session.ReaderBytesAllocated;
            meta.NettraceBytes = session.TraceBytes;
            if (bundleMode) WriteBundle(dir, meta);
            // The capture session has fully stopped here, so no other EventPipe session holds the runtime Type keyword.
            // Gross per-mod allocation bytes are only built later from the trace by .sparkvs report, so the column stays empty.
            if (bundleMode && memoryResolver != null)
                Guard("memory", () =>
                {
                    memoryRunning = true;
                    try
                    {
                        var snap = HeapDumpEventPipe.Capture(Environment.ProcessId, TimeSpan.FromSeconds(120), m => capi?.Logger.Debug("SparkVS memory: " + m));
                        var table = MemoryReport.Build(snap, memoryResolver, null, GrossNote);
                        MemoryReport.WriteCsvOnly(dir, table);
                    }
                    finally { memoryRunning = false; }
                });
            File.WriteAllText(Path.Combine(dir, ReportBuilder.MetaFile), JsonSerializer.Serialize(meta));
            lastCaptureDir = dir;
            message = session.ReaderError != null
                ? "SparkVS: trace stream failed: " + session.ReaderError.Message
                : $"SparkVS: capture saved, {session.TraceBytes / 1048576} MB. Use .sparkvs report. Folder: {dir}";
        }
        catch (Exception ex)
        {
            message = "SparkVS: stop failed: " + ex.Message;
            capi?.Logger.Error("SparkVS: stop failed: " + ex);
        }
        state = State.Idle;
        Tell(message);
        if (autoReport) StartReport(dir);
    }

    /// <summary>Writes every bundle file except the CPU report files, which ReportBuilder adds. Each piece fails alone.</summary>
    void WriteBundle(string dir, CaptureMeta meta)
    {
        var c = clock!;
        var summary = new BundleSummary { StartLocal = c.StartIso, SparkVsVersion = BuildInfo.SparkVsVersion };
        Guard("frames", () => summary.Frames = BundleWriter.WriteFrames(dir, frames, c, config.SpikeThresholdMs, meta.Spikes, meta.SpikesDropped));
        Guard("tick", () => summary.Tick = BundleWriter.WriteTick(dir, tickLog, c, tickProbeFailure));
        Guard("heap", () => summary.Heap = heap!.Write(dir, heapSampler?.LastError));
        Guard("gpu memory", () => summary.Vram = gpu!.Write(dir));
        Guard("stubs", () => BundleWriter.WriteStubs(dir));
        Guard("gpu phases", () => BundleWriter.WriteJson(Path.Combine(dir, "gpu-phases.json"), BuildGpuPhases()));
        Guard("allocations placeholder", () => BundleWriter.WriteJson(Path.Combine(dir, "allocations.json"), AllocationReport.Disabled("pending: allocations.json is built by the report step; run .sparkvs report")));
        Guard("chunk pipeline", () => BundleWriter.WriteJson(Path.Combine(dir, "chunk-pipeline.json"), !config.ChunkPipeline
            ? ChunkPipelineWriter.Disabled("ChunkPipeline is false in SparkVS.json")
            : chunkStats == null
                ? ChunkPipelineWriter.Disabled("chunk timers did not run in this capture (no hook could be installed: " + string.Join("; ", ChunkPipelineHooks.Entries.Select(e => e.Info.Target + ": " + e.Info.FailureReason)) + ")")
                : ChunkPipelineHooks.BuildReport(c, chunkEndMs, chunkStats, queueRecorder, queueFailure)));
        Guard("patch map", () =>
        {
            if (!config.PatchMap) { BundleWriter.WriteJson(Path.Combine(dir, "patch-map.json"), PatchMapBuilder.Disabled("PatchMap is false in SparkVS.json")); return; }
            PatchMapCollector.TakeClient();
            BundleWriter.WriteJson(Path.Combine(dir, "patch-map.json"), PatchMapBuilder.Build(PatchMapCollector.Client, PatchMapCollector.Server, null, "no server side in this process, or its first game tick had not run when the capture ended"));
        });
        Guard("asset inventory", () => BundleWriter.WriteJson(Path.Combine(dir, "asset-inventory.json"), AssetInventoryCopy.Build(config.AssetInventoryFolder.Length > 0 ? config.AssetInventoryFolder : Path.Combine(GamePaths.DataPath, "ModData", "sparkvs", "asset-inventory"), DateTime.Now)));
        Guard("writer placeholders", () => BundleWriter.WritePlaceholdersForMissing(dir, "writer failed, see collection_errors in context.json"));
        if (heapSampler?.Errors > 0) bundleErrors.Add("heap sampler errors: " + heapSampler.Errors + ", last " + heapSampler.LastError);
        if (gpuSampler?.Errors > 0) bundleErrors.Add("gpu sampler errors: " + gpuSampler.Errors + ", last " + gpuSampler.LastError);
        Guard("context", () => WriteContext(dir, meta, c));
        meta.Bundle = summary;
    }

    void WriteContext(string dir, CaptureMeta meta, BundleClock c)
    {
        var ctx = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["capture"] = new JsonObject
            {
                ["label"] = label,
                ["folder"] = Path.GetFileName(dir),
                ["requested_seconds"] = durationSeconds,
                ["start_local"] = c.StartIso,
                ["end_local"] = BundleClock.Iso(captureEndLocal == default ? DateTime.Now : captureEndLocal),
                ["sampling_rate_knob_hex_ms"] = meta.SamplingRateKnob,
                ["main_thread_os_id"] = meta.MainThreadOsId,
                ["server_thread_os_id"] = meta.ServerThreadOsId,
                ["integrated_server"] = meta.IntegratedServer,
            },
            ["versions"] = ctxVersions?.DeepClone(),
            ["world"] = ctxWorld?.DeepClone(),
            ["graphics_settings"] = ctxGraphics?.DeepClone(),
            ["system"] = ContextCollector.System(),
            ["state_at_start"] = startState?.DeepClone(),
            ["state_at_end"] = endState?.DeepClone(),
            ["etw"] = EtwProfileFromEnv(out string etwNote),
            ["etw_note"] = etwNote,
        };
        var mods = new JsonObject
        {
            ["count"] = ctxMods?.Count ?? 0,
            ["entries"] = ctxMods == null ? new JsonArray() : ContextCollector.ModManifest(ctxMods),
            ["disabled_mod_ids"] = ctxDisabled?.DeepClone(),
            ["disabled_note"] = "disabled ids come from clientsettings.json on disk; every entry in entries is a loaded mod",
        };
        ctx["mods"] = mods;
        ctx["load_phases"] = LoadPhaseHook.Log.ToJson(ctxMods?.Select(m => m.ModId).ToList() ?? new List<string>());
        ctx["collection_errors"] = new JsonArray(bundleErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        BundleWriter.WriteJson(Path.Combine(dir, "context.json"), ctx);
    }

    /// <summary>The bench runner passes the ETW profile it started (or "none") in SPARKVS_ETW through the plan env; a manual capture has no such variable.</summary>
    internal static string? EtwProfileFromEnv(out string note)
    {
        var v = Environment.GetEnvironmentVariable(EtwEnv)?.Trim().ToLowerInvariant();
        if (v == "bench-safe" || v == "latency-hunt" || v == "full-trace" || v == "none") { note = "profile the bench runner started for this run (SPARKVS_ETW)"; return v; }
        note = string.IsNullOrEmpty(v) ? "unknown: SPARKVS_ETW was not set, so this capture was not started by the bench runner" : "unrecognised SPARKVS_ETW value ignored: " + v;
        return null;
    }

    TextCommandResult OnReport()
    {
        if (state != State.Idle) return TextCommandResult.Error("Stop the capture first");
        if (lastCaptureDir.Length == 0) return TextCommandResult.Error("No capture yet this session");
        if (reportRunning) return TextCommandResult.Error("A report is already building");
        StartReport(lastCaptureDir);
        return TextCommandResult.Success("SparkVS: building the report in the background, large captures take a while.");
    }

    void StartReport(string dir)
    {
        reportRunning = true;
        bool keepRaw = config.KeepRawTrace;
        var thread = new Thread(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var report = ReportBuilder.Run(dir, null, keepRaw, config.ThreadsInReport);
                Tell($"SparkVS: report ready in {clock.Elapsed.TotalSeconds:F0} s: {report.MainThreadSamples} main thread samples, {report.MainResolvedPct:F1} % resolved. {Path.Combine(dir, "summary.md")}");
            }
            catch (Exception ex)
            {
                capi?.Logger.Error("SparkVS: report failed: " + ex);
                Tell("SparkVS: report failed: " + ex.Message);
            }
            finally
            {
                reportRunning = false;
            }
        })
        {
            IsBackground = true,
            Name = "SparkVS report",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    const string GrossNote = "not measured: run a capture for gross allocations";

    TextCommandResult OnMemory()
    {
        if (capi == null) return TextCommandResult.Error("Client not ready");
        if (memoryRunning) return TextCommandResult.Error("A memory snapshot is already running");
        if (state != State.Idle) return TextCommandResult.Error("SparkVS is " + state + ", take a memory snapshot when no capture is running");
        InProcModResolver resolver;
        try { resolver = InProcModResolver.Build(capi); }
        catch (Exception ex) { return TextCommandResult.Error("SparkVS memory: could not build the mod map: " + ex.Message); }
        int topN = config.MemoryTopN;
        string frontEnd = "SparkVS mod " + BuildInfo.SparkVsVersion;
        string dir = Path.Combine(GamePaths.DataPath, "ModData", "sparkvs", "memory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        memoryRunning = true;
        var thread = new Thread(() =>
        {
            string message;
            try
            {
                var snap = HeapDumpEventPipe.Capture(Environment.ProcessId, TimeSpan.FromSeconds(120), m => capi?.Logger.Debug("SparkVS memory: " + m));
                var table = MemoryReport.Build(snap, resolver, null, GrossNote);
                MemoryReport.Write(dir, table, topN, frontEnd);
                message = MemorySummary(table, dir);
            }
            catch (Exception ex)
            {
                message = "SparkVS memory: snapshot failed: " + ex.Message;
                capi?.Logger.Error("SparkVS memory: snapshot failed: " + ex);
            }
            finally { memoryRunning = false; }
            Tell(message);
        })
        {
            IsBackground = true,
            Name = "SparkVS memory",
        };
        thread.Start();
        return TextCommandResult.Success("SparkVS: taking a heap snapshot, the game pauses for a few seconds.");
    }

    static string MemorySummary(MemoryTable table, string dir)
    {
        var inv = CultureInfo.InvariantCulture;
        string Mb(long bytes) => MemoryReport.Mb(bytes).ToString("0.0", inv);
        var top = new List<string>();
        foreach (var row in table.Rows)
        {
            if (row.Kind != OwnerKind.Mod || top.Count >= 5) continue;
            top.Add(row.Name + " " + Mb(row.LiveBytes) + " MB");
        }
        long shared = 0, unknown = 0;
        foreach (var row in table.Rows)
        {
            if (row.Kind == OwnerKind.Shared) shared += row.LiveBytes;
            else if (row.Kind == OwnerKind.Unknown) unknown += row.LiveBytes;
        }
        var snap = table.Snapshot;
        string text = "SparkVS memory: live " + Mb(table.TotalLiveBytes) + " MB in " + table.TotalLiveObjects.ToString("N0", inv) + " objects, dump pause "
            + snap.GcPauseMs.ToString("0", inv) + " ms. Top mods: " + (top.Count > 0 ? string.Join(", ", top) : "none") + ". system/shared " + Mb(shared)
            + " MB, unknown " + Mb(unknown) + " MB.";
        if (!snap.Complete) text += " Snapshot incomplete.";
        if (snap.Warnings.Count > 0) text += " Warnings: " + snap.Warnings.Count + " (see memory.md).";
        return text + " Folder: " + dir;
    }

    void Tell(string message)
    {
        capi?.Logger.Notification(message);
        try { capi?.Event.EnqueueMainThreadTask(() => capi?.ShowChatMessage(message), "sparkvs"); }
        catch (Exception ex) { capi?.Logger.Warning("SparkVS: could not post to chat: " + ex.Message); }
    }

    public override void Dispose()
    {
        if (state == State.Capturing)
        {
            // Leaving the world mid capture: close the session so the runtime is not left sampling,
            // and keep what was captured.
            long now = Stopwatch.GetTimestamp();
            state = State.Stopping;
            RemoveRenderer();
            if (bundleMode) EndBundleState();
            var meta = BuildMeta(frames.Frames / ((now - phaseStartStamp) / (double)Stopwatch.Frequency));
            autoReport = false;
            FinishCapture(capture!, meta, captureDir, null);
        }
        base.Dispose();
    }
}
