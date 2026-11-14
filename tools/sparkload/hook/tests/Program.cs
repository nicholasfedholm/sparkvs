using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

// Offline harness for SparkLoad.Hook.dll. Never starts the game. Game dlls are read as metadata only (PEReader).
// Proof line at the end: "tests passed=N failed=M".

namespace SparkLoad.Hook.Tests
{
    internal static class T
    {
        public static int Pass, Fail;

        public static void Check(string name, bool ok, string detail = "")
        {
            if (ok) Pass++; else Fail++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 && !ok ? "  :: " + detail : ""));
        }

        public static void Note(string s) { Console.WriteLine("     " + s); }
    }

    internal sealed class Rec
    {
        public int Id;
        public string Name;
        public string[] Names;
        public string[] Vals;
        public string Key { get { return Id + "|" + string.Join("|", Vals); } }
    }

    internal sealed class Cap : EventListener
    {
        public readonly List<Rec> Events = new List<Rec>();
        public readonly List<string> Errors = new List<string>();
        public EventSource Source;

        protected override void OnEventSourceCreated(EventSource es)
        {
            if (es.Name == "SparkLoad") { Source = es; EnableEvents(es, EventLevel.Verbose, EventKeywords.All); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventSource != Source) return;
            if (e.EventId == 0 || e.EventName == "EventSourceMessage")
            {
                lock (Errors) Errors.Add(e.Message ?? "(no message)");
                return;
            }
            var vals = new string[e.Payload == null ? 0 : e.Payload.Count];
            for (int i = 0; i < vals.Length; i++) vals[i] = Convert.ToString(e.Payload[i], CultureInfo.InvariantCulture);
            var r = new Rec { Id = e.EventId, Name = e.EventName, Names = e.PayloadNames == null ? new string[0] : e.PayloadNames.ToArray(), Vals = vals };
            lock (Events) Events.Add(r);
        }

        public List<Rec> Snapshot() { lock (Events) return Events.ToList(); }
    }

    // ---------- metadata reader (same naming as the target table) ----------
    internal sealed class TP : ISignatureTypeProvider<string, object>
    {
        public static string DefName(MetadataReader r, TypeDefinitionHandle h)
        {
            var t = r.GetTypeDefinition(h); string n = r.GetString(t.Name);
            var decl = t.GetDeclaringType();
            if (!decl.IsNil) return DefName(r, decl) + "+" + n;
            string ns = r.GetString(t.Namespace);
            return ns.Length == 0 ? n : ns + "." + n;
        }
        public static string RefName(MetadataReader r, TypeReferenceHandle h)
        {
            var t = r.GetTypeReference(h); string n = r.GetString(t.Name);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference) return RefName(r, (TypeReferenceHandle)t.ResolutionScope) + "+" + n;
            string ns = r.GetString(t.Namespace);
            return ns.Length == 0 ? n : ns + "." + n;
        }
        public string GetPrimitiveType(PrimitiveTypeCode c)
        {
            switch (c)
            {
                case PrimitiveTypeCode.Void: return "System.Void";
                case PrimitiveTypeCode.Boolean: return "System.Boolean";
                case PrimitiveTypeCode.Char: return "System.Char";
                case PrimitiveTypeCode.SByte: return "System.SByte";
                case PrimitiveTypeCode.Byte: return "System.Byte";
                case PrimitiveTypeCode.Int16: return "System.Int16";
                case PrimitiveTypeCode.UInt16: return "System.UInt16";
                case PrimitiveTypeCode.Int32: return "System.Int32";
                case PrimitiveTypeCode.UInt32: return "System.UInt32";
                case PrimitiveTypeCode.Int64: return "System.Int64";
                case PrimitiveTypeCode.UInt64: return "System.UInt64";
                case PrimitiveTypeCode.Single: return "System.Single";
                case PrimitiveTypeCode.Double: return "System.Double";
                case PrimitiveTypeCode.String: return "System.String";
                case PrimitiveTypeCode.Object: return "System.Object";
                case PrimitiveTypeCode.IntPtr: return "System.IntPtr";
                case PrimitiveTypeCode.UIntPtr: return "System.UIntPtr";
                default: return c.ToString();
            }
        }
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) { return DefName(reader, handle); }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) { return RefName(reader, handle); }
        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            var br = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
            return new SignatureDecoder<string, object>(this, reader, genericContext).DecodeType(ref br);
        }
        public string GetSZArrayType(string e) { return e + "[]"; }
        public string GetArrayType(string e, ArrayShape s) { return e + "[" + new string(',', s.Rank - 1) + "]"; }
        public string GetByReferenceType(string e) { return e + "&"; }
        public string GetPointerType(string e) { return e + "*"; }
        public string GetGenericInstantiation(string g, ImmutableArray<string> args) { return g + "<" + string.Join(",", args) + ">"; }
        public string GetGenericMethodParameter(object ctx, int i) { return "!!" + i; }
        public string GetGenericTypeParameter(object ctx, int i) { return "!" + i; }
        public string GetFunctionPointerType(MethodSignature<string> s) { return "fnptr"; }
        public string GetModifiedType(string modifier, string unmodified, bool isRequired) { return unmodified; }
        public string GetPinnedType(string e) { return e; }
    }

    // ---------- stand-ins for game types, patched through the real Installer ----------
}

namespace Fake
{
        public enum RunPhase { Zero = 0, Gen = 7 }
        public enum Pass { None = 0, Terrain = 3 }
        public enum Side { Client = 1, Server = 2 }
        public enum ModPhase { Start = 5 }
        public class Req { public int ChunkX; public int ChunkZ; public Pass CurrentIncompletePass; }
        public class ModInfo { public string ModID { get; set; } }
        public class Mod { public ModInfo Info { get; set; } }
        public class ModSystem { }
        public interface IApi { Side Side { get; } }
        public class Api : IApi { public Side Side { get; set; } }

        public class Boot
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Entry(string[] rawArgs) { }
        }

        public class Server
        {
            public static int EnterCalls;
            public static int LoadCalls;
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void EnterRunPhase(RunPhase p) { EnterCalls++; }
            [MethodImpl(MethodImplOptions.NoInlining)]
            public bool loadOrGenerateChunkColumn_OnChunkThread(Req r, int stage) { LoadCalls++; if (r.ChunkX == 777) Thread.Sleep(5); return true; }
        }

        public class Loader
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            private bool TryRunModPhase(Mod mod, ModSystem system, IApi api, ModPhase phase) { return true; }
            public bool CallTry(Mod m, IApi a) { return TryRunModPhase(m, new ModSystem(), a, ModPhase.Start); }
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void LoadMods(List<string> a, IEnumerable<string> b) { }
        }

        public class Handler
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void OnLoadAssets()
            {
                Vintagestory.ModDb.ModDbUtil.ModBlockListMRE.WaitOne();
                new Loader().LoadMods(null, null);
            }
        }
}

