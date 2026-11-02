using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace SparkVS.Core;

/// <summary>Decodes a SampleProfiler .nettrace into a SampleSet. Report time only, never on the capture path.</summary>
public static class NettraceLoader
{
    const string Provider = "Microsoft-DotNETCore-SampleProfiler";
    static readonly string[] ModuleSuffixes = { ".dll", ".il", ".ni" };

    public static SampleSet Load(string nettracePath, CaptureMeta meta, Action<string>? progress = null)
    {
        var set = new SampleSet();
        string etlx = nettracePath + ".etlx";
        TraceLog.CreateFromEventPipeDataFile(nettracePath, etlx);
        progress?.Invoke("converted");
        try
        {
            var frameIds = new Dictionary<string, int>();
            var stackIds = new Dictionary<int, int>();
            int empty = -1;
            using var log = new TraceLog(etlx);

            var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);

            int StackIdOf(TraceEvent data)
            {
                var cs = data.CallStack();
                if (cs == null)
                {
                    if (empty < 0) { set.Stacks.Add(Array.Empty<int>()); empty = set.Stacks.Count - 1; }
                    return empty;
                }
                int key = (int)cs.CallStackIndex;
                if (stackIds.TryGetValue(key, out int stackId)) return stackId;
                var frames = new List<int>();
                for (var f = cs; f != null; f = f.Caller)
                {
                    // The runtime samples at GC safe points, so a leaf GC poll is the sampler's own
                    // footprint in the method that called it. Bill the caller.
                    if (frames.Count == 0 && f.Caller != null && (f.CodeAddress.FullMethodName ?? "").Contains("PollGC", StringComparison.Ordinal)) continue;
                    frames.Add(Intern(set, frameIds, f.CodeAddress));
                }
                set.Stacks.Add(frames.ToArray());
                stackId = set.Stacks.Count - 1;
                stackIds[key] = stackId;
                return stackId;
            }

            foreach (var data in log.Events)
            {
                if (data.ProviderName == Provider)
                {
                    int stackId = StackIdOf(data);
                    // Raw QPC, the clock Stopwatch reads, so frames and samples line up exactly.
#pragma warning disable CS0618
                    long qpc = data.TimeStampQPC;
#pragma warning restore CS0618
                    set.Samples.Add(new Sample
                    {
                        ThreadId = data.ThreadID,
                        TimeMs = (qpc - meta.StartQpc) * 1000.0 / meta.QpcFrequency,
                        StackId = stackId,
                        External = IsExternal(data),
                    });
                }
                else if (data.ProviderName == AllocationTick.RuntimeProvider && (int)data.ID == AllocationTick.EventId)
                {
                    var payload = data.EventData();
                    if (payload == null || !AllocationTick.TryParse(payload, out long bytes, out bool large, out string type)) continue;
                    if (!typeIds.TryGetValue(type, out int typeId))
                    {
                        set.TypeNames.Add(type);
                        typeId = set.TypeNames.Count - 1;
                        typeIds[type] = typeId;
                    }
#pragma warning disable CS0618
                    long qpc = data.TimeStampQPC;
#pragma warning restore CS0618
                    set.Allocs.Add(new AllocSample
                    {
                        ThreadId = data.ThreadID,
                        TimeMs = (qpc - meta.StartQpc) * 1000.0 / meta.QpcFrequency,
                        StackId = StackIdOf(data),
                        Bytes = bytes,
                        TypeId = typeId,
                        Large = large,
                    });
                }
            }
        }
        finally
        {
            try { File.Delete(etlx); } catch (IOException) { }
        }
        return set;
    }

    /// <summary>The event payload is one int: 1 external (native), 2 managed.</summary>
    static bool IsExternal(TraceEvent data)
    {
        var payload = data.EventData();
        return payload != null && payload.Length >= 4 && BitConverter.ToInt32(payload, 0) == 1;
    }

    static int Intern(SampleSet set, Dictionary<string, int> ids, TraceCodeAddress address)
    {
        string method = address.FullMethodName ?? "";
        string module = address.ModuleName ?? "";
        // Modules that load from a renamed file, like moonshotcore's X.dll.module, keep the .dll here.
        // Precompiled code reports Name.il or Name.ni.
        foreach (var suffix in ModuleSuffixes)
            if (module.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) module = module.Substring(0, module.Length - suffix.Length);
        bool resolved = method.Length > 0;
        string key = resolved ? method + "\u0001" + module : "?" + module;
        if (ids.TryGetValue(key, out int id)) return id;
        set.Frames.Add(new FrameInfo
        {
            Method = resolved ? method : "?" + (module.Length > 0 ? " in " + module : ""),
            Module = module,
            Resolved = resolved,
        });
        id = set.Frames.Count - 1;
        ids[key] = id;
        return id;
    }
}
