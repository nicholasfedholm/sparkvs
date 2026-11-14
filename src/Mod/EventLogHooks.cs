using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SparkVS.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace SparkVS;

/// <summary>
/// Server side half of the EventStream module, for a dedicated and an integrated server. It subscribes the game events and the log hook,
/// patches Entity.OnHurt (postfix, same target as the old TestLoggerHarmony), registers /testlog and /testlogh, and in always mode
/// opens the session sink. Every handler returns at once while no sink is open (EventStreamHub.Active). The storage, clock and reply
/// texts live in Core/EventStream.cs.
/// </summary>
internal sealed class EventStreamHooks : IDisposable
{
    public const string HarmonyId = "sparkvs.eventlog";
    static volatile EventStreamHooks? current;

    readonly ICoreServerAPI api;
    readonly EventStreamMode mode;
    readonly bool hurtLog;
    readonly string outputFolder;
    object? harmony;
    volatile bool hurtLive;
    bool subscribed;
    bool disposed;

    EventStreamHooks(ICoreServerAPI api, EventStreamMode mode, bool hurtLog, string outputFolder)
    {
        this.api = api;
        this.mode = mode;
        this.hurtLog = hurtLog;
        this.outputFolder = outputFolder;
    }

    public bool HurtLive => hurtLive;

