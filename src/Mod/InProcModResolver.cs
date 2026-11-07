using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SparkVS.Core;
using Vintagestory.API.Common;

namespace SparkVS;

/// <summary>
/// Maps a heap snapshot module to a mod using what the running game knows: the assembly of each mod's ModSystem and each
/// mod's unpack folder (the same sources as SparkVSSystem.FillAttribution). Build it on the main thread; Resolve only reads
/// immutable dictionaries, so it is safe on any thread afterwards. Nothing is guessed: an unmapped module is Unknown.
/// </summary>
public sealed class InProcModResolver : IModResolver
{
    static readonly Owner SharedOwner = new(OwnerKind.Shared, MemoryReport.SharedId, MemoryReport.SharedId);
    static readonly Owner UnknownOwner = new(OwnerKind.Unknown, MemoryReport.UnknownId, MemoryReport.UnknownId);

    readonly Dictionary<string, Owner> modByAssembly = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> gameAssemblies = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Dir, Owner Owner)> folders = new();
    readonly string gameDir;

    public string Describe { get; private set; } = "";

    InProcModResolver(string gameDir)
    {
        this.gameDir = gameDir;
    }

    /// <summary>Main thread only: reads api.ModLoader.Mods and the loaded assemblies.</summary>
    public static InProcModResolver Build(ICoreAPI api)
    {
        string gameDir = NormalizeDir(AppContext.BaseDirectory);
        var r = new InProcModResolver(gameDir);
        int mods = 0;
        foreach (var mod in api.ModLoader.Mods)
        {
            mods++;
            var owner = new Owner(OwnerKind.Mod, mod.Info.ModID, string.IsNullOrEmpty(mod.Info.Name) ? mod.Info.ModID : mod.Info.Name);
            foreach (var system in mod.Systems)
            {
                var assembly = system.GetType().Assembly;
                if (!IsUnder(SafeLocation(assembly), gameDir)) r.modByAssembly[assembly.GetName().Name ?? ""] = owner;
            }
            // FolderPath is on ModContainer, not on the public Mod type.
            if (mod.GetType().GetField("FolderPath")?.GetValue(mod) is string folder && folder.Length > 0)
                r.folders.Add((NormalizeDir(folder), owner));
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string location = SafeLocation(assembly);
            string name = assembly.GetName().Name ?? "";
            if (name.Length == 0) continue;
            if (location.Length > 0 && IsUnder(location, gameDir))
            {
                r.gameAssemblies.Add(name);
                continue;
            }
            if (r.modByAssembly.ContainsKey(name)) continue;
            bool owned = false;
            if (location.Length > 0)
                foreach (var (dir, owner) in r.folders)
                {
                    if (IsUnder(location, dir))
                    {
                        r.modByAssembly[name] = owner;
                        owned = true;
                        break;
                    }
                }
            if (!owned && !assembly.IsDynamic && Attribution.ShipsWithGame(name, gameDir, File.Exists)) r.gameAssemblies.Add(name);
        }

        r.Describe = $"in-process: api.ModLoader.Mods ({mods} mods, {r.modByAssembly.Count} assemblies mapped)";
        return r;
    }

    /// <summary>Module is a full path or a simple name; the match is on the file name without extension, case-insensitive.</summary>
    public Owner Resolve(string module)
    {
        if (string.IsNullOrEmpty(module)) return UnknownOwner;
        string norm = module.Replace('/', '\\');
        string name = Path.GetFileNameWithoutExtension(norm);
        if (name.Length == 0) return UnknownOwner;

        if (modByAssembly.TryGetValue(name, out var mod)) return mod;
        if (norm.IndexOf('\\') >= 0)
            foreach (var (dir, owner) in folders)
                if (IsUnder(norm, dir)) return owner;
        if (gameAssemblies.Contains(name)) return SharedOwner;
        if (norm.IndexOf('\\') >= 0 && IsUnder(norm, gameDir)) return SharedOwner;
        if (Attribution.IsRuntimeModule(name) || name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)) return SharedOwner;
        return UnknownOwner;
    }

    static string SafeLocation(Assembly assembly)
    {
        try { return assembly.IsDynamic ? "" : assembly.Location; }
        catch (NotSupportedException) { return ""; }
    }

    static string NormalizeDir(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    static bool IsUnder(string file, string dir) => file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
}