namespace Vintagestory.ModDb
{
    public static class ModDbUtil
    {
        public static readonly System.Threading.ManualResetEvent ModBlockListMRE = new System.Threading.ManualResetEvent(false);
    }
}

namespace SparkLoad.Hook.Tests
{
    internal static class Program
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        static readonly string Targets = Path.Combine(Root, "targets", "targets.json");
        static readonly string TestRun = Path.Combine(Root, "runs", "hook-tests-run");

        static readonly string[][] ExpectedNames =
        {
            null,
            new[] { "pid", "qpc", "qpcFreq", "hookVersion", "runtime" },
            new[] { "markerId", "name" },
            new[] { "markerId", "tid", "seq", "qpc", "a", "b", "c", "s" },
            new[] { "markerId", "tid", "seq", "qpc", "a", "b", "c", "s" },
            new[] { "tid", "managedId", "name" },
            new[] { "marker", "target", "status", "detail" },
            new[] { "tid", "seq", "qpc", "index", "durTicks" },
            new[] { "tid", "seq", "qpc", "count", "sumTicks", "maxTicks", "over50" },
            new[] { "tid", "seq", "qpc", "frameIndex" },
            new[] { "marker", "hits" },
        };
        static readonly string[] EventNames = { null, "Hello", "MarkerDef", "Begin", "End", "Thread", "Patch", "Frame", "FrameAgg", "Playable", "Stats" };

        static readonly string[] SpecMarkers =
        {
            "client.boot.main", "client.boot.screenmanager", "client.boot.stage1", "client.boot.stage2", "client.boot.stage3",
            "client.boot.stage4", "client.boot.stage5", "server.launch", "server.phase", "mod.phase", "server.blocklist.wait",
            "server.blocklist.set", "server.remap.blocks", "server.remap.items", "server.save.load", "server.spawn.pregen",
            "column.pass", "column.load", "client.assets.handle", "client.assets.finish", "client.shapes.block", "client.shapes.item",
            "client.shapes.entity", "client.atlas.A", "client.atlas.B", "client.atlas.C", "client.handbook",
            "client.finalize.handle", "client.finalize.trigger", "frame",
        };

        static int Main()
        {
            try
            {
                Directory.CreateDirectory(TestRun);
                foreach (string f in Directory.GetFiles(TestRun)) { try { File.Delete(f); } catch { } }
                SectionA_Assembly();
                SectionB_Signatures();
                SectionCD_EventsAndFailSafe();
            }
            catch (Exception e)
            {
                T.Check("harness completed without an unhandled exception", false, e.ToString());
            }
            Console.WriteLine("tests passed=" + T.Pass + " failed=" + T.Fail);
            return T.Fail == 0 ? 0 : 1;
        }

