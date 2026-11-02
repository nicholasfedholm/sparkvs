using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace SparkVS.Core;

/// <summary>One timed stage of the chunk pipeline. Ids index ChunkStages.All and the timer arrays.</summary>
public sealed class StageDef
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>server, client, or both (shared game code that runs on either side).</summary>
    public string Side { get; init; } = "";
    public string Target { get; init; } = "";
    public string Meaning { get; init; } = "";
}

public static class ChunkStages
{
    public const int WorldgenTerrain = 0, WorldgenTerrainFeatures = 1, WorldgenVegetation = 2, WorldgenLightFlood = 3, WorldgenPreDone = 4, WorldgenOtherPass = 5;
    public const int WorldgenPopulate = 6, StreamReadDb = 7, StreamReadMiss = 8, StreamDeserialize = 9, StreamDecompress = 10, StreamLoadColumn = 11;
    public const int ClientReceive = 12, TesselatePriority = 13, TesselateNormal = 14, UploadChunk = 15, UploadMesh = 16;
    public const int Count = 17;

    public static readonly StageDef[] All =
    {
        new() { Id = WorldgenTerrain, Name = "worldgen_pass_terrain", Side = "server", Target = "ServerSystemSupplyChunks.runGenerators(request, forPass=1)", Meaning = "all generators of the Terrain pass for one chunk column" },
        new() { Id = WorldgenTerrainFeatures, Name = "worldgen_pass_terrain_features", Side = "server", Target = "ServerSystemSupplyChunks.runGenerators(request, forPass=2)", Meaning = "all generators of the TerrainFeatures pass for one column" },
        new() { Id = WorldgenVegetation, Name = "worldgen_pass_vegetation", Side = "server", Target = "ServerSystemSupplyChunks.runGenerators(request, forPass=3)", Meaning = "all generators of the Vegetation pass for one column" },
        new() { Id = WorldgenLightFlood, Name = "worldgen_pass_neighbour_sunlight_flood", Side = "server", Target = "ServerSystemSupplyChunks.runGenerators(request, forPass=4)", Meaning = "all generators of the NeighbourSunLightFlood pass for one column" },
        new() { Id = WorldgenPreDone, Name = "worldgen_pass_pre_done", Side = "server", Target = "ServerSystemSupplyChunks.runGenerators(request, forPass=5)", Meaning = "all generators of the PreDone pass for one column" },
        new() { Id = WorldgenOtherPass, Name = "worldgen_pass_other", Side = "server", Target = "ServerSystemSupplyChunks.runGenerators(request, forPass=0 or 6+)", Meaning = "any other pass index" },
        new() { Id = WorldgenPopulate, Name = "worldgen_populate_column", Side = "server", Target = "ServerSystemSupplyChunks.PopulateChunk(request)", Meaning = "one worldgen pass of one column including unpack, locking and the generators" },
        new() { Id = StreamReadDb, Name = "stream_read_db", Side = "server", Target = "GameDatabase.GetChunk(x, y, z, dimension)", Meaning = "SQLite read of one stored chunk that exists (a column is several chunks)" },
        new() { Id = StreamReadMiss, Name = "stream_read_db_miss", Side = "server", Target = "GameDatabase.GetChunk(x, y, z, dimension) returning null", Meaning = "lookup of a chunk that is not in the save yet (worldgen will follow)" },
        new() { Id = StreamDeserialize, Name = "stream_deserialize", Side = "server", Target = "ServerChunk.FromBytes(bytes, pool, world)", Meaning = "protobuf deserialize of one stored chunk into a ServerChunk" },
        new() { Id = StreamDecompress, Name = "decompress_all_contexts", Side = "both", Target = "ChunkData.DecompressFrom(...)", Meaning = "real block/light decompression of one chunk; lazy, so it runs wherever a packed chunk is first used (load, worldgen, tesselation, block access), not only on the streaming path" },
        new() { Id = StreamLoadColumn, Name = "stream_load_column", Side = "server", Target = "ServerSystemSupplyChunks.TryLoadChunkColumn(request)", Meaning = "read plus deserialize of a whole stored column, only columns that loaded" },
        new() { Id = ClientReceive, Name = "client_receive_chunk", Side = "client", Target = "ClientWorldMap.LoadChunkFromPacket(packet)", Meaning = "client stores one chunk received from the server" },
        new() { Id = TesselatePriority, Name = "tesselate_priority", Side = "client", Target = "ChunkTesselatorManager.TesselateChunk(..., priority=true)", Meaning = "mesh build of a priority chunk on the tesselation thread, calls that produced vertices" },
        new() { Id = TesselateNormal, Name = "tesselate_normal", Side = "client", Target = "ChunkTesselatorManager.TesselateChunk(..., priority=false)", Meaning = "mesh build of a normal chunk on the tesselation thread, calls that produced vertices" },
        new() { Id = UploadChunk, Name = "upload_chunk", Side = "client", Target = "ChunkRenderer.AddTesselatedChunk(tesselated, chunk)", Meaning = "main-thread hand-over of one tesselated chunk to the render pools, includes GPU buffer updates" },
        new() { Id = UploadMesh, Name = "upload_mesh_part", Side = "client", Target = "MeshDataPoolManager.AddModel(meshData, ...)", Meaning = "one mesh part added to a GPU pool; a chunk has several parts" },
    };
}

