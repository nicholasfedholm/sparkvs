using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using SparkVS.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SparkVS;

internal sealed class HookEntry
{
    public HookInfo Info { get; init; } = new();
    public MethodBase? Method { get; set; }
}

/// <summary>
/// Harmony timers on the chunk pipeline. The patch bodies are static, take only value types and allocate nothing; with no
/// capture running a prefix costs one volatile read and a postfix returns on a zero state. Server targets are installed
/// from StartServerSide, client targets from StartClientSide, so a client only patch never lands on a dedicated server.
/// A patch that cannot be resolved or applied is recorded with its reason and the stage reports null.
/// </summary>
internal static class ChunkPipelineHooks
{
    public const string OwnerId = "sparkvs.chunkpipeline";
    const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static readonly object gate = new();
    static readonly List<HookEntry> entries = new();
    static Harmony? harmony;

    public static HookEntry[] Entries { get { lock (gate) return entries.ToArray(); } }

    public static void Install(string side, ILogger logger)
    {
        lock (gate)
        {
            harmony ??= new Harmony(OwnerId);
            foreach (var d in Defs)
            {
                if (d.Side != side) continue;
                if (entries.Any(e => e.Info.Target == d.Target)) continue;
                var entry = new HookEntry { Info = new HookInfo { Stages = d.Stages, Target = d.Target, Note = d.Note } };
                entries.Add(entry);
                try { Apply(entry, d); }
                catch (Exception ex) { entry.Info.Installed = false; entry.Info.FailureReason = "patch failed: " + ex.GetType().Name + ": " + ex.Message; }
                if (!entry.Info.Installed) logger.Warning("SparkVS chunk pipeline: " + d.Target + " not hooked: " + entry.Info.FailureReason);
            }
        }
    }

    static readonly HookTarget[] Defs = ChunkHookTargets.All;

    static void Apply(HookEntry entry, HookTarget d)
    {
        var type = AccessTools.TypeByName(d.TypeName);
        if (type == null) { entry.Info.FailureReason = "type " + d.TypeName + " not found in the running game (renamed or removed in this fork)"; return; }
        var candidates = type.GetMethods(AnyMember).Where(m => m.Name == d.Method && m.GetParameters().Length == d.ParamCount).ToList();
        var all = candidates;
        if (d.ParamTypes != null && candidates.Count > 1)
            candidates = candidates.Where(m => ParamTypesMatch(m, d.ParamTypes)).ToList();
        if (candidates.Count == 0 && all.Count > 1) { entry.Info.FailureReason = "method " + d.Method + " has " + all.Count + " overloads with " + d.ParamCount + " parameters and none takes " + string.Join(", ", d.ParamTypes ?? Array.Empty<string>()) + " (found: " + Signatures(all) + ")"; return; }
        if (candidates.Count == 0) { entry.Info.FailureReason = "method " + d.TypeName + "." + d.Method + " with " + d.ParamCount + " parameters not found (signature changed in this fork)"; return; }
        if (candidates.Count > 1) { entry.Info.FailureReason = "method " + d.Method + " with " + d.ParamCount + " parameters is ambiguous (" + candidates.Count + " overloads: " + Signatures(candidates) + ")"; return; }
        var target = candidates[0];
        var pre = typeof(Hooks).GetMethod(d.Pre, BindingFlags.Public | BindingFlags.Static)!;
        var post = typeof(Hooks).GetMethod(d.Post, BindingFlags.Public | BindingFlags.Static)!;
        harmony!.Patch(target, prefix: new HarmonyMethod(pre) { priority = Priority.First }, postfix: new HarmonyMethod(post) { priority = Priority.Last });
        entry.Method = target;
        entry.Info.Installed = true;
    }

    /// <summary>True when every parameter's full type name (or simple name, if the pinned name has no namespace) equals the pinned one, in order.</summary>
    internal static bool ParamTypesMatch(MethodBase m, string[] pinned)
    {
        var ps = m.GetParameters();
        if (ps.Length != pinned.Length) return false;
        for (int i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            if (t.IsByRef) t = t.GetElementType()!;
            if (t.FullName != pinned[i] && t.Name != pinned[i]) return false;
        }
        return true;
    }

