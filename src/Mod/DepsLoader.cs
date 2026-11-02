using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace SparkVS;

// The game calls LoadFrom and GetTypes on every dll in a mod's root. That fails for these dependencies: other mods
// already load a different Microsoft.Extensions.Logging.Abstractions file with the same identity, and TraceEvent has
// types that need Dia2Lib and TraceReloggerLib, which are not shipped. So they live in deps\ as .bin files, in a
// private load context that also keeps their own Logging.Abstractions apart from the other mods' copy.
internal sealed class DepsLoader : AssemblyLoadContext
{
    private readonly string dir;

    private DepsLoader(string dir) : base("sparkvs-deps")
    {
        this.dir = dir;
    }

#pragma warning disable CA2255 // Must run before the first method that touches a dependency is compiled.
    [ModuleInitializer]
    internal static void Install()
#pragma warning restore CA2255
    {
        string root = Path.GetDirectoryName(typeof(DepsLoader).Assembly.Location) ?? "";
        string dir = Path.Combine(root, "deps");
        if (!Directory.Exists(dir)) return;
        var loader = new DepsLoader(dir);
        Default.Resolving += (_, name) => loader.Find(name);
    }

    protected override Assembly? Load(AssemblyName name) => Find(name);

    private Assembly? Find(AssemblyName name)
    {
        string path = Path.Combine(dir, name.Name + ".dll.bin");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
