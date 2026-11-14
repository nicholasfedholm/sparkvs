using System;
using System.Threading;

namespace SparkLoad.Hook
{
    // Per-thread sequence and thread announcement. All writers go through here.
    internal static class Emit
    {
        [ThreadStatic] private static long t_seq;
        [ThreadStatic] private static int t_tid;
        [ThreadStatic] private static bool t_announced;

        private static int Tid()
        {
            int t = t_tid;
            if (t == 0)
            {
                t = (int)Native.GetCurrentThreadId();
                t_tid = t;
            }
            return t;
        }

        private static void Announce(SparkLoadSource src, int tid)
        {
            if (t_announced) return;
            t_announced = true;
            Thread th = Thread.CurrentThread;
            src.Thread(tid, th.ManagedThreadId, th.Name ?? "");
        }

        public static void Begin(int id, long qpc, long a, long b, long c, string s)
        {
            SparkLoadSource src = Core.Src;
            if (src == null || !src.IsEnabled()) return;
            int tid = Tid();
            Announce(src, tid);
            src.Begin(id, tid, ++t_seq, qpc, a, b, c, s);
        }

        public static void End(int id, long qpc, long a, long b, long c, string s)
        {
            SparkLoadSource src = Core.Src;
            if (src == null || !src.IsEnabled()) return;
            int tid = Tid();
            Announce(src, tid);
            src.End(id, tid, ++t_seq, qpc, a, b, c, s);
        }

        public static void Frame(long qpc, long index, long durTicks)
        {
            SparkLoadSource src = Core.Src;
            if (src == null || !src.IsEnabled()) return;
            int tid = Tid();
            Announce(src, tid);
            src.Frame(tid, ++t_seq, qpc, index, durTicks);
        }

        public static void FrameAgg(long qpc, long count, long sumTicks, long maxTicks, long over50)
        {
            SparkLoadSource src = Core.Src;
            if (src == null || !src.IsEnabled()) return;
            int tid = Tid();
            Announce(src, tid);
            src.FrameAgg(tid, ++t_seq, qpc, count, sumTicks, maxTicks, over50);
        }

        public static void Playable(long qpc, long frameIndex)
        {
            SparkLoadSource src = Core.Src;
            if (src == null || !src.IsEnabled()) return;
            int tid = Tid();
            Announce(src, tid);
            src.Playable(tid, ++t_seq, qpc, frameIndex);
        }
    }
}
