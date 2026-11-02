using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using SparkVS.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SparkVS;

/// <summary>
/// Times each mod's StartPre, Start, AssetsLoaded, AssetsFinalize and StartClientSide/StartServerSide with a Harmony
/// pair on ModLoader.TryRunModPhase. Installed once per process from the ModSystem constructor, which runs before the
/// first phase, so every mod phase after SparkVS is constructed is seen. The config file is read straight from disk here
/// because the game has not handed the mod an API yet.
/// </summary>
internal static class LoadPhaseHook
{
    public const string OwnerId = "sparkvs.loadphases";
    public static readonly LoadPhaseLog Log = new();
    static int installAttempted;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModEntry> seenMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mods the phase hook has seen on either side, with the sides each was seen on.</summary>
    public static ModEntry[] SeenMods() => seenMods.Values.ToArray();

    public static bool FlagInConfigFile(string name)
    {
        try
        {
            string path = Path.Combine(GamePaths.ModConfig, "SparkVS.json");
            if (!File.Exists(path)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind != JsonValueKind.False;
        }
        catch { }
        return true;
    }

    public static void InstallOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref installAttempted, 1) != 0) return;
        if (!FlagInConfigFile("LoadPhases"))
        {
            Log.MarkFailed("LoadPhases is false in SparkVS.json");
            return;
        }
        try
        {
            var type = AccessTools.TypeByName("Vintagestory.Common.ModLoader");
            var method = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "TryRunModPhase" && m.GetParameters().Length == 4);
            if (method == null)
            {
                Log.MarkFailed("Vintagestory.Common.ModLoader.TryRunModPhase(4 params) not found in this game build");
                return;
            }
            var harmony = new Harmony(OwnerId);
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(LoadPhaseHook).GetMethod(nameof(Pre))!) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(LoadPhaseHook).GetMethod(nameof(Post))!) { priority = Priority.Last });
            Log.MarkInstalled();
        }
        catch (Exception ex)
        {
            Log.MarkFailed("patch failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // The enum ModRunPhase lives in the game library; its name comes from __args so this file does not reference that assembly.
    public static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void Post(Mod mod, ICoreAPI api, object[] __args, long __state)
    {
        try
        {
            long end = Stopwatch.GetTimestamp();
            string? phase = __args.Length > 3 ? __args[3]?.ToString() : null;
            string side = api.Side == EnumAppSide.Client ? "client" : "server";
            string? name = phase switch
            {
                "Pre" => "StartPre",
                "Start" => "Start",
                "AssetsLoaded" => "AssetsLoaded",
                "AssetsFinalize" => "AssetsFinalize",
                "Normal" => side == "client" ? "StartClientSide" : "StartServerSide",
                _ => null,
            };
            var seen = seenMods.GetOrAdd(mod.Info.ModID, _ => new ModEntry { ModId = mod.Info.ModID, Version = mod.Info.Version ?? "", SourcePath = mod.SourcePath ?? "", SourceType = mod.SourceType.ToString() });
            lock (seen.Sides) seen.Sides.Add(side);
            if (name != null) Log.Add(mod.Info.ModID, name, side, __state, end);
        }
        catch { }
    }
}
