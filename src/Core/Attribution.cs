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
