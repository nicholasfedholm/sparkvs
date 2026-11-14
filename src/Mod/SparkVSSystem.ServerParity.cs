using System;
using System.Diagnostics;
using SparkVS.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SparkVS;

// Dedicated server side of SparkVS. It drives the same capture engine as the client (PrepareCapture, BeginSession,
// BeginStop, FinishCapture, WriteBundle, StartReport, OnMemory in SparkVSSystem.cs); this file only adds what a server
// needs around it: commands, autostart, the tick-driven start and stop that replace the render hook, and the server
// flavoured context snapshots. Nothing here runs in a process that also runs a client.
public sealed partial class SparkVSSystem
{
    // Set when StartClientSide runs. A process with a client instance (singleplayer) lets that instance own captures.
    static volatile bool clientSideStarted;

    bool serverDedicated;
    readonly OnceFlag serverAutostartFired = new();
    string notifyPlayerUid = "";

    void StartDedicatedServerCapture(ICoreServerAPI api)
    {
        serverDedicated = true;
        sapi = api;
        config = LoadConfig(api);
        if (Mod?.Info?.Version is string modVersion) BuildInfo.SparkVsVersion = modVersion;
        RegisterServerCommands(api);
        // Replaces the render hook: the capture ends from the server tick thread, so the end-of-capture reads run there.
        api.Event.RegisterGameTickListener(_ => OnServerCaptureTick(), 50);
        // RunGame fires once per boot, after the world is loaded and the tick loop is about to run.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, ScheduleServerAutostart);
        api.Logger.Notification("SparkVS " + BuildInfo.SparkVsVersion + ": dedicated server capture ready. Use /sparkvs start|capture|stop|report|memory|status from the console or as an admin.");
    }

    // RECONCILE-OPEN: 0ce1bcd registers /sparkvs capture, stop and status (the light server bundle, SparkVSSystem.Server.cs); the 0.4.0 line registered the same three names for its full-engine server capture. 0ce1bcd's three stay; this engine adds start, report and memory to the same command, and its full bundle capture runs only via SPARKVS_AUTOSTART (bundle flag).
    void RegisterServerCommands(ICoreServerAPI api)
    {
        var parsers = api.ChatCommands.Parsers;
        string priv = Privilege.controlserver;
        api.ChatCommands.GetOrCreate("sparkvs")
            .WithDescription("SparkVS server capture: census, chunk events, heap and tick series; start | report | memory run the full capture engine")
            .RequiresPrivilege(priv)
            .BeginSubCommand("start")
                .WithDescription("Start a capture of the full engine (CPU sampling, plain trace): /sparkvs start [seconds] [label]")
                .RequiresPrivilege(priv)
                .WithArgs(parsers.OptionalInt("seconds", -1), parsers.OptionalWord("label"))
                .HandleWith(args => ServerStart((int)args[0], (string?)args[1] ?? "", false, false, args.Caller))
            .EndSubCommand()
            .BeginSubCommand("report")
                .WithDescription("Build summary.md, summary.json and the Speedscope file for the last capture")
                .RequiresPrivilege(priv)
                .HandleWith(_ => ServerGuarded(ServerReport))
            .EndSubCommand()
            .BeginSubCommand("memory")
                .WithDescription("Take a live managed heap snapshot now and write memory.md/csv/json per mod under ModData/sparkvs/memory-<time>. Pauses the server for about 3.5 s per 3 GB of heap.")
                .RequiresPrivilege(priv)
                .HandleWith(_ => ServerGuarded(OnMemory))
            .EndSubCommand();
    }

    /// <summary>Null when this instance may capture; otherwise why not. Checked per command because the client instance may start after this one.</summary>
    string? ServerInactiveReason() => CaptureSide.ServerCaptureActive(serverDedicated, clientSideStarted)
        ? null
        : "SparkVS server capture is off in this process because it also runs a client; use .sparkvs on the client";

    TextCommandResult ServerGuarded(Func<TextCommandResult> action)
    {
        string? inactive = ServerInactiveReason();
        return inactive != null ? TextCommandResult.Error(inactive) : action();
    }

    TextCommandResult ServerReport()
    {
        var result = OnReport();
        if (result.Status == EnumCommandStatus.Success) sapi?.Logger.Notification("SparkVS: report started for " + lastCaptureDir);
        return result;
    }

