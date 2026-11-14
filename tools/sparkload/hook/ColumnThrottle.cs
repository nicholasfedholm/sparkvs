using System;
using System.Diagnostics;
using System.Threading;

namespace SparkLoad.Hook
{
    // column.load throttle. The server chunk thread calls loadOrGenerateChunkColumn_OnChunkThread tens of
    // thousands of times per column (59.4M spans on 3,024 columns in one Earth run). Per (x,z) column the first
    // KeepFirst calls always emit a span, and so does any later call that takes at least one millisecond (its
    // Begin is written late, from the postfix, with the QPC taken in the prefix). The rest are counted only.
    // The suppressed total per column leaves through the existing Stats event as marker
    // "column.load.suppressed <x> <z>" (hits = suppressed so far): at each power-of-two call count past
    // KeepFirst, and for every column again in Core.StatsAll. The suppressed path allocates nothing.
    internal static class ColumnThrottle
    {
        public const int KeepFirst = 4;
        public const string StatsPrefix = "column.load.suppressed";

        private const int Cap = 1 << 16;
        private const int Mask = Cap - 1;
        private const int MaxProbe = 64;
        private const long Empty = long.MinValue;

        private static readonly long[] Keys = NewKeys();
        private static readonly int[] Seen = new int[Cap];
        private static readonly int[] Emitted = new int[Cap];

        // Spans at least this long are always emitted (1 ms).
        public static readonly long SlowTicks = Math.Max(1, Stopwatch.Frequency / 1000);

        private static long[] NewKeys()
        {
            var k = new long[Cap];
            for (int i = 0; i < k.Length; i++) k[i] = Empty;
            return k;
        }

        // Slot of the column, claiming one when new. -1 when the table is crowded: that column is never throttled.
        public static int Slot(int x, int z)
        {
            long key = ((long)x << 32) | (uint)z;
            if (key == Empty) return -1;
            int i = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 48);
            for (int p = 0; p < MaxProbe; p++)
            {
                int s = (i + p) & Mask;
                long k = Volatile.Read(ref Keys[s]);
                if (k == key) return s;
                if (k == Empty)
                {
                    long prev = Interlocked.CompareExchange(ref Keys[s], key, Empty);
                    if (prev == Empty || prev == key) return s;
                }
            }
            return -1;
        }

        // Called from the prefix for every call. True = emit the Begin now.
        public static bool Admit(int slot, int x, int z)
        {
            int n = Interlocked.Increment(ref Seen[slot]);
            if (n <= KeepFirst)
            {
                Interlocked.Increment(ref Emitted[slot]);
                return true;
            }
            if ((n & (n - 1)) == 0) Report(slot, x, z);
            return false;
        }

        // A suppressed call that turned out slow: its span is emitted after all.
        public static void LateEmitted(int slot)
        {
            if (slot >= 0) Interlocked.Increment(ref Emitted[slot]);
        }

        public static long Suppressed(int slot)
        {
            long d = (long)Volatile.Read(ref Seen[slot]) - Volatile.Read(ref Emitted[slot]);
            return d < 0 ? 0 : d;
        }

        private static void Report(int slot, int x, int z)
        {
            try
            {
                SparkLoadSource s = Core.Src;
                if (s != null && s.IsEnabled())
                    s.Stats(StatsPrefix + " " + x.ToString() + " " + z.ToString(), Suppressed(slot));
            }
            catch { Core.Fault(); }
        }

        // One Stats event per column called more than KeepFirst times (the last value per column is the total; a
        // mid-run report can count a still-running call as suppressed). Low rate: StatsAll runs a few times per run.
        public static void FlushStats(SparkLoadSource s)
        {
            for (int i = 0; i < Cap; i++)
            {
                long key = Volatile.Read(ref Keys[i]);
                if (key == Empty) continue;
                if (Volatile.Read(ref Seen[i]) <= KeepFirst) continue;
                long sup = Suppressed(i);
                s.Stats(StatsPrefix + " " + ((int)(key >> 32)).ToString() + " " + ((int)(key & 0xFFFFFFFFL)).ToString(), sup);
            }
        }
    }
}
