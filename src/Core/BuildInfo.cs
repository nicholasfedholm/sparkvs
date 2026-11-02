using System.Reflection;

namespace SparkVS.Core;

/// <summary>The SparkVS version in one place. The mod sets it from modinfo.json at load; tools fall back to the assembly version.</summary>
public static class BuildInfo
{
    public const int SchemaVersion = 1;

    static string? version;

    public static string SparkVsVersion
    {
        get => version ??= FromAssembly();
        set => version = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    static string FromAssembly()
    {
        var info = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info.Substring(0, plus) : info;
    }
}