    /// <summary>Always created, so /testlog answers in every mode. Events, the log hook, the patch and the session exist only when the mode is not off.</summary>
    public static EventStreamHooks Create(ICoreServerAPI api, SparkVSConfig config)
    {
        var mode = EventStreamModes.Parse(config.EventLog, out _);
        var hooks = new EventStreamHooks(api, mode, config.HurtLog, config.OutputFolder ?? "");
        current = hooks;
        if (EventStreamPolicy.Subscribe(mode)) hooks.Subscribe();
        hooks.PatchHurt();
        hooks.RegisterCommands();
        if (EventStreamPolicy.SessionFile(mode)) api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, hooks.StartAlwaysSession);
        api.Logger.Notification("SparkVS EventStream: mode " + EventStreamModes.Name(mode) + (mode == EventStreamMode.Off ? " (nothing subscribed)" : ""));
        return hooks;
    }

    void Subscribe()
    {
        api.Event.OnEntitySpawn += OnSpawn;
        api.Event.OnEntityDespawn += OnDespawn;
        api.Event.OnEntityDeath += OnDeath;
        api.Event.PlayerChat += OnChat;
        api.Logger.EntryAdded += OnLogEntry;
        subscribed = true;
        EventStreamHub.Attached = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (subscribed)
        {
            try
            {
                api.Event.OnEntitySpawn -= OnSpawn;
                api.Event.OnEntityDespawn -= OnDespawn;
                api.Event.OnEntityDeath -= OnDeath;
                api.Event.PlayerChat -= OnChat;
                api.Logger.EntryAdded -= OnLogEntry;
            }
            catch { }
            EventStreamHub.Attached = false;
        }
        if (harmony != null)
        {
            try { RemoveHurtPatch(); } catch { }
            harmony = null;
        }
        hurtLive = false;
        try { EventStreamHub.StopSession(); } catch { }
        if (current == this) current = null;
    }

    // Harmony types stay inside these two methods, so a missing Harmony fails inside the callers' try blocks instead of while this class loads.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static object ApplyHurtPatch()
    {
        var target = AccessTools.Method(typeof(Entity), "OnHurt", new[] { typeof(DamageSource), typeof(float) })
            ?? throw new MissingMethodException("Entity.OnHurt(DamageSource, float) not found in this game build");
        var postfix = AccessTools.Method(typeof(EventStreamHooks), nameof(HurtPostfix))
            ?? throw new MissingMethodException("EventStreamHooks.HurtPostfix not found");
        var h = new Harmony(HarmonyId);
        h.UnpatchAll(HarmonyId);   // a second world in the same process must not stack a second postfix
        h.Patch(target, postfix: new HarmonyMethod(postfix));
        return h;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void RemoveHurtPatch() => ((Harmony)harmony!).UnpatchAll(HarmonyId);

    void PatchHurt()
    {
        if (!EventStreamPolicy.PatchHurt(mode, hurtLog))
        {
            string why = mode == EventStreamMode.Off ? "EventLog is off in SparkVS.json" : "HurtLog is false in SparkVS.json";
            api.Logger.Notification("SparkVS EventStream: HURT patch not applied (" + why + ")");
            return;
        }
        try
        {
            harmony = ApplyHurtPatch();
            hurtLive = true;
            api.Logger.Notification("SparkVS EventStream: HURT patch applied on Entity.OnHurt");
        }
        catch (Exception ex)
        {
            hurtLive = false;
            api.Logger.Error("SparkVS EventStream: HURT logging DISABLED: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Runs after Entity.OnHurt returns. Overrides that skip base (EntityPlayer) never reach it, same as the old patch.</summary>
    public static void HurtPostfix(Entity __instance, DamageSource dmgSource, float damage)
    {
        if (!EventStreamHub.Active) return;
        try { current?.RecordHurt(__instance, dmgSource, damage); }
        catch { }
    }

    void RecordHurt(Entity entity, DamageSource? source, float damage)
    {
        long qpc = Stopwatch.GetTimestamp();
        // The client calls OnHurt with a null source for its own copy of the entity; the server call is the event.
        if (entity?.World?.Side != EnumAppSide.Server) return;
        EventStreamHub.Record(new EventRow
        {
            Type = EventRow.Hurt,
            Entity = Describe(entity),
            Pos = PosOf(entity),
            Source = SourceName(source),
            DmgType = TypeName(source),
            Damage = damage,
            Attacker = AttackerOf(source),
        }, qpc);
    }

    void OnSpawn(Entity entity)
    {
        if (!EventStreamHub.Active) return;
        long qpc = Stopwatch.GetTimestamp();
        try { EventStreamHub.Record(new EventRow { Type = EventRow.Spawn, Entity = Describe(entity), Pos = PosOf(entity) }, qpc); }
        catch { }
    }

    void OnDespawn(Entity entity, EntityDespawnData data)
    {
        if (!EventStreamHub.Active) return;
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            // Deaths have their own row, so a death despawn is not logged twice.
            if (data == null || data.Reason == EnumDespawnReason.Death) return;
            EventStreamHub.Record(new EventRow { Type = EventRow.Despawn, Entity = Describe(entity), Pos = PosOf(entity), Reason = data.Reason.ToString() }, qpc);
        }
        catch { }
    }

    void OnDeath(Entity entity, DamageSource? source)
    {
        if (!EventStreamHub.Active) return;
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            EventStreamHub.Record(new EventRow
            {
                Type = EventRow.Death,
                Entity = Describe(entity),
                Pos = PosOf(entity),
                Source = SourceName(source),
                DmgType = TypeName(source),
                Attacker = AttackerOf(source),
            }, qpc);
        }
        catch { }
    }

    void OnChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (!EventStreamHub.Active) return;
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            string? name = null;
            try { name = byPlayer?.PlayerName; } catch { }
            var row = EventRow.FromChat(name, message);
            if (row != null) EventStreamHub.Record(row, qpc);
        }
        catch { }
    }

    void OnLogEntry(EnumLogType logType, string message, params object[] args)
    {
        if (!EventStreamHub.Active) return;
        string level;
        switch (logType)
        {
            case EnumLogType.Warning: level = "warning"; break;
            case EnumLogType.Error: level = "error"; break;
            case EnumLogType.Fatal: level = "fatal"; break;
            default: return;
        }
        long qpc = Stopwatch.GetTimestamp();
        try
        {
            string text = message ?? "";
            if (args != null && args.Length > 0)
            {
                try { text = string.Format(text, args); } catch { }
            }
            EventStreamHub.Record(new EventRow { Type = EventRow.Log, Level = level, Message = text }, qpc);
        }
        catch { }
    }

    static EventEntity Describe(Entity e)
    {
        string code = "?", domain = "?", name = "?";
        long id = 0;
        try { code = e.Code?.ToString() ?? "?"; } catch { }
        try { domain = e.Code?.Domain ?? "?"; } catch { }
        try { name = e.GetName() ?? "?"; } catch { }
        try { id = e.EntityId; } catch { }
        return new EventEntity(code, id, domain, name);
    }

    static double[]? PosOf(Entity e)
    {
        try
        {
            var p = e.Pos?.XYZ;
            return p == null ? null : new[] { p.X, p.Y, p.Z };
        }
        catch { return null; }
    }

    static string? SourceName(DamageSource? ds)
    {
        try { return ds?.Source.ToString(); } catch { return null; }
    }

    static string? TypeName(DamageSource? ds)
    {
        try { return ds?.Type.ToString(); } catch { return null; }
    }

    static EventAttacker? AttackerOf(DamageSource? ds)
    {
        try
        {
            var a = ds?.GetCauseEntity();
            return a == null ? null : new EventAttacker(a.Code?.ToString() ?? "?", a.EntityId);
        }
        catch { return null; }
    }

    // ---- always mode and the old commands ----

    string NewSessionPath() => EventStreamPaths.SessionFile(outputFolder, GamePaths.DataPath, DateTime.Now);

    void StartAlwaysSession()
    {
        try
        {
            var (ok, text) = EventStreamCommands.Start(mode, NewSessionPath);
            if (ok) api.Logger.Notification(text);
            else api.Logger.Warning(text);
        }
        catch (Exception ex) { api.Logger.Warning("SparkVS EventStream: could not start the session: " + ex.Message); }
    }

    /// <summary>/testlog and /testlogh, the commands of the old standalone mods. Both go to the same hub. A name that already exists (the old mod is installed) is skipped with a warning.</summary>
    void RegisterCommands()
    {
        foreach (var name in new[] { "testlog", "testlogh" })
        {
            try
            {
                bool exists = false;
                foreach (var kv in api.ChatCommands)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (exists)
                {
                    api.Logger.Warning($"SparkVS EventStream: /{name} already exists (is the old TestLogger mod installed?), not registering SparkVS's /{name}");
                    continue;
                }
                var parsers = api.ChatCommands.Parsers;
                string priv = Privilege.controlserver;
                api.ChatCommands.Create(name)
                    .WithDescription("SparkVS event log: " + name + " start | stop | status | note <text>")
                    .RequiresPrivilege(priv)
                    .HandleWith(_ => Reply(EventStreamCommands.Status(mode, hurtLog, hurtLive)))
                    .BeginSubCommand("start")
                        .WithDescription("Always mode: start the session file. Capture mode: no-op, events are bound to SparkVS captures.")
                        .RequiresPrivilege(priv)
                        .HandleWith(_ => Reply(EventStreamCommands.Start(mode, NewSessionPath)))
                    .EndSubCommand()
                    .BeginSubCommand("stop")
                        .WithDescription("Always mode: stop the session file. Capture mode: no-op.")
                        .RequiresPrivilege(priv)
                        .HandleWith(_ => Reply(EventStreamCommands.Stop(mode)))
                    .EndSubCommand()
                    .BeginSubCommand("status")
                        .WithDescription("Show the event log mode, hurt patch and open sinks")
                        .RequiresPrivilege(priv)
                        .HandleWith(_ => Reply(EventStreamCommands.Status(mode, hurtLog, hurtLive)))
                    .EndSubCommand()
                    .BeginSubCommand("note")
                        .WithDescription("Write a note row into every open sink: note <text>")
                        .RequiresPrivilege(priv)
                        .WithArgs(parsers.OptionalAll("text"))
                        .HandleWith(args => Reply(EventStreamCommands.Note(mode, args.Caller?.Player?.PlayerName, (string?)args[0], Stopwatch.GetTimestamp())))
                    .EndSubCommand();
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"SparkVS EventStream: could not register /{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    static TextCommandResult Reply((bool Ok, string Text) r) => r.Ok ? TextCommandResult.Success(r.Text) : TextCommandResult.Error(r.Text);
}
