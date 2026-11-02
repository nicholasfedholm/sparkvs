SparkVS 0.2.0
Sampling profiler for Vintage Story 1.22.7

SparkVS tells you what the game is actually spending its time on, and which mod owns the code that is doing it. If you have ever stared at a hitch or a bad area and thought "ok, but who is doing that", this is the tool for finding out.

It is not a port of spark and it uses no spark code. It just borrows the basic idea. SparkVS samples the running game through the .NET runtime EventPipe profiler, builds real call stacks from the samples, and then blames the time on the mod that owns each method. Stuff it cannot pin on a mod goes in an honest "unknown" bucket instead of being guessed.

Nothing uploads anywhere. Captures and reports are written to disk on your own PC and stay there.

What it does

- Samples all threads while you play, not just one system at a time, so worldgen, meshing, entity code and plain mod logic all show up in the same capture.
- Groups the samples by thread, by top method, and by owning mod.
- Records long frames (over 50 ms by default) with the nearest stack it has and the GC collections around them, which is the part that helps with random hitches.
- Can write a full capture bundle with extra context for digging later: allocations by mod, chunk pipeline stage timings, per mod load times, a Harmony patch map, and an experimental GPU render phase timing file. Anything experimental or switched off says so inside its own file, with the reason.
- Cheap enough to run while you reproduce a problem. It only samples when you start a capture, it is not profiling all the time.

Commands

All commands are client chat commands, so they start with a dot.

.sparkvs start [seconds] [label]
Start a normal capture. Default is 120 seconds, max is 300. The label is one word and ends up in the folder name, so use labels like travel or forest.

.sparkvs stop
Stop the running capture early.

.sparkvs report
Build the report for the last capture: summary.md, summary.json and profile.speedscope.json. Drag the speedscope file into speedscope.app or any speedscope viewer if you want the flame graph view. The summary.md is the readable version.

.sparkvs capture <seconds> <label>
Run a capture and write the full bundle, then build the report. The bundle has the summary files plus context.json, allocations.json, chunk-pipeline.json, patch-map.json and gpu-phases.json.

To turn a capture bundle into a self-contained HTML report, run python report/make_report.py (see the usage notes at the top of the script). GpuMemoryDxgi is on by default since the 2026-11-02 live VRAM gate passed 3/3 (DXGI local vs PDH Local within 0.2 pct, bar 10 pct).

.sparkvs status
Shows whether a capture is running and where the last one went.

Output and config

Normal captures go to VintagestoryData/ModData/sparkvs unless you change OutputFolder.
Settings live in ModConfig/SparkVS.json. The useful ones are DefaultDurationSec, MaxDurationSec, SpikeThresholdMs, and KeepRawTrace. Leave KeepRawTrace off unless you really want the raw nettrace file kept, it gets big fast.

Known quirk in this build: the default BundleFolder in the config points at the folder on my own PC. Before using .sparkvs capture, open ModConfig/SparkVS.json and set BundleFolder to a folder on your PC, or clear it so it falls back to the normal output folder. Plain start, stop and report are not affected by this.

Notes

This is a profiling tool, not a performance mod. It does not make the game faster, it tells you what is making it slow. Run a capture while the problem is actually happening, an idle capture of a healthy game mostly proves the game was idle.

Not part of MoonshotCore. SparkVS is its own standalone mod.
