using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SparkVS.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SparkVS;

/// <summary>
/// Server side capture: /sparkvs capture [seconds] [label], stop, status. Works on a dedicated server (no client at all) and on the
/// integrated server of a single player game. Writes a lighter bundle than the client one: no frames, no CPU stacks, no heap dump,
/// but the same census, events, heap series, tick series and context.json, all on one BundleClock.
/// </summary>
public sealed partial class SparkVSSystem
{
    const string ServerAutostartEnv = "SPARKVS_SERVER_AUTOSTART";   // label;delaySec;durationSec, delay counted from savegame load

    enum ServerPhase { Idle, Running, Finishing }

    volatile ServerPhase serverPhase = ServerPhase.Idle;
    string serverDir = "", serverLabel = "", lastServerDir = "";
    int serverRequested;
    long serverEndStamp, serverCheckListener;
    bool serverTickOwned;
    BundleClock? serverClock;
    CaptureExtras? serverExtras;
    HeapRecorder? serverHeap;
    PeriodicSampler? serverHeapSampler;
    JsonObject? serverStartState, serverEndState, serverWorld;
    DateTime serverEndLocal;

    void RegisterServerCapture(ICoreServerAPI api)
    {
        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create("sparkvs")
            .WithDescription("SparkVS server capture: census, chunk events, heap and tick series")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("capture")
                .WithDescription("Run a server capture bundle: /sparkvs capture [seconds] [label]. Writes a -server folder under the bundle folder.")
                .WithArgs(parsers.OptionalInt("seconds", -1), parsers.OptionalWord("label"))
                .HandleWith(args => OnServerStart((int)args[0], (string?)args[1] ?? ""))
            .EndSubCommand()
            .BeginSubCommand("stop")
                .WithDescription("Stop the running server capture")
                .HandleWith(_ => OnServerStop())
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Show server capture state")
                .HandleWith(_ => TextCommandResult.Success($"SparkVS server capture {serverPhase}. Last: {(lastServerDir.Length > 0 ? lastServerDir : "none")}"));

        // Headless runs: SPARKVS_SERVER_AUTOSTART=label;delaySec;durationSec starts a capture delaySec after the savegame loaded.
        var spec = Environment.GetEnvironmentVariable(ServerAutostartEnv);
        if (string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split(';');
        if (parts.Length < 3
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            api.Logger.Warning("SparkVS: ignoring malformed " + ServerAutostartEnv);
            return;
        }
        api.Event.SaveGameLoaded += () =>
            api.Event.RegisterCallback(_ => api.Logger.Notification("SparkVS server autostart: " + OnServerStart(seconds, parts[0]).StatusMessage), Math.Max(delay, 1) * 1000);
    }

    TextCommandResult OnServerStart(int seconds, string requestedLabel)
    {
        var api = sapi;
        if (api == null) return TextCommandResult.Error("Server not ready");
        if (serverPhase != ServerPhase.Idle) return TextCommandResult.Error("SparkVS server capture is already " + serverPhase);

        int duration = Math.Clamp(seconds > 0 ? seconds : config.DefaultDurationSec, 1, config.MaxDurationSec);
        string root = config.BundleFolder.Length > 0 ? config.BundleFolder
            : config.OutputFolder.Length > 0 ? config.OutputFolder
            : Path.Combine(GamePaths.DataPath, "ModData", "sparkvs", "captures");
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-server" + (requestedLabel.Length > 0 ? "-" + Sanitize(requestedLabel) : "");
        try
        {
            serverDir = Path.Combine(root, name);
            Directory.CreateDirectory(serverDir);
            serverLabel = requestedLabel;
            serverRequested = duration;
            bundleErrors.Clear();
            serverStartState = serverEndState = serverWorld = null;
            var clock = serverClock = BundleClock.FromStamp(Stopwatch.GetTimestamp());
            perfNewestAtStart = ContextCollector.NewestPerfFileNames();
            Guard("server world", () => serverWorld = ContextCollector.ServerWorld(api));
            Guard("server state at start", () => serverStartState = ContextCollector.ServerState(api, clock));

            var ex = serverExtras = new CaptureExtras(clock, null, api, m => api.Logger.Warning("SparkVS server bundle: " + m), config.EntityTickTiming);
            var heapRecorder = serverHeap = new HeapRecorder(clock);
            var hs = serverHeapSampler = new PeriodicSampler("SparkVS server heap 1Hz", clock, heapRecorder.Sample);
            Guard("heap sampler", () => hs.Start());
            serverTickOwned = !tickLog.Active;
            if (serverTickOwned)
            {
                tickProbe?.Reset();
                tickLog.Begin();
            }
            Guard("events and census", () => ex.Begin(duration, requestedLabel, "server"));

            serverEndStamp = Stopwatch.GetTimestamp() + duration * Stopwatch.Frequency;
            serverPhase = ServerPhase.Running;
            serverCheckListener = api.Event.RegisterGameTickListener(_ =>
            {
                if (serverPhase == ServerPhase.Running && Stopwatch.GetTimestamp() >= serverEndStamp) FinishServerCapture();
            }, 1000);
        }
        catch (Exception ex)
        {
            serverPhase = ServerPhase.Idle;
            api.Logger.Error("SparkVS: server capture could not start: " + ex);
            return TextCommandResult.Error("SparkVS: server capture could not start: " + ex.Message);
        }
        return TextCommandResult.Success($"SparkVS: server capture running for {duration} s. Folder: {serverDir}");
    }

    TextCommandResult OnServerStop()
    {
        if (serverPhase != ServerPhase.Running) return TextCommandResult.Error("SparkVS server capture is not running");
        FinishServerCapture();
        return TextCommandResult.Success("SparkVS: stopping the server capture, files are written in the background.");
    }

    /// <summary>Server main thread: close the window (census, unsubscribe), then hand the slow part to a worker thread.</summary>
    void FinishServerCapture()
    {
        var api = sapi;
        var ex = serverExtras;
        var clock = serverClock;
        if (api == null || ex == null || clock == null || serverPhase != ServerPhase.Running) return;
        serverPhase = ServerPhase.Finishing;
        try { api.Event.UnregisterGameTickListener(serverCheckListener); } catch { }
        serverEndLocal = DateTime.Now;
        Guard("server state at end", () => serverEndState = ContextCollector.ServerState(api, clock));
        Guard("census end", () => ex.EndWindow());
        if (serverTickOwned) tickLog.End();
        var thread = new Thread(() => WriteServerBundle(api, ex, clock, serverDir, serverEndLocal))
        {
            IsBackground = true,
            Name = "SparkVS server finish",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    void WriteServerBundle(ICoreServerAPI api, CaptureExtras ex, BundleClock clock, string dir, DateTime endLocal)
    {
        string message;
        try
        {
            Guard("events sampler stop", () => ex.StopSampler());
            Guard("heap sampler stop", () => serverHeapSampler?.Stop());
            var summary = new BundleSummary { StartLocal = clock.StartIso, SparkVsVersion = BuildInfo.SparkVsVersion };
            Guard("heap", () => summary.Heap = serverHeap!.Write(dir, serverHeapSampler?.LastError));
            Guard("tick", () => summary.Tick = BundleWriter.WriteTick(dir, serverTickOwned ? tickLog : null, clock,
                serverTickOwned ? tickProbeFailure : "the tick log was in use by another capture in this process (a client bundle capture), so no server tick series was recorded"));
            Guard("census files", () => ex.WriteCensusFiles(dir));
            Guard("entity ticks", () => ex.WriteEntityTicks(dir));
            Guard("events", () => ex.Events.Write(Path.Combine(dir, ExtraSections.EventsFile)));
            if (serverHeapSampler?.Errors > 0) bundleErrors.Add("heap sampler errors: " + serverHeapSampler.Errors + ", last " + serverHeapSampler.LastError);
            if (ex.SamplerErrors > 0) bundleErrors.Add("events sampler errors: " + ex.SamplerErrors + ", last " + ex.SamplerLastError);
            Guard("context", () => WriteServerContext(api, dir, clock, endLocal));

            var meta = new JsonObject
            {
                ["mode"] = "server",
                ["label"] = serverLabel,
                ["folder"] = Path.GetFileName(dir),
                ["started_local"] = clock.StartIso,
                ["ended_local"] = BundleClock.Iso(endLocal),
                ["requested_seconds"] = serverRequested,
                ["duration_sec"] = Math.Round((endLocal - clock.StartLocal).TotalSeconds, 1),
                ["game_version"] = GameVersion.OverallVersion,
                ["sparkvs_version"] = BuildInfo.SparkVsVersion,
                ["entity_source"] = ex.EntitySource,
                ["qpc_frequency"] = clock.Frequency,
                ["server_thread_os_id"] = serverThreadId,
                ["tick_series_owned"] = serverTickOwned,
                ["bundle"] = JsonSerializer.SerializeToNode(summary),
                [ExtraSections.OverheadKey] = ex.OverheadNode(),
                ["collection_errors"] = new JsonArray(bundleErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            };
            BundleWriter.WriteJson(Path.Combine(dir, ReportBuilder.MetaFile), meta);
            File.WriteAllText(Path.Combine(dir, "summary.md"), ExtraSections.ServerSummaryMarkdown(dir));
            lastServerDir = dir;
            message = "SparkVS: server capture saved. Folder: " + dir;
        }
        catch (Exception e)
        {
            message = "SparkVS: server capture write failed: " + e.Message;
            api.Logger.Error("SparkVS: server capture write failed: " + e);
        }
        serverPhase = ServerPhase.Idle;
        api.Logger.Notification(message);
    }

    void WriteServerContext(ICoreServerAPI api, string dir, BundleClock clock, DateTime endLocal)
    {
        var ctx = new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["capture"] = new JsonObject
            {
                ["mode"] = "server",
                ["label"] = serverLabel,
                ["folder"] = Path.GetFileName(dir),
                ["requested_seconds"] = serverRequested,
                ["start_local"] = clock.StartIso,
                ["end_local"] = BundleClock.Iso(endLocal),
                ["server_thread_os_id"] = serverThreadId,
                ["dedicated"] = SafeBool(() => api.Server.IsDedicated),
            },
            ["versions"] = new JsonObject { ["game"] = GameVersion.OverallVersion, ["sparkvs"] = BuildInfo.SparkVsVersion, ["dotnet_runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
            ["world"] = serverWorld?.DeepClone(),
            ["state_at_start"] = serverStartState?.DeepClone(),
            ["state_at_end"] = serverEndState?.DeepClone(),
            ["etw"] = EtwProfileFromEnv(out string etwNote),
            ["etw_note"] = etwNote,
        };
        try { ctx["system"] = ContextCollector.System(); }
        catch (Exception e) { ctx["system"] = null; ctx["system_reason"] = e.GetType().Name + ": " + e.Message; bundleErrors.Add("system: " + e.Message); }
        List<ModEntry>? mods = null;
        Guard("mod list", () => mods = ContextCollector.ServerModList(api));
        ctx["mods"] = new JsonObject
        {
            ["count"] = mods?.Count ?? 0,
            ["entries"] = mods == null ? new JsonArray() : ContextCollector.ModManifest(mods),
            ["note"] = "mods the server loaded; sides_loaded says server for every entry",
        };
        AddCommonContext(ctx, clock.StartLocal);
        ctx["collection_errors"] = new JsonArray(bundleErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        BundleWriter.WriteJson(Path.Combine(dir, "context.json"), ctx);
    }

    static bool? SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
