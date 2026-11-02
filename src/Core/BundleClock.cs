using System;
using System.Diagnostics;
using System.Globalization;

namespace SparkVS.Core;

/// <summary>
/// One time origin for every series in a bundle: the QPC stamp of capture start, tied to a local wall clock reading.
/// Every series line carries ms since this origin and an ISO 8601 local timestamp derived from it.
/// </summary>
public sealed class BundleClock
{
    public long StartQpc { get; }
    public long Frequency { get; }
    public DateTime StartLocal { get; }

    public BundleClock(long startQpc, DateTime startLocal, long frequency)
    {
        StartQpc = startQpc;
        StartLocal = startLocal;
        Frequency = frequency;
    }

    /// <summary>Clock whose origin is a QPC stamp taken a moment ago.</summary>
    public static BundleClock FromStamp(long startQpc)
    {
        long nowQpc = Stopwatch.GetTimestamp();
        var start = DateTime.Now.AddMilliseconds(-(nowQpc - startQpc) * 1000.0 / Stopwatch.Frequency);
        return new BundleClock(startQpc, start, Stopwatch.Frequency);
    }

    public double MsSince(long qpc) => (qpc - StartQpc) * 1000.0 / Frequency;

    public string IsoAt(double tMs) => Iso(StartLocal.AddMilliseconds(tMs));

    public string StartIso => Iso(StartLocal);

    public static string Iso(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}
