using System;
using System.Diagnostics;
using System.Threading;

namespace SparkVS.Core;

/// <summary>
/// Runs a callback at 1 Hz on a low priority background thread, drift corrected, with one sample at start and
/// one at stop. The callback gets ms since the bundle clock origin. Setup and teardown run on the same thread.
/// </summary>
public sealed class PeriodicSampler
{
    readonly BundleClock clock;
    readonly Action? setup;
    readonly Action<double> sample;
    readonly Action? teardown;
    readonly ManualResetEventSlim stop = new(false);
    readonly ManualResetEventSlim ready = new(false);
    readonly int periodMs;
    Thread? thread;
    long errors;

    public long Errors => Interlocked.Read(ref errors);
    public string? LastError { get; private set; }
    public string Name { get; }

    public PeriodicSampler(string name, BundleClock clock, Action<double> sample, Action? setup = null, Action? teardown = null, int periodMs = 1000)
    {
        this.clock = clock;
        this.sample = sample;
        this.setup = setup;
        this.teardown = teardown;
        this.periodMs = periodMs;
        Name = name;
    }

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = Name, Priority = ThreadPriority.BelowNormal };
        thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Takes the final sample, then waits for the thread to finish.</summary>
    public void Stop()
    {
        stop.Set();
        thread?.Join(5000);
    }

    void Loop()
    {
        try { setup?.Invoke(); }
        catch (Exception ex) { Fail(ex); }
        ready.Set();

        long next = Stopwatch.GetTimestamp();
        while (true)
        {
            Take();
            next += periodMs * Stopwatch.Frequency / 1000;
            long waitTicks = next - Stopwatch.GetTimestamp();
            int waitMs = (int)Math.Max(0, waitTicks * 1000 / Stopwatch.Frequency);
            if (stop.Wait(waitMs)) break;
        }
        Take();   // final sample at stop
        try { teardown?.Invoke(); }
        catch (Exception ex) { Fail(ex); }
    }

    void Take()
    {
        try { sample(clock.MsSince(Stopwatch.GetTimestamp())); }
        catch (Exception ex) { Fail(ex); }
    }

    void Fail(Exception ex)
    {
        Interlocked.Increment(ref errors);
        LastError = ex.GetType().Name + ": " + ex.Message;
    }
}
