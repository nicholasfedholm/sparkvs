using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SparkVS.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SparkVS;

/// <summary>What the server side knows, left here by its tick listener for the client side to read.</summary>
internal static class ServerInfo
{
    public static volatile string? WorldName;
    static volatile object? seed;
    public static int? Seed { get => (int?)seed; set => seed = value; }
    public static volatile Dictionary<string, string?>? WorldConfig;
    public static volatile int[]? MapSize;
    public static volatile ICoreServerAPI? Api;
}

internal sealed class ModEntry
{
    public string ModId = "", Version = "", SourcePath = "", SourceType = "";
    public HashSet<string> Sides = new(StringComparer.Ordinal);
}

/// <summary>Everything in context.json. API reads happen on the main thread; hashing and OS queries happen later off thread.</summary>
internal static class ContextCollector
{
    static readonly string[] WorldConfigKeys = { "worldHeight", "landcover", "oceanscale", "upheavelCommonness", "landformScale", "spawnRadius", "worldWidth", "worldLength" };

    static readonly string[] IntKeys = { "viewDistance", "fieldOfView", "maxFps", "vsyncMode", "gameWindowMode", "screenWidth", "screenHeight", "graphicsPresetId", "shadowMapQuality", "ssaoQuality", "godRays", "cloudRenderMode", "mipmapLevel", "particleLevel", "maxDynamicLights", "maxAnimatedElements", "chunkVerticesUploadRateLimiter", "optimizeRamMode", "maxQuadParticles", "maxCubeParticles", "maxAsyncQuadParticles", "maxAsyncCubeParticles", "maxTextureAtlasWidth", "maxTextureAtlasHeight", "modelDataPoolMaxVertexSize", "modelDataPoolMaxIndexSize", "modelDataPoolMaxParts" };
    static readonly string[] BoolKeys = { "smoothShadows", "bloom", "fxaa", "occlusionculling", "wavingStuff", "liquidFoamAndShinyEffect", "dynamicColorGrading", "highQualityAnimations", "renderParticles", "transparentRenderPass", "allowSSBOs", "ambientParticles", "offThreadMipMaps" };
    static readonly string[] FloatKeys = { "ssaa", "lodBias", "lodBiasFar", "ambientBloomLevel", "gameTickFrameRate" };
    static readonly string[] StringKeys = { "glContextVersion" };

