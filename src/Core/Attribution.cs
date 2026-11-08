using System;
using System.Collections.Generic;

namespace SparkVS.Core;

/// <summary>
/// Maps a frame to an owner. A mod is named only when the running game's mod list said that
/// assembly belongs to it; there is no name matching, so nothing here can guess a mod.
/// </summary>
public sealed class Attribution
{
    public const string Game = "game itself";
    public const string Runtime = ".NET runtime";
    public const string Patched = "harmony patched method";
    public const string Unknown = "unknown";

    readonly Dictionary<string, string> modByAssembly;
    readonly HashSet<string> gameAssemblies;

    public Attribution(IDictionary<string, string> modByAssembly, IEnumerable<string> gameAssemblies)
    {
        this.modByAssembly = new Dictionary<string, string>(modByAssembly, StringComparer.OrdinalIgnoreCase);
        this.gameAssemblies = new HashSet<string>(gameAssemblies, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a file named after the assembly exists in the game folder, its Lib folder or its Mods folder (where the built-in mods live).
    /// For assemblies the game loads from memory, which have no Location to compare with the game folder.
    /// </summary>
    public static bool ShipsWithGame(string assemblyName, string gameDir, Func<string, bool> fileExists)
    {
        if (assemblyName.Length == 0) return false;
        foreach (var sub in new[] { "", "Lib", "Mods" })
            if (fileExists(System.IO.Path.Combine(gameDir, sub, assemblyName + ".dll"))) return true;
        return false;
    }

    public static bool IsRuntimeModule(string module)
    {
        if (module.Length == 0) return false;
        return module.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || module.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || module.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || module.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || module.Equals("coreclr", StringComparison.OrdinalIgnoreCase)
            || module.Equals("clrjit", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDynamic(FrameInfo f)
    {
        return f.Method.Contains("DMD<", StringComparison.Ordinal)
            || f.Method.Contains("_Patch", StringComparison.Ordinal)
            || f.Module.Contains("DynamicMethods", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Owner of one frame on its own terms.</summary>
    public string OwnerOf(FrameInfo f)
    {
        if (IsDynamic(f)) return Patched;
        if (f.Module.Length == 0) return Unknown;
        if (modByAssembly.TryGetValue(f.Module, out var mod)) return mod;
        if (gameAssemblies.Contains(f.Module)) return Game;
        if (IsRuntimeModule(f.Module)) return Runtime;
        return Unknown;
    }

    /// <summary>
    /// Owner of a sample's self time: the first frame up from the leaf that is not runtime code,
    /// so List.Sort called by a mod bills that mod. A stack of only runtime frames bills the runtime.
    /// </summary>
    public string OwnerOfStack(int[] stack, List<FrameInfo> frames)
    {
        foreach (var idx in stack)
        {
            var owner = OwnerOf(frames[idx]);
            if (owner != Runtime) return owner;
        }
        return stack.Length == 0 ? Unknown : Runtime;
    }

    /// <summary>
    /// The frame OwnerOfStack bills: the first frame up from the leaf that is not runtime code.
    /// Returns -1 when the stack is empty or only runtime frames, so callers can bill the runtime.
    /// </summary>
    public int OwnerFrameOfStack(int[] stack, List<FrameInfo> frames)
    {
        foreach (var idx in stack)
            if (OwnerOf(frames[idx]) != Runtime) return idx;
        return -1;
    }
}

public enum AssemblyKind { None, Game, Framework }

/// <summary>
/// Shared classification of a module (a path or a simple assembly name) into a named bucket, used by the memory report in
/// the mod (InProcModResolver) and in sparkx (ExternalModResolver). Game assemblies become "game:Name", framework assemblies
/// "framework:Name". Anything else is None and stays with the resolver (mods) or goes to unknown. Nothing is guessed from
/// partial name matches except the documented prefixes.
/// </summary>
public readonly struct AssemblyBucket
{
    public AssemblyKind Kind { get; }
    public string Assembly { get; }
    public string Bucket { get; }
    public AssemblyBucket(AssemblyKind kind, string assembly)
    {
        Kind = kind;
        Assembly = assembly;
        Bucket = kind == AssemblyKind.Game ? "game:" + assembly : kind == AssemblyKind.Framework ? "framework:" + assembly : "";
    }
}

public static class AssemblyBuckets
{
    /// <summary>Exact game assembly names (canonical casing).</summary>
    public static readonly string[] GameExact =
    {
        "VintagestoryAPI", "VintagestoryLib", "VSEssentials", "VSSurvivalMod", "VSCreativeMod", "Vintagestory", "VintagestoryServer",
        "Optimum", "cairo-sharp", "OpenTK", "protobuf-net", "Newtonsoft.Json", "0Harmony", "SkiaSharp",
    };

    /// <summary>Game assembly name prefixes (Optimum*, OpenTK.*, protobuf-net.*, SkiaSharp.*).</summary>
    public static readonly string[] GamePrefixes = { "Optimum", "OpenTK.", "protobuf-net.", "SkiaSharp." };

    static readonly string[] RuntimeDirs = { "\\dotnet\\shared\\", "\\Microsoft.NETCore.App\\", "\\Microsoft.WindowsDesktop.App\\" };

    /// <summary>Simple assembly name of a module: file name without a .dll or .exe extension.</summary>
    public static string SimpleName(string module)
    {
        if (string.IsNullOrEmpty(module)) return "";
        string f = module.Replace('/', '\\');
        int i = f.LastIndexOf('\\');
        if (i >= 0) f = f.Substring(i + 1);
        if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            f = f.Substring(0, f.Length - 4);
        return f;
    }

    public static bool IsGameName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var g in GameExact) if (name.Equals(g, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var p in GamePrefixes) if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsFrameworkName(string name)
    {
        if (name.Length == 0) return false;
        return name.Equals("System", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
            || name.Equals("coreclr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("clrjit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Order: game names, framework names, runtime directories, then (only when gameDir is given) any path under the game install dir.
    /// Pass gameDir null for the name and runtime-folder rules only, so a resolver can look for mods before the install-dir rule.
    /// </summary>
    public static AssemblyBucket Classify(string module, string? gameDir = null)
    {
        if (string.IsNullOrEmpty(module)) return new AssemblyBucket(AssemblyKind.None, "");
        string path = module.Replace('/', '\\');
        string name = SimpleName(path);
        if (name.Length == 0) return new AssemblyBucket(AssemblyKind.None, "");
        if (IsGameName(name)) return new AssemblyBucket(AssemblyKind.Game, CanonicalGameName(name));
        if (IsFrameworkName(name)) return new AssemblyBucket(AssemblyKind.Framework, name);
        foreach (var d in RuntimeDirs)
            if (path.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) return new AssemblyBucket(AssemblyKind.Framework, name);
        if (!string.IsNullOrEmpty(gameDir) && path.IndexOf('\\') >= 0)
        {
            string dir = gameDir!.Replace('/', '\\').TrimEnd('\\') + "\\";
            if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return new AssemblyBucket(AssemblyKind.Game, name);
        }
        return new AssemblyBucket(AssemblyKind.None, "");
    }

    static string CanonicalGameName(string name)
    {
        foreach (var g in GameExact) if (name.Equals(g, StringComparison.OrdinalIgnoreCase)) return g;
        return name;
    }
}
