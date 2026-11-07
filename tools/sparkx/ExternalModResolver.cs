using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SparkVS.Core;

namespace SparkX;

/// <summary>
/// Maps module paths of another process to mods without the game API: modinfo.json beside the loaded DLL
/// (VintagestoryData/Cache/unpack/... for zip mods), then DLL names found in the data folder Mods directory.
/// Never fails because the mapping is missing: unmapped modules go to unknown.
/// </summary>
public sealed class ExternalModResolver : IModResolver
{
    readonly string? installDir;
    readonly Dictionary<string, Owner> modsByDllName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Owner> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Owner?> modinfoCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Owner Shared = new(OwnerKind.Shared, MemoryReport.SharedId, MemoryReport.SharedId);
    static readonly Owner Unknown = new(OwnerKind.Unknown, MemoryReport.UnknownId, MemoryReport.UnknownId);

    public string Describe { get; private set; } = "";

    ExternalModResolver(string? installDir) { this.installDir = installDir; }

    public static ExternalModResolver Create(int pid, string? dataOverride)
    {
        string? install = null;
        var loaded = new List<string>();
        try
        {
            using var p = Process.GetProcessById(pid);
            try { install = Path.GetDirectoryName(p.MainModule?.FileName); } catch { }
            try
            {
                foreach (ProcessModule m in p.Modules)
                {
                    string? fn = null;
                    try { fn = m.FileName; } catch { }
                    if (fn != null) loaded.Add(fn);
                }
            }
            catch { }
        }
        catch { }

        var r = new ExternalModResolver(install);
        string? data = null;
        string how;
        if (!string.IsNullOrWhiteSpace(dataOverride))
        {
            data = dataOverride;
            how = "--data";
        }
        else
        {
            string? inferred = null;
            foreach (var f in loaded)
            {
                int i = f.IndexOf("\\Cache\\unpack\\", StringComparison.OrdinalIgnoreCase);
                if (i > 0) { inferred = f.Substring(0, i); break; }
            }
            if (inferred != null)
            {
                data = inferred;
                how = "a loaded module path containing \\Cache\\unpack\\";
            }
            else
            {
                string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string guess = Path.Combine(appdata, "VintagestoryData");
                if (appdata.Length > 0 && Directory.Exists(guess)) { data = guess; how = "%APPDATA%\\VintagestoryData"; }
                else how = "not found";
            }
        }

        int scanned = 0;
        if (data != null && Directory.Exists(Path.Combine(data, "Mods")))
            scanned = r.ScanModsFolder(Path.Combine(data, "Mods"));

        r.Describe = "external: modinfo.json beside loaded DLLs (up to 4 parent dirs)"
            + (scanned > 0 ? " plus DLL names from " + scanned + " mods in the data Mods folder" : "")
            + "; data folder " + (data != null ? data : "none") + " (found via " + how + ")"
            + "; game dir " + (install ?? "unknown");
        return r;
    }

    int ScanModsFolder(string modsDir)
    {
        int count = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(modsDir))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        var owner = ReadModinfoFile(Path.Combine(entry, "modinfo.json"));
                        if (owner == null) continue;
                        count++;
                        foreach (var dll in Directory.EnumerateFiles(entry, "*.dll", SearchOption.AllDirectories))
                            modsByDllName.TryAdd(Path.GetFileName(dll), owner);
                    }
                    else if (entry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = ZipFile.OpenRead(entry);
                        Owner? owner = null;
                        foreach (var e in zip.Entries)
                        {
                            if (e.FullName.Equals("modinfo.json", StringComparison.OrdinalIgnoreCase))
                            {
                                using var rd = new StreamReader(e.Open());
                                owner = ParseModinfo(rd.ReadToEnd());
                                break;
                            }
                        }
                        if (owner == null) continue;
                        count++;
                        foreach (var e in zip.Entries)
                            if (e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                modsByDllName.TryAdd(e.Name, owner);
                    }
                }
                catch { }
            }
        }
        catch { }
        return count;
    }

    static Owner? ReadModinfoFile(string path)
    {
        try { return File.Exists(path) ? ParseModinfo(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    static Owner? ParseModinfo(string json)
    {
        string? id = null, name = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("modid", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) id = prop.Value.GetString();
                else if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String) name = prop.Value.GetString();
            }
        }
        catch
        {
            var m = Regex.Match(json, "\"modid\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success) id = m.Groups[1].Value;
            var n = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (n.Success) name = n.Groups[1].Value;
        }
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) return null;
        string finalId = (!string.IsNullOrWhiteSpace(id) ? id! : name!).Trim().ToLowerInvariant();
        string finalName = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : finalId;
        return new Owner(OwnerKind.Mod, finalId, finalName);
    }

    public Owner Resolve(string module)
    {
        if (string.IsNullOrEmpty(module)) return Unknown;
        if (cache.TryGetValue(module, out var hit)) return hit;
        var o = ResolveCore(module);
        cache[module] = o;
        return o;
    }

    Owner ResolveCore(string module)
    {
        string path = module.Replace('/', '\\');
        string file = Path.GetFileName(path);
        string simple = file;
        if (simple.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || simple.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            simple = simple.Substring(0, simple.Length - 4);

        if (simple.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || simple.Equals("System", StringComparison.OrdinalIgnoreCase)
            || simple.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || simple.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("netstandard", StringComparison.OrdinalIgnoreCase) || simple.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
            || simple.Equals("0Harmony", StringComparison.OrdinalIgnoreCase))
            return Shared;
        if (path.IndexOf("\\dotnet\\shared\\", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("\\Microsoft.NETCore.App\\", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("\\Microsoft.WindowsDesktop.App\\", StringComparison.OrdinalIgnoreCase) >= 0)
            return Shared;

        bool rooted = Path.IsPathRooted(path);
        if (rooted)
        {
            string? dir = Path.GetDirectoryName(path);
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.Equals("Mods", StringComparison.OrdinalIgnoreCase) || leaf.Equals("unpack", StringComparison.OrdinalIgnoreCase)) break;
                if (installDir != null && dir.Equals(installDir, StringComparison.OrdinalIgnoreCase)) break;
                string mi = Path.Combine(dir, "modinfo.json");
                if (!modinfoCache.TryGetValue(mi, out var owner))
                {
                    owner = ReadModinfoFile(mi);
                    modinfoCache[mi] = owner;
                }
                if (owner != null) return owner;
                dir = Path.GetDirectoryName(dir);
            }

            if (installDir != null && path.StartsWith(installDir + "\\", StringComparison.OrdinalIgnoreCase))
                return Shared;
        }

        if (modsByDllName.TryGetValue(simple + ".dll", out var byName)) return byName;
        return Unknown;
    }
}
