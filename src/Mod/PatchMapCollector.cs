using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SparkVS.Core;

namespace SparkVS;

/// <summary>Reads Harmony's view of the process into a plain PatchSnapshot. Allocates freely; it runs once per side, never per frame.</summary>
internal static class PatchMapCollector
{
    const string OwnPrefix = "sparkvs.";
    static readonly object gate = new();
    static PatchSnapshot? clientSnap, serverSnap;

    public static PatchSnapshot? Client { get { lock (gate) return clientSnap; } }
    public static PatchSnapshot? Server { get { lock (gate) return serverSnap; } }

    public static void TakeServer() { var s = Take("server"); lock (gate) serverSnap = s; }
    public static void TakeClient() { var s = Take("client"); lock (gate) clientSnap = s; }

    public static PatchSnapshot Take(string side)
    {
        DateTime now = DateTime.Now;
        try
        {
            var methods = new List<PatchedMethod>();
            foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                var pm = new PatchedMethod { Method = Describe(m) };
                Add(pm, PatchMapBuilder.Prefix, info.Prefixes);
                Add(pm, PatchMapBuilder.Postfix, info.Postfixes);
                Add(pm, PatchMapBuilder.Transpiler, info.Transpilers);
                Add(pm, PatchMapBuilder.Finalizer, info.Finalizers);
                if (pm.Patches.Count > 0) methods.Add(pm);
            }
            return new PatchSnapshot { Side = side, TakenLocal = now, Methods = methods };
        }
        catch (Exception ex)
        {
            return new PatchSnapshot { Side = side, TakenLocal = now, FailureReason = "Harmony read failed: " + ex.GetType().Name + ": " + ex.Message };
        }
    }

    static void Add(PatchedMethod pm, string kind, IEnumerable<Patch> patches)
    {
        foreach (var p in patches)
        {
            string owner = p.owner ?? "(no owner)";
            if (owner.StartsWith(OwnPrefix, StringComparison.Ordinal)) continue;
            pm.Patches.Add(new PatchEntry(owner, kind, (p.PatchMethod.DeclaringType?.FullName ?? "?") + "." + p.PatchMethod.Name));
        }
    }

    static string Describe(MethodBase m) =>
        (m.DeclaringType?.FullName ?? "?") + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";
}
