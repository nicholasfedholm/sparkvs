using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace SparkLoad.Hook
{
    // Late-bound accessors for game types, built once at patch time (no per-call reflection lookups).
    // No game assembly is referenced at compile time.
    internal static class Accessors
    {
        public static Func<object, int> ReqXFn, ReqZFn, ReqPassFn;
        public static Func<object, string> ModIdFn, SideFn;
        public static ManualResetEvent BlockMre;
        public static RegisteredWaitHandle BlockWait;

        public static int ReqX(object o) { Func<object, int> f = ReqXFn; return f == null ? -1 : f(o); }
        public static int ReqZ(object o) { Func<object, int> f = ReqZFn; return f == null ? -1 : f(o); }
        public static int ReqPass(object o) { Func<object, int> f = ReqPassFn; return f == null ? -1 : f(o); }

        public static string ModKey(object mod, object api)
        {
            string id = "?", side = "?";
            try { Func<object, string> f = ModIdFn; if (f != null) id = f(mod) ?? "?"; } catch { Core.Fault(); }
            try { Func<object, string> g = SideFn; if (g != null) side = g(api) ?? "?"; } catch { Core.Fault(); }
            return id + "|" + side;
        }

        private static Func<object, T> Build<T>(Type owner, params string[] path)
        {
            ParameterExpression p = Expression.Parameter(typeof(object), "o");
            Expression e = Expression.Convert(p, owner);
            foreach (string name in path) e = Expression.PropertyOrField(e, name);
            if (typeof(T) == typeof(string) && e.Type != typeof(string))
                e = Expression.Call(e, e.Type.GetMethod("ToString", Type.EmptyTypes));
            else if (e.Type != typeof(T))
                e = Expression.Convert(e, typeof(T));
            return Expression.Lambda<Func<object, T>>(e, p).Compile();
        }

        // ChunkColumnLoadRequest: ChunkX, ChunkZ, CurrentIncompletePass (falls back to CurrentIncompletePass_AsInt).
        public static string BuildRequest(Type req)
        {
            ReqXFn = Build<int>(req, "ChunkX");
            ReqZFn = Build<int>(req, "ChunkZ");
            try { ReqPassFn = Build<int>(req, "CurrentIncompletePass"); }
            catch { ReqPassFn = Build<int>(req, "CurrentIncompletePass_AsInt"); }
            return "accessors built";
        }

        // TryRunModPhase(Mod mod, ModSystem system, ICoreAPI api, ModRunPhase phase)
        public static void BuildMod(Type modType, Type apiType)
        {
            ModIdFn = Build<string>(modType, "Info", "ModID");
            SideFn = Build<string>(apiType, "Side");
            // side text: Client / Server -> client / server
            Func<object, string> raw = SideFn;
            SideFn = o => raw(o).ToLowerInvariant();
        }

        // public static readonly ManualResetEvent ModDbUtil.ModBlockListMRE (Vintagestory.ModDb.ModDbUtil)
        public static bool FindMre(Assembly lib)
        {
            Type t = lib.GetType("Vintagestory.ModDb.ModDbUtil", false);
            if (t == null)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = a.GetType("Vintagestory.ModDb.ModDbUtil", false); } catch { t = null; }
                    if (t != null) break;
                }
            }
            if (t == null) return false;
            FieldInfo f = t.GetField("ModBlockListMRE", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            BlockMre = f?.GetValue(null) as ManualResetEvent;
            return BlockMre != null;
        }
    }
}
