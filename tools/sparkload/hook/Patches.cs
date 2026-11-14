using System;
using System.Diagnostics;
using System.Threading;

namespace SparkLoad.Hook
{
    // Prefix and postfix bodies. Plain static methods, no Harmony types: the Harmony bridge passes them as
    // MethodInfo. Every body is wrapped so an exception inside the hook never reaches the game.
    // Game arguments are taken as object (__0, __1 ...) and read through delegates built once (Accessors).
    internal static partial class Patches
    {
        [ThreadStatic] private static bool t_waitPending;
        [ThreadStatic] private static long t_waitA;

        private static int ToInt(object o) { return o == null ? -1 : Convert.ToInt32(o); }

        // ---- server.phase: a = (int) EnumServerRunPhase
        public static void Phase_Pre(object __0)
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ServerPhase]) return;
                Interlocked.Increment(ref Core.Hits[Mk.ServerPhase]);
                Emit.Begin(Mk.ServerPhase, q, ToInt(__0), 0, 0, null);
            }
            catch { Core.Fault(); }
        }

        public static void Phase_Post(object __0)
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ServerPhase]) return;
                Emit.End(Mk.ServerPhase, q, ToInt(__0), 0, 0, null);
            }
            catch { Core.Fault(); }
        }

        // ---- mod.phase: a = (int) ModRunPhase, s = modid|client or modid|server
        public static void ModPhase_Pre(object __0, object __2, object __3, out string __state)
        {
            __state = null;
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ModPhase]) return;
                Interlocked.Increment(ref Core.Hits[Mk.ModPhase]);
                string s = Accessors.ModKey(__0, __2);
                __state = s;
                Emit.Begin(Mk.ModPhase, q, ToInt(__3), 0, 0, s);
            }
            catch { Core.Fault(); }
        }

        public static void ModPhase_Post(object __3, string __state)
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ModPhase]) return;
                Emit.End(Mk.ModPhase, q, ToInt(__3), 0, 0, __state);
            }
            catch { Core.Fault(); }
        }

        // ---- server.blocklist.wait / server.blocklist.set (no transpiler)
        // OnLoadAssets prefix: Begin(wait) with a = 1 when the static ManualResetEvent is already set, else 0.
        // a = 0: a thread-pool wait on the event emits End(set) with the qpc taken when the wait fires.
        // a = 1: End(set) is emitted at once (its qpc is then only an upper bound of the real set time).
        public static void Blocklist_Pre()
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                ManualResetEvent mre = Accessors.BlockMre;
                if (mre == null) return;
                bool already = mre.WaitOne(0);
                long a = already ? 1 : 0;
                if (Core.On[Mk.ServerBlocklistWait])
                {
                    Interlocked.Increment(ref Core.Hits[Mk.ServerBlocklistWait]);
                    t_waitPending = true;
                    t_waitA = a;
                    Emit.Begin(Mk.ServerBlocklistWait, q, a, 0, 0, null);
                }
                if (Core.On[Mk.ServerBlocklistSet])
                {
                    if (already)
                    {
                        Interlocked.Increment(ref Core.Hits[Mk.ServerBlocklistSet]);
                        Emit.End(Mk.ServerBlocklistSet, Stopwatch.GetTimestamp(), 1, 0, 0, null);
                    }
                    else
                    {
                        Accessors.BlockWait = ThreadPool.RegisterWaitForSingleObject(mre, BlocklistSetFired, null, Timeout.Infinite, true);
                    }
                }
            }
            catch { Core.Fault(); }
        }

        private static void BlocklistSetFired(object state, bool timedOut)
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ServerBlocklistSet]) return;
                Interlocked.Increment(ref Core.Hits[Mk.ServerBlocklistSet]);
                Emit.End(Mk.ServerBlocklistSet, q, 0, 0, 0, null);
            }
            catch { Core.Fault(); }
        }

        // ModLoader.LoadMods(List, IEnumerable) prefix: first thing after the WaitOne in OnLoadAssets.
        public static void BlocklistEnd_Pre()
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!t_waitPending) return;
                t_waitPending = false;
                if (!Core.On[Mk.ServerBlocklistWait]) return;
                Emit.End(Mk.ServerBlocklistWait, q, t_waitA, 0, 0, null);
            }
            catch { Core.Fault(); }
        }

        // ---- column.pass: a = ChunkX, b = ChunkZ, c = CurrentIncompletePass at entry (the pass being run)
        public static void ColPass_Pre(object __0, out int __state)
        {
            __state = 0;
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ColumnPass]) return;
                Interlocked.Increment(ref Core.Hits[Mk.ColumnPass]);
                int pass = Accessors.ReqPass(__0);
                __state = pass;
                Emit.Begin(Mk.ColumnPass, q, Accessors.ReqX(__0), Accessors.ReqZ(__0), pass, null);
            }
            catch { Core.Fault(); }
        }

        public static void ColPass_Post(object __0, int __state)
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ColumnPass]) return;
                Emit.End(Mk.ColumnPass, q, Accessors.ReqX(__0), Accessors.ReqZ(__0), __state, null);
            }
            catch { Core.Fault(); }
        }

        // ---- column.load: a = ChunkX, b = ChunkZ, c = the int stage argument (replaces the from-db meaning in SPEC)
        // Throttled per column (see ColumnThrottle): __state 0 = Begin already emitted, else the prefix QPC of a
        // suppressed call. Hits still counts every call.
        public static void ColLoad_Pre(object __0, int __1, out long __state)
        {
            __state = 0;
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ColumnLoad]) return;
                Interlocked.Increment(ref Core.Hits[Mk.ColumnLoad]);
                int x = Accessors.ReqX(__0), z = Accessors.ReqZ(__0);
                int slot = ColumnThrottle.Slot(x, z);
                if (slot >= 0 && !ColumnThrottle.Admit(slot, x, z)) { __state = q; return; }
                Emit.Begin(Mk.ColumnLoad, q, x, z, __1, null);
            }
            catch { Core.Fault(); }
        }

        public static void ColLoad_Post(object __0, int __1, long __state)
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ColumnLoad]) return;
                if (__state == 0)
                {
                    Emit.End(Mk.ColumnLoad, q, Accessors.ReqX(__0), Accessors.ReqZ(__0), __1, null);
                    return;
                }
                if (q - __state < ColumnThrottle.SlowTicks) return; // suppressed and fast: nothing to do
                int x = Accessors.ReqX(__0), z = Accessors.ReqZ(__0);
                ColumnThrottle.LateEmitted(ColumnThrottle.Slot(x, z));
                Emit.Begin(Mk.ColumnLoad, __state, x, z, __1, null);
                Emit.End(Mk.ColumnLoad, q, x, z, __1, null);
            }
            catch { Core.Fault(); }
        }

        // ---- client.finalize.trigger (also arms the frame logic)
        public static void Fin_Pre()
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.ClientFinalizeTrigger]) return;
                Interlocked.Increment(ref Core.Hits[Mk.ClientFinalizeTrigger]);
                Emit.Begin(Mk.ClientFinalizeTrigger, q, 0, 0, 0, null);
            }
            catch { Core.Fault(); }
        }

        public static void Fin_Post()
        {
            try
            {
                long q = Stopwatch.GetTimestamp();
                if (Core.On[Mk.ClientFinalizeTrigger]) Emit.End(Mk.ClientFinalizeTrigger, q, 0, 0, 0, null);
                FrameStats.OnFinalizeEnd();
            }
            catch { Core.Fault(); }
        }

        // ---- frame
        public static void Frame_Pre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Frame_Post(long __state)
        {
            try
            {
                long end = Stopwatch.GetTimestamp();
                if (!Core.On[Mk.Frame]) return;
                Interlocked.Increment(ref Core.Hits[Mk.Frame]);
                FrameStats.OnFrame(__state, end);
            }
            catch { Core.Fault(); }
        }
    }
}
