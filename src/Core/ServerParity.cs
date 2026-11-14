using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace SparkVS.Core;

/// <summary>Which process side a capture was taken on, and the names that follow from it. Null or "client" means the original client capture.</summary>
public static class CaptureSide
{
    public const string Client = "client";
    public const string Server = "server";

    /// <summary>Bundle files that only a client can produce. A server bundle omits them and names them in summary.json not_collected.</summary>
    public static readonly string[] ClientOnlyFiles = { "frames.jsonl", "frames-summary.json", "gpu-memory.jsonl", "gpu-phases.json", "vram-by-mod.json" };

    /// <summary>Bundle writer files a server still produces, so a missing one gets a labelled placeholder (gpu-phases.json is client only).</summary>
    public static readonly string[] ServerWriterFiles = { "allocations.json", "chunk-pipeline.json", "patch-map.json", "asset-inventory.json" };

    public const string ClientOnlyReason = "client only: this capture was taken on a dedicated server, which has no render loop, frame hook or GPU";

    public static bool IsServer(string? side) => string.Equals(side, Server, StringComparison.Ordinal);

    /// <summary>Thread role of the capture's main thread: the render thread on a client, the tick thread on a server.</summary>
    public static string MainRole(string? side) => IsServer(side) ? "server-main" : "client-main";

    public static string MainLabel(string? side) => IsServer(side) ? "Server main thread" : "Client main thread";

    /// <summary>
    /// A server side SparkVS runs captures only in a dedicated server process. In a singleplayer process the integrated
    /// server shares the process with the client instance, which owns the commands, the autostart and the capture, so the
    /// server instance must not start a second one. Evaluated late (at world start or command time) because the order of
    /// StartClientSide and StartServerSide in one process is not something to rely on.
    /// </summary>
    public static bool ServerCaptureActive(bool isDedicatedServer, bool clientSideStartedInProcess) => isDedicatedServer && !clientSideStartedInProcess;
}

/// <summary>Output folder selection shared by the client and server capture paths.</summary>
public static class CapturePaths
{
    public const string ModDataFolder = "ModData";
    public const string ModFolder = "sparkvs";
    public const string CapturesFolder = "captures";

    /// <summary>Bundle captures go to BundleFolder, else OutputFolder, else ModData/sparkvs/captures; plain captures to OutputFolder, else ModData/sparkvs.</summary>
    public static string Root(string bundleFolder, string outputFolder, bool bundle, string dataPath) =>
        bundle && bundleFolder.Length > 0 ? bundleFolder
        : outputFolder.Length > 0 ? outputFolder
        : bundle ? Path.Combine(dataPath, ModDataFolder, ModFolder, CapturesFolder)
        : Path.Combine(dataPath, ModDataFolder, ModFolder);

    public static string CaptureName(DateTime now, string label) =>
        now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (label.Length > 0 ? "-" + Sanitize(label) : "");

    public static string MemoryDir(string dataPath, DateTime now) =>
        Path.Combine(dataPath, ModDataFolder, ModFolder, "memory-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    public static string AssetInventoryDir(string configured, string dataPath) =>
        configured.Length > 0 ? configured : Path.Combine(dataPath, ModDataFolder, ModFolder, "asset-inventory");

    public static string Sanitize(string text) =>
        new string(text.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
}

public enum AutostartOutcome { None, Ok, Malformed }

/// <summary>SPARKVS_AUTOSTART: label;delaySec;durationSec[;flags], where the delay may be mark:&lt;text&gt; and flags are report and bundle.</summary>
public sealed class AutostartSpec
{
    public string Label { get; private init; } = "";
    public int DelaySeconds { get; private init; }
    /// <summary>Non-null when the capture starts once the bench director's log contains this text.</summary>
    public string? Mark { get; private init; }
    public int DurationSeconds { get; private init; }
    public bool Bundle { get; private init; }
    /// <summary>True for the report flag and also for bundle, which implies it.</summary>
    public bool Report { get; private init; }

    public static AutostartOutcome TryParse(string? spec, out AutostartSpec? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(spec)) return AutostartOutcome.None;
        var parts = spec.Split(';');
        int delay = 0;
        bool waitForMark = parts.Length >= 3 && parts[1].StartsWith("mark:", StringComparison.Ordinal);
        if (parts.Length < 3
            || (!waitForMark && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out delay))
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            return AutostartOutcome.Malformed;
        var flags = parts.Length > 3 ? parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
        bool bundle = flags.Contains("bundle", StringComparer.Ordinal);
        result = new AutostartSpec
        {
            Label = parts[0],
            DelaySeconds = delay,
            Mark = waitForMark ? parts[1].Substring(5) : null,
            DurationSeconds = seconds,
            Bundle = bundle,
            Report = bundle || flags.Contains("report", StringComparer.Ordinal),
        };
        return AutostartOutcome.Ok;
    }
}

/// <summary>Fires true exactly once, however many threads or events ask. Guards autostart against a world start event that can repeat.</summary>
public sealed class OnceFlag
{
    int fired;
    public bool TryFire() => Interlocked.Exchange(ref fired, 1) == 0;
    public bool HasFired => Volatile.Read(ref fired) != 0;
}