    static string Signatures(IEnumerable<MethodInfo> ms) => string.Join(" | ", ms.Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)) + ")"));

    /// <summary>Reads the live patches other owners have on each hooked method. Called at report time.</summary>
    public static void RefreshOtherPatches()
    {
        foreach (var e in Entries)
        {
            e.Info.OtherPatches.Clear();
            if (e.Method == null) continue;
            try
            {
                var info = Harmony.GetPatchInfo(e.Method);
                if (info == null) continue;
                void Add(string kind, IEnumerable<Patch> list)
                {
                    foreach (var p in list)
                        if (p.owner != OwnerId)
                            e.Info.OtherPatches.Add((p.owner, kind, (p.PatchMethod.DeclaringType?.FullName ?? "?") + "." + p.PatchMethod.Name));
                }
                Add("prefix", info.Prefixes);
                Add("postfix", info.Postfixes);
                Add("transpiler", info.Transpilers);
                Add("finalizer", info.Finalizers);
            }
            catch (Exception ex) { e.Info.Note = (e.Info.Note ?? "") + " (could not read other patches: " + ex.GetType().Name + ")"; }
        }
    }

    public static Func<QueueSnapshot?>? CreateQueueReader(out string? failure)
    {
        failure = null;
        try
        {
            var t = AccessTools.TypeByName("Vintagestory.Client.RuntimeStats");
            var f1 = t?.GetField("chunksAwaitingTesselation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var f2 = t?.GetField("chunksAwaitingPooling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f1 == null || f2 == null || f1.FieldType != typeof(int) || f2.FieldType != typeof(int))
            {
                failure = "Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation/chunksAwaitingPooling int fields not found in this game build";
                return null;
            }
            return () => new QueueSnapshot((int)f1.GetValue(null)!, (int)f2.GetValue(null)!);
        }
        catch (Exception ex)
        {
            failure = "queue counters unreadable: " + ex.GetType().Name;
            return null;
        }
    }

    /// <summary>Patch bodies. Public static, value type parameters only, no allocation.</summary>
    public static class Hooks
    {
        public static void Pre(out long __state) => __state = ChunkTimers.Start();

        public static void PostGen(int forPass, long __state) => ChunkTimers.Stop(ChunkTimers.WorldgenStageForPass(forPass), __state);
        public static void PostPopulate(long __state) => ChunkTimers.Stop(ChunkStages.WorldgenPopulate, __state);
        public static void PostLoadColumn(object __result, long __state)
        {
            if (__result != null) ChunkTimers.Stop(ChunkStages.StreamLoadColumn, __state);
        }
        public static void PostGetChunk(byte[] __result, long __state) => ChunkTimers.Stop(__result == null ? ChunkStages.StreamReadMiss : ChunkStages.StreamReadDb, __state);
        public static void PostFromBytes(long __state) => ChunkTimers.Stop(ChunkStages.StreamDeserialize, __state);
        public static void PostDecompress(long __state) => ChunkTimers.Stop(ChunkStages.StreamDecompress, __state);
        public static void PostReceive(long __state) => ChunkTimers.Stop(ChunkStages.ClientReceive, __state);
        public static void PostTess(bool priority, int __result, long __state)
        {
            if (__result > 0) ChunkTimers.Stop(priority ? ChunkStages.TesselatePriority : ChunkStages.TesselateNormal, __state);
        }
        public static void PostUploadChunk(long __state) => ChunkTimers.Stop(ChunkStages.UploadChunk, __state);

        public static void PreUploadMesh(MeshData modeldata, out long __state)
        {
            __state = ChunkTimers.Start();
            if (__state != 0 && modeldata != null) ChunkTimers.AddUpload(modeldata.VerticesCount, modeldata.IndicesCount, EstimateBytes(modeldata));
        }
        public static void PostUploadMesh(long __state) => ChunkTimers.Stop(ChunkStages.UploadMesh, __state);

        /// <summary>28 bytes per vertex (xyz 12, uv 8, rgba 4, flags 4), 4 per index, plus the custom parts.</summary>
        public static long EstimateBytes(MeshData m)
        {
            long bytes = m.VerticesCount * 28L + m.IndicesCount * 4L;
            if (m.CustomFloats != null) bytes += m.CustomFloats.Count * 4L;
            if (m.CustomInts != null) bytes += m.CustomInts.Count * 4L;
            if (m.CustomShorts != null) bytes += m.CustomShorts.Count * 2L;
            if (m.CustomBytes != null) bytes += m.CustomBytes.Count;
            return bytes;
        }
    }

    /// <summary>chunk-pipeline.json for a finished capture.</summary>
    public static JsonObject BuildReport(BundleClock clock, double durationMs, StageStats[] stats, QueueRecorder? queue, string? queueFailure)
    {
        RefreshOtherPatches();
        var hooks = Entries.Select(e => e.Info).ToList();
        var queueNode = queue != null
            ? queue.ToJson("RuntimeStats.chunksAwaitingTesselation and chunksAwaitingPooling, the game's own counters, read at 1 Hz", queueFailure)
            : new JsonObject { ["enabled"] = false, ["reason"] = queueFailure ?? "queue recorder did not start" };
        string install = hooks.Count == 0 ? "no hooks were installed in this process" : hooks.Count(h => h.Installed) + " of " + hooks.Count + " hooks installed";
        return ChunkPipelineWriter.Build(clock, durationMs, stats, ChunkTimers.UploadTotals(), hooks, queueNode, install);
    }
}
