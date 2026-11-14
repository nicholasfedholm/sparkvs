using System;

namespace SparkVS;

public sealed class SparkVSConfig
{
    public const int CurrentSchema = 2;
    public const int OldDefaultDurationSec = 120, OldMaxDurationSec = 300;
    public const int NewDefaultDurationSec = 600, NewMaxDurationSec = 1800;
    /// <summary>Absolute safety cap on MaxDurationSec, whatever the file says.</summary>
    public const int HardMaxSeconds = 7200;

    /// <summary>
    /// Config file schema, 2 = 600/1800 s duration defaults. The initializer is 0 on purpose: a file written before this key existed
    /// deserializes to 0, which is how Migrate recognises it. New files come from CreateDefault, which sets 2.
    /// </summary>
    public int ConfigSchema { get; set; } = 0;

    public static SparkVSConfig CreateDefault() => new() { ConfigSchema = CurrentSchema };

    /// <summary>Default capture length. Clamped to MaxDurationSec.</summary>
    public int DefaultDurationSec { get; set; } = NewDefaultDurationSec;

    /// <summary>Longest allowed capture. Clamped to the hard limit of 7200.</summary>
    public int MaxDurationSec { get; set; } = NewMaxDurationSec;

    /// <summary>
    /// Sampling interval you want. The runtime reads its rate once at process start, so this cannot change
    /// it from inside the game: launch with DOTNET_EventPipeThreadSamplingRate set to this value in hex.
    /// SparkVS warns at capture start if the launch setting does not match.
    /// </summary>
    public int IntervalMs { get; set; } = 10;

    /// <summary>How many threads go into the reports, busiest first. The runtime always samples all of them.</summary>
    public int ThreadsInReport { get; set; } = 16;

    /// <summary>Capture folder. Empty means ModData/sparkvs in the game data folder.</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>
    /// Where .sparkvs capture writes its bundle folders (one subfolder per capture). Empty means resolved at runtime:
    /// OutputFolder if set, else VintagestoryData/ModData/sparkvs/captures.
    /// </summary>
    public string BundleFolder { get; set; } = "";

    public double SpikeThresholdMs { get; set; } = 50;

    /// <summary>Keep the raw nettrace after the report is built. It can be hundreds of MB.</summary>
    public bool KeepRawTrace { get; set; } = false;

    /// <summary>GC allocation tick sampling (about 1 tick per 100 KB) added to the existing EventPipe session, for allocations.json. Switch off for the overhead gate comparison.</summary>
    public bool AllocationSampling { get; set; } = true;

    /// <summary>Harmony timers on chunk pipeline stages plus the 1 Hz queue series, for chunk-pipeline.json. Read at game start; a change needs a restart.</summary>
    public bool ChunkPipeline { get; set; } = true;

    /// <summary>Per-mod load phase timing (hook on ModLoader.TryRunModPhase), for context.json load_phases. Read at game start; a change needs a restart.</summary>
    public bool LoadPhases { get; set; } = true;

    /// <summary>
    /// Read VRAM through DXGI QueryVideoMemoryInfo. The 20261101 live audit showed DXGI 37 percent below PDH, but that was a counter-pairing error:
    /// DXGI CurrentUsage is resident memory and was compared with PDH Dedicated Usage, which is committed memory (Task Manager's column) and keeps
    /// allocations the driver demoted. The corrected gate pairs DXGI with PDH Local Usage. That gate passed live 3/3 on 2026-11-02, so the module is on by default;
    /// set false to switch it off, and gpu-memory.jsonl then says DXGI was switched off and reports PDH only.
    /// </summary>
    // On by default since the 2026-11-02 live VRAM gate passed 3/3 (diff_pct -0.191, -0.189, -0.189; bar 10 pct).
    public bool GpuMemoryDxgi { get; set; } = true;

    /// <summary>
    /// GPU time by render phase from GL timestamp queries, for gpu-phases.json (experimental). Only active during a bundle capture.
    /// Queries are read three frames late and never waited on. Per-mod GPU time is excluded permanently.
    /// </summary>
    public bool GpuPhases { get; set; } = true;

