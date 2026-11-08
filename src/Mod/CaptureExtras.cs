using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using SparkVS.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace SparkVS;

/// <summary>
/// Per capture extras shared by the client bundle and the server bundle: entity census at start and end, chunk event counters
/// (subscribed only while a capture runs), and a 1 Hz sampler for teleports and gen2 GCs. All of it lands in events.jsonl and the census files,
/// on the same BundleClock as every other series. Idle cost outside a capture is zero: nothing here exists then.
/// </summary>
internal sealed class CaptureExtras
{
    readonly BundleClock clock;
    readonly ICoreClientAPI? capi;
    readonly ICoreServerAPI? sapi;
    readonly Action<string> warn;
    readonly TeleportDetector teleports = new();
    readonly GcWatcher gcWatcher = new();
    PeriodicSampler? sampler;
    ChunkColumnLoadedDelegate? onColumnLoaded;
    ChunkColumnUnloadDelegate? onColumnUnloaded;
    ChunkDirtyDelegate? onChunkDirty;
    long samplerTicks;
    bool subscribed;
    readonly bool entityTickTiming;
    bool ownsTickHook;
    string? tickSkipReason;

    public EventLog Events { get; }
    public EntityCensus? CensusStart { get; private set; }
    public EntityCensus? CensusEnd { get; private set; }
    public string? CensusStartFailure { get; private set; }
    public string? CensusEndFailure { get; private set; }

    /// <summary>Server API when one runs in this process (dedicated, or the integrated server of a single player game); null on a pure client.</summary>
    public CaptureExtras(BundleClock clock, ICoreClientAPI? capi, ICoreServerAPI? sapi, Action<string> warn, bool entityTickTiming = true)
    {
        this.entityTickTiming = entityTickTiming;
        this.clock = clock;
        this.capi = capi;
        this.sapi = sapi;
        this.warn = warn;
        Events = new EventLog(clock);
    }

    public string EntitySource => sapi != null ? "server LoadedEntities (ICoreServerAPI.World.LoadedEntities)"
        : capi != null ? "client LoadedEntities (no server in this process)" : "none";

    public void Begin(int requestedSeconds, string label, string mode)
    {
        Events.Tool("capture_start", ("mode", mode), ("label", label), ("requested_seconds", requestedSeconds));
        CensusStart = RunCensus("census_start", out string? failure);
        CensusStartFailure = failure;
        BeginEntityTicks();
        try
        {
            Subscribe();
            // Constructed after the subscription: the sampler's first sample primes the GC watcher and the teleport positions.
            sampler = new PeriodicSampler("SparkVS events 1Hz", clock, Sample);
            sampler.Start();
        }
        catch (Exception ex) { warn("events setup failed: " + ex.Message); }
    }

    /// <summary>Main thread at the end of the capture window: stop counting chunk events, take the closing census. The sampler stops later, off thread.</summary>
    public void EndWindow()
    {
        Unsubscribe();
        EndEntityTicks();
        CensusEnd = RunCensus("census_end", out string? failure);
        CensusEndFailure = failure;
        Events.Tool("capture_stop");
    }

    /// <summary>Finish thread: final sample and flush.</summary>
    public void StopSampler()
    {
        sampler?.Stop();
        Events.Flush(clock.MsSince(Stopwatch.GetTimestamp()));
    }

    void BeginEntityTicks()
    {
        ownsTickHook = false;
        if (!entityTickTiming) { tickSkipReason = "EntityTickTiming is false in SparkVS.json"; return; }
        if (sapi == null) { tickSkipReason = "no server in this process (pure client capture), so there is no server entity tick to time"; return; }
        string? failure = EntityTickHook.Begin();
        if (failure != null)
        {
            tickSkipReason = failure;
            warn("entity tick timing not active: " + failure);
            Events.Tool("harmony_patch", ("target", "ServerSystemEntitySimulation.TickEntities"), ("result", "failed"), ("reason", failure));
            return;
        }
        ownsTickHook = true;
        Events.Tool("harmony_patch", ("target", EntityTickHook.TargetName), ("result", "patched"), ("wrapper_calibration_ns_per_call", Math.Round(EntityTickHook.OverheadNs, 1)));
    }

    void EndEntityTicks()
    {
        if (!ownsTickHook) return;
        string? failure = EntityTickHook.End();
        if (failure != null) warn("entity tick unpatch failed: " + failure);
        Events.Tool("harmony_unpatch", ("target", EntityTickHook.TargetName), ("result", failure == null ? "unpatched" : "failed"), ("reason", failure),
            ("wrapped_calls_total", EntityTickHook.Accumulator?.CallsTotal ?? 0));
    }

    public void WriteEntityTicks(string dir)
    {
        var node = ownsTickHook ? EntityTickHook.BuildFile(null) : EntityTickReport.Disabled(tickSkipReason ?? "entity tick timing did not run");
        BundleWriter.WriteJson(Path.Combine(dir, EntityTickReport.FileName), node);
    }

    public long SamplerErrors => sampler?.Errors ?? 0;
    public string? SamplerLastError => sampler?.LastError;

    void Subscribe()
    {
        if (sapi != null)
        {
            onColumnLoaded = (_, _) => Events.ChunkLoaded();
            onColumnUnloaded = _ => Events.ChunkUnloaded();
            sapi.Event.ChunkColumnLoaded += onColumnLoaded;
            sapi.Event.ChunkColumnUnloaded += onColumnUnloaded;
        }
        if (capi != null)
        {
            onChunkDirty = (_, _, reason) =>
            {
                if (reason == EnumChunkDirtyReason.NewlyLoaded) Events.ClientChunkReceived();
                else Events.Ignored();
            };
            capi.Event.ChunkDirty += onChunkDirty;
        }
        subscribed = true;
    }

