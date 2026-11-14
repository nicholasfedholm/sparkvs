using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

[assembly: InternalsVisibleTo("SparkLoad.Hook.Tests")]

namespace SparkLoad.Hook
{
    // One row of the embedded target table (generated into Table.g.cs from targets.json).
    internal sealed class TargetRow
    {
        public readonly string Id;
        public readonly string[] Markers;
        public readonly string Assembly;
        public readonly string TypeName;
        public readonly string Method;
        public readonly bool IsStatic;
        public readonly string[] Returns;   // accepted return types (stock variant first, Optimum extra overloads after)
        public readonly string[] Params;
        public readonly string Pre;
        public readonly string Post;

        public bool Done;            // final status reported
        public bool FlushReported;   // a missing/error status was reported at flush time
        public int Attempts;
        public string LastError;

        public TargetRow(string id, string[] markers, string assembly, string typeName, string method, bool isStatic,
            string[] returns, string[] pars, string pre, string post)
        {
            Id = id; Markers = markers; Assembly = assembly; TypeName = typeName; Method = method; IsStatic = isStatic;
            Returns = returns; Params = pars; Pre = pre; Post = post;
        }

        public string Target { get { return TypeName + "." + Method; } }
    }

    // A method as seen by the matcher: built from reflection at run time, or from PE metadata in the offline tests.
    internal struct Cand
    {
        public string Name;
        public bool IsStatic;
        public string Ret;
        public string[] Params;
    }