        // ================= (a) hook assembly =================
        static void SectionA_Assembly()
        {
            Console.WriteLine("== (a) hook assembly");
            Assembly hook = typeof(Core).Assembly;
            T.Check("a1 hook assembly loaded as SparkLoad.Hook", hook.GetName().Name == "SparkLoad.Hook");
            T.Note("hook dll: " + hook.Location);
            Type sh = hook.GetType("StartupHook", false);
            MethodInfo init = sh == null ? null : sh.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            T.Check("a2 global StartupHook.Initialize() public static void", init != null && init.ReturnType == typeof(void) && init.GetParameters().Length == 0);

            // Harmony usage: no transpilers, only the Patch entry points.
            using (var fs = new FileStream(hook.Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var pe = new PEReader(fs))
            {
                MetadataReader r = pe.GetMetadataReader();
                var harmonyTypes = new SortedSet<string>();
                var harmonyMembers = new SortedSet<string>();
                foreach (var h in r.TypeReferences)
                {
                    var t = r.GetTypeReference(h);
                    if (r.GetString(t.Namespace) == "HarmonyLib") harmonyTypes.Add(r.GetString(t.Name));
                }
                foreach (var h in r.MemberReferences)
                {
                    var m = r.GetMemberReference(h);
                    if (m.Parent.Kind != HandleKind.TypeReference) continue;
                    var tr = r.GetTypeReference((TypeReferenceHandle)m.Parent);
                    if (r.GetString(tr.Namespace) == "HarmonyLib") harmonyMembers.Add(r.GetString(tr.Name) + "." + r.GetString(m.Name));
                }
                T.Note("HarmonyLib types referenced: " + string.Join(",", harmonyTypes));
                T.Note("HarmonyLib members referenced: " + string.Join(",", harmonyMembers));
                T.Check("a3 only Harmony and HarmonyMethod are referenced", harmonyTypes.SetEquals(new[] { "Harmony", "HarmonyMethod" }));
                T.Check("a4 no transpiler or reverse patch member referenced",
                    !harmonyMembers.Any(x => x.IndexOf("Transpiler", StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf("Reverse", StringComparison.OrdinalIgnoreCase) >= 0)
                    && harmonyMembers.Any(x => x.StartsWith("Harmony.Patch")));
            }

            // table sanity
            T.Check("a5 marker count 31 (30 SPEC markers plus client.boot.start)", Mk.Count == 31 && Mk.Names.Length == 32);
            T.Check("a6 every SPEC marker name present", SpecMarkers.All(m => Array.IndexOf(Mk.Names, m) > 0));
            var missingMethods = new List<string>();
            foreach (TargetRow row in TargetTable.Rows)
            {
                foreach (string n in new[] { row.Pre, row.Post })
                {
                    if (n == null) continue;
                    var mi = typeof(Patches).GetMethod(n, BindingFlags.Public | BindingFlags.Static);
                    if (mi == null || mi.ReturnType != typeof(void)) missingMethods.Add(row.Id + ":" + n);
                }
                foreach (string m in row.Markers) if (Array.IndexOf(Mk.Names, m) <= 0) missingMethods.Add(row.Id + ":marker " + m);
            }
            T.Check("a7 every table row has its prefix/postfix methods and known markers", missingMethods.Count == 0, string.Join(",", missingMethods));
            T.Check("a8 table has 31 rows (30 targets plus ML_LoadMods)", TargetTable.Rows.Length == 31);
        }

        // ================= (b) 30 targets against the game dlls =================
        static Dictionary<string, List<Cand>> LoadCandidates(string dll, Func<string, bool> wantType)
        {
            var result = new Dictionary<string, List<Cand>>();
            using (var fs = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var pe = new PEReader(fs))
            {
                MetadataReader r = pe.GetMetadataReader();
                var tp = new TP();
                foreach (var th in r.TypeDefinitions)
                {
                    string tn = TP.DefName(r, th);
                    if (!wantType(tn)) continue;
                    var list = new List<Cand>();
                    foreach (var mh in r.GetTypeDefinition(th).GetMethods())
                    {
                        var md = r.GetMethodDefinition(mh);
                        MethodSignature<string> sig = md.DecodeSignature(tp, null);
                        list.Add(new Cand
                        {
                            Name = r.GetString(md.Name),
                            IsStatic = (md.Attributes & MethodAttributes.Static) != 0,
                            Ret = sig.ReturnType,
                            Params = sig.ParameterTypes.ToArray(),
                        });
                    }
                    result[tn] = list;
                }
            }
            return result;
        }

        static void SectionB_Signatures()
        {
            Console.WriteLine("== (b) 30 target signatures vs game dlls (metadata only)");
            using (var doc = JsonDocument.Parse(File.ReadAllText(Targets)))
            {
                JsonElement[] jrows = doc.RootElement.EnumerateArray().ToArray();
                T.Check("b0 targets.json has 30 rows", jrows.Length == 30);
                var byId = TargetTable.Rows.ToDictionary(x => x.Id);
                var covered = new HashSet<string>();

                foreach (string set in new[] { "stock", "optimum" })
                {
                    var neededTypes = new HashSet<string>(TargetTable.Rows.Select(x => x.TypeName));
                    var cache = new Dictionary<string, Dictionary<string, List<Cand>>>();
                    int ok = 0, bad = 0;
                    var badList = new List<string>();
                    // dll per assembly name from targets.json
                    var dllOf = new Dictionary<string, string>();
                    foreach (JsonElement jr in jrows)
                        dllOf[jr.GetProperty("assembly").GetString()] = jr.GetProperty(set).GetProperty("dllPath").GetString();
                    foreach (var kv in dllOf)
                        cache[kv.Key] = LoadCandidates(kv.Value, tn => neededTypes.Contains(tn));
                    foreach (string a in dllOf.Keys) T.Note(set + " " + a + " <- " + dllOf[a]);

                    foreach (TargetRow row in TargetTable.Rows)
                    {
                        var types = cache[row.Assembly];
                        List<Cand> cands;
                        if (!types.TryGetValue(row.TypeName, out cands)) { bad++; badList.Add(row.Id + " type missing"); continue; }
                        var idx = new List<int>();
                        string detail;
                        string status = Sig.Select(cands, row, idx, out detail);
                        bool good = status == "ok" && idx.Count >= 1;
                        if (row.Id == "SSSC_PopulateChunk")
                        {
                            int want = set == "stock" ? 1 : 2;
                            good = good && idx.Count == want;
                            T.Note(set + " PopulateChunk matches=" + idx.Count + " returns=" + string.Join("+", idx.Select(i => cands[i].Ret)));
                        }
                        else good = good && idx.Count == 1;
                        if (good) { ok++; covered.Add(row.Id); } else { bad++; badList.Add(row.Id + " " + status + " " + detail + " matches=" + idx.Count); }
                    }
                    T.Check("b1 " + set + ": all 31 table rows resolve by exact signature (PopulateChunk overloads: "
                        + (set == "stock" ? "1" : "2 by return type") + ")", bad == 0 && ok == 31, string.Join(" | ", badList));
                }

                // table rows correspond one to one to targets.json (plus ML_LoadMods)
                var jsonIds = new HashSet<string>(jrows.Select(j => j.GetProperty("marker").GetString()));
                var tableIds = new HashSet<string>(TargetTable.Rows.Select(x => x.Id));
                tableIds.ExceptWith(new[] { "ML_LoadMods" });
                T.Check("b2 table ids equal targets.json ids (30)", jsonIds.SetEquals(tableIds));

                // the table agrees with targets.json field by field
                var diffs = new List<string>();
                foreach (JsonElement jr in jrows)
                {
                    TargetRow row;
                    if (!byId.TryGetValue(jr.GetProperty("marker").GetString(), out row)) continue;
                    var jparams = jr.GetProperty("params").EnumerateArray().Select(p => p.GetProperty("type").GetString()).ToArray();
                    var jrets = new List<string> { jr.GetProperty("returnType").GetString() };
                    foreach (JsonElement o in jr.GetProperty("optimumExtraOverloads").EnumerateArray()) jrets.Add(o.GetProperty("returnType").GetString());
                    if (!jparams.SequenceEqual(row.Params)) diffs.Add(row.Id + " params");
                    if (!jrets.SequenceEqual(row.Returns)) diffs.Add(row.Id + " returns");
                    if (row.Method != jr.GetProperty("method").GetString() || row.TypeName != jr.GetProperty("type").GetString()
                        || row.IsStatic != jr.GetProperty("isStatic").GetBoolean() || row.Assembly != jr.GetProperty("assembly").GetString())
                        diffs.Add(row.Id + " identity");
                }
                T.Check("b3 embedded table matches targets.json (type, method, static, params, return variants)", diffs.Count == 0, string.Join(",", diffs));

                // negative control: a wrong signature must be rejected against a real dll
                string lib = doc.RootElement.EnumerateArray().First(j => j.GetProperty("assembly").GetString() == "VintagestoryLib")
                    .GetProperty("stock").GetProperty("dllPath").GetString();
                var cc = LoadCandidates(lib, tn => tn == "Vintagestory.Server.ServerMain");
                var bogus = new TargetRow("BOGUS", new[] { "server.phase" }, "VintagestoryLib", "Vintagestory.Server.ServerMain", "EnterRunPhase", false,
                    new[] { "System.Void" }, new[] { "System.Int32" }, "Phase_Pre", "Phase_Post");
                var bi = new List<int>(); string bd;
                string bs = Sig.Select(cc["Vintagestory.Server.ServerMain"], bogus, bi, out bd);
                T.Check("b4 negative control: wrong parameter type gives mismatch against the real dll", bs == "mismatch" && bi.Count == 0, bs + " " + bd);
                var bogus2 = new TargetRow("BOGUS2", new[] { "server.phase" }, "VintagestoryLib", "Vintagestory.Server.ServerMain", "NoSuchMethod", false,
                    new[] { "System.Void" }, new string[0], "Phase_Pre", "Phase_Post");
                string bs2 = Sig.Select(cc["Vintagestory.Server.ServerMain"], bogus2, new List<int>(), out bd);
                T.Check("b5 negative control: unknown method gives missing against the real dll", bs2 == "missing", bs2);
            }
        }

        // ================= (c) events, (d) fail-safe =================
        static string Vals(Rec r, string name)
        {
            int i = Array.IndexOf(r.Names, name);
            return i < 0 ? null : r.Vals[i];
        }

        static void SectionCD_EventsAndFailSafe()
        {
            Console.WriteLine("== (c) events in-process and through EventPipe, (d) fail-safe");

            // --- role check: noop paths must not create the source ---
            string logPath = Path.Combine(TestRun, "hook.log");
            Environment.SetEnvironmentVariable("SPARKLOAD_RUN_DIR", TestRun);
            Environment.SetEnvironmentVariable("SPARKLOAD_TARGET_EXE", null);
            Environment.SetEnvironmentVariable("SPARKLOAD_LAUNCHER_PID", null);
            StartupHook.Initialize();
            string log1 = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
            T.Check("c0a role check: no SPARKLOAD_TARGET_EXE gives role=noop, no event source", log1.Contains("role=noop") && Core.Src == null, log1);
            Environment.SetEnvironmentVariable("SPARKLOAD_TARGET_EXE", Environment.ProcessPath);
            Environment.SetEnvironmentVariable("SPARKLOAD_LAUNCHER_PID", "1");
            StartupHook.Initialize();
            string log2 = File.ReadAllText(logPath);
            T.Check("c0b role check: wrong launcher pid gives role=noop (parent pid mismatch), no event source",
                log2.Contains("parent pid") && Core.Src == null, log2);

            // --- EventPipe session on this process, then the listener, then the real entry point ---
            var ms = new MemoryStream();
            EventPipeSession session = null;
            Task copy = null;
            try
            {
                var client = new DiagnosticsClient(Environment.ProcessId);
                var prov = new EventPipeProvider("SparkLoad", EventLevel.Verbose, unchecked((long)0xFFFFFFFFFFFFFFFF));
                session = client.StartEventPipeSession(new[] { prov }, false, 64);
                copy = Task.Run(() => { try { session.EventStream.CopyTo(ms); } catch { } });
                T.Check("c1 EventPipe session on this process started", true);
            }
            catch (Exception e)
            {
                T.Check("c1 EventPipe session on this process started", false, e.GetType().Name + " " + e.Message);
            }

            var cap = new Cap();

            long parent = Native.GetParentPid();
            Environment.SetEnvironmentVariable("SPARKLOAD_LAUNCHER_PID", parent.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("SPARKLOAD_OFF", null);
            StartupHook.Initialize(); // game role: role=game, EventSource created, definitions emitted
            string log3 = File.ReadAllText(logPath);
            T.Check("c2 role=game with matching exe and parent pid; event source created", log3.Contains("role=game") && Core.Src != null && Core.Ready, log3);
            T.Check("c2b AppContext SparkLoad.RunDir set", (AppContext.GetData("SparkLoad.RunDir") as string) == TestRun);
            T.Check("c2c EventSource constructed without exception", Core.Src.ConstructionException == null, Convert.ToString(Core.Src.ConstructionException));
            T.Check("c2d startup path did not load 0Harmony (Harmony is touched only when a patch is applied)",
                !AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "0Harmony"));
            Environment.SetEnvironmentVariable("SPARKLOAD_TARGET_EXE", null);

            // --- driven events on a dedicated thread (seq starts at 1) ---
            const long Q = 123456789012L;
            int workerTid = 0;
            var th = new Thread(() =>
            {
                workerTid = (int)Native.GetCurrentThreadId();
                Emit.Begin(Mk.ColumnPass, Q, 11, -22, 3, "str");
                Emit.End(Mk.ColumnPass, Q + 5, 11, -22, 3, null);
                Emit.Frame(Q + 6, 7, 1234);
                Emit.FrameAgg(Q + 7, 60, 600, 50, 2);
                Emit.Playable(Q + 8, 60);
            });
            th.Name = "hooktest-worker";
            th.Start(); th.Join();

            // --- (d) fail-safe on the embedded table: nothing loaded, every target reported missing, no throw ---
            Exception thrown = null;
            try { Core.StatsAll(); } catch (Exception e) { thrown = e; }
            T.Check("d1 StatsAll with no game assemblies loaded does not throw", thrown == null, Convert.ToString(thrown));
            var afterStats = cap.Snapshot().Where(e => e.Id == 6).ToList();
            T.Check("d1b embedded table: every one of the 31 table rows reported missing (assembly not loaded) per marker, 33 events",
                afterStats.Count == 33 && afterStats.All(e => Vals(e, "status") == "missing" && Vals(e, "detail").Contains("not loaded"))
                && afterStats.Select(e => Vals(e, "marker")).Distinct().Count() == 31, "events=" + afterStats.Count);

            // --- (d) fake game types patched through the real Installer ---
            Fail_Safe_And_Patching(cap);

            // --- frame logic: finalize, 130 frames at 10 ms, one over-50 ms frame later ---
            long f = Stopwatch.Frequency;
            FrameStats.OnFinalizeEnd();
            long t0 = 5_000_000_000L;
            for (int i = 0; i < 130; i++) FrameStats.OnFrame(t0 + i * (f / 100), t0 + i * (f / 100) + f / 1000);
            Core.StatsAll();

            // --- late attach: a listener created now gets the definitions again (Replay) ---
            var late = new Cap();
            Thread.Sleep(300);

            if (session != null)
            {
                try { session.Stop(); } catch (Exception e) { T.Note("session.Stop: " + e.GetType().Name); }
                try { copy.Wait(10000); } catch { }
                try { session.Dispose(); } catch { }
            }

            // ---- analysis of the in-process capture ----
            List<Rec> ev = cap.Snapshot();
            List<Rec> lateEv = late.Snapshot();
            T.Note("in-process captured events: " + ev.Count + ", late listener: " + lateEv.Count + ", errors: " + cap.Errors.Count);
            T.Check("c3 no EventSource error or manifest messages", cap.Errors.Count == 0 && late.Errors.Count == 0, string.Join(" | ", cap.Errors.Concat(late.Errors)));
            T.Check("c4 late-attach Replay delivered Hello and all 31 MarkerDefs",
                lateEv.Count(e => e.Id == 1) >= 1 && lateEv.Count(e => e.Id == 2) >= 31);

            var all = ev.Concat(lateEv).ToList();
            for (int id = 1; id <= 10; id++)
            {
                Rec first = all.FirstOrDefault(e => e.Id == id);
                bool have = first != null;
                T.Check("c5." + id + " in-process: event " + id + " " + EventNames[id] + " seen with payload names in SPEC order",
                    have && first.Names.SequenceEqual(ExpectedNames[id]) && first.Name == EventNames[id],
                    have ? string.Join(",", first.Names) + " / " + first.Name : "never seen");
            }

            // exact positional values of the driven events
            Rec b = ev.FirstOrDefault(e => e.Id == 3 && Vals(e, "qpc") == Q.ToString());
            T.Check("c6 Begin payload values positional (markerId=17 tid seq=1 qpc a=11 b=-22 c=3 s=str)",
                b != null && b.Vals.SequenceEqual(new[] { Mk.ColumnPass.ToString(), workerTid.ToString(), "1", Q.ToString(), "11", "-22", "3", "str" }),
                b == null ? "missing" : string.Join(",", b.Vals));
            Rec en = ev.FirstOrDefault(e => e.Id == 4 && Vals(e, "qpc") == (Q + 5).ToString());
            T.Check("c7 End payload values positional (seq=2; null string becomes empty)",
                en != null && en.Vals.SequenceEqual(new[] { Mk.ColumnPass.ToString(), workerTid.ToString(), "2", (Q + 5).ToString(), "11", "-22", "3", "" }),
                en == null ? "missing" : string.Join(",", en.Vals));
            Rec fr = ev.FirstOrDefault(e => e.Id == 7 && Vals(e, "qpc") == (Q + 6).ToString());
            T.Check("c8 Frame values (tid seq=3 qpc index=7 dur=1234)", fr != null && fr.Vals.SequenceEqual(new[] { workerTid.ToString(), "3", (Q + 6).ToString(), "7", "1234" }), fr == null ? "missing" : string.Join(",", fr.Vals));
            Rec fa = ev.FirstOrDefault(e => e.Id == 8 && Vals(e, "qpc") == (Q + 7).ToString());
            T.Check("c9 FrameAgg values (seq=4 count=60 sum=600 max=50 over50=2)", fa != null && fa.Vals.SequenceEqual(new[] { workerTid.ToString(), "4", (Q + 7).ToString(), "60", "600", "50", "2" }), fa == null ? "missing" : string.Join(",", fa.Vals));
            Rec pl = ev.FirstOrDefault(e => e.Id == 9 && Vals(e, "qpc") == (Q + 8).ToString());
            T.Check("c10 Playable values (seq=5 frameIndex=60)", pl != null && pl.Vals.SequenceEqual(new[] { workerTid.ToString(), "5", (Q + 8).ToString(), "60" }), pl == null ? "missing" : string.Join(",", pl.Vals));
            Rec thr = ev.FirstOrDefault(e => e.Id == 5 && Vals(e, "tid") == workerTid.ToString());
            T.Check("c11 Thread announced once per thread with managed id and name",
                thr != null && Vals(thr, "name") == "hooktest-worker" && ev.Count(e => e.Id == 5 && Vals(e, "tid") == workerTid.ToString()) == 1,
                thr == null ? "missing" : string.Join(",", thr.Vals));
            Rec hello = lateEv.FirstOrDefault(e => e.Id == 1);
            T.Check("c12 Hello values (pid, qpcFreq, version 0.1.0, runtime text)",
                hello != null && Vals(hello, "pid") == Environment.ProcessId.ToString() && Vals(hello, "qpcFreq") == Stopwatch.Frequency.ToString()
                && Vals(hello, "hookVersion") == "0.1.0" && Vals(hello, "runtime").Length > 0, hello == null ? "missing" : string.Join(",", hello.Vals));
            var defs = lateEv.Where(e => e.Id == 2).Take(31).ToList();
            bool defsOk = defs.Count == 31;
            for (int i = 0; defsOk && i < 31; i++) defsOk = Vals(defs[i], "markerId") == (i + 1).ToString() && Vals(defs[i], "name") == Mk.Names[i + 1];
            T.Check("c13 MarkerDef 1..31 carry the table ids and names", defsOk);
            T.Check("c14 FrameStats: exactly 120 Frame events, one Playable at frame 60, at least one FrameAgg",
                ev.Count(e => e.Id == 7 && Vals(e, "tid") != workerTid.ToString()) == 120
                && ev.Count(e => e.Id == 9 && Vals(e, "tid") != workerTid.ToString() && Vals(e, "frameIndex") == "60") == 1
                && ev.Count(e => e.Id == 8 && Vals(e, "tid") != workerTid.ToString()) >= 1,
                "frames=" + ev.Count(e => e.Id == 7) + " playable=" + ev.Count(e => e.Id == 9) + " agg=" + ev.Count(e => e.Id == 8));
            var stats = ev.Where(e => e.Id == 10).ToList();
            var badStats = new List<string>();
            for (int i = 1; i <= Mk.Count; i++)
            {
                Rec last = stats.LastOrDefault(e => Vals(e, "marker") == Mk.Names[i]);
                if (last == null || Vals(last, "hits") != Interlocked.Read(ref Core.Hits[i]).ToString()) badStats.Add(Mk.Names[i]);
            }
            T.Check("c15 final Stats events carry the hit counter of every marker (31) plus hook.faults; server.phase=1 column.pass=3 blocklist.wait=2",
                badStats.Count == 0 && stats.Any(e => Vals(e, "marker") == "hook.faults")
                && Core.Hits[Mk.ServerPhase] == 1 && Core.Hits[Mk.ColumnPass] == 3 && Core.Hits[Mk.ServerBlocklistWait] == 2,
                string.Join(",", badStats) + " phase=" + Core.Hits[Mk.ServerPhase] + " pass=" + Core.Hits[Mk.ColumnPass] + " wait=" + Core.Hits[Mk.ServerBlocklistWait]);

            // ---- EventPipe (nettrace) ----
            if (session != null)
            {
                string nt = Path.Combine(TestRun, "hook-selftrace.nettrace");
                File.WriteAllBytes(nt, ms.ToArray());
                T.Note("nettrace bytes: " + ms.Length + " saved " + nt);
                var np = new List<Rec>();
                int totalNt = 0;
                try
                {
                    using (var fs = new FileStream(nt, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var src = new EventPipeEventSource(fs))
                    {
                        src.Dynamic.All += e =>
                        {
                            if (!string.Equals(e.ProviderName, "SparkLoad", StringComparison.Ordinal)) return;
                            totalNt++;
                            int id = Array.IndexOf(EventNames, e.EventName);
                            if (id <= 0) return;
                            string[] names = e.PayloadNames ?? new string[0];
                            var vals = new string[names.Length];
                            for (int i = 0; i < names.Length; i++) vals[i] = Convert.ToString(e.PayloadValue(i), CultureInfo.InvariantCulture);
                            np.Add(new Rec { Id = id, Name = e.EventName, Names = names, Vals = vals });
                        };
                        src.Process();
                    }
                }
                catch (Exception e)
                {
                    T.Check("c16 nettrace parsed", false, e.GetType().Name + " " + e.Message);
                }
                T.Note("SparkLoad events in nettrace: " + totalNt + " (named 1..10: " + np.Count + ")");
                // everything written after the session started is in the in-process capture (cap, created after Initialize) plus the
                // definitions emitted during Initialize, which only the session saw: compare per id against cap + late replay.
                for (int id = 1; id <= 10; id++)
                {
                    Rec first = np.FirstOrDefault(e => e.Id == id);
                    T.Check("c17." + id + " nettrace: event " + id + " " + EventNames[id] + " present, payload names in SPEC order",
                        first != null && first.Names.SequenceEqual(ExpectedNames[id]),
                        first == null ? "LOST in nettrace" : string.Join(",", first.Names));
                }
                // content equality: every in-process driven event must exist in nettrace with identical payload
                var ntKeys = new HashSet<string>(np.Select(e => e.Key));
                var ntCount = np.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
                int missing = 0; var missList = new List<string>();
                foreach (Rec e in ev)
                {
                    if (!ntKeys.Contains(e.Key)) { missing++; if (missList.Count < 5) missList.Add(e.Key); }
                }
                T.Check("c18 every in-process event (" + ev.Count + ") is in the nettrace with identical positional payload", missing == 0, "missing " + missing + ": " + string.Join(" || ", missList));
                // per-id counts of in-process events must not exceed nettrace counts
                var shortIds = new List<string>();
                for (int id = 1; id <= 10; id++)
                {
                    int a = ev.Count(e => e.Id == id), n2 = np.Count(e => e.Id == id);
                    if (n2 < a) shortIds.Add(id + ":" + n2 + "<" + a);
                }
                T.Check("c19 nettrace holds at least as many events of each id as the in-process listener", shortIds.Count == 0, string.Join(",", shortIds));
                Rec nb = np.FirstOrDefault(e => e.Id == 3 && Vals(e, "qpc") == Q.ToString());
                T.Check("c20 nettrace Begin payload equals the driven values positionally",
                    nb != null && nb.Vals.SequenceEqual(new[] { Mk.ColumnPass.ToString(), workerTid.ToString(), "1", Q.ToString(), "11", "-22", "3", "str" }),
                    nb == null ? "missing" : string.Join(",", nb.Vals));
                T.Check("c21 nettrace holds the 31 Hello-time MarkerDefs and a Hello (written during Initialize, before the listener)",
                    np.Count(e => e.Id == 2) >= 31 && np.Any(e => e.Id == 1 && Vals(e, "hookVersion") == "0.1.0"));
                T.Check("c22 nettrace sequence per thread is gapless (seq 1..n for the worker thread)",
                    np.Where(e => (e.Id == 3 || e.Id == 4 || e.Id == 7 || e.Id == 8 || e.Id == 9) && Vals(e, "tid") == workerTid.ToString())
                      .Select(e => long.Parse(Vals(e, "seq"))).OrderBy(x => x).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }));
            }
            else
            {
                T.Check("c16 nettrace parsed", false, "no EventPipe session");
            }
        }

        // ---- (d) negative and positive patching with stand-in game types ----
        static void Fail_Safe_And_Patching(Cap cap)
        {
            Assembly self = typeof(Program).Assembly;
            string sa = self.GetName().Name;

            // dynamic assembly with a type that has PopulateChunk(Req) twice: once void, once bool (Optimum shape)
            AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FakeVSLib"), AssemblyBuilderAccess.Run);
            ModuleBuilder mb = ab.DefineDynamicModule("FakeVSLib");
            TypeBuilder tb = mb.DefineType("Fake.SupplyChunks", TypeAttributes.Public | TypeAttributes.Class);
            tb.DefineDefaultConstructor(MethodAttributes.Public);
            MethodBuilder m1 = tb.DefineMethod("PopulateChunk", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), new[] { typeof(Fake.Req) });
            m1.GetILGenerator().Emit(OpCodes.Ret);
            MethodBuilder m2 = tb.DefineMethod("PopulateChunk", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(bool), new[] { typeof(Fake.Req) });
            ILGenerator g2 = m2.GetILGenerator(); g2.Emit(OpCodes.Ldc_I4_1); g2.Emit(OpCodes.Ret);
            Type fakeSupply = tb.CreateType();

            // ---- negative rows first: must report and never throw ----
            var neg = new[]
            {
                new TargetRow("N_type", new[] { "client.boot.stage1" }, sa, "Fake.NoSuchType", "X", false, new[] { "System.Void" }, new string[0], "Span_3_Pre", "Span_3_Post"),
                new TargetRow("N_method", new[] { "client.boot.stage2" }, sa, "Fake.Server", "NoSuchMethod", false, new[] { "System.Void" }, new string[0], "Span_4_Pre", "Span_4_Post"),
                new TargetRow("N_params", new[] { "client.boot.stage3" }, sa, "Fake.Server", "EnterRunPhase", false, new[] { "System.Void" }, new[] { "System.Int32" }, "Span_5_Pre", "Span_5_Post"),
                new TargetRow("N_return", new[] { "client.boot.stage4" }, sa, "Fake.Server", "EnterRunPhase", false, new[] { "System.Int32" }, new[] { "Fake.RunPhase" }, "Span_6_Pre", "Span_6_Post"),
                new TargetRow("N_hook", new[] { "client.boot.stage5" }, sa, "Fake.Server", "EnterRunPhase", false, new[] { "System.Void" }, new[] { "Fake.RunPhase" }, "NoSuchPrefix", null),
                new TargetRow("N_off", new[] { "server.launch" }, sa, "Fake.Server", "EnterRunPhase", false, new[] { "System.Void" }, new[] { "Fake.RunPhase" }, "Span_8_Pre", "Span_8_Post"),
            };
            Installer.RowsOverride = neg;
            Core.On[Mk.ServerLaunch] = false;
            Exception thrown = null;
            try { Installer.InstallFor(self); } catch (Exception e) { thrown = e; }
            Core.On[Mk.ServerLaunch] = true;
            T.Check("d2 six bad targets (type, method, params, return, hook method, off) do not throw", thrown == null, Convert.ToString(thrown));
            Thread.Sleep(100);
            var snap = cap.Snapshot().Where(e => e.Id == 6).ToList();
            Func<string, string> st = target => { Rec r = snap.LastOrDefault(e => Vals(e, "target") == target); return r == null ? "(none)" : Vals(r, "status"); };
            T.Check("d3 missing type reported status=missing", snap.Any(e => Vals(e, "marker") == "client.boot.stage1" && Vals(e, "status") == "missing"));
            T.Check("d4 missing method reported status=missing", snap.Any(e => Vals(e, "marker") == "client.boot.stage2" && Vals(e, "status") == "missing"));
            T.Check("d5 wrong params reported status=mismatch", snap.Any(e => Vals(e, "marker") == "client.boot.stage3" && Vals(e, "status") == "mismatch"));
            T.Check("d6 wrong return type reported status=mismatch", snap.Any(e => Vals(e, "marker") == "client.boot.stage4" && Vals(e, "status") == "mismatch"));
            T.Check("d7 absent hook method reported status=error", snap.Any(e => Vals(e, "marker") == "client.boot.stage5" && Vals(e, "status") == "error"));
            T.Check("d8 SPARKLOAD_OFF-style switch reported status=off and nothing patched", snap.Any(e => Vals(e, "marker") == "server.launch" && Vals(e, "status") == "off"));
            int before = Fake.Server.EnterCalls;
            new Fake.Server().EnterRunPhase(Fake.RunPhase.Zero);
            T.Check("d9 stood-down targets left the method unpatched and callable", Fake.Server.EnterCalls == before + 1
                && !cap.Snapshot().Any(e => e.Id == 3 && Vals(e, "markerId") == Mk.ServerPhase.ToString()));
            T.Check("d10 hook.log carries the patch lines", File.ReadAllText(Path.Combine(TestRun, "hook.log")).Contains("patch client.boot.stage1 Fake.NoSuchType.X missing"));

            // ---- positive rows: real Harmony patches, real prefix/postfix bodies ----
            var pos = new[]
            {
                new TargetRow("CP_Main", new[] { "client.boot.main" }, sa, "Fake.Boot", "Entry", true, new[] { "System.Void" }, new[] { "System.String[]" }, "Span_1_Pre", "Span_1_Post"),
                new TargetRow("SRV_EnterRunPhase", new[] { "server.phase" }, sa, "Fake.Server", "EnterRunPhase", false, new[] { "System.Void" }, new[] { "Fake.RunPhase" }, "Phase_Pre", "Phase_Post"),
                new TargetRow("SSSC_LoadOrGenColumn", new[] { "column.load" }, sa, "Fake.Server", "loadOrGenerateChunkColumn_OnChunkThread", false, new[] { "System.Boolean" }, new[] { "Fake.Req", "System.Int32" }, "ColLoad_Pre", "ColLoad_Post"),
                new TargetRow("ML_TryRunModPhase", new[] { "mod.phase" }, sa, "Fake.Loader", "TryRunModPhase", false, new[] { "System.Boolean" }, new[] { "Fake.Mod", "Fake.ModSystem", "Fake.IApi", "Fake.ModPhase" }, "ModPhase_Pre", "ModPhase_Post"),
                new TargetRow("SSMH_OnLoadAssets", new[] { "server.blocklist.wait", "server.blocklist.set" }, sa, "Fake.Handler", "OnLoadAssets", false, new[] { "System.Void" }, new string[0], "Blocklist_Pre", null),
                new TargetRow("ML_LoadMods", new[] { "server.blocklist.wait" }, sa, "Fake.Loader", "LoadMods", false, new[] { "System.Void" }, new[] { "System.Collections.Generic.List`1<System.String>", "System.Collections.Generic.IEnumerable`1<System.String>" }, "BlocklistEnd_Pre", null),
            };
            var posDyn = new[]
            {
                new TargetRow("SSSC_PopulateChunk", new[] { "column.pass" }, "FakeVSLib", "Fake.SupplyChunks", "PopulateChunk", false, new[] { "System.Void", "System.Boolean" }, new[] { "Fake.Req" }, "ColPass_Pre", "ColPass_Post"),
            };
            Installer.RowsOverride = pos.Concat(posDyn).ToArray();
            Exception t2 = null;
            try { Installer.InstallFor(self); Installer.InstallFor(fakeSupply.Assembly); } catch (Exception e) { t2 = e; }
            T.Check("d11 stand-in rows installed without throwing", t2 == null, Convert.ToString(t2));
            Thread.Sleep(100);
            snap = cap.Snapshot().Where(e => e.Id == 6).ToList();
            var bad = new List<string>();
            foreach (TargetRow r in Installer.RowsOverride)
                foreach (string mk in r.Markers)
                    if (!snap.Any(e => Vals(e, "marker") == mk && Vals(e, "target") == r.Target && Vals(e, "status") == "ok")) bad.Add(r.Id + "/" + mk + "=" + (snap.LastOrDefault(e => Vals(e, "target") == r.Target) == null ? "none" : Vals(snap.Last(e => Vals(e, "target") == r.Target), "detail")));
            T.Check("d12 every stand-in row reports status=ok through Harmony", bad.Count == 0, string.Join(",", bad));
            Rec pc = snap.LastOrDefault(e => Vals(e, "marker") == "column.pass");
            T.Check("d13 PopulateChunk: both overloads (void and bool) patched, selected by return type", pc != null && Vals(pc, "detail").StartsWith("2 overloads") && Vals(pc, "detail").Contains("Void") && Vals(pc, "detail").Contains("Boolean"), pc == null ? "none" : Vals(pc, "detail"));
            T.Check("d14 TryRunModPhase patch sets the HoldsTryRunModPhase switch", AppContext.TryGetSwitch("SparkLoad.Hook.HoldsTryRunModPhase", out bool held) && held);

            // drive them
            long faults0 = Interlocked.Read(ref Core.Faults);
            Fake.Boot.Entry(new string[0]);
            new Fake.Server().EnterRunPhase(Fake.RunPhase.Gen);
            var req = new Fake.Req { ChunkX = 101, ChunkZ = -202, CurrentIncompletePass = Fake.Pass.Terrain };
            bool lg = new Fake.Server().loadOrGenerateChunkColumn_OnChunkThread(req, 4);
            object sup = Activator.CreateInstance(fakeSupply);
            fakeSupply.GetMethods().Where(m => m.Name == "PopulateChunk" && m.ReturnType == typeof(void)).First().Invoke(sup, new object[] { req });
            object bres = fakeSupply.GetMethods().Where(m => m.Name == "PopulateChunk" && m.ReturnType == typeof(bool)).First().Invoke(sup, new object[] { req });
            bool tr = new Fake.Loader().CallTry(new Fake.Mod { Info = new Fake.ModInfo { ModID = "testmod" } }, new Fake.Api { Side = Fake.Side.Server });
            T.Check("d15 patched methods still return their original results", lg && (bool)bres && tr);
            Thread.Sleep(150);
            var ev = cap.Snapshot();
            Func<int, string, Rec> findSpan = (id, mk) => ev.LastOrDefault(e => e.Id == id && Vals(e, "markerId") == Array.IndexOf(Mk.Names, mk).ToString());
            Rec bs = findSpan(3, "server.phase"), es = findSpan(4, "server.phase");
            T.Check("d16 server.phase: enum boxed into object __0 by Harmony (a = 7 on Begin and End)", bs != null && es != null && Vals(bs, "a") == "7" && Vals(es, "a") == "7", bs == null ? "no Begin" : Vals(bs, "a"));
            Rec cl = findSpan(3, "column.load");
            T.Check("d17 column.load: x=101 z=-202 stage c=4", cl != null && Vals(cl, "a") == "101" && Vals(cl, "b") == "-202" && Vals(cl, "c") == "4", cl == null ? "none" : string.Join(",", cl.Vals));
            var cps = ev.Where(e => e.Id == 3 && Vals(e, "markerId") == Mk.ColumnPass.ToString() && Vals(e, "a") == "101").ToList();
            var cpe = ev.Where(e => e.Id == 4 && Vals(e, "markerId") == Mk.ColumnPass.ToString() && Vals(e, "a") == "101").ToList();
            T.Check("d18 column.pass: Begin and End for both overloads (2 each) with x=101 z=-202 c=3 (CurrentIncompletePass)",
                cps.Count == 2 && cpe.Count == 2 && cps.All(e => Vals(e, "b") == "-202" && Vals(e, "c") == "3") && cpe.All(e => Vals(e, "c") == "3"), cps.Count + "/" + cpe.Count);
            Rec mp = findSpan(3, "mod.phase");
            T.Check("d19 mod.phase: a=5, s=testmod|server", mp != null && Vals(mp, "a") == "5" && Vals(mp, "s") == "testmod|server", mp == null ? "none" : string.Join(",", mp.Vals));
            Rec mpe = findSpan(4, "mod.phase");
            T.Check("d20 mod.phase End carries the same key", mpe != null && Vals(mpe, "s") == "testmod|server");
            Rec bm = findSpan(3, "client.boot.main");
            T.Check("d21 simple span on a static method: client.boot.main Begin and End", bm != null && findSpan(4, "client.boot.main") != null);

            // column.load throttle: first 4 spans per column always, later ones only when >= 1 ms, the rest counted.
            {
                int before0 = cap.Snapshot().Count;
                var fastReq = new Fake.Req { ChunkX = 500, ChunkZ = 600, CurrentIncompletePass = Fake.Pass.Terrain };
                var slowReq = new Fake.Req { ChunkX = 777, ChunkZ = 888, CurrentIncompletePass = Fake.Pass.Terrain };
                var srvT = new Fake.Server();
                long hitsBefore = Interlocked.Read(ref Core.Hits[Mk.ColumnLoad]);
                for (int i = 0; i < 40; i++) srvT.loadOrGenerateChunkColumn_OnChunkThread(fastReq, 1);
                for (int i = 0; i < 10; i++) srvT.loadOrGenerateChunkColumn_OnChunkThread(slowReq, 1);
                Core.StatsAll();
                Thread.Sleep(200);
                var thr = cap.Snapshot().Skip(before0).ToList();
                string clId = Mk.ColumnLoad.ToString();
                int fastB = thr.Count(e => e.Id == 3 && Vals(e, "markerId") == clId && Vals(e, "a") == "500");
                int fastE = thr.Count(e => e.Id == 4 && Vals(e, "markerId") == clId && Vals(e, "a") == "500");
                int slowB = thr.Count(e => e.Id == 3 && Vals(e, "markerId") == clId && Vals(e, "a") == "777");
                int slowE = thr.Count(e => e.Id == 4 && Vals(e, "markerId") == clId && Vals(e, "a") == "777");
                T.Check("e1 throttle: fast column emits its first 4 spans (a few later ones only if a call took 1 ms), Begin = End", fastB >= 4 && fastB <= 10 && fastB == fastE, fastB + "/" + fastE);
                T.Check("e2 throttle: every slow (5 ms) call is emitted, none suppressed", slowB == 10 && slowE == 10, slowB + "/" + slowE);
                var supEv = thr.Where(e => e.Id == 10 && Vals(e, "marker") == "column.load.suppressed 500 600").ToList();
                long lastSup = supEv.Count > 0 ? long.Parse(Vals(supEv.Last(), "hits"), CultureInfo.InvariantCulture) : -1;
                T.Check("e3 throttle: suppressed total per column reaches Stats (power-of-two crossing and StatsAll), last = calls - emitted",
                    supEv.Count >= 2 && lastSup == 40 - fastB, "events=" + supEv.Count + " last=" + lastSup + " expected=" + (40 - fastB));
                var slowSup = thr.Where(e => e.Id == 10 && Vals(e, "marker") == "column.load.suppressed 777 888").ToList();
                T.Check("e4 throttle: slow column's final suppressed total is 0", slowSup.Count > 0 && Vals(slowSup.Last(), "hits") == "0", slowSup.Count == 0 ? "none" : Vals(slowSup.Last(), "hits"));
                T.Check("e5 throttle: Hits counts every call (50)", Interlocked.Read(ref Core.Hits[Mk.ColumnLoad]) - hitsBefore == 50, (Interlocked.Read(ref Core.Hits[Mk.ColumnLoad]) - hitsBefore).ToString());
            }

            // block-list wait: timed without a transpiler. Not yet set: Begin(wait,a=0), pool End(set,a=0), End(wait,a=0).
            ev = null;
            int n0 = cap.Snapshot().Count;
            var setter = new Thread(() => { Thread.Sleep(120); Vintagestory.ModDb.ModDbUtil.ModBlockListMRE.Set(); });
            setter.Start();
            new Fake.Handler().OnLoadAssets();
            setter.Join();
            Thread.Sleep(250);
            var newEv = cap.Snapshot().Skip(n0).ToList();
            Rec bw = newEv.FirstOrDefault(e => e.Id == 3 && Vals(e, "markerId") == Mk.ServerBlocklistWait.ToString());
            Rec ew = newEv.FirstOrDefault(e => e.Id == 4 && Vals(e, "markerId") == Mk.ServerBlocklistWait.ToString());
            Rec es2 = newEv.FirstOrDefault(e => e.Id == 4 && Vals(e, "markerId") == Mk.ServerBlocklistSet.ToString());
            T.Check("d22 blocklist (event not yet set): Begin(wait,a=0), End(set,a=0), End(wait,a=0) all emitted",
                bw != null && ew != null && es2 != null && Vals(bw, "a") == "0" && Vals(es2, "a") == "0" && Vals(ew, "a") == "0",
                (bw == null ? "noBeginWait " : "") + (ew == null ? "noEndWait " : "") + (es2 == null ? "noEndSet" : ""));
            if (bw != null && ew != null && es2 != null)
            {
                long qb = long.Parse(Vals(bw, "qpc")), qs = long.Parse(Vals(es2, "qpc")), qe = long.Parse(Vals(ew, "qpc"));
                double waitMs = (qe - qb) * 1000.0 / Stopwatch.Frequency, setMs = (qs - qb) * 1000.0 / Stopwatch.Frequency;
                T.Check("d23 blocklist timing: set fired inside the wait (about 120 ms), wait >= set, both from the real wait",
                    waitMs >= 100 && waitMs < 1500 && setMs >= 100 && qs <= qe + Stopwatch.Frequency / 10, "wait=" + waitMs.ToString("F1") + " set=" + setMs.ToString("F1"));
                T.Check("d24 blocklist: End(set) came from a different OS thread than the waiter", Vals(es2, "tid") != Vals(bw, "tid"));
            }
            // already set
            n0 = cap.Snapshot().Count;
            new Fake.Handler().OnLoadAssets();
            Thread.Sleep(100);
            newEv = cap.Snapshot().Skip(n0).ToList();
            Rec bw2 = newEv.FirstOrDefault(e => e.Id == 3 && Vals(e, "markerId") == Mk.ServerBlocklistWait.ToString());
            Rec es3 = newEv.FirstOrDefault(e => e.Id == 4 && Vals(e, "markerId") == Mk.ServerBlocklistSet.ToString());
            T.Check("d25 blocklist (event already set): a=1 on wait Begin and set End", bw2 != null && es3 != null && Vals(bw2, "a") == "1" && Vals(es3, "a") == "1");

            // bodies swallow exceptions: accessors fed null must not throw, fault counter rises
            Exception t3 = null;
            try
            {
                int stt;
                Patches.ColPass_Pre(null, out stt);
                Patches.ColPass_Post(null, 0);
                long cls;
                Patches.ColLoad_Pre(null, 0, out cls);
                string ss;
                Patches.ModPhase_Pre(null, null, null, out ss);
            }
            catch (Exception e) { t3 = e; }
            T.Check("d26 hook bodies fed null game objects do not throw; faults counted", t3 == null && Interlocked.Read(ref Core.Faults) > faults0, Convert.ToString(t3) + " faults=" + Core.Faults);
        }
    }
}
