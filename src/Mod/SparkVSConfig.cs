namespace SparkVS;

public sealed class SparkVSConfig
{
    /// <summary>Default capture length. Clamped to MaxDurationSec.</summary>
    public int DefaultDurationSec { get; set; } = 120;

    /// <summary>Longest allowed capture. Clamped to the hard limit of 300.</summary>
    public int MaxDurationSec { get; set; } = 300;

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
    /// Where .sparkvs capture writes its bundle folders (one subfolder per capture). Empty falls back to OutputFolder.
    /// </summary>
    public string BundleFolder { get; set; } = @"C:\Users\<you>\vs_scratch\sparkvs\captures";

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
    /// allocations the driver demoted. The corrected gate pairs DXGI with PDH Local Usage. The module stays off (false) until that gate passes in a
    /// live settled run; gpu-memory.jsonl then says DXGI was switched off and why, and reports PDH only.
    /// </summary>
    public bool GpuMemoryDxgi { get; set; } = false;

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

    /// <summary>Harmony patch map of the process, for patch-map.json.</summary>
    public bool PatchMap { get; set; } = true;

    /// <summary>Folder holding the static inventories made by asset_inventory.py. The newest .json is copied into the bundle.</summary>
    public string AssetInventoryFolder { get; set; } = @"C:\Users\<you>\vs_scratch\sparkvs\asset-inventory";
}