/// <summary>One Harmony target, as plain data so a test can resolve it against the game's own assemblies.</summary>
public sealed record HookTarget(string Side, string TypeName, string Method, int ParamCount, int[] Stages, string Pre, string Post,
    string[] RequiredParams, string? ReturnType, string? Note = null, string[]? ParamTypes = null)
{
    public string Target => TypeName.Substring(TypeName.LastIndexOf('.') + 1) + "." + Method + "(" + ParamCount + " params)";
}

public static class ChunkHookTargets
{
    public static readonly HookTarget[] All =
    {
        new("server", "Vintagestory.Server.ServerSystemSupplyChunks", "runGenerators", 2, new[] { ChunkStages.WorldgenTerrain, ChunkStages.WorldgenTerrainFeatures, ChunkStages.WorldgenVegetation, ChunkStages.WorldgenLightFlood, ChunkStages.WorldgenPreDone, ChunkStages.WorldgenOtherPass }, "Pre", "PostGen", new[] { "forPass" }, "Void", "private; one call per pass per column. The JIT may inline a small private method into its caller, in which case calls would read 0."),
        new("server", "Vintagestory.Server.ServerSystemSupplyChunks", "PopulateChunk", 1, new[] { ChunkStages.WorldgenPopulate }, "Pre", "PostPopulate", Array.Empty<string>(), "Void", "two one-parameter overloads exist in the running game, so the parameter type is pinned", new[] { "Vintagestory.Server.ChunkColumnLoadRequest" }),
        new("server", "Vintagestory.Server.ServerSystemSupplyChunks", "TryLoadChunkColumn", 1, new[] { ChunkStages.StreamLoadColumn }, "Pre", "PostLoadColumn", Array.Empty<string>(), "ServerChunk[]", "only calls that returned a column are counted"),
        new("server", "Vintagestory.Common.GameDatabase", "GetChunk", 4, new[] { ChunkStages.StreamReadDb, ChunkStages.StreamReadMiss }, "Pre", "PostGetChunk", Array.Empty<string>(), "Byte[]", "the (x, y, z, dimension) overload the column loader uses; the ulong and 3 int overloads are not hooked"),
        new("server", "Vintagestory.Server.ServerChunk", "FromBytes", 3, new[] { ChunkStages.StreamDeserialize }, "Pre", "PostFromBytes", Array.Empty<string>(), "ServerChunk"),
        new("server", "Vintagestory.Common.ChunkData", "DecompressFrom", 5, new[] { ChunkStages.StreamDecompress }, "Pre", "PostDecompress", Array.Empty<string>(), "Void", "runs on whichever side first uses a packed chunk, so it is one stage for client and server"),
        new("client", "Vintagestory.Client.NoObf.ClientWorldMap", "LoadChunkFromPacket", 1, new[] { ChunkStages.ClientReceive }, "Pre", "PostReceive", Array.Empty<string>(), "Void"),
        new("client", "Vintagestory.Client.NoObf.ChunkTesselatorManager", "TesselateChunk", 6, new[] { ChunkStages.TesselatePriority, ChunkStages.TesselateNormal }, "Pre", "PostTess", new[] { "priority" }, "Int32", "only calls that returned vertices are counted, so skipped no-op calls do not dilute the mean"),
        new("client", "Vintagestory.Client.NoObf.ChunkRenderer", "AddTesselatedChunk", 2, new[] { ChunkStages.UploadChunk }, "Pre", "PostUploadChunk", Array.Empty<string>(), "Void"),
        new("client", "Vintagestory.API.Client.MeshDataPoolManager", "AddModel", 4, new[] { ChunkStages.UploadMesh }, "PreUploadMesh", "PostUploadMesh", new[] { "modeldata" }, null, "also counts mesh parts, vertices, indices and estimated bytes"),
    };
}

