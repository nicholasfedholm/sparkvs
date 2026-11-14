using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SparkVS.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SparkVS;

public sealed partial class SparkVSSystem : ModSystem, IRenderer
{
    const int HardMaxSeconds = SparkVSConfig.HardMaxSeconds;   // absolute safety cap; MaxDurationSec in SparkVS.json is the configurable cap below it
    const int BaselineSeconds = 3;
    const int SpikeCapacity = 8192;
    // The frame ring is sized per capture by SparkVSConfig.FrameRingCapacityFor (3500 fps assumption, 13 bytes per entry, 109 MB at the cap).
    const int TickRingCapacity = 1 << 18;
    const string EtwEnv = "SPARKVS_ETW";
    const string AutostartEnv = "SPARKVS_AUTOSTART";    // label;delaySec;durationSec[;report]
    const string RateEnv = "DOTNET_EventPipeThreadSamplingRate";
    const string GpuAbEnv = "SPARKVS_GPUPHASES_AB";      // seconds per A/B block, overrides GpuPhasesAbBlockSeconds

    enum State { Idle, Baseline, Capturing, Stopping }

    // Both sides of a singleplayer game run in this process, so the server side leaves its thread id here.
    static volatile int serverThreadId;

    ICoreClientAPI? capi;
    // Exactly one of capi (client capture) and sapi (dedicated server capture, see SparkVSSystem.ServerParity.cs) is set on a capturing instance.
    ICoreServerAPI? sapi;
    SparkVSConfig config = SparkVSConfig.CreateDefault();
    long serverListener;
    CaptureExtras? extras;            // entity census, chunk events, teleports (bundle captures)
    List<string> perfNewestAtStart = new();
    ILogger? Log => capi?.Logger ?? sapi?.Logger;
    EventStreamHooks? eventHooks;
    static int eventLogWarned;

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

    /// <summary>The API of whichever side this instance captures on.</summary>
    ICoreAPI? Api => capi ?? (ICoreAPI?)sapi;

    bool IsServerCapture => sapi != null;

    public override void StartServerSide(ICoreServerAPI api)
    {
        ServerInfo.Api = api;
        sapi = api;
        config = LoadConfig(api);
        RegisterServerCapture(api);
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

        // A dedicated server has no client instance, so this instance owns the commands, the autostart and the capture.
        bool dedicated = false;
        try { dedicated = api.Server.IsDedicated; } catch (Exception ex) { api.Logger.Warning("SparkVS: could not read IsDedicated, server capture stays off: " + ex.Message); }
        if (dedicated) StartDedicatedServerCapture(api);

        // Server side event log (spawn, death, hurt, log lines, chat, commands). A dedicated server loaded the config above; an integrated one loads it here.
        try { eventHooks = EventStreamHooks.Create(api, dedicated ? config : LoadConfig(api)); }
        catch (Exception ex) { api.Logger.Error("SparkVS EventStream: disabled, could not start: " + ex.GetType().Name + ": " + ex.Message); }

        // The integrated server keeps its old behaviour: the listener exists only when the probe does. A dedicated server also
        // gets one without the probe, so tick.jsonl can still carry the tick period.
        if (tickProbe != null || dedicated)
            serverTickListener = api.Event.RegisterGameTickListener(_ => OnServerTick(), 1);
    }

