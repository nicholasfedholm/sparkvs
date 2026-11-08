using System.Text.Json;
using SparkVS.Core;

/// <summary>Unit tests for the shared memory engine: assembly buckets, unknown reasons, snapshot completeness and stamps.</summary>
static class MemoryEngineTests
{
    /// <summary>A resolver that knows nothing: every module is Unknown (what sparkx did for bare game module names).</summary>
    sealed class UnknownResolver : IModResolver
    {
        public string Describe => "test: everything unknown";
        public Owner Resolve(string module) => new(OwnerKind.Unknown, MemoryReport.UnknownId, MemoryReport.UnknownId);
    }

    static void Bucket(Action<string, bool, string> check, string module, AssemblyKind kind, string bucket, string? gameDir = null)
    {
        var b = AssemblyBuckets.Classify(module, gameDir);
        check("bucket " + module, b.Kind == kind && b.Bucket == bucket, "got " + b.Kind + " " + b.Bucket);
    }

    public static void Run(Action<string, bool, string> checkAll)
    {
        void check(string name, bool ok, string detail = "") => checkAll(name, ok, detail);
        // Known-answer mapping of module names and paths.
        Bucket(check, "VintagestoryAPI", AssemblyKind.Game, "game:VintagestoryAPI");
        Bucket(check, "vintagestoryapi", AssemblyKind.Game, "game:VintagestoryAPI");
        Bucket(check, "C:\\Game\\Lib\\VintagestoryAPI.dll", AssemblyKind.Game, "game:VintagestoryAPI");
        Bucket(check, "VintagestoryLib", AssemblyKind.Game, "game:VintagestoryLib");
        Bucket(check, "VSEssentials", AssemblyKind.Game, "game:VSEssentials");
        Bucket(check, "VSSurvivalMod", AssemblyKind.Game, "game:VSSurvivalMod");
        Bucket(check, "VSCreativeMod", AssemblyKind.Game, "game:VSCreativeMod");
        Bucket(check, "Vintagestory.exe", AssemblyKind.Game, "game:Vintagestory");
        Bucket(check, "VintagestoryServer", AssemblyKind.Game, "game:VintagestoryServer");
        Bucket(check, "OptimumClient", AssemblyKind.Game, "game:OptimumClient");
        Bucket(check, "OpenTK.Graphics", AssemblyKind.Game, "game:OpenTK.Graphics");
        Bucket(check, "cairo-sharp", AssemblyKind.Game, "game:cairo-sharp");
        Bucket(check, "protobuf-net", AssemblyKind.Game, "game:protobuf-net");
        Bucket(check, "Newtonsoft.Json", AssemblyKind.Game, "game:Newtonsoft.Json");
        Bucket(check, "0Harmony", AssemblyKind.Game, "game:0Harmony");
        Bucket(check, "SkiaSharp", AssemblyKind.Game, "game:SkiaSharp");
        Bucket(check, "System.Private.CoreLib.dll", AssemblyKind.Framework, "framework:System.Private.CoreLib");
        Bucket(check, "System", AssemblyKind.Framework, "framework:System");
        Bucket(check, "Microsoft.CodeAnalysis", AssemblyKind.Framework, "framework:Microsoft.CodeAnalysis");
        Bucket(check, "mscorlib", AssemblyKind.Framework, "framework:mscorlib");
        Bucket(check, "netstandard", AssemblyKind.Framework, "framework:netstandard");
        Bucket(check, "C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App\\10.0.0\\Odd.dll", AssemblyKind.Framework, "framework:Odd");
        Bucket(check, "C:\\VS\\Mods\\survival\\Foo.dll", AssemblyKind.None, "");
        Bucket(check, "C:\\VS\\Mods\\survival\\Foo.dll", AssemblyKind.Game, "game:Foo", "C:\\VS");
        Bucket(check, "C:\\Users\\x\\Mods\\ModA.dll", AssemblyKind.None, "", "C:\\VS");
        Bucket(check, "ModA", AssemblyKind.None, "");
        Bucket(check, "SystemTools", AssemblyKind.None, "");
        Bucket(check, "", AssemblyKind.None, "");

        // Engine: a resolver that answers Unknown for everything still puts game and framework modules in named buckets.
        var snap = new HeapSnapshot
        {
            Route = "A: test", Pid = 1, ProcessName = "test", TakenUtc = DateTime.UtcNow, Complete = true,
            Types =
            {
                new HeapTypeStat { TypeId = 1, TypeName = "Vintagestory.API.Common.Entities.Entity", Module = "VintagestoryAPI", Count = 5, Bytes = 5000 },
                new HeapTypeStat { TypeId = 2, TypeName = "Vintagestory.GameContent.EntityBehavior", Module = "VSEssentials", Count = 4, Bytes = 4000 },
                new HeapTypeStat { TypeId = 3, TypeName = "System.String", Module = "System.Private.CoreLib.dll", Count = 3, Bytes = 3000 },
                new HeapTypeStat { TypeId = 4, TypeName = "Mystery.Blob", Module = "C:\\mods\\MysteryAsm.dll", Count = 2, Bytes = 2000 },
                new HeapTypeStat { TypeId = 5, TypeName = "Anon.Thing", Module = "", Count = 1, Bytes = 1000 },
                new HeapTypeStat { TypeId = 6, TypeName = "<type 0x1234>", Module = "MysteryAsm", Count = 1, Bytes = 500 },
            },
        };
        var table = MemoryReport.Build(snap, new UnknownResolver(), null, "");
        var buckets = table.ByAssembly.ToDictionary(x => x.Bucket, x => x);
        check("game types land in game:VintagestoryAPI", buckets.TryGetValue("game:VintagestoryAPI", out var gapi) && gapi.Bytes == 5000 && gapi.Kind == OwnerKind.Shared, string.Join("|", buckets.Keys));
        check("game types land in game:VSEssentials", buckets.TryGetValue("game:VSEssentials", out var ves) && ves.Bytes == 4000);
        check("framework types land in framework:System.Private.CoreLib", buckets.TryGetValue("framework:System.Private.CoreLib", out var fcl) && fcl.Bytes == 3000);
        long shared = table.Rows.Where(r => r.Kind == OwnerKind.Shared).Sum(r => r.LiveBytes);
        long unknown = table.Rows.Where(r => r.Kind == OwnerKind.Unknown).Sum(r => r.LiveBytes);
        check("game and framework bytes are shared, rest is unknown", shared == 12000 && unknown == 3500, $"shared {shared} unknown {unknown}");
        check("by-assembly bytes add up to the live total", table.ByAssembly.Sum(x => x.Bytes) == table.TotalLiveBytes);
        var reasons = table.UnknownReasons.ToDictionary(x => x.Reason, x => x.Bytes);
        check("unknown reasons: unmapped module, empty module, unresolved type name",
            reasons.GetValueOrDefault("unmapped module") == 2000 && reasons.GetValueOrDefault("empty module") == 1000 && reasons.GetValueOrDefault("unresolved type name") == 500,
            string.Join("|", reasons.Select(kv => kv.Key + "=" + kv.Value)));
        check("unmapped module list names the module", table.UnknownModules.Count == 1 && table.UnknownModules[0].Bucket == "unknown:MysteryAsm");
        string md = MemoryReport.RenderMarkdown(table, 20, "tests");
        check("memory.md has a by-assembly table with game bucket", md.Contains("## By assembly") && md.Contains("| game:VintagestoryAPI | shared |"));
        check("memory.md has an unknown section with reasons and top types", md.Contains("## Unknown bucket") && md.Contains("unmapped module") && md.Contains("Top 10 types in unknown"));
        check("complete memory.md has no PARTIAL banner", !md.Contains("PARTIAL"));
        using (var doc = JsonDocument.Parse(MemoryReport.RenderJson(table, 20, "tests")))
        {
            var root = doc.RootElement;
            check("memory.json has by_assembly and unknown", root.GetProperty("by_assembly").GetArrayLength() == table.ByAssembly.Count
                && root.GetProperty("unknown").GetProperty("reasons").GetArrayLength() == 3 && root.GetProperty("unknown").GetProperty("top_types").GetArrayLength() == 3);
        }

        // A mod's own DLL still wins over the bucket rule (the resolver answers Mod first).
        var modSnap = new HeapSnapshot { Complete = true, Types = { new HeapTypeStat { TypeId = 1, TypeName = "ModA.Thing", Module = "ModA", Count = 1, Bytes = 100 } } };
        var modTable = MemoryReport.Build(modSnap, new ModAResolver(), null, "");
        check("mod module stays a mod", modTable.Rows.Any(r => r.Kind == OwnerKind.Mod && r.Id == "moda" && r.LiveBytes == 100));

        // Snapshot stamps and PARTIAL marking.
        snap.Complete = false;
        snap.EventsLost = 6861;
        snap.Attempts = 3;
        snap.StartCondition = "timeout";
        snap.AttemptLog.Add(new SnapshotAttempt { Attempt = 1, BufferMB = 1000, Outcome = "events lost", EventsLost = 6861, GcPauseMs = 812.3 });
        snap.AttemptLog.Add(new SnapshotAttempt { Attempt = 2, BufferMB = 1500, Outcome = "dump GC not observed", GcPauseMs = -1 });
        var ptable = MemoryReport.Build(snap, new UnknownResolver(), null, "");
        string pmd = MemoryReport.RenderMarkdown(ptable, 20, "tests");
        check("partial memory.md carries the stamp", pmd.Contains("Snapshot stamp: complete=false events_lost=6861 attempts=3 start_condition=timeout"));
        check("partial memory.md has the PARTIAL banner and marked table", pmd.Contains("PARTIAL SNAPSHOT (lower bound)") && pmd.Contains("live MB (PARTIAL, lower bound)"));
        check("memory.md lists each attempt with its GC pause", pmd.Contains("## Attempts") && pmd.Contains("| 1 | 1000 | events lost | 6861 | 812.3 |") && pmd.Contains("| n/a |"));
        using (var pdoc = JsonDocument.Parse(MemoryReport.RenderJson(ptable, 20, "tests")))
        {
            var pr = pdoc.RootElement;
            check("memory.json carries complete, events_lost, attempts, start_condition",
                !pr.GetProperty("complete").GetBoolean() && pr.GetProperty("partial").GetBoolean() && pr.GetProperty("events_lost").GetInt64() == 6861
                && pr.GetProperty("attempts").GetInt32() == 3 && pr.GetProperty("start_condition").GetString() == "timeout" && pr.GetProperty("attempt_log").GetArrayLength() == 2);
        }
        check("timeline marker text", SnapshotPolicy.Marker("start", snap) == "snapshot:start complete=false events_lost=6861 attempts=3 start_condition=timeout", SnapshotPolicy.Marker("start", snap));
        check("timeline marker without start condition", SnapshotPolicy.Marker("end", new HeapSnapshot { Complete = true, Attempts = 1 }) == "snapshot:end complete=true events_lost=0 attempts=1");

        // Completeness rule: finished, no reader error, dump GC observed, zero events lost.
        check("complete when all four hold", SnapshotPolicy.IsComplete(true, false, 2, 0));
        check("events lost make it incomplete", !SnapshotPolicy.IsComplete(true, false, 2, 1));
        check("timeout (not finished) is incomplete", !SnapshotPolicy.IsComplete(false, false, 2, 0));
        check("reader error is incomplete", !SnapshotPolicy.IsComplete(true, true, 2, 0));
        check("dump GC not observed is incomplete", !SnapshotPolicy.IsComplete(true, false, 1, 0));

        // Buffer growth per attempt and clamp.
        check("buffer grows 50 percent per attempt", SnapshotPolicy.BufferForAttempt(1000, 0) == 1000 && SnapshotPolicy.BufferForAttempt(1000, 1) == 1500 && SnapshotPolicy.BufferForAttempt(1000, 2) == 2250);
        check("buffer clamps at 4000", SnapshotPolicy.BufferForAttempt(3500, 2) == 4000 && SnapshotPolicy.BufferForAttempt(4000, 1) == 4000);

        // Stability rule for the first snapshot: last 3 samples within 5 percent.
        check("stable when 3 samples are within 5 percent", SnapshotPolicy.HeapStable(new long[] { 100, 2000, 2010, 2040, 2050 }));
        check("not stable when growing more than 5 percent", !SnapshotPolicy.HeapStable(new long[] { 2000, 2100, 2300 }));
        check("not stable with fewer than 3 samples", !SnapshotPolicy.HeapStable(new long[] { 2000, 2000 }));
        check("not stable with a missing sample", !SnapshotPolicy.HeapStable(new long[] { 2000, 0, 2000 }));
        check("only the last 3 samples count", !SnapshotPolicy.HeapStable(new long[] { 2000, 2000, 2000, 3000 }));
    }

    sealed class ModAResolver : IModResolver
    {
        public string Describe => "test: ModA";
        public Owner Resolve(string module)
            => module == "ModA" ? new Owner(OwnerKind.Mod, "moda", "ModA") : new Owner(OwnerKind.Unknown, MemoryReport.UnknownId, MemoryReport.UnknownId);
    }
}