/// <summary>
/// Lock free totals for one stage. Percentiles come from a ring of the most recent samples, preallocated at construction,
/// so Record never allocates.
/// </summary>
public sealed class StageStats
{
    public const int RingSize = 16384;
    readonly int[] ring = new int[RingSize];
    readonly double usPerTick;
    long count, totalTicks, maxTicks;

    public StageStats(long frequency) { usPerTick = 1_000_000.0 / frequency; }

    public long Count => Interlocked.Read(ref count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(long ticks)
    {
        long n = Interlocked.Increment(ref count);
        Interlocked.Add(ref totalTicks, ticks);
        long cur;
        while (ticks > (cur = Volatile.Read(ref maxTicks)))
            if (Interlocked.CompareExchange(ref maxTicks, ticks, cur) == cur) break;
        double us = ticks * usPerTick;
        ring[(int)((n - 1) & (RingSize - 1))] = us >= int.MaxValue ? int.MaxValue : (int)us;
    }

    public double TotalMs => Interlocked.Read(ref totalTicks) * usPerTick / 1000.0;
    public double MaxMs => Volatile.Read(ref maxTicks) * usPerTick / 1000.0;

    /// <summary>p95 over the most recent samples in the ring; exact when count is within RingSize.</summary>
    public double P95Ms(out int samplesUsed)
    {
        long n = Count;
        int used = (int)Math.Min(n, RingSize);
        samplesUsed = used;
        if (used == 0) return 0;
        var copy = new int[used];
        Array.Copy(ring, copy, used);
        Array.Sort(copy);
        int idx = (int)Math.Ceiling(0.95 * used) - 1;
        return copy[Math.Clamp(idx, 0, used - 1)] / 1000.0;
    }
}

/// <summary>
/// Static timer used by the Harmony hooks. Start and Stop allocate nothing and Start costs one volatile read when no
/// capture runs. Stage arrays are built at Begin, once per capture.
/// </summary>
public static class ChunkTimers
{
    static volatile bool active;
    static StageStats[] stages = Array.Empty<StageStats>();
    static long uploadParts, uploadVertices, uploadIndices, uploadBytes;

    public static bool Active => active;

    public static void Begin()
    {
        var fresh = new StageStats[ChunkStages.Count];
        for (int i = 0; i < fresh.Length; i++) fresh[i] = new StageStats(Stopwatch.Frequency);
        Interlocked.Exchange(ref uploadParts, 0);
        Interlocked.Exchange(ref uploadVertices, 0);
        Interlocked.Exchange(ref uploadIndices, 0);
        Interlocked.Exchange(ref uploadBytes, 0);
        stages = fresh;
        active = true;
    }

    public static StageStats[] End()
    {
        active = false;
        return stages;
    }