    void OnServerTick()
    {
        if (!tickLog.Active) return;
        var probe = tickProbe;
        if (probe != null)
        {
            if (probe.TryRead(out int mspt)) tickLog.Record(Stopwatch.GetTimestamp(), mspt);
        }
        else if (IsServerCapture) tickLog.Record(Stopwatch.GetTimestamp(), TickLog.NoMspt);
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
        clientSideStarted = true;
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
                .HandleWith(_ => TextCommandResult.Success(StatusText()))
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
            // Null with a file on disk means the file could not be read (or LoadModConfig swallowed the parse error). Keep it before writing defaults over it.
            try
            {
                string path = Path.Combine(GamePaths.ModConfig, file);
                if (File.Exists(path))
                {
                    string bad = SparkVSConfig.BadFileName(path, DateTime.Now);
                    File.Copy(path, bad, false);
                    api.Logger.Warning("SparkVS: unreadable " + file + " kept as " + Path.GetFileName(bad) + ", writing defaults");
                }
            }
            catch (Exception ex) { api.Logger.Warning("SparkVS: could not back up the unreadable config: " + ex.Message); }
            loaded = SparkVSConfig.CreateDefault();
            api.StoreModConfig(loaded, file);
        }
        else if (loaded.Migrate(out string migration))
        {
            api.StoreModConfig(loaded, file);
            api.Logger.Notification("SparkVS: " + migration);
        }
        loaded.MaxDurationSec = Math.Clamp(loaded.MaxDurationSec, 1, HardMaxSeconds);
        loaded.DefaultDurationSec = Math.Max(1, loaded.DefaultDurationSec);
        loaded.MemoryTopN = Math.Clamp(loaded.MemoryTopN, 1, 200);
        var eventMode = EventStreamModes.Parse(loaded.EventLog, out bool eventModeValid);
        if (!eventModeValid && Interlocked.Exchange(ref eventLogWarned, 1) == 0)
            api.Logger.Warning("SparkVS: EventLog value \"" + loaded.EventLog + "\" in SparkVS.json is not off, capture or always; using capture");
        loaded.EventLog = EventStreamModes.Name(eventMode);
        return loaded;
    }

    /// <summary>The validated EventLog setting of this instance.</summary>
    EventStreamMode EventMode => EventStreamModes.Parse(config.EventLog, out _);

    void ScheduleAutostart()
    {
        if (capi == null) return;
        // Flags are comma separated and optional: report (build the report after the capture), bundle (write the full bundle; implies report).
        var outcome = AutostartSpec.TryParse(Environment.GetEnvironmentVariable(AutostartEnv), out var spec);
        if (outcome == AutostartOutcome.None) return;
        if (outcome == AutostartOutcome.Malformed)
        {
            capi.Logger.Warning("SparkVS: ignoring malformed " + AutostartEnv);
            return;
        }
        void Begin()
        {
            var result = OnStart(spec!.DurationSeconds, spec.Label, spec.Bundle, spec.Report);
            capi.Logger.Notification("SparkVS autostart: " + result.StatusMessage);
        }
        if (spec!.Mark != null) WaitForBenchMark(capi.Event, capi.Logger, spec.Mark, Begin);
        else capi.Event.RegisterCallback(_ => Begin(), Math.Max(spec.DelaySeconds, 1) * 1000);
    }

    /// <summary>Bench runs only: lines a capture up with the director's measure window by watching its log. Used by both sides.</summary>
    static void WaitForBenchMark(IEventAPI events, ILogger logger, string mark, Action begin)
    {
        var dir = Environment.GetEnvironmentVariable("VSBENCH_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            logger.Warning("SparkVS: autostart mark needs VSBENCH_OUT");
            return;
        }
        string path = Path.Combine(dir, "director.jsonl");
        long listener = 0;
        listener = events.RegisterGameTickListener(_ =>
        {
            if (!File.Exists(path)) return;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (!reader.ReadToEnd().Contains(mark, StringComparison.Ordinal)) return;
            }
            catch (IOException) { return; }
            events.UnregisterGameTickListener(listener);
            begin();
        }, 500);
    }

    TextCommandResult OnStart(int seconds, string requestedLabel, bool bundle, bool report)
    {
        if (capi == null) return TextCommandResult.Error("Client not ready");
        string? busy = BusyReason();
        if (busy != null) return TextCommandResult.Error(busy);

        PrepareCapture(seconds, requestedLabel, bundle, report);
        mainThreadId = Os.CurrentThreadId();

        if (bundle)
        {
            int ring = SparkVSConfig.FrameRingCapacityFor(durationSeconds);
            if (bundleFrames == null || bundleFrames.RingCapacity < ring) bundleFrames = new FrameLog(SpikeCapacity, config.SpikeThresholdMs, ring);
            frames = bundleFrames;
        }
        else frames = new FrameLog(SpikeCapacity, config.SpikeThresholdMs);
        phaseStartStamp = Stopwatch.GetTimestamp();
        frames.Reset(phaseStartStamp);
        phaseEndStamp = phaseStartStamp + BaselineSeconds * Stopwatch.Frequency;
        state = State.Baseline;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "sparkvs");
        rendererOn = true;

        return TextCommandResult.Success($"SparkVS: {BaselineSeconds} s fps baseline, then capturing {durationSeconds} s. {RateNote()}");
    }

    /// <summary>Why a capture cannot start now, or null. Shared by both sides.</summary>
    string? BusyReason()
    {
        if (state != State.Idle) return "SparkVS is already " + state;
        if (reportRunning) return "A report is still building";
        if (memoryRunning) return "A memory snapshot is still running";
        return null;
    }

    /// <summary>Sets the capture parameters and creates the output folder. Shared by both sides.</summary>
    void PrepareCapture(int seconds, string requestedLabel, bool bundle, bool report)
    {
        durationSeconds = Math.Clamp(seconds > 0 ? seconds : config.DefaultDurationSec, 1, config.MaxDurationSec);
        label = requestedLabel;
        bundleMode = bundle;
        autoReport = report;
        string root = CapturePaths.Root(config.BundleFolder, config.OutputFolder, bundle, GamePaths.DataPath);
        captureDir = Path.Combine(root, CapturePaths.CaptureName(DateTime.Now, label));
        Directory.CreateDirectory(captureDir);
    }

    string StatusText() => $"SparkVS {state}{(memoryRunning ? ", memory snapshot running" : "")}. Last capture: {(lastCaptureDir.Length > 0 ? lastCaptureDir : "none")}";

    string RateNote()
    {
        var knob = Environment.GetEnvironmentVariable(RateEnv) ?? "";
        string wanted = config.IntervalMs.ToString("X", CultureInfo.InvariantCulture);
        if (knob.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return $"Interval {config.IntervalMs} ms from launch setting.";
        if (knob.Length > 0) return $"Launch setting {RateEnv}={knob} (hex ms) overrides the configured {config.IntervalMs} ms.";
        return $"This game was launched without {RateEnv}={wanted}, so the runtime samples at about 1 ms, which costs more. It can only be set at launch.";
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
        // A server has no frames and no baseline phase, so there is no fps to compute.
        baselineFps = IsServerCapture ? 0 : frames.Frames / ((now - phaseStartStamp) / (double)Stopwatch.Frequency);
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
            Api?.Logger.Error("SparkVS: could not open the trace session: " + ex);
            capi?.ShowChatMessage("SparkVS: could not open the trace session: " + ex.Message);
            if (IsServerCapture) NotifyServerCaller("SparkVS: could not open the trace session: " + ex.Message);
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
            Log?.Warning("SparkVS bundle: " + what + " failed: " + ex.Message);
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
        // Event rows share this clock. Nothing at all when EventLog is off, or when no server side hooks exist in this process (a client on a remote server).
        if (EventMode != EventStreamMode.Off && EventStreamHub.Attached) EventStreamHub.BeginCapture(c);
        if (sapi != null) StartServerContext(sapi, c);
        else
        {
            var api = capi!;
            Guard("start state", () => startState = ContextCollector.State(api, c));
            Guard("world", () => ctxWorld = ContextCollector.World(api));
            Guard("graphics settings", () => ctxGraphics = ContextCollector.Graphics(api));
            Guard("versions", () => ctxVersions = ContextCollector.Versions(api, BuildInfo.SparkVsVersion));
            Guard("mod list", () => ctxMods = ContextCollector.ModList(api));
            Guard("disabled mods", () => ctxDisabled = ContextCollector.DisabledModIds());
        }
        perfNewestAtStart = ContextCollector.NewestPerfFileNames();
        // Census at start, chunk event subscription and the 1 Hz events sampler (teleports, gen2 GCs). Chunk handlers exist only until EndBundleState.
        // A client capture passes the integrated server (if any) as the second API; a server capture has no client API.
        var ex = extras = new CaptureExtras(c, capi, sapi ?? ServerInfo.Api, m => Log?.Warning("SparkVS bundle: " + m), config.EntityTickTiming);
        Guard("events and census", () =>
        {
            ex.Begin(durationSeconds, label, sapi != null ? "server" : "client");
            ex.Events.ToolAt(ex.Events.Clock.MsSince(Stopwatch.GetTimestamp()), "hooks_present", ("chunk_pipeline_hooks_installed", ChunkPipelineHooks.Entries.Count(e => e.Info.Installed)), ("note", "Harmony hooks are installed at game start, not by the capture"));
        });

        var heapRecorder = heap = new HeapRecorder(c);
        var hs = heapSampler = new PeriodicSampler("SparkVS heap 1Hz", c, heapRecorder.Sample);
        PeriodicSampler? gs = null;
        if (!IsServerCapture)
        {
            var recorder = gpu = new GpuRecorder(c, config.GpuMemoryDxgi);
            gs = gpuSampler = new PeriodicSampler("SparkVS gpu-memory 1Hz", c, recorder.Sample, () => recorder.Setup(Environment.ProcessId), recorder.Teardown);
        }
        Guard("heap sampler", () => hs.Start());
        if (gs != null) Guard("gpu sampler", () => gs.Start());
        tickProbe?.Reset();
        tickLog.Begin();
        if (!IsServerCapture) SetupGpuPhases();
        chunkStats = null;
        queueRecorder = null;
        queueSampler = null;
        queueFailure = IsServerCapture ? "client only: the chunk tesselation and pooling queues do not exist on a dedicated server, and the server worldgen queue has no public counter" : null;
        if (config.ChunkPipeline && ChunkPipelineHooks.Entries.Any(e => e.Info.Installed))
        {
            var reader = IsServerCapture ? null : ChunkPipelineHooks.CreateQueueReader(out queueFailure);
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
    [MethodImpl(MethodImplOptions.NoInlining)]
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
    [MethodImpl(MethodImplOptions.NoInlining)]
    void RegisterGpuMarkers()
    {
        if (capi == null || gpuPhases == null || gpuMarkersOn || state != State.Capturing) return;
        gpuMarkers = GpuPhaseMarker.CreateAll(gpuPhases);
        foreach (var m in gpuMarkers) capi.Event.RegisterRenderer(m, m.Stage, "sparkvs-gpu");
        gpuMarkersOn = true;
        gpuStartStamp = Stopwatch.GetTimestamp();
        gpuPhases.Begin(gpuStartStamp, (long)(gpuAbSeconds * Stopwatch.Frequency));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
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
        else if (clock != null && sapi != null) Guard("end state", () => endState = ContextCollector.State(sapi, clock));
        Guard("census end", () => extras?.EndWindow());
    }

    void StopBundleSamplers()
    {
        tickLog.End();
        EndChunkTimers();
        Guard("queue sampler stop", () => queueSampler?.Stop());
        Guard("heap sampler stop", () => heapSampler?.Stop());
        Guard("gpu sampler stop", () => gpuSampler?.Stop());
        Guard("events sampler stop", () => extras?.StopSampler());
    }

    TextCommandResult OnStop()
    {
        if (IsServerCapture) return ServerStop();
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
        double captureFps = IsServerCapture ? 0 : frames.Frames / ((now - phaseStartStamp) / (double)Stopwatch.Frequency);
        var meta = BuildMeta(captureFps);
        var session = capture!;
        var dir = captureDir;
        // The resolver reads the mod list and loaded assemblies, so it is built here on the main thread.
        InProcModResolver? memoryResolver = null;
        if (bundleMode && config.MemorySnapshotOnCaptureEnd && Api != null)
            Guard("memory resolver", () => memoryResolver = InProcModResolver.Build(Api));
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
            World = capi?.World?.SavegameIdentifier ?? ServerWorldId(),
            GameVersion = GameVersion.OverallVersion,
            StartedLocal = DateTime.Now.AddSeconds(-durationSeconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            QpcFrequency = Stopwatch.Frequency,
            MainThreadOsId = mainThreadId,
            ServerThreadOsId = IsServerCapture ? mainThreadId : serverThreadId,
            IntegratedServer = capi?.IsSinglePlayer ?? false,
            Side = IsServerCapture ? CaptureSide.Server : null,
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
        if (Api != null) FillAttribution(Api, meta);
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
                    // Tool-induced event: the heap snapshot forces a full blocking GC, so events around it are the tool's doing.
                    extras?.Events.Tool("heap_snapshot_begin", ("note", "forces a full blocking GC and pauses the game"));
                    try
                    {
                        var snap = HeapDumpEventPipe.Capture(Environment.ProcessId, TimeSpan.FromSeconds(120), m => Api?.Logger.Debug("SparkVS memory: " + m));
                        extras?.Events.Tool("heap_snapshot_end", ("gc_pause_ms", Math.Round(snap.GcPauseMs, 1)), ("complete", snap.Complete));
                        var table = MemoryReport.Build(snap, memoryResolver, null, GrossNote);
                        MemoryReport.WriteCsvOnly(dir, table);
                    }
                    finally { memoryRunning = false; }
                });
            if (bundleMode && extras != null) Guard("events", () => extras.Events.Write(Path.Combine(dir, ExtraSections.EventsFile)));
            var metaNode = JsonSerializer.SerializeToNode(meta)!.AsObject();
            metaNode["mode"] = IsServerCapture ? (bundleMode ? "server-engine-bundle" : "server-engine") : bundleMode ? "client-bundle" : "client";
            if (bundleMode && extras != null) metaNode[ExtraSections.OverheadKey] = extras.OverheadNode();
            File.WriteAllText(Path.Combine(dir, ReportBuilder.MetaFile), metaNode.ToJsonString());
            lastCaptureDir = dir;
            message = session.ReaderError != null
                ? "SparkVS: trace stream failed: " + session.ReaderError.Message
                : $"SparkVS: capture saved, {session.TraceBytes / 1048576} MB. Use .sparkvs report. Folder: {dir}";
        }
        catch (Exception ex)
        {
            message = "SparkVS: stop failed: " + ex.Message;
            Api?.Logger.Error("SparkVS: stop failed: " + ex);
        }
        state = State.Idle;
        Tell(message);
        if (autoReport) StartReport(dir);
    }

    /// <summary>Writes every bundle file except the CPU report files, which ReportBuilder adds. Each piece fails alone.</summary>
    void WriteBundle(string dir, CaptureMeta meta)
    {
        var c = clock!;
        bool server = IsServerCapture;
        var summary = new BundleSummary { StartLocal = c.StartIso, SparkVsVersion = BuildInfo.SparkVsVersion };
        if (server)
        {
            // No frame hook, GPU or render phases on a dedicated server: those files are not written at all.
            summary.Side = CaptureSide.Server;
            summary.Frames = null;
            summary.Vram = null;
        }
        else Guard("frames", () => summary.Frames = BundleWriter.WriteFrames(dir, frames, c, config.SpikeThresholdMs, meta.Spikes, meta.SpikesDropped));
        Guard("tick", () => summary.Tick = BundleWriter.WriteTick(dir, tickLog, c, tickProbeFailure, server));
        Guard("heap", () => summary.Heap = heap!.Write(dir, heapSampler?.LastError));
        if (!server)
        {
            Guard("gpu memory", () => summary.Vram = gpu!.Write(dir));
            Guard("stubs", () => BundleWriter.WriteStubs(dir));
        }
        Guard("census files", () => extras?.WriteCensusFiles(dir));
        Guard("entity ticks", () => extras?.WriteEntityTicks(dir));
        if (!server) Guard("gpu phases", () => BundleWriter.WriteJson(Path.Combine(dir, "gpu-phases.json"), BuildGpuPhases()));
        Guard("allocations placeholder", () => BundleWriter.WriteJson(Path.Combine(dir, "allocations.json"), AllocationReport.Disabled("pending: allocations.json is built by the report step; run .sparkvs report")));
        Guard("chunk pipeline", () => BundleWriter.WriteJson(Path.Combine(dir, "chunk-pipeline.json"), !config.ChunkPipeline
            ? ChunkPipelineWriter.Disabled("ChunkPipeline is false in SparkVS.json")
            : chunkStats == null
                ? ChunkPipelineWriter.Disabled("chunk timers did not run in this capture (no hook could be installed: " + string.Join("; ", ChunkPipelineHooks.Entries.Select(e => e.Info.Target + ": " + e.Info.FailureReason)) + ")")
                : ChunkPipelineHooks.BuildReport(c, chunkEndMs, chunkStats, queueRecorder, queueFailure)));
        Guard("patch map", () =>
        {
            if (!config.PatchMap) { BundleWriter.WriteJson(Path.Combine(dir, "patch-map.json"), PatchMapBuilder.Disabled("PatchMap is false in SparkVS.json")); return; }
            if (server)
            {
                if (PatchMapCollector.Server == null) PatchMapCollector.TakeServer();
                BundleWriter.WriteJson(Path.Combine(dir, "patch-map.json"), PatchMapBuilder.Build(null, PatchMapCollector.Server, "no client side in a dedicated server process", "the server snapshot could not be taken"));
                return;
            }
            PatchMapCollector.TakeClient();
            BundleWriter.WriteJson(Path.Combine(dir, "patch-map.json"), PatchMapBuilder.Build(PatchMapCollector.Client, PatchMapCollector.Server, null, "no server side in this process, or its first game tick had not run when the capture ended"));
        });
        Guard("asset inventory", () => BundleWriter.WriteJson(Path.Combine(dir, "asset-inventory.json"), AssetInventoryCopy.Build(CapturePaths.AssetInventoryDir(config.AssetInventoryFolder, GamePaths.DataPath), DateTime.Now)));
        Guard("writer placeholders", () => BundleWriter.WritePlaceholdersForMissing(dir, "writer failed, see collection_errors in context.json", server ? CaptureSide.ServerWriterFiles : null));
        if (heapSampler?.Errors > 0) bundleErrors.Add("heap sampler errors: " + heapSampler.Errors + ", last " + heapSampler.LastError);
        if (gpuSampler?.Errors > 0) bundleErrors.Add("gpu sampler errors: " + gpuSampler.Errors + ", last " + gpuSampler.LastError);
        Guard("game_events", () => { if (EventMode != EventStreamMode.Off) EventStreamHub.EndCapture(dir); });
        Guard("sparkload loading files", () =>
        {
            string? runDir = AppContext.GetData("SparkLoad.RunDir") as string;
            if (string.IsNullOrEmpty(runDir)) runDir = Environment.GetEnvironmentVariable("SPARKLOAD_RUN_DIR");
            if (string.IsNullOrEmpty(runDir) || !Directory.Exists(runDir)) return;
            foreach (var f in new[] { "loading.md", "loading.csv" })
            {
                string src = Path.Combine(runDir, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f), true);
            }
        });
        Guard("context", () => WriteContext(dir, meta, c));
        meta.Bundle = summary;
    }

    void WriteContext(string dir, CaptureMeta meta, BundleClock c)
    {
        bool server = IsServerCapture;
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
            ["system"] = ContextCollector.System(!server),
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
            ["disabled_note"] = server
                ? "disabled mod ids are read from clientsettings.json, which a dedicated server does not have, so none are listed; every entry in entries is a loaded mod"
                : "disabled ids come from clientsettings.json on disk; every entry in entries is a loaded mod",
        };
        ctx["mods"] = mods;
        AddCommonContext(ctx, c.StartLocal);
        if (server) ((JsonObject)ctx["capture"]!)["side"] = CaptureSide.Server;
        ctx["load_phases"] = LoadPhaseHook.Log.ToJson(ctxMods?.Select(m => m.ModId).ToList() ?? new List<string>());
        ctx["collection_errors"] = new JsonArray(bundleErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        BundleWriter.WriteJson(Path.Combine(dir, "context.json"), ctx);
    }

    /// <summary>Context keys that do not depend on which side captured. Each is null with a reason on failure.</summary>
    void AddCommonContext(JsonObject ctx, DateTime windowStart)
    {
        void Put(string key, Func<JsonNode?> make)
        {
            try { ctx[key] = make(); }
            catch (Exception ex)
            {
                ctx[key] = null;
                ctx[key + "_reason"] = ex.GetType().Name + ": " + ex.Message;
                bundleErrors.Add(key + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        Put("runtime", () => ContextCollector.Runtime());
        Put("gc", () => ContextCollector.Gc());
        Put("game_version", () => ContextCollector.GameVersionNode());
        Put("sparkvs_config", () => JsonSerializer.SerializeToNode(config));
        Put("perf_files", () => ContextCollector.PerfFiles(windowStart, perfNewestAtStart));
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
                Api?.Logger.Error("SparkVS: report failed: " + ex);
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
        var api = Api;
        if (api == null) return TextCommandResult.Error("Client not ready");
        if (memoryRunning) return TextCommandResult.Error("A memory snapshot is already running");
        if (state != State.Idle) return TextCommandResult.Error("SparkVS is " + state + ", take a memory snapshot when no capture is running");
        InProcModResolver resolver;
        try { resolver = InProcModResolver.Build(api); }
        catch (Exception ex) { return TextCommandResult.Error("SparkVS memory: could not build the mod map: " + ex.Message); }
        int topN = config.MemoryTopN;
        string frontEnd = "SparkVS mod " + BuildInfo.SparkVsVersion;
        string dir = CapturePaths.MemoryDir(GamePaths.DataPath, DateTime.Now);
        memoryRunning = true;
        var thread = new Thread(() =>
        {
            string message;
            try
            {
                var snap = HeapDumpEventPipe.Capture(Environment.ProcessId, TimeSpan.FromSeconds(120), m => api.Logger.Debug("SparkVS memory: " + m));
                var table = MemoryReport.Build(snap, resolver, null, GrossNote);
                MemoryReport.Write(dir, table, topN, frontEnd);
                message = MemorySummary(table, dir);
            }
            catch (Exception ex)
            {
                message = "SparkVS memory: snapshot failed: " + ex.Message;
                api.Logger.Error("SparkVS memory: snapshot failed: " + ex);
            }
            finally { memoryRunning = false; }
            Tell(message);
        })
        {
            IsBackground = true,
            Name = "SparkVS memory",
        };
        thread.Start();
        return TextCommandResult.Success(IsServerCapture
            ? "SparkVS: taking a heap snapshot, the server pauses for a few seconds. Folder: " + dir
            : "SparkVS: taking a heap snapshot, the game pauses for a few seconds.");
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
        Api?.Logger.Notification(message);
        if (sapi != null) { NotifyServerCaller(message); return; }
        try { capi?.Event.EnqueueMainThreadTask(() => capi?.ShowChatMessage(message), "sparkvs"); }
        catch (Exception ex) { capi?.Logger.Warning("SparkVS: could not post to chat: " + ex.Message); }
    }

    public override void Dispose()
    {
        if (serverPhase == ServerPhase.Running)
        {
            // Server shutting down mid capture: keep what was captured and give the writer thread a few seconds.
            FinishServerCapture();
            for (int i = 0; i < 100 && serverPhase != ServerPhase.Idle; i++) Thread.Sleep(100);
        }
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
        eventHooks?.Dispose();
        base.Dispose();
    }
}