    TextCommandResult ServerStart(int seconds, string requestedLabel, bool bundle, bool report, Caller? caller)
    {
        var api = sapi;
        string? inactive = ServerInactiveReason();
        if (api == null || inactive != null) return TextCommandResult.Error(inactive ?? "Server not ready");
        string? busy = BusyReason();
        if (busy != null) return TextCommandResult.Error(busy);
        // The two server capture paths share the static tick log, so only one runs at a time.
        if (serverPhase != ServerPhase.Idle) return TextCommandResult.Error("SparkVS server bundle capture is already " + serverPhase);

        PrepareCapture(seconds, requestedLabel, bundle, report);
        notifyPlayerUid = caller?.Player?.PlayerUID ?? "";
        // Pending until the server tick thread begins the session: the context snapshots and the thread id must come from it.
        state = State.Baseline;
        api.Event.EnqueueMainThreadTask(() =>
        {
            if (state != State.Baseline) return;   // stopped before it began
            mainThreadId = Os.CurrentThreadId();
            long now = Stopwatch.GetTimestamp();
            phaseStartStamp = now;
            BeginSession(now);
            if (state == State.Capturing)
                api.Logger.Notification($"SparkVS: server capture running for {durationSeconds} s ({(bundleMode ? "bundle" : "plain")}). Folder: {captureDir}");
        }, "sparkvs");

        string text = $"SparkVS: server capture of {durationSeconds} s ({(bundle ? "bundle, report follows" : "plain")}) starts on the next tick. {RateNote()} Folder: {captureDir}";
        api.Logger.Notification(text);
        return TextCommandResult.Success(text);
    }

    TextCommandResult ServerStop()
    {
        var api = sapi;
        if (api == null) return TextCommandResult.Error("Server not ready");
        if (state == State.Baseline)
        {
            state = State.Idle;
            api.Logger.Notification("SparkVS: server capture cancelled before it began.");
            return TextCommandResult.Success("SparkVS: stopped before the capture began.");
        }
        if (state != State.Capturing) return TextCommandResult.Error("SparkVS is not capturing");
        string dir = captureDir;
        api.Event.EnqueueMainThreadTask(() =>
        {
            if (state == State.Capturing) BeginStop(Stopwatch.GetTimestamp());
        }, "sparkvs");
        api.Logger.Notification("SparkVS: stop requested for the server capture. Folder: " + dir);
        return TextCommandResult.Success("SparkVS: stopping, the rundown can take a few seconds. Folder: " + dir);
    }

    /// <summary>Server tick thread, every 50 ms: ends a capture whose time is up (the client does this from the render hook).</summary>
    void OnServerCaptureTick()
    {
        if (state == State.Capturing)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= phaseEndStamp) BeginStop(now);
        }
    }

    /// <summary>Server tick thread. The same SPARKVS_AUTOSTART format as the client, started once the world is running.</summary>
    void ScheduleServerAutostart()
    {
        var api = sapi;
        if (api == null || !serverAutostartFired.TryFire()) return;
        var outcome = AutostartSpec.TryParse(Environment.GetEnvironmentVariable(AutostartEnv), out var spec);
        if (outcome == AutostartOutcome.None) return;
        if (outcome == AutostartOutcome.Malformed)
        {
            api.Logger.Warning("SparkVS: ignoring malformed " + AutostartEnv);
            return;
        }
        void Begin()
        {
            var result = ServerStart(spec!.DurationSeconds, spec.Label, spec.Bundle, spec.Report, null);
            api.Logger.Notification("SparkVS autostart: " + result.StatusMessage);
        }
        if (spec!.Mark != null) WaitForBenchMark(api.Event, api.Logger, spec.Mark, Begin);
        else api.Event.RegisterCallback(_ => Begin(), Math.Max(spec.DelaySeconds, 1) * 1000);
    }

    /// <summary>Server tick thread, at bundle start: the server flavour of the context snapshots. A dedicated server has no graphics settings or client disabled list.</summary>
    void StartServerContext(ICoreServerAPI s, BundleClock c)
    {
        Guard("start state", () => startState = ContextCollector.State(s, c));
        Guard("world", () => ctxWorld = ContextCollector.World(s));
        ctxGraphics = ContextCollector.GraphicsNotApplicable();
        Guard("versions", () => ctxVersions = ContextCollector.Versions(s, BuildInfo.SparkVsVersion));
        Guard("mod list", () => ctxMods = ContextCollector.ModList(s));
        ctxDisabled = null;
    }

    string ServerWorldId()
    {
        try { return sapi?.WorldManager.SaveGame.SavegameIdentifier ?? ""; }
        catch { return ""; }
    }

    /// <summary>Echoes a background-thread message to the admin who started the capture in chat. The console sees it through the logger.</summary>
    void NotifyServerCaller(string message)
    {
        var api = sapi;
        string uid = notifyPlayerUid;
        if (api == null || uid.Length == 0) return;
        try
        {
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (api.World.PlayerByUid(uid) is IServerPlayer player)
                    player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
            }, "sparkvs");
        }
        catch (Exception ex) { api.Logger.Warning("SparkVS: could not post to chat: " + ex.Message); }
    }
}