    /// <summary>
    /// In-launch overhead A/B for GpuPhases: seconds per block, alternating off, on, off, on. 0 keeps the queries on for the whole capture.
    /// The env var SPARKVS_GPUPHASES_AB overrides it.
    /// </summary>
    public double GpuPhasesAbBlockSeconds { get; set; } = 0;

    /// <summary>Per entity code server tick timing (a Harmony transpiler on TickEntities, applied only while a capture runs), for entity-ticks.json.</summary>
    public bool EntityTickTiming { get; set; } = true;

    /// <summary>Harmony patch map of the process, for patch-map.json.</summary>
    public bool PatchMap { get; set; } = true;

    /// <summary>Folder holding the static inventories made by asset_inventory.py. The newest .json is copied into the bundle. Empty means resolved at runtime under VintagestoryData/ModData/sparkvs/asset-inventory.</summary>
    public string AssetInventoryFolder { get; set; } = "";

    /// <summary>Write a memory snapshot (top consumers) at the end of a bundle capture.</summary>
    public bool MemorySnapshotOnCaptureEnd { get; set; } = true;

    /// <summary>How many top entries the memory snapshot lists. Clamped to 1..200 at load.</summary>
    public int MemoryTopN { get; set; } = 20;

    /// <summary>
    /// Moves a config written before schema 2 to it. Only the untouched old defaults (120/300) are raised to 600/1800; any value the user
    /// changed stays. Returns true when the file needs saving.
    /// </summary>
    public bool Migrate(out string note)
    {
        note = "";
        if (ConfigSchema >= CurrentSchema) return false;
        bool raised = DefaultDurationSec == OldDefaultDurationSec && MaxDurationSec == OldMaxDurationSec;
        if (raised)
        {
            DefaultDurationSec = NewDefaultDurationSec;
            MaxDurationSec = NewMaxDurationSec;
        }
        ConfigSchema = CurrentSchema;
        note = raised
            ? $"config migrated to schema {CurrentSchema}: duration defaults {OldDefaultDurationSec}/{OldMaxDurationSec} raised to {NewDefaultDurationSec}/{NewMaxDurationSec} s"
            : $"config migrated to schema {CurrentSchema}: customized duration values ({DefaultDurationSec}/{MaxDurationSec} s) kept";
        return true;
    }

    /// <summary>Where an unreadable config is kept before defaults overwrite it: SparkVS.json.bad-yyyyMMddHHmmss next to it.</summary>
    public static string BadFileName(string path, DateTime now) => path + ".bad-" + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Frame ring size for a bundle capture of this length. Keeps the old assumption of up to 3500 fps (1 &lt;&lt; 20 entries covered 300 s), rounded up
    /// to a power of two, 13 bytes per entry (8 B end stamp, 4 B frame ms, 1 B gc flags): 600 s = 4M entries = 55 MB, 1800 s = 8M entries = 109 MB
    /// (the cap, so longer captures overwrite the oldest frames and say so in frames.jsonl). Allocated once and reused while it is big enough.
    /// </summary>
    public static int FrameRingCapacityFor(int seconds)
    {
        long wanted = 3500L * Math.Max(1, seconds);
        int capacity = 1 << 16;
        while (capacity < wanted && capacity < (1 << 23)) capacity <<= 1;
        return capacity;
    }

    /// <summary>
    /// Server side game event stream, key kept as EventLog (separate from events.jsonl of the capture-gap collection) (spawn, despawn, death, hurt, warning/error log lines, chat, commands, notes), replacing the standalone TestLogger mods.
    /// "off": nothing is subscribed or patched. "capture": events go into gameevents.jsonl of each SparkVS bundle capture. "always": the same, plus a session
    /// file under events/ in the output folder from world start. An unknown value falls back to "capture" with a warning. Read at game start; a change needs a restart.
    /// </summary>
    public string EventLog { get; set; } = "capture";

    /// <summary>Log every Entity.OnHurt call (Harmony postfix) as hurt rows. Needs EventLog not off. Read at game start; a change needs a restart.</summary>
    public bool HurtLog { get; set; } = true;
}
