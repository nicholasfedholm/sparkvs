using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SparkVSTestHelper;

/// <summary>
/// Test helper, server side, no player. Timeline (seconds from the helper start, after RunGame):
/// load 3x3 columns KeepLoaded, spawn 40 hens + 25 pigs, run the SparkVS capture command (90 s), mid capture spawn 15 hens,
/// load then unload a ring of extra columns, log counts at the end. Everything goes to the log named by SPARKVS_TEST_LOG.
/// </summary>
public sealed class HelperSystem : ModSystem
{
    const string Hen = "game:chicken-hen", Pig = "game:pig-eurasian-adult-female";
    ICoreServerAPI sapi = null!;
    string logPath = "";
    int cx, cz;
    readonly List<(int X, int Z)> ring = new();
    int hensSpawned, pigsSpawned, capSeconds = 90;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    void Log(string msg)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg;
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        sapi.Logger.Notification("[TestHelper] " + msg);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        logPath = Environment.GetEnvironmentVariable("SPARKVS_TEST_LOG") ?? Path.Combine(AppContext.BaseDirectory, "helper.log");
        int.TryParse(Environment.GetEnvironmentVariable("SPARKVS_TEST_CAPTURE_SEC"), out capSeconds);
        if (capSeconds <= 0) capSeconds = 90;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Step1(), 3000));
    }

    void After(double seconds, Action a) => sapi.Event.RegisterCallback(_ => { try { a(); } catch (Exception e) { Log("ERROR " + e); } }, (int)(seconds * 1000));

    void Step1()
    {
        var sp = sapi.World.DefaultSpawnPosition;
        cx = (int)Math.Floor(sp.X / 32.0);
        cz = (int)Math.Floor(sp.Z / 32.0);
        Log($"start: spawn=({sp.X:F1},{sp.Y:F1},{sp.Z:F1}) center chunk column ({cx},{cz}) capture_seconds={capSeconds}");
        int pending = 9;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                int x = cx + dx, z = cz + dz;
                sapi.WorldManager.LoadChunkColumnPriority(x, z, new ChunkLoadOptions
                {
                    KeepLoaded = true,
                    OnLoaded = () => { if (--pending == 0) Log("3x3 columns loaded (KeepLoaded)"); },
                });
            }
        After(6, Step2);
    }

    void Step2()
    {
        Log($"loaded-and-kept check: column ({cx},{cz}) loaded={sapi.WorldManager.GetChunk(cx, 0, cz) != null}");
        SpawnMany(Hen, 40, ref hensSpawned);
        SpawnMany(Pig, 25, ref pigsSpawned);
        Log($"spawned hens={hensSpawned} pigs={pigsSpawned}; counts now: {Counts()}");
        After(4, Step3);
    }

    void Step3()
    {
        Log("counts before capture: " + Counts());
        var caller = new Caller { Type = EnumCallerType.Console, CallerRole = "admin", CallerPrivileges = new[] { "*" } };
        string cmd = $"/sparkvs capture {capSeconds} test";
        sapi.ChatCommands.ExecuteUnparsed(cmd, new TextCommandCallingArgs { Caller = caller, RawArgs = new CmdArgs() }, r => Log($"command '{cmd}' -> status={r.Status} message={r.StatusMessage}"));
        double t = capSeconds;
        After(t * 0.33, () => { SpawnMany(Hen, 15, ref hensSpawned); Log($"MID spawned +15 hens, hens total spawned={hensSpawned}; counts: {Counts()}"); });
        After(t * 0.44, LoadRing);
        After(t * 0.62, UnloadRing);
        After(t - 2, () => Log("counts 2 s before capture end: " + Counts()));
        After(t + 3, () => Log("counts after capture end: " + Counts()));
        After(t + 8, () => Log("helper done"));
    }

    void LoadRing()
    {
        int n = 0;
        for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1) continue;
                int x = cx + dx, z = cz + dz;
                ring.Add((x, z));
                sapi.WorldManager.LoadChunkColumnPriority(x, z, new ChunkLoadOptions { KeepLoaded = false });
                n++;
            }
        Log($"MID requested load of {n} ring columns (KeepLoaded=false)");
    }

    void UnloadRing()
    {
        foreach (var (x, z) in ring) sapi.WorldManager.UnloadChunkColumn(x, z);
        Log($"MID requested unload of {ring.Count} ring columns");
    }

    void SpawnMany(string code, int count, ref int counter)
    {
        var type = sapi.World.GetEntityType(new AssetLocation(code));
        if (type == null) { Log("ERROR entity type not found: " + code); return; }
        var rnd = new Random(code.GetHashCode() ^ counter);
        for (int i = 0; i < count; i++)
        {
            double x = (cx - 1) * 32 + 6 + rnd.NextDouble() * (96 - 12);
            double z = (cz - 1) * 32 + 6 + rnd.NextDouble() * (96 - 12);
            int h = sapi.World.BlockAccessor.GetRainMapHeightAt((int)x, (int)z);
            var e = sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(x, h + 1, z);
            e.Pos.SetFrom(e.ServerPos);
            sapi.World.SpawnEntity(e);
            counter++;
        }
    }

    string Counts()
    {
        var d = new SortedDictionary<string, int>();
        foreach (var kv in sapi.World.LoadedEntities)
        {
            string c = kv.Value.Code.ToString();
            d[c] = d.GetValueOrDefault(c) + 1;
        }
        return $"total={sapi.World.LoadedEntities.Count} hens={d.GetValueOrDefault(Hen)} pigs={d.GetValueOrDefault(Pig)}";
    }
}
