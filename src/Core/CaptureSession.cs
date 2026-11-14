using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Diagnostics.NETCore.Client;

namespace SparkVS.Core;

public static class Os
{
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("libc", EntryPoint = "syscall")]
    static extern long LinuxSyscall(long number);

    /// <summary>
    /// The id the trace reports for a thread. Managed thread ids do not match it. Windows uses GetCurrentThreadId; Linux
    /// (a dedicated server may run there) uses the gettid syscall on x64 and arm64. 0 means unknown, and the report then
    /// finds the main thread from its stack instead.
    /// </summary>
    public static int CurrentThreadId()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return (int)GetCurrentThreadId();
            if (OperatingSystem.IsLinux())
            {
                var arch = RuntimeInformation.ProcessArchitecture;
                if (arch == Architecture.X64) return (int)LinuxSyscall(186);
                if (arch == Architecture.Arm64) return (int)LinuxSyscall(178);
            }
        }
        catch (Exception) { }
        return 0;
    }
}

/// <summary>
/// The runtime's own sampling thread does the stack walks. This class only opens the session on our own
/// process and copies its stream to disk from one thread, through one preallocated buffer.
/// </summary>
public sealed class CaptureSession
{
    const int CopyBufferBytes = 1 << 20;
    const int CircularBufferMb = 256;

    readonly string tracePath;
    readonly byte[] buffer = new byte[CopyBufferBytes];
    EventPipeSession? session;
    Thread? reader;
    volatile Exception? readerError;

    public long ReaderBytesAllocated { get; private set; }
    public long TraceBytes { get; private set; }
    public long StartQpc { get; private set; }
    public long EndQpc { get; private set; }
    public Exception? ReaderError => readerError;

    readonly bool allocationSampling;

    /// <summary>
    /// allocationSampling adds the runtime's GC keyword at verbose level to the same session, which is what
    /// makes it emit one GCAllocationTick (with a stack) about every 100 KB a thread allocates. Nothing else is traced.
    /// </summary>
    public CaptureSession(string tracePath, bool allocationSampling = false)
    {
        this.tracePath = tracePath;
        this.allocationSampling = allocationSampling;
    }

    public bool AllocationSampling => allocationSampling;

    public void Start()
    {
        var list = new System.Collections.Generic.List<EventPipeProvider>
        {
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
        };
        if (allocationSampling)
            list.Add(new EventPipeProvider(AllocationTick.RuntimeProvider, EventLevel.Verbose, AllocationTick.GcKeyword));
        var providers = list.ToArray();
        var client = new DiagnosticsClient(Environment.ProcessId);
        StartQpc = Stopwatch.GetTimestamp();
        session = client.StartEventPipeSession(providers, true, CircularBufferMb);
        reader = new Thread(CopyLoop) { IsBackground = true, Name = "SparkVS stream reader" };
        reader.Start();
    }

    /// <summary>Blocks through the rundown, which can take seconds, so call it off the main thread.</summary>
    public void Stop(int timeoutMs)
    {
        EndQpc = Stopwatch.GetTimestamp();
        session?.Stop();
        reader?.Join(timeoutMs);
        session?.Dispose();
        session = null;
    }

    void CopyLoop()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using var file = new FileStream(tracePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan);
            var stream = session!.EventStream;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                file.Write(buffer, 0, read);
                TraceBytes += read;
                ReaderBytesAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }
        catch (Exception ex)
        {
            readerError = ex;
        }
    }
}
