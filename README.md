SparkVS 0.3.0
Sampling profiler for Vintage Story 1.22.7

SparkVS tells you what the game is actually spending its time on, and which mod owns the code that is doing it. If you have ever stared at a hitch or a bad area and thought "ok, but who is doing that", this is the tool for finding out.

It is not a port of spark and it uses no spark code. It just borrows the basic idea. SparkVS samples the running game through the .NET runtime EventPipe profiler, builds real call stacks from the samples, and then blames the time on the mod that owns each method. Stuff it cannot pin on a mod goes in an honest "unknown" bucket instead of being guessed.

Nothing uploads anywhere. Captures and reports are written to disk on your own PC and stay there.

What it does

- Samples all threads while you play, not just one system at a time, so worldgen, meshing, entity code and plain mod logic all show up in the same capture.
- Groups the samples by thread, by top method, and by owning mod.
- Records long frames (over 50 ms by default) with the nearest stack it has and the GC collections around them, which is the part that helps with random hitches.
- Can write a full capture bundle with extra context for digging later: allocations by mod, chunk pipeline stage timings, per mod load times, a Harmony patch map, and an experimental GPU render phase timing file. Anything experimental or switched off says so inside its own file, with the reason.
- Shows live managed memory per mod with .sparkvs memory (new in 0.3.0).
- Cheap enough to run while you reproduce a problem. It only samples when you start a capture, it is not profiling all the time.

What's new in 0.3.0

- New .sparkvs memory command: a live managed-heap snapshot attributed per mod, written as memory.md, memory.csv and memory.json.
- Full capture bundles now also write memory.csv at capture end (config MemorySnapshotOnCaptureEnd, on by default).
- New config keys MemorySnapshotOnCaptureEnd and MemoryTopN.
- BundleFolder and AssetInventoryFolder now default to empty and resolve at runtime under VintagestoryData/ModData/sparkvs. The old hardcoded default folder is gone.
- New companion command line tool sparkx 0.1.0 (see below), which does the same memory snapshot from outside the game.

Commands

All commands are client chat commands, so they start with a dot.

.sparkvs start [seconds] [label]
Start a normal capture. Default is 120 seconds, max is 300. The label is one word and ends up in the folder name, so use labels like travel or forest.

.sparkvs stop
Stop the running capture early.

.sparkvs report
Build the report for the last capture: summary.md, summary.json and profile.speedscope.json. Drag the speedscope file into speedscope.app or any speedscope viewer if you want the flame graph view. The summary.md is the readable version.

.sparkvs capture <seconds> <label>
Run a capture and write the full bundle, then build the report. The bundle has the summary files plus context.json, allocations.json, chunk-pipeline.json, patch-map.json and gpu-phases.json. Unless MemorySnapshotOnCaptureEnd is off, it also gets a memory.csv written at capture end (see the memory command below).

To turn a capture bundle into a self-contained HTML report, run python report/make_report.py (see the usage notes at the top of the script). GpuMemoryDxgi is on by default since the 2026-11-02 live VRAM gate passed 3/3 (DXGI local vs PDH Local within 0.2 pct, bar 10 pct).

.sparkvs memory [top]
Takes a snapshot of the live managed heap of the running game and attributes it per mod (object type to owning assembly to owning mod). Shows the top N mods (default MemoryTopN, 20) and writes memory.md, memory.csv and memory.json under the output folder. memory.csv and memory.json hold every row, memory.md is the readable version. Each snapshot forces a full blocking GC, see "What a memory snapshot costs" below.

.sparkvs status
Shows whether a capture is running and where the last one went. While a memory snapshot is running it shows that too, and .sparkvs start is refused until the snapshot finishes.

Output and config

Normal captures go to VintagestoryData/ModData/sparkvs unless you change OutputFolder.
Settings live in ModConfig/SparkVS.json. The useful ones are DefaultDurationSec, MaxDurationSec, SpikeThresholdMs, and KeepRawTrace. Leave KeepRawTrace off unless you really want the raw nettrace file kept, it gets big fast.

Memory settings: MemorySnapshotOnCaptureEnd (default true) writes memory.csv into the bundle when a capture ends. MemoryTopN (default 20, clamped to 1 to 200) is how many mod rows memory.md shows.