    void Unsubscribe()
    {
        if (!subscribed) return;
        subscribed = false;
        try
        {
            if (sapi != null)
            {
                if (onColumnLoaded != null) sapi.Event.ChunkColumnLoaded -= onColumnLoaded;
                if (onColumnUnloaded != null) sapi.Event.ChunkColumnUnloaded -= onColumnUnloaded;
            }
            if (capi != null && onChunkDirty != null) capi.Event.ChunkDirty -= onChunkDirty;
        }
        catch (Exception ex) { warn("chunk event unsubscribe failed: " + ex.Message); }
    }

    /// <summary>1 Hz, on the sampler thread: close the chunk bucket, note gen2 GCs, look for teleports.</summary>
    void Sample(double tMs)
    {
        Interlocked.Increment(ref samplerTicks);
        Events.Flush(tMs);
        if (gcWatcher.Observe(GC.CollectionCount(1), GC.CollectionCount(2), out int gen1, out int gen2))
            Events.Gc(tMs, gen2, gen1);
        try { ObservePlayers(tMs); }
        catch (Exception ex) { warn("teleport sampling failed: " + ex.Message); }
    }

    void ObservePlayers(double tMs)
    {
        var online = new List<string>();
        void See(string? name, EntityPos? pos)
        {
            if (name == null || pos == null) return;
            online.Add(name);
            var hit = teleports.Observe(name, pos.X, pos.Y, pos.Z, pos.Dimension);
            if (hit != null) Events.Teleport(tMs, hit);
        }
        if (sapi != null)
        {
            foreach (var p in sapi.World.AllOnlinePlayers)
                See(p?.PlayerName, p?.Entity?.Pos);
        }
        else if (capi != null)
        {
            var p = capi.World.Player;
            See(p?.PlayerName, p?.Entity?.Pos);
        }
        teleports.Retain(online);
    }

    static IEnumerable<(string Code, double X, double Y, double Z)> Tuples(IEnumerable<KeyValuePair<long, Entity>> source)
    {
        foreach (var kv in source)
        {
            var e = kv.Value;
            var pos = e?.Pos;
            if (e == null || pos == null) continue;
            yield return (e.Code?.ToString() ?? "", pos.X, pos.Y, pos.Z);
        }
    }

    /// <summary>One pass over the loaded entity list, timed. Null with a reason when no list could be read.</summary>
    EntityCensus? RunCensus(string toolRow, out string? failure)
    {
        failure = null;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            string side;
            IEnumerable<KeyValuePair<long, Entity>> source;
            if (sapi != null) { side = "server"; source = sapi.World.LoadedEntities; }
            else if (capi != null) { side = "client"; source = ((IClientWorldAccessor)capi.World).LoadedEntities; }
            else { failure = "no world API available"; return null; }
            var census = EntityCensus.Build(Tuples(source), side);
            long t1 = Stopwatch.GetTimestamp();
            census.Source = EntitySource;
            census.DurationMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            census.TMs = clock.MsSince(t0);
            census.TsLocal = clock.IsoAt(census.TMs);
            Events.ToolAt(census.TMs, toolRow, ("duration_ms", Math.Round(census.DurationMs, 3)), ("entity_total", census.Total), ("side", side));
            return census;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            warn("entity census failed: " + failure);
            Events.Tool(toolRow, ("failed", failure));
            return null;
        }
    }

    public void WriteCensusFiles(string dir)
    {
        BundleWriter.WriteJson(Path.Combine(dir, ExtraSections.CensusStartFile),
            CensusStart?.ToJson(BuildInfo.SchemaVersion) ?? EntityCensus.Disabled(BuildInfo.SchemaVersion, CensusStartFailure ?? "census did not run"));
        BundleWriter.WriteJson(Path.Combine(dir, ExtraSections.CensusEndFile),
            CensusEnd?.ToJson(BuildInfo.SchemaVersion) ?? EntityCensus.Disabled(BuildInfo.SchemaVersion, CensusEndFailure ?? "the capture ended before the closing census ran"));
    }

    /// <summary>The block stored in capture.meta.json under "sparkvs_overhead".</summary>
    public JsonObject OverheadNode()
    {
        long calls = Events.HandlerCalls, ns = Events.HandlerNs;
        return new JsonObject
        {
            ["event_handler_calls"] = calls,
            ["event_handler_ns"] = ns,
            ["event_handler_ns_per_call"] = calls > 0 ? Math.Round((double)ns / calls, 1) : 0,
            ["census_start_ms"] = CensusStart == null ? null : Math.Round(CensusStart.DurationMs, 3),
            ["census_end_ms"] = CensusEnd == null ? null : Math.Round(CensusEnd.DurationMs, 3),
            ["census_entity_total_end"] = CensusEnd?.Total,
            ["events_sampler_ticks"] = Interlocked.Read(ref samplerTicks),
            ["events_sampler_errors"] = SamplerErrors,
            ["events_rows"] = Events.RowCount,
            ["entity_tick_wrapper_ns_per_call"] = ownsTickHook ? Math.Round(EntityTickHook.OverheadNs, 1) : null,
            ["entity_tick_wrapped_calls"] = ownsTickHook ? EntityTickHook.Accumulator?.CallsTotal : null,
            ["handler_note"] = "handler time covers the counter work inside each subscribed chunk event handler (two Stopwatch reads and Interlocked adds); the game's delegate dispatch is not included",
        };
    }
}