    static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Hash)> HashCache = new();

    /// <summary>Player position and loaded counts at one moment. Main thread only.</summary>
    public static JsonObject State(ICoreClientAPI capi, BundleClock clock)
    {
        double t = clock.MsSince(Stopwatch.GetTimestamp());
        var node = new JsonObject { ["ts_local"] = clock.IsoAt(t), ["t_ms"] = Math.Round(t, 1) };
        try
        {
            var pos = capi.World.Player?.Entity?.Pos;
            node["player_pos"] = pos == null ? null : new JsonObject { ["x"] = Math.Round(pos.X, 2), ["y"] = Math.Round(pos.Y, 2), ["z"] = Math.Round(pos.Z, 2) };
        }
        catch (Exception ex) { node["player_pos"] = null; node["player_pos_error"] = ex.Message; }
        try { node["loaded_chunks_client"] = capi.World.LoadedChunkIndices.Length; } catch (Exception ex) { node["loaded_chunks_client"] = null; node["loaded_chunks_client_error"] = ex.Message; }
        try { node["loaded_entities_client"] = ((IClientWorldAccessor)capi.World).LoadedEntities.Count; } catch (Exception ex) { node["loaded_entities_client"] = null; node["loaded_entities_client_error"] = ex.Message; }
        var server = ServerInfo.Api;
        if (server != null)
        {
            // Counts only, read from another thread: approximate by nature.
            try { node["loaded_chunks_server"] = server.WorldManager.AllLoadedChunks.Count; } catch { node["loaded_chunks_server"] = null; }
            try { node["loaded_entities_server"] = server.World.LoadedEntities.Count; } catch { node["loaded_entities_server"] = null; }
        }
        else
        {
            node["loaded_chunks_server"] = null;
            node["loaded_entities_server"] = null;
        }
        return node;
    }

    /// <summary>
    /// Every mod the process loaded on any side. The client loader omits server-only mods (tungsten), so the integrated server's
    /// loader and the mods the TryRunModPhase hook saw are merged in; each entry says which sides loaded it.
    /// </summary>
    public static List<ModEntry> ModList(ICoreClientAPI capi)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string? version, string? path, string type, string side)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!byId.TryGetValue(id, out var e)) byId[id] = e = new ModEntry { ModId = id, Version = version ?? "", SourcePath = path ?? "", SourceType = type };
            e.Sides.Add(side);
        }
        foreach (var mod in capi.ModLoader.Mods)
            Add(mod.Info.ModID, mod.Info.Version, mod.SourcePath, mod.SourceType.ToString(), "client");
        try
        {
            var server = ServerInfo.Api;
            if (server != null)
                foreach (var mod in server.ModLoader.Mods)
                    Add(mod.Info.ModID, mod.Info.Version, mod.SourcePath, mod.SourceType.ToString(), "server");
        }
        catch { }
        foreach (var seen in LoadPhaseHook.SeenMods())
        {
            string[] sides;
            lock (seen.Sides) sides = seen.Sides.ToArray();
            foreach (var side in sides)
                Add(seen.ModId, seen.Version, seen.SourcePath, seen.SourceType, side);
        }
        return byId.Values.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>World name, seed, world config headline values. Main thread.</summary>
    public static JsonObject World(ICoreClientAPI capi)
    {
        var config = new JsonObject();
        foreach (var key in WorldConfigKeys)
        {
            string? value = null;
            try { value = capi.World.Config?.GetAsString(key, null!); } catch { }
            string source = "client World.Config";
            if (value == null && ServerInfo.WorldConfig != null && ServerInfo.WorldConfig.TryGetValue(key, out var serverValue)) { value = serverValue; source = "server World.Config"; }
            config[key] = value == null ? null : new JsonObject { ["value"] = value, ["source"] = source };
        }
        int[]? server = ServerInfo.MapSize;
        var node = new JsonObject
        {
            ["name"] = ServerInfo.WorldName,
            ["name_source"] = ServerInfo.WorldName == null ? "unavailable: no integrated server in this process, or its tick listener had not fired" : "integrated server IWorldManagerAPI.CurrentWorldName",
            ["seed"] = SafeSeed(capi),
            ["savegame_identifier"] = capi.World.SavegameIdentifier,
            ["single_player"] = capi.IsSinglePlayer,
            ["worldconfig_headline"] = config,
            ["map_size_blocks"] = new JsonObject
            {
                ["x"] = SafeInt(() => capi.World.BlockAccessor.MapSizeX) ?? server?[0],
                ["y"] = SafeInt(() => capi.World.BlockAccessor.MapSizeY) ?? server?[1],
                ["z"] = SafeInt(() => capi.World.BlockAccessor.MapSizeZ) ?? server?[2],
            },
        };
        return node;
    }

    static int? SafeSeed(ICoreClientAPI capi) { try { return capi.World.Seed; } catch { return ServerInfo.Seed; } }

    static int? SafeInt(Func<int> read) { try { return read(); } catch { return null; } }

    /// <summary>Live values of the render relevant client settings. Main thread.</summary>
    public static JsonObject Graphics(ICoreClientAPI capi)
    {
        var node = new JsonObject();
        void Add<T>(string key, System.Func<string, T> get, System.Func<string, bool> exists)
        {
            try { node[key] = exists(key) ? JsonValue.Create(get(key)) : null; }
            catch { node[key] = null; }
        }
        foreach (var k in IntKeys) Add<int>(k, key => capi.Settings.Int.Get(key, 0), key => capi.Settings.Int.Exists(key));
        foreach (var k in BoolKeys) Add<bool>(k, key => capi.Settings.Bool.Get(key, false), key => capi.Settings.Bool.Exists(key));
        foreach (var k in FloatKeys) Add<float>(k, key => capi.Settings.Float.Get(key, 0f), key => capi.Settings.Float.Exists(key));
        foreach (var k in StringKeys) Add<string>(k, key => capi.Settings.String.Get(key, ""), key => capi.Settings.String.Exists(key));
        return new JsonObject { ["source"] = "ICoreClientAPI.Settings, live values at capture start", ["values"] = node };
    }

    /// <summary>Mod ids the client settings file lists as disabled (read from disk, read only).</summary>
    public static JsonArray DisabledModIds()
    {
        var result = new JsonArray();
        try
        {
            string path = Path.Combine(GamePaths.DataPath, "clientsettings.json");
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["stringListSettings"]?["disabledMods"] is JsonArray arr)
                foreach (var n in arr) if (n != null) result.Add(n.GetValue<string>());
        }
        catch { }
        return result;
    }

    public static JsonObject Versions(ICoreClientAPI capi, string sparkVsVersion)
    {
        string? optimum = null;
        string optimumNote = "";
        try
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "Optimum.dll");
            if (File.Exists(dll))
            {
                var info = FileVersionInfo.GetVersionInfo(dll);
                optimum = info.ProductVersion ?? info.FileVersion;
            }
            else optimumNote = "Optimum.dll not found next to the running game, so this is not the Optimum fork";
        }
        catch (Exception ex) { optimumNote = ex.Message; }
        return new JsonObject
        {
            ["game"] = GameVersion.OverallVersion,
            ["optimum_fork"] = optimum,
            ["optimum_fork_note"] = optimumNote.Length == 0 ? null : optimumNote,
            ["sparkvs"] = sparkVsVersion,
            ["dotnet_runtime"] = RuntimeInformation.FrameworkDescription,
        };
    }

    /// <summary>Mod manifest with zip SHA256. Slow for many large zips, so call it off the main thread; results are cached by size and mtime.</summary>
    public static JsonArray ModManifest(List<ModEntry> mods)
    {
        var arr = new JsonArray();
        foreach (var m in mods)
        {
            string? hash = null;
            long? bytes = null;
            string? note = null;
            try
            {
                if (File.Exists(m.SourcePath))
                {
                    var fi = new FileInfo(m.SourcePath);
                    bytes = fi.Length;
                    if (HashCache.TryGetValue(m.SourcePath, out var c) && c.Length == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) hash = c.Hash;
                    else
                    {
                        using var stream = new FileStream(m.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        HashCache[m.SourcePath] = (fi.Length, fi.LastWriteTimeUtc.Ticks, hash);
                    }
                }
                else note = Directory.Exists(m.SourcePath) ? "unpacked folder, no single file to hash" : "source path not found";
            }
            catch (Exception ex) { note = ex.GetType().Name + ": " + ex.Message; }
            arr.Add(new JsonObject
            {
                ["modid"] = m.ModId,
                ["version"] = m.Version,
                ["enabled"] = true,
                ["sides_loaded"] = new JsonArray(m.Sides.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["source_type"] = m.SourceType,
                ["file_name"] = string.IsNullOrEmpty(m.SourcePath) ? null : Path.GetFileName(m.SourcePath),
                ["zip_bytes"] = bytes,
                ["zip_sha256"] = hash,
                ["note"] = note,
            });
        }
        return arr;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetPhysicallyInstalledSystemMemory(out long kilobytes);

    /// <summary>CPU, GPU, driver, RAM, OS build. Registry and DXGI reads, off thread.</summary>
    public static JsonObject System()
    {
        var node = new JsonObject();
        string? cpu = null;
        try { cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string; } catch { }
        node["cpu_model"] = cpu?.Trim();
        node["cpu_logical_cores"] = Environment.ProcessorCount;

        var gpus = new JsonArray();
        try
        {
            using var reader = new GpuMemoryReader(Environment.ProcessId);
            var drivers = DisplayDrivers();
            foreach (var name in reader.AdapterNames())
            {
                var match = drivers.FirstOrDefault(d => string.Equals(d.Desc, name, StringComparison.OrdinalIgnoreCase));
                gpus.Add(new JsonObject { ["model"] = name, ["driver_version"] = match.Version, ["driver_date"] = match.Date });
            }
        }
        catch (Exception ex) { node["gpu_error"] = ex.GetType().Name + ": " + ex.Message; }
        node["gpus"] = gpus;
        node["gpu_note"] = "Adapters DXGI lists, hardware only. Which one the game renders on is shown by the adapter field in gpu-memory.jsonl.";

        long? ram = null;
        try { if (GetPhysicallyInstalledSystemMemory(out long kb)) ram = kb * 1024; } catch { }
        ram ??= GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        node["total_ram_bytes"] = ram;

        string? display = null, ubr = null;
        try
        {
            display = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
            ubr = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null)?.ToString();
        }
        catch { }
        var v = Environment.OSVersion.Version;
        node["os_build"] = $"{v.Major}.{v.Minor}.{v.Build}" + (ubr != null ? "." + ubr : "");
        node["os_display_version"] = display;
        node["os_description"] = RuntimeInformation.OSDescription;
        return node;
    }

    static List<(string Desc, string? Version, string? Date)> DisplayDrivers()
    {
        var result = new List<(string, string?, string?)>();
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return result;
            foreach (var name in cls.GetSubKeyNames())
            {
                using var sub = cls.OpenSubKey(name);
                if (sub?.GetValue("DriverDesc") is string desc)
                    result.Add((desc, sub.GetValue("DriverVersion") as string, sub.GetValue("DriverDate") as string));
            }
        }
        catch { }
        return result;
    }

    public static string Iso(DateTime t) => BundleClock.Iso(t);

    public static string N(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