BundleFolder and AssetInventoryFolder default to empty. Empty means they resolve at runtime to folders under VintagestoryData/ModData/sparkvs, so nothing needs to be set before using .sparkvs capture.

Memory: what the numbers mean

1. Live managed heap only, attributed by object type to owning assembly to owning mod. Game, VintagestoryAPI, System.*, Microsoft.*, and shared Harmony go to system/shared. Unmapped goes to unknown. Nothing is guessed.
2. Content-only mods (JSON, no DLL) hold their data inside game types, so their footprint lands in system/shared.
3. Native and unmanaged memory, including GPU / VRAM, is not per-mod separable and is not in this table (see the capture's gpu-memory output).
4. Gross allocations are not live RAM. The two columns are separate and never merged.

The capture-end memory.csv has an empty gross column. Gross per-mod allocations come from .sparkvs report.

What a memory snapshot costs

- A snapshot forces a full blocking GC. The game pauses about 3.5 s per 3 GB of heap (measured 17 ms at 50 MB, 1.1 s at 1 GB, 3.4 to 3.5 s at 3 GB). Do not snapshot in the middle of something timing sensitive.
- Peak working set rises by up to the EventPipe buffer, which auto-sizes to 0.9 x working set, clamped 512 to 4000 MB, and is committed only as it fills (3 GB heap test: peak 5.1 GB).
- At very large heaps events can be lost (seen at 5 GB heap with a 2 GB buffer). The tool warns and marks the result incomplete when that happens.

Verification status: .sparkvs memory and the capture-end memory.csv are verified by unit tests and cross-process gates, but not yet in a live game session.

sparkx (external companion tool)

sparkx 0.1.0 is a standalone command line tool that reads the managed heap of a running Vintagestory (client) or VintagestoryServer process and reports how much live memory each mod's code is holding, by owning assembly. It ships as a single file, sparkx.exe, which is self-contained (no .NET install needed) and does NOT go in the Mods folder. Keep it anywhere and run it from a terminal.

How it attaches: over the .NET diagnostics port (the same mechanism as dotnet-gcdump). No injection, no debugger, no code is loaded into the game, and no admin rights are needed. Run it as the same Windows user as the game, at the same elevation. The game does not need a special launch, the port is on by default in .NET.

  sparkx list
      Lists running Vintagestory / VintagestoryServer processes that expose a diagnostics port: pid, name, start time, working set in MB.

  sparkx memory [--pid N] [--out DIR] [--top N] [--data DIR]
      One heap snapshot, then the report. Without --pid, exactly one Vintage Story process must be running, otherwise the list is printed and sparkx exits with code 2.
      --top is the number of mod rows in memory.md (default 20, 1 to 200). --data is the VintagestoryData folder used to find mod names.

  sparkx watch [--pid N] [--minutes M] [--snapshot-every K] [--out DIR] [--top N] [--data DIR]
      Watches for M minutes (default 10). Snapshots at the start and the end (and every K minutes with --snapshot-every), samples process totals every 5 s, and estimates gross allocations per mod from sampled allocation ticks. Ctrl+C ends early and still writes the outputs.

Outputs: memory gives memory.md, memory.csv and memory.json. watch gives report.md (snapshot table, live MB per owner at start vs end with the delta, gross allocation estimate per mod), timeline.csv (utc, elapsed_s, working_set_bytes, private_bytes, gc_heap_bytes, gross_alloc_bytes_total) and one snapshot-NN folder per snapshot. The gross allocation estimate is its own column and is never merged with live bytes.

Mods are identified by walking up from each loaded assembly's path (at most 4 folders) to a modinfo.json. Game folder, .NET shared folder, System.*, Microsoft.* and 0Harmony go to system/shared, anything else is unknown. The same four limits listed above are printed in every memory.md, and the same costs apply to each snapshot.

Notes

This is a profiling tool, not a performance mod. It does not make the game faster, it tells you what is making it slow. Run a capture while the problem is actually happening, an idle capture of a healthy game mostly proves the game was idle.

Not part of MoonshotCore. SparkVS is its own standalone mod.
