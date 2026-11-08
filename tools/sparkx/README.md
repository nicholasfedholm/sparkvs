sparkx 0.1.0: per-mod live memory for a running Vintage Story

sparkx is a standalone command line tool. It reads the managed heap of a running
Vintagestory (client) or VintagestoryServer process and reports how much live memory
each mod's code is holding, by owning assembly.

How it attaches
- It attaches over the .NET diagnostics port (the same mechanism as dotnet-gcdump).
- No injection, no debugger, no code is loaded into the game, and no admin rights are needed.
- Run it as the same Windows user as the game, at the same elevation (both normal, or both elevated).
- The game does not need to be started in any special way. The diagnostics port is on by default in .NET.
- sparkx.exe is a self-contained program (no .NET install needed). It does NOT go in the Mods folder.
  Keep it anywhere, for example next to this file, and run it from a terminal.

Commands
  sparkx list
      Lists running Vintagestory / VintagestoryServer processes that expose a diagnostics port:
      pid, name, start time, working set in MB.

  sparkx memory [--pid N] [--out DIR] [--top N] [--data DIR]
      Takes one heap snapshot and writes the report. Without --pid, exactly one Vintage Story
      process must be running; otherwise the list is printed and sparkx exits with code 2.
      --out   output folder (default: ./sparkx-memory-<yyyyMMdd-HHmmss>)
      --top   number of mod rows shown in memory.md (default 20, 1 to 200); memory.csv and memory.json hold every row
      --data  VintagestoryData folder, used to find mod names (default: inferred from the loaded mod paths,
              else %APPDATA%\VintagestoryData)

  sparkx watch [--pid N] [--minutes M] [--snapshot-every K] [--out DIR] [--top N] [--data DIR]
               [--start-delay S] [--start-max-wait S] [--snapshot-attempts N]
      Watches for M minutes (default 10). Takes a snapshot at the start and at the end (and every K minutes
      if --snapshot-every is given), samples process totals every 5 s, and estimates gross allocations per mod
      from sampled allocation ticks. Ctrl+C ends early and still writes the outputs.
      --out default: ./sparkx-watch-<yyyyMMdd-HHmmss>
      --start-delay         seconds after attach before the first snapshot may run (default 60)
      --start-max-wait      give up waiting for a stable heap after this many seconds and snapshot anyway,
                            recorded as start_condition=timeout (default 600)
      --snapshot-attempts   tries per snapshot (default 3; memory takes the same flag). A snapshot is complete only
                            if it finished, saw the dump GC end, had no reader error and lost 0 events. Anything
                            else is retried with a 50 % larger EventPipe buffer (max 4000 MB). Each attempt
                            forces a full GC and is listed with its GC pause.
      The first snapshot runs once --start-delay has passed AND the GC heap bytes changed under 5 % across the
      last 3 timeline samples (5 s apart). If the GC heap is not reported, working set is used and says so.

What it costs the game
- Each snapshot induces one full garbage collection, so the game pauses for about 3.5 s per 3 GB of live heap
  (a few hundred ms for a small heap). Do not snapshot in the middle of something timing sensitive.
- The target's peak working set rises by roughly the size of the EventPipe buffer while the snapshot runs
  (sized automatically from the target's working set, 512 to 4000 MB). It is released afterwards.
- If events are lost, the report says so in its warnings and the per-type numbers are a lower bound.

Outputs
  memory
    memory.md    readable report: header (front end, route, pid, UTC time, how mods were mapped, process totals,
                 dump pause, events lost), the honest limits, and a table of mods plus system/shared and unknown
    memory.csv   one row per owner. Header: rank,owner_kind,owner_id,owner_name,live_bytes,live_mb,live_objects,
                 pct_of_live_heap,gross_alloc_bytes,top_types (gross_alloc_bytes is empty when not measured)
    memory.json  the same data, with per-type detail
  watch
    report.md    snapshot table (complete, events lost, attempts, start condition, per-attempt GC pause), live MB per
                 owner at start vs end with the delta, live MB by assembly, gross allocation estimate per mod.
                 Numbers from a snapshot that is not complete are marked PARTIAL (lower bound).
    timeline.csv utc,elapsed_s,working_set_bytes,private_bytes,gc_heap_bytes,gross_alloc_bytes_total,marker
                 sample rows leave marker empty; marker rows leave the numbers empty and carry text such as
                 snapshot:start complete=false events_lost=6861 attempts=3 start_condition=timeout
    snapshot-NN\memory.md / memory.csv / memory.json   one folder per snapshot
  Every memory.md and memory.json is stamped complete / events_lost / attempts / start_condition (memory.json also has
  attempt_log, by_assembly and an unknown section with reasons). memory.csv keeps its fixed header.

How mods are identified
  Each loaded assembly is matched to a mod by walking up from its path (at most 4 folders) to a modinfo.json.
  Game assemblies (VintagestoryAPI, VintagestoryLib, VSEssentials, VSSurvivalMod, VSCreativeMod, Vintagestory,
  VintagestoryServer, Optimum*, cairo-sharp, OpenTK.*, protobuf-net, Newtonsoft.Json, 0Harmony, SkiaSharp, and anything
  in the game folder) are bucketed as game:<AssemblyName>; System.*, Microsoft.*, mscorlib, netstandard and the .NET
  shared folder as framework:<AssemblyName>. Both roll up to system/shared in the owner table and are split out in the
  by-assembly table. Anything else is unknown, and the unknown section says why (empty module, unmapped module,
  unresolved type name) with its top 10 types. Nothing is guessed. The report header says how the mapping was built.

Honest limits (also printed in every memory.md)
  1. Live managed heap only, attributed by object type to owning assembly to owning mod. Game, VintagestoryAPI,
     System.*, Microsoft.*, and shared Harmony go to system/shared. Unmapped goes to unknown. Nothing is guessed.
  2. Content-only mods (JSON, no DLL) hold their data inside game types, so their footprint lands in system/shared.
  3. Native and unmanaged memory, including GPU / VRAM, is not per-mod separable and is not in this table
     (see the capture's gpu-memory output).
  4. Gross allocations are not live RAM. The two columns are separate and never merged.

The SparkVS mod (0.3.0 and later) has the same engine built in: the in-game command .sparkvs memory takes the same
kind of snapshot of the running game and writes the same files under VintagestoryData\ModData\sparkvs.