    /// <summary>0 when no capture runs, else a timestamp. A caller keeps it in the Harmony __state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => active ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stop(int stage, long start)
    {
        if (start == 0) return;
        var s = stages;
        if ((uint)stage < (uint)s.Length) s[stage].Record(Stopwatch.GetTimestamp() - start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddUpload(int vertices, int indices, long bytes)
    {
        Interlocked.Increment(ref uploadParts);
        Interlocked.Add(ref uploadVertices, vertices);
        Interlocked.Add(ref uploadIndices, indices);
        Interlocked.Add(ref uploadBytes, bytes);
    }

    public static (long Parts, long Vertices, long Indices, long Bytes) UploadTotals() =>
        (Interlocked.Read(ref uploadParts), Interlocked.Read(ref uploadVertices), Interlocked.Read(ref uploadIndices), Interlocked.Read(ref uploadBytes));

    /// <summary>Stage for a worldgen pass index (EnumWorldGenPass: Terrain 1 .. PreDone 5).</summary>
    public static int WorldgenStageForPass(int pass) => pass switch
    {
        1 => ChunkStages.WorldgenTerrain,
        2 => ChunkStages.WorldgenTerrainFeatures,
        3 => ChunkStages.WorldgenVegetation,
        4 => ChunkStages.WorldgenLightFlood,
        5 => ChunkStages.WorldgenPreDone,
        _ => ChunkStages.WorldgenOtherPass,
    };
}

public readonly record struct QueueSnapshot(int AwaitingTesselation, int AwaitingPooling);

/// <summary>1 Hz queue depth series with ms since capture start and an ISO timestamp on every row.</summary>
public sealed class QueueRecorder
{
    readonly BundleClock clock;
    readonly Func<QueueSnapshot?> read;
    readonly List<(double TMs, string Ts, QueueSnapshot Q)> rows = new();
    readonly object gate = new();

    public QueueRecorder(BundleClock clock, Func<QueueSnapshot?> read)
    {
        this.clock = clock;
        this.read = read;
    }

    public void Sample(double tMs)
    {
        var q = read();
        if (q == null) return;
        var row = (tMs, clock.IsoAt(tMs), q.Value);
        lock (gate) rows.Add(row);
    }

    public JsonObject ToJson(string source, string? failure)
    {
        List<(double TMs, string Ts, QueueSnapshot Q)> copy;
        lock (gate) copy = rows.ToList();
        if (copy.Count == 0)
            return new JsonObject { ["enabled"] = false, ["reason"] = failure ?? "no queue samples were taken", ["source"] = source };
        var series = new JsonArray(copy.Select(r => (JsonNode?)new JsonObject
        {
            ["t_ms"] = Math.Round(r.TMs, 1),
            ["ts_local"] = r.Ts,
            ["chunks_awaiting_tesselation"] = r.Q.AwaitingTesselation,
            ["chunks_awaiting_pooling"] = r.Q.AwaitingPooling,
        }).ToArray());
        return new JsonObject
        {
            ["enabled"] = true,
            ["source"] = source,
            ["rate_hz"] = 1,
            ["samples"] = copy.Count,
            ["chunks_awaiting_tesselation"] = new JsonObject { ["mean"] = Math.Round(copy.Average(r => (double)r.Q.AwaitingTesselation), 2), ["max"] = copy.Max(r => r.Q.AwaitingTesselation) },
            ["chunks_awaiting_pooling"] = new JsonObject { ["mean"] = Math.Round(copy.Average(r => (double)r.Q.AwaitingPooling), 2), ["max"] = copy.Max(r => r.Q.AwaitingPooling) },
            ["series"] = series,
        };
    }
}

/// <summary>What the hook installer learned about one patch target. Filled by the mod, written by chunk-pipeline.json.</summary>
public sealed class HookInfo
{
    public int[] Stages { get; init; } = Array.Empty<int>();
    public string Target { get; init; } = "";
    public bool Installed { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Patches by other owners found on the same method (owner, kind, patch method), read at report time.</summary>
    public List<(string Owner, string Kind, string PatchMethod)> OtherPatches { get; } = new();
    public string? Note { get; set; }
}

public static class ChunkPipelineWriter
{
    /// <summary>
    /// What runtime.db (runtime-dump 20261030-121211, 162 mods) knew about patches on these targets. The dump is a dedicated
    /// server run, so client-only targets are not covered by it; the live list in hooks[].other_patches is the one to trust.
    /// </summary>
    public static readonly string[] RuntimeDbSnapshot =
    {
        "ServerSystemSupplyChunks.runGenerators: watersheds prefix + finalizer",
        "ServerSystemSupplyChunks.PopulateChunk: chunklod prefix + postfix (WorldgenTimingProfiler)",
        "ServerSystemSupplyChunks.TryLoadChunkColumn: chunklod prefix + postfix (WorldgenTimingProfiler)",
        "SQLiteDbConnectionv2.GetChunk(UInt64,DbCommand), one call below GameDatabase.GetChunk: watersheds prefix + finalizer",
        "ChunkTesselatorManager.TesselateChunk: rivers prefix + postfix + transpiler",
        "no entries for GameDatabase.GetChunk, ServerChunk.FromBytes, ChunkData.DecompressFrom, ClientWorldMap.LoadChunkFromPacket, ChunkRenderer.AddTesselatedChunk, MeshDataPoolManager.AddModel",
        "Optimum, Tungsten and P5WorldgenEngine had no Harmony patch on any of these targets in that snapshot (Optimum and P5 replace worldgen and renderer code without Harmony; see hooks[].other_patches live)",
    };

    public static JsonObject Disabled(string reason) => new()
    {
        ["schema_version"] = BuildInfo.SchemaVersion,
        ["enabled"] = false,
        ["reason"] = reason,
    };

    public static JsonObject Build(BundleClock clock, double durationMs, StageStats[] stats, (long Parts, long Vertices, long Indices, long Bytes) upload,
        IReadOnlyList<HookInfo> hooks, JsonObject queue, string? installNote)
    {
        var stages = new JsonObject();
        foreach (var def in ChunkStages.All)
        {
            var hook = hooks.FirstOrDefault(h => h.Stages.Contains(def.Id));
            var node = new JsonObject
            {
                ["side"] = def.Side,
                ["target"] = def.Target,
                ["meaning"] = def.Meaning,
            };
            if (hook == null || !hook.Installed)
            {
                node["hooked"] = false;
                node["calls"] = null;
                node["total_ms"] = null;
                node["mean_ms"] = null;
                node["p95_ms"] = null;
                node["max_ms"] = null;
                node["reason"] = hook?.FailureReason ?? "hook was not installed (chunk pipeline timers off in config, or this side did not start in this process)";
            }
            else
            {
                var s = def.Id < stats.Length ? stats[def.Id] : null;
                long calls = s?.Count ?? 0;
                node["hooked"] = true;
                node["calls"] = calls;
                if (s == null || calls == 0)
                {
                    node["total_ms"] = null;
                    node["mean_ms"] = null;
                    node["p95_ms"] = null;
                    node["max_ms"] = null;
                    node["reason"] = "hook installed, no calls in the capture window (stage not exercised, or the JIT inlined the caller around the patched method; check a busier scene before reading this as zero cost)";
                }
                else
                {
                    node["total_ms"] = Math.Round(s.TotalMs, 3);
                    node["mean_ms"] = Math.Round(s.TotalMs / calls, 4);
                    node["p95_ms"] = Math.Round(s.P95Ms(out int used), 4);
                    node["max_ms"] = Math.Round(s.MaxMs, 3);
                    node["percentile_samples"] = used;
                    node["percentile_window"] = calls > StageStats.RingSize ? "most recent " + StageStats.RingSize + " calls" : "all calls";
                }
            }
            stages[def.Name] = node;
        }

        var hookNotes = new JsonArray(hooks.Select(h => (JsonNode?)new JsonObject
        {
            ["target"] = h.Target,
            ["stages"] = new JsonArray(h.Stages.Select(i => (JsonNode?)ChunkStages.All[i].Name).ToArray()),
            ["installed"] = h.Installed,
            ["failure_reason"] = h.FailureReason,
            ["note"] = h.Note,
            ["other_patches"] = new JsonArray(h.OtherPatches.Select(p => (JsonNode?)new JsonObject { ["owner"] = p.Owner, ["kind"] = p.Kind, ["patch_method"] = p.PatchMethod }).ToArray()),
        }).ToArray());

        return new JsonObject
        {
            ["schema_version"] = BuildInfo.SchemaVersion,
            ["enabled"] = true,
            ["label"] = "measured",
            ["capture"] = new JsonObject { ["start_local"] = clock.StartIso, ["duration_ms"] = Math.Round(durationMs, 1) },
            ["timing_note"] = "wall time inside the patched method, in ms, on whichever thread calls it. Our prefix runs first and our postfix last, so other mods' patches on the same method are inside the number, as the pipeline pays for them. Lock waits are inside too. Percentiles cover the most recent " + StageStats.RingSize + " calls of a stage.",
            ["stages"] = stages,
            ["upload"] = new JsonObject
            {
                ["label"] = "measured counts, estimated bytes",
                ["mesh_parts_uploaded"] = upload.Parts,
                ["vertices_uploaded"] = upload.Vertices,
                ["indices_uploaded"] = upload.Indices,
                ["bytes_uploaded_estimated"] = upload.Bytes,
                ["bytes_note"] = "28 bytes per vertex (xyz, uv, rgba, flags) + 4 per index + custom float/int/short/byte parts, read from the MeshData handed to MeshDataPoolManager.AddModel; the real GPU upload size also depends on the pool's interleave strides",
                ["chunk_uploads"] = stats.Length > ChunkStages.UploadChunk ? stats[ChunkStages.UploadChunk].Count : 0,
            },
            ["queue_depth"] = queue,
            ["hook_notes"] = hookNotes,
            ["install_note"] = installNote,
            ["runtime_db_snapshot"] = new JsonArray(RuntimeDbSnapshot.Select(l => (JsonNode?)l).ToArray()),
            ["not_hooked"] = new JsonObject
            {
                ["server_worldgen_queue_depth"] = null,
                ["server_worldgen_queue_depth_reason"] = "no public counter; the request queue is an internal ChunkServerThread field and polling it from a sampler thread is not thread safe",
                ["worldgen_per_generator"] = null,
                ["worldgen_per_generator_reason"] = "generators are delegates registered by each mod; wrapping every delegate would patch each mod's code. The per-pass totals above include all of them",
            },
        };
    }
}
