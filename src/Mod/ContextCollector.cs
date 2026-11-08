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
    public string ModId = "", Name = "", Version = "", SourcePath = "", SourceType = "";
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
        void Add(string id, string? version, string? path, string type, string side, string? name = null)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!byId.TryGetValue(id, out var e)) byId[id] = e = new ModEntry { ModId = id, Name = name ?? "", Version = version ?? "", SourcePath = path ?? "", SourceType = type };
            else if (e.Name.Length == 0 && name != null) e.Name = name;
            e.Sides.Add(side);
        }
        foreach (var mod in capi.ModLoader.Mods)
            Add(mod.Info.ModID, mod.Info.Version, mod.SourcePath, mod.SourceType.ToString(), "client", mod.Info.Name);
        try
        {
            var server = ServerInfo.Api;
            if (server != null)
                foreach (var mod in server.ModLoader.Mods)
                    Add(mod.Info.ModID, mod.Info.Version, mod.SourcePath, mod.SourceType.ToString(), "server", mod.Info.Name);
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
                ["name"] = m.Name.Length == 0 ? null : m.Name,
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

    static readonly string[] GcAppContextKeys =
    {
        "GCHeapHardLimit", "GCHeapHardLimitPercent", "System.GC.HeapHardLimit", "System.GC.HeapHardLimitPercent",
        "System.GC.Server", "System.GC.Concurrent", "System.GC.RetainVM", "System.GC.ConserveMemory", "GCgen0size",
    };

    static readonly string[] GcEnvPrefixes = { "DOTNET_GC", "COMPlus_GC" };   // compared case-insensitively, so DOTNET_gc* is covered
    static readonly string[] RuntimeEnvNames = { "DOTNET_gcServer", "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun", "COMPlus_gcServer", "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_ReadyToRun" };

    /// <summary>Process runtime facts. No game API needed.</summary>
    public static JsonObject Runtime() => new()
    {
        ["framework_description"] = RuntimeInformation.FrameworkDescription,
        ["environment_version"] = Environment.Version.ToString(),
        ["os"] = RuntimeInformation.OSDescription,
        ["process_arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["processor_count"] = Environment.ProcessorCount,
    };

    /// <summary>GC configuration as the runtime reports it, plus the settings and environment variables that can change it.</summary>
    public static JsonObject Gc()
    {
        var node = new JsonObject();
        void Field(string key, Func<JsonNode?> read)
        {
            try { node[key] = read(); }
            catch (Exception ex) { node[key] = null; node[key + "_error"] = ex.GetType().Name + ": " + ex.Message; }
        }
        Field("is_server_gc", () => global::System.Runtime.GCSettings.IsServerGC);
        Field("latency_mode", () => global::System.Runtime.GCSettings.LatencyMode.ToString());
        Field("lohcompaction_mode", () => global::System.Runtime.GCSettings.LargeObjectHeapCompactionMode.ToString());
        Field("concurrent", () => AppContext.GetData("System.GC.Concurrent")?.ToString());
        Field("total_available_memory_bytes", () => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        Field("high_memory_load_threshold_bytes", () => GC.GetGCMemoryInfo().HighMemoryLoadThresholdBytes);
        var ctx = new JsonObject();
        foreach (var key in GcAppContextKeys)
        {
            try { ctx[key] = AppContext.GetData(key)?.ToString(); }
            catch (Exception ex) { ctx[key] = "error: " + ex.Message; }
        }
        node["appcontext"] = ctx;
        var env = new JsonObject();
        try
        {
            foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            {
                string name = (string)kv.Key;
                if (GcEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || RuntimeEnvNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    env[name] = kv.Value?.ToString();
            }
        }
        catch (Exception ex) { node["env_error"] = ex.Message; }
        node["env"] = env;
        return node;
    }

    public static JsonObject GameVersionNode() => new()
    {
        ["long"] = GameVersion.LongGameVersion,
        ["short"] = GameVersion.ShortGameVersion,
        ["overall"] = GameVersion.OverallVersion,
    };

    /// <summary>
    /// PerfLog files (perf-*.jsonl, perfattrib-*.jsonl in DataPath\PerfLog) that overlap a capture: written during the window, or among the
    /// newest of their kind when the capture started (a running game keeps appending to the file it opened earlier).
    /// </summary>
    public static JsonObject PerfFiles(DateTime windowStart, IReadOnlyCollection<string> newestAtStart)
    {
        string dir = Path.Combine(GamePaths.DataPath, "PerfLog");
        var node = new JsonObject { ["folder"] = dir };
        var files = new JsonArray();
        if (!Directory.Exists(dir))
        {
            node["files"] = files;
            node["note"] = "PerfLog folder does not exist";
            return node;
        }
        var all = new DirectoryInfo(dir).EnumerateFiles("perf*-*.jsonl").Where(f => f.Name.StartsWith("perf-", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("perfattrib-", StringComparison.OrdinalIgnoreCase));
        foreach (var f in all.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool inWindow = f.LastWriteTime >= windowStart.AddSeconds(-2);   // anything modified since the capture began (the capture ends about now)
            bool newest = newestAtStart.Contains(f.Name, StringComparer.OrdinalIgnoreCase);
            if (!inWindow && !newest) continue;
            files.Add(new JsonObject
            {
                ["name"] = f.Name,
                ["bytes"] = f.Length,
                ["mtime_local"] = Iso(f.LastWriteTime),
                ["reason"] = inWindow ? "modified during or after the capture window start" : "newest of its kind when the capture started",
            });
        }
        node["files"] = files;
        return node;
    }

    /// <summary>Names of the newest few perf and perfattrib files right now, taken at capture start.</summary>
    public static List<string> NewestPerfFileNames(int perKind = 2)
    {
        var names = new List<string>();
        try
        {
            string dir = Path.Combine(GamePaths.DataPath, "PerfLog");
            if (!Directory.Exists(dir)) return names;
            foreach (var prefix in new[] { "perf-", "perfattrib-" })
                names.AddRange(new DirectoryInfo(dir).EnumerateFiles(prefix + "*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(perKind).Select(f => f.Name));
        }
        catch { }
        return names;
    }

    /// <summary>Mod list read from a server API only (dedicated server). Same entry shape as ModList.</summary>
    public static List<ModEntry> ServerModList(ICoreServerAPI sapi)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in sapi.ModLoader.Mods)
        {
            if (string.IsNullOrEmpty(mod.Info.ModID)) continue;
            var e = new ModEntry { ModId = mod.Info.ModID, Name = mod.Info.Name ?? "", Version = mod.Info.Version ?? "", SourcePath = mod.SourcePath ?? "", SourceType = mod.SourceType.ToString() };
            e.Sides.Add("server");
            byId[mod.Info.ModID] = e;
        }
        return byId.Values.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Server side world facts for a server bundle. Main (server tick) thread.</summary>
    public static JsonObject ServerWorld(ICoreServerAPI sapi)
    {
        var node = new JsonObject();
        try { node["name"] = Path.GetFileNameWithoutExtension(sapi.WorldManager.CurrentWorldFilepath); } catch (Exception ex) { node["name"] = null; node["name_error"] = ex.Message; }
        try { node["seed"] = sapi.World.Seed; } catch { node["seed"] = null; }
        try { node["savegame_identifier"] = sapi.World.SavegameIdentifier; } catch { node["savegame_identifier"] = null; }
        try
        {
            var config = new JsonObject();
            foreach (var key in WorldConfigKeys) config[key] = sapi.World.Config?.GetAsString(key, null!);
            node["worldconfig_headline"] = config;
        }
        catch { node["worldconfig_headline"] = null; }
        return node;
    }

    /// <summary>Server counts and online players at one moment. Counts only, approximate by nature.</summary>
    public static JsonObject ServerState(ICoreServerAPI sapi, BundleClock clock)
    {
        double t = clock.MsSince(Stopwatch.GetTimestamp());
        var node = new JsonObject { ["ts_local"] = clock.IsoAt(t), ["t_ms"] = Math.Round(t, 1) };
        try { node["loaded_chunks_server"] = sapi.WorldManager.AllLoadedChunks.Count; } catch { node["loaded_chunks_server"] = null; }
        try { node["loaded_entities_server"] = sapi.World.LoadedEntities.Count; } catch { node["loaded_entities_server"] = null; }
        try { node["online_players"] = sapi.World.AllOnlinePlayers.Length; } catch { node["online_players"] = null; }
        return node;
    }

    public static string Iso(DateTime t) => BundleClock.Iso(t);

    public static string N(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
