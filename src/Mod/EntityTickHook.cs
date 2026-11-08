using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using HarmonyLib;
using SparkVS.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace SparkVS;

/// <summary>
/// Item 7: per entity code server tick timing. A Harmony transpiler on Vintagestory.Server.ServerSystemEntitySimulation.TickEntities
/// replaces the callvirt Entity.OnGameTick(float) with a call to <see cref="Timed"/>, which times the call. Applied when a capture starts,
/// removed when the capture window ends. Nothing exists outside a capture.
/// </summary>
internal static class EntityTickHook
{
    const string OwnerId = "sparkvs.entityticks";
    const string TargetType = "Vintagestory.Server.ServerSystemEntitySimulation";
    const string TargetMethod = "TickEntities";

    static Harmony? harmony;
    static MethodBase? patched;
    static EntityTickAccumulator? acc;
    static long startStamp, endStamp;

    public static bool Active => patched != null;
    public static string? TargetName { get; private set; }
    public static double OverheadNs { get; private set; }
    public static string? FailureReason { get; private set; }
    public static EntityTickAccumulator? Accumulator => acc;
    public static double WindowSeconds => endStamp > startStamp ? (endStamp - startStamp) / (double)Stopwatch.Frequency : startStamp > 0 ? (Stopwatch.GetTimestamp() - startStamp) / (double)Stopwatch.Frequency : 0;

    /// <summary>The wrapper the transpiler calls in place of Entity.OnGameTick. Same stack signature: (Entity, float).</summary>
    public static void Timed(Entity entity, float dt)
    {
        var a = acc;
        if (a == null || !a.AcceptsThread(Environment.CurrentManagedThreadId)) { entity.OnGameTick(dt); return; }
        long t0 = Stopwatch.GetTimestamp();
        try { entity.OnGameTick(dt); }
        finally { a.Add(entity.Properties, Stopwatch.GetTimestamp() - t0); }
    }

    /// <summary>Calibrate, then patch. Returns null on success, else the reason it did not patch.</summary>
    public static string? Begin()
    {
        if (patched != null) return "an entity tick patch is already active in this process";
        FailureReason = null;
        OverheadNs = 0;
        acc = null;
        try
        {
            var type = AccessTools.TypeByName(TargetType);
            if (type == null) return FailureReason = "type " + TargetType + " not found";
            var method = AccessTools.Method(type, TargetMethod);
            if (method == null) return FailureReason = "method " + TargetMethod + " not found on " + TargetType;
            var instr = PatchProcessor.GetOriginalInstructions(method);
            if (!instr.Any(IsOnGameTickCall)) return FailureReason = TargetType + "." + TargetMethod + " contains no callvirt Entity.OnGameTick(float) (the method may have been rewritten)";

            OverheadNs = EntityTickAccumulator.Calibrate();
            acc = new EntityTickAccumulator();
            startStamp = Stopwatch.GetTimestamp();
            endStamp = 0;
            harmony ??= new Harmony(OwnerId);
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(EntityTickHook).GetMethod(nameof(Transpile), BindingFlags.Static | BindingFlags.NonPublic)));
            patched = method;
            TargetName = type.FullName + "." + TargetMethod;
            return null;
        }
        catch (Exception ex)
        {
            acc = null;
            return FailureReason = ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>Remove the patch and freeze the accumulator. Returns null on success, else the reason.</summary>
    public static string? End()
    {
        if (patched == null) return null;
        try
        {
            harmony!.Unpatch(patched, HarmonyPatchType.Transpiler, OwnerId);
            return null;
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            patched = null;
            endStamp = Stopwatch.GetTimestamp();
            acc?.Freeze();
        }
    }

    static bool IsOnGameTickCall(CodeInstruction ci) =>
        ci.opcode == OpCodes.Callvirt && ci.operand is MethodInfo m && m.Name == "OnGameTick"
        && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float)
        && typeof(Entity).IsAssignableFrom(m.DeclaringType);

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var wrapper = typeof(EntityTickHook).GetMethod(nameof(Timed), BindingFlags.Static | BindingFlags.Public)!;
        foreach (var ci in instructions)
        {
            if (IsOnGameTickCall(ci))
            {
                // Keep labels/blocks on the replacement so branch targets and try blocks stay valid.
                yield return new CodeInstruction(OpCodes.Call, wrapper) { labels = ci.labels, blocks = ci.blocks };
            }
            else yield return ci;
        }
    }

    public static (string Code, string Domain) Resolve(object key)
    {
        if (key is EntityProperties p && p.Code != null) return (p.Code.ToString(), p.Code.Domain ?? "game");
        return ("(no properties)", "");
    }

    public static JsonObject BuildFile(string? skipReason)
    {
        if (acc == null) return EntityTickReport.Disabled(skipReason ?? FailureReason ?? "entity tick timing did not run");
        return EntityTickReport.Build(acc, Resolve, Stopwatch.Frequency, OverheadNs, WindowSeconds, TargetName ?? TargetType + "." + TargetMethod,
            "transpiler replaces callvirt Entity.OnGameTick(float) with SparkVS.EntityTickHook.Timed; applied at capture start, removed at capture end");
    }
}
