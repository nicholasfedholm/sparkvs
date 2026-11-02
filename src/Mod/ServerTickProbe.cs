using System;
using System.Reflection;

namespace SparkVS;

/// <summary>
/// Reads the integrated server's own per tick work time. ServerMain.Process times each tick with a Stopwatch and
/// stores it in StatsCollector[StatsCollectorIndex].tickTimes (whole ms) after the game tick listeners have run.
/// A listener on the server thread therefore sees the previous completed tick there. These members are public
/// but live in the game's internal ServerMain type, so they are read by reflection; if any is missing the probe
/// does not exist and tick.jsonl says so rather than inventing numbers.
/// </summary>
internal sealed class ServerTickProbe
{
    readonly object world;
    readonly FieldInfo collectors, collectorIndex, ticksTotal, tickTimes, tickTimeIndex;
    volatile bool primed;
    int lastIndex = -1, lastTickTimeIndex = -1;
    long lastTicksTotal = -1;

    ServerTickProbe(object world, FieldInfo collectors, FieldInfo collectorIndex, FieldInfo ticksTotal, FieldInfo tickTimes, FieldInfo tickTimeIndex)
    {
        this.world = world;
        this.collectors = collectors;
        this.collectorIndex = collectorIndex;
        this.ticksTotal = ticksTotal;
        this.tickTimes = tickTimes;
        this.tickTimeIndex = tickTimeIndex;
    }

    public static ServerTickProbe? Create(object world, out string reason)
    {
        reason = "";
        try
        {
            var t = world.GetType();
            var collectors = t.GetField("StatsCollector");
            var index = t.GetField("StatsCollectorIndex");
            var element = collectors?.FieldType.GetElementType();
            var total = element?.GetField("ticksTotal");
            var times = element?.GetField("tickTimes");
            var timeIndex = element?.GetField("tickTimeIndex");
            if (collectors == null || index == null || total == null || times == null || timeIndex == null)
            {
                reason = $"server world type {t.FullName} has no StatsCollector/tickTimes members; the game build differs from what this probe was written against, so no honest per tick hook exists";
                return null;
            }
            return new ServerTickProbe(world, collectors, index, total, times, timeIndex);
        }
        catch (Exception ex)
        {
            reason = "reflection failed: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Call when a capture begins so the first observation only primes the probe.</summary>
    public void Reset() => primed = false;

    /// <summary>
    /// True when a new completed tick has appeared since the last call, with its work time in ms. Server thread only.
    /// </summary>
    public bool TryRead(out int msptMs)
    {
        msptMs = 0;
        var array = (Array?)collectors.GetValue(world);
        if (array == null) return false;
        int index = (int)collectorIndex.GetValue(world)!;
        var collector = array.GetValue(index);
        if (collector == null) return false;
        long total = (long)ticksTotal.GetValue(collector)!;
        int ti = (int)tickTimeIndex.GetValue(collector)!;
        var times = (long[]?)tickTimes.GetValue(collector);
        if (times == null || times.Length == 0) return false;

        bool changed = index != lastIndex || ti != lastTickTimeIndex || total != lastTicksTotal;
        lastIndex = index; lastTickTimeIndex = ti; lastTicksTotal = total;
        if (!changed) return false;
        if (!primed) { primed = true; return false; }
        if (total <= 0) return false;
        msptMs = (int)times[(ti + times.Length - 1) % times.Length];
        return true;
    }
}