    internal static class Sig
    {
        // Same naming as the target table: nested types with '+', generics as Def`N<a,b>, arrays as T[].
        public static string Name(Type t)
        {
            if (t == null) return "?";
            if (t.IsArray)
            {
                int rank = t.GetArrayRank();
                return Name(t.GetElementType()) + (rank == 1 ? "[]" : "[" + new string(',', rank - 1) + "]");
            }
            if (t.IsByRef) return Name(t.GetElementType()) + "&";
            if (t.IsPointer) return Name(t.GetElementType()) + "*";
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                Type[] args = t.GetGenericArguments();
                var sb = new StringBuilder(Name(t.GetGenericTypeDefinition()));
                sb.Append('<');
                for (int i = 0; i < args.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Name(args[i])); }
                return sb.Append('>').ToString();
            }
            if (t.IsGenericParameter) return (t.DeclaringMethod != null ? "!!" : "!") + t.GenericParameterPosition;
            return t.FullName ?? t.Name;
        }

        public static string Describe(Cand c)
        {
            return (c.IsStatic ? "static " : "") + c.Ret + " " + c.Name + "(" + string.Join(", ", c.Params) + ")";
        }

        // status: ok (matches holds the indexes to patch) | missing | mismatch
        public static string Select(IList<Cand> cands, TargetRow row, List<int> matches, out string detail)
        {
            detail = "";
            int named = 0;
            string seen = "";
            for (int i = 0; i < cands.Count; i++)
            {
                Cand c = cands[i];
                if (!string.Equals(c.Name, row.Method, StringComparison.Ordinal)) continue;
                named++;
                if (seen.Length < 300) seen += (seen.Length > 0 ? "; " : "") + Describe(c);
                if (c.IsStatic != row.IsStatic) continue;
                if (!SameList(c.Params, row.Params)) continue;
                if (Array.IndexOf(row.Returns, c.Ret) < 0) continue;
                matches.Add(i);
            }
            if (matches.Count > 0)
            {
                var rets = new List<string>();
                foreach (int m in matches) rets.Add(ShortName(cands[m].Ret));
                detail = matches.Count == 1 ? "" : matches.Count + " overloads (" + string.Join(",", rets) + ")";
                return "ok";
            }
            if (named == 0) return "missing";
            detail = "no exact signature; found " + seen;
            return "mismatch";
        }

        private static string ShortName(string n)
        {
            int i = n.LastIndexOf('.');
            return i < 0 ? n : n.Substring(i + 1);
        }

        private static bool SameList(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    // Applies one patch. The only class that references Harmony types, so the hook can start, role-check and
    // stay a noop without 0Harmony being resolvable.
    internal static class HarmonyBridge
    {
        private static HarmonyLib.Harmony _h;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Apply(MethodBase target, MethodInfo pre, MethodInfo post)
        {
            if (_h == null) _h = new HarmonyLib.Harmony("sparkload.hook");
            _h.Patch(target,
                pre == null ? null : new HarmonyLib.HarmonyMethod(pre),
                post == null ? null : new HarmonyLib.HarmonyMethod(post));
        }
    }

    // Subscribes to assembly loads and installs the patches for VintagestoryLib and VSSurvivalMod.
    // Fail-safe: nothing here throws into the game; a target that cannot be patched logs and stands down.
    internal static class Installer
    {
        private const int MaxAttempts = 3;
        private static readonly object L = new object();
        [ThreadStatic] private static bool t_busy;
        private static bool _started;
        private static volatile bool _retryWanted;

        // Rows used by the tests (null = the embedded table).
        internal static TargetRow[] RowsOverride;
        private static TargetRow[] Rows { get { return RowsOverride ?? TargetTable.Rows; } }

        public static void Start()
        {
            try
            {
                if (_started) return;
                _started = true;
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) InstallFor(a);
            }
            catch (Exception e)
            {
                Core.Fault();
                HookLog.Line("installer start failed " + e.GetType().Name + " " + e.Message);
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                Assembly a = args.LoadedAssembly;
                if (a == null) return;
                if (_retryWanted) RetryLoaded();
                else InstallFor(a);
            }
            catch (Exception e)
            {
                Core.Fault();
                HookLog.Line("assembly load handler failed " + e.GetType().Name + " " + e.Message);
            }
        }

        private static bool Wanted(string asmName)
        {
            foreach (TargetRow r in Rows)
                if (!r.Done && string.Equals(r.Assembly, asmName, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void RetryLoaded()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) InstallFor(a);
        }

        // Installs every pending row that belongs to this assembly.
        internal static void InstallFor(Assembly asm)
        {
            if (asm == null || t_busy) return;
            string name;
            try { name = asm.GetName().Name; } catch { return; }
            if (!Wanted(name)) return;
            if (!Monitor.TryEnter(L)) { _retryWanted = true; return; }
            t_busy = true;
            try
            {
                foreach (TargetRow row in Rows)
                {
                    if (row.Done || !string.Equals(row.Assembly, name, StringComparison.Ordinal)) continue;
                    PatchRow(row, asm);
                }
                bool anyRetry = false;
                foreach (TargetRow row in Rows) if (!row.Done && row.Attempts > 0) anyRetry = true;
                _retryWanted = anyRetry;
            }
            catch (Exception e)
            {
                Core.Fault();
                HookLog.Line("install failed " + name + " " + e.GetType().Name + " " + e.Message);
            }
            finally
            {
                t_busy = false;
                Monitor.Exit(L);
            }
        }

        private static void Report(TargetRow row, string status, string detail)
        {
            foreach (string m in row.Markers) Core.PatchResult(m, row.Target, status, detail);
        }

        private static void Finish(TargetRow row, string status, string detail)
        {
            row.Done = true;
            Report(row, status, detail);
        }

        private static bool AllOff(TargetRow row)
        {
            foreach (string m in row.Markers)
            {
                int id = Array.IndexOf(Mk.Names, m);
                if (id > 0 && Core.On[id]) return false;
            }
            return true;
        }

        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static void PatchRow(TargetRow row, Assembly asm)
        {
            row.Attempts++;
            try
            {
                if (AllOff(row)) { Finish(row, "off", "SPARKLOAD_OFF"); return; }

                Type t = asm.GetType(row.TypeName, false);
                if (t == null) { Finish(row, "missing", "type not found"); return; }

                MethodInfo[] methods = t.GetMethods(All);
                var cands = new List<Cand>(methods.Length);
                foreach (MethodInfo m in methods)
                {
                    ParameterInfo[] ps = m.GetParameters();
                    var pn = new string[ps.Length];
                    for (int i = 0; i < ps.Length; i++) pn[i] = Sig.Name(ps[i].ParameterType);
                    cands.Add(new Cand { Name = m.Name, IsStatic = m.IsStatic, Ret = Sig.Name(m.ReturnType), Params = pn });
                }
                var idx = new List<int>();
                string detail;
                string status = Sig.Select(cands, row, idx, out detail);
                if (status != "ok") { Finish(row, status, detail); return; }

                string prep = Prepare(row, asm, methods[idx[0]]);
                if (prep != null && prep.StartsWith("!")) { Finish(row, "error", prep.Substring(1)); return; }
                if (prep != null) detail = detail.Length == 0 ? prep : detail + "; " + prep;

                MethodInfo pre = row.Pre == null ? null : typeof(Patches).GetMethod(row.Pre, BindingFlags.Public | BindingFlags.Static);
                MethodInfo post = row.Post == null ? null : typeof(Patches).GetMethod(row.Post, BindingFlags.Public | BindingFlags.Static);
                if ((row.Pre != null && pre == null) || (row.Post != null && post == null))
                {
                    Finish(row, "error", "hook method missing " + row.Pre + "/" + row.Post);
                    return;
                }

                foreach (int i in idx) HarmonyBridge.Apply(methods[i], pre, post);

                if (row.Id == "ML_TryRunModPhase") { try { AppContext.SetSwitch("SparkLoad.Hook.HoldsTryRunModPhase", true); } catch { } }
                Finish(row, "ok", detail);
            }
            catch (Exception e)
            {
                Core.Fault();
                row.LastError = e.GetType().Name + ": " + e.Message;
                HookLog.Line("patch attempt " + row.Attempts + " " + row.Id + " failed " + row.LastError);
                if (e is System.IO.FileNotFoundException || e is System.IO.FileLoadException) HarmonyFallback(asm);
                if (row.Attempts >= MaxAttempts) Finish(row, "error", row.LastError);
            }
        }

        // Last resort when 0Harmony is not resolvable by the game's own resolver: probe the Lib folders next to the
        // game assembly and the target exe. Registered only after a real resolve failure, so it never pre-empts the
        // game's resolver.
        private static bool _fallbackSet;
        private static void HarmonyFallback(Assembly asm)
        {
            if (_fallbackSet) return;
            _fallbackSet = true;
            var dirs = new List<string>();
            try { string loc = asm.Location; if (!string.IsNullOrEmpty(loc)) dirs.Add(System.IO.Path.GetDirectoryName(loc)); } catch { }
            try { string x = Environment.GetEnvironmentVariable("SPARKLOAD_TARGET_EXE"); if (!string.IsNullOrEmpty(x)) dirs.Add(System.IO.Path.GetDirectoryName(x)); } catch { }
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs a)
            {
                try
                {
                    if (!a.Name.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase)) return null;
                    foreach (string d in dirs)
                    {
                        foreach (string sub in new[] { "Lib", "" })
                        {
                            string f = System.IO.Path.Combine(d, sub, "0Harmony.dll");
                            if (System.IO.File.Exists(f)) { HookLog.Line("0Harmony fallback " + f); return Assembly.LoadFrom(f); }
                        }
                    }
                }
                catch { }
                return null;
            };
            HookLog.Line("0Harmony fallback resolver registered");
        }

        // Late-bound accessors a patch needs. Returns null, a note to append to the detail, or "!reason" to stand down.
        private static string Prepare(TargetRow row, Assembly asm, MethodInfo first)
        {
            switch (row.Id)
            {
                case "SSSC_PopulateChunk":
                case "SSSC_LoadOrGenColumn":
                    if (Accessors.ReqXFn != null) return null;
                    try { Accessors.BuildRequest(first.GetParameters()[0].ParameterType); return null; }
                    catch (Exception e) { return "accessors failed (" + e.GetType().Name + "), x/z/pass will read -1"; }
                case "ML_TryRunModPhase":
                    try
                    {
                        ParameterInfo[] ps = first.GetParameters();
                        Accessors.BuildMod(ps[0].ParameterType, ps[2].ParameterType);
                        return null;
                    }
                    catch (Exception e) { return "mod accessors failed (" + e.GetType().Name + "), mod id will read ?"; }
                case "SSMH_OnLoadAssets":
                    return Accessors.FindMre(asm) ? null : "!ModBlockListMRE not found";
            }
            return null;
        }

        // Called with the stats: one more try for anything the load events skipped, then report what never resolved.
        public static void FlushPending()
        {
            try
            {
                RetryLoaded();
                foreach (TargetRow row in Rows)
                {
                    if (row.Done || row.FlushReported) continue;
                    row.FlushReported = true;
                    if (row.Attempts == 0) Report(row, "missing", "assembly " + row.Assembly + " not loaded");
                    else Report(row, "error", row.LastError ?? "not installed");
                }
            }
            catch { Core.Fault(); }
        }
    }
}
