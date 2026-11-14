using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SparkVS.Core;

public static class Analyzer
{
    const int MethodRowsKept = 300;
    const int PathDepth = 8;

    /// <summary>Landmarks that name a thread's job, since thread names are not in the trace.</summary>
    static readonly (string Role, string[] Marks)[] Roles =
    {
        ("worldgen", new[] { "ChunkGenerator", "ServerSystemSupplyChunks", "GenTerra", "WorldGenHandler" }),
        ("tesselation", new[] { "ChunkTesselator", "GenTesselate", "TesselateChunk" }),
        ("server", new[] { "ServerMain.Process", "ServerMain.DoTick", "ServerThread" }),
        ("client-main", new[] { "ClientPlatformWindows.window_RenderFrame" }),
    };

    static readonly string[] IdleLeaves =
    {
        "Monitor.Wait", "Thread.Sleep", "WaitHandle.Wait", "LowLevelLifoSemaphore", "PortableThreadPool",
        "ManualResetEventSlim.Wait", "SemaphoreSlim.Wait", "WaitOneNative", "Thread.Join",
    };

    public static string ShortName(string method)
    {
        int paren = method.IndexOf('(');
        return paren > 0 ? method.Substring(0, paren) : method;
    }

    public static Report Build(SampleSet set, CaptureMeta meta, int maxThreads = 16)
    {
        var attribution = new Attribution(meta.AssemblyToMod, meta.GameAssemblies);
        double durationMs = meta.QpcFrequency > 0 ? (meta.EndQpc - meta.StartQpc) * 1000.0 / meta.QpcFrequency : 0;

        var byThread = new Dictionary<int, List<Sample>>();
        int resolved = 0;
        foreach (var s in set.Samples)
        {
            if (!byThread.TryGetValue(s.ThreadId, out var list)) byThread[s.ThreadId] = list = new List<Sample>();
            list.Add(s);
            if (StackResolved(set, s.StackId)) resolved++;
        }

        var threads = new List<ThreadReport>();
        foreach (var kv in byThread)
            threads.Add(BuildThread(kv.Key, kv.Value, set, attribution, meta));

        var ordered = threads
            .OrderByDescending(t => t.Role == CaptureSide.MainRole(meta.Side))
            .ThenByDescending(t => t.Role == "server")
            .ThenByDescending(t => t.DurationMs * (100 - t.IdlePct))
            .Take(maxThreads)
            .ToList();

        var report = new Report
        {
            Label = meta.Label,
            World = meta.World,
            GameVersion = meta.GameVersion,
            StartedLocal = meta.StartedLocal,
            DurationSec = durationMs / 1000.0,
            SamplingRateKnob = meta.SamplingRateKnob,
            Side = CaptureSide.IsServer(meta.Side) ? CaptureSide.Server : null,
            ScopeNote = CaptureSide.IsServer(meta.Side)
                ? "Dedicated server process only. Captured in process by the SparkVS server side: there is no render loop, frame hook or GPU here, and no client is profiled."
                : meta.IntegratedServer
                ? "Client process only. Server threads are the integrated singleplayer server in this same process; this is not remote dedicated server profiling."
                : "Client process only. No server thread in this process (connected to a remote server, which is not profiled).",
            TotalSamples = set.Samples.Count,
            SamplesWithResolvedStack = resolved,
            BaselineFps = meta.BaselineFps,
            CaptureFps = meta.CaptureFps,
            CaptureFpsDeltaPct = meta.BaselineFps > 0 ? (meta.CaptureFps - meta.BaselineFps) / meta.BaselineFps * 100.0 : 0,
            SamplerBytesAllocated = meta.SamplerBytesAllocated,
            FrameHookBytesAllocated = meta.FrameHookBytesAllocated,
            NettraceBytes = meta.NettraceBytes,
            Gen0 = meta.Gen0,
            Gen1 = meta.Gen1,
            Gen2 = meta.Gen2,
            FrameCount = meta.FrameCount,
            SpikesOverThreshold = meta.Spikes.Count + meta.SpikesDropped,
            SpikesDropped = meta.SpikesDropped,
            SpikeThresholdMs = meta.SpikeThresholdMs,
            Threads = ordered,
        };
        if (meta.Bundle != null)
        {
            report.SchemaVersion = 1;
            report.Bundle = meta.Bundle;
        }

        int mainId = meta.MainThreadOsId;
        // A server whose tick thread id is unknown (non Windows) is found by its ServerMain stack role instead.
        if (CaptureSide.IsServer(meta.Side) && !byThread.ContainsKey(mainId))
            mainId = threads.FirstOrDefault(t => t.Role == "server")?.OsThreadId ?? mainId;
        if (byThread.TryGetValue(mainId, out var mainSamples))
        {
            report.MainThreadSamples = mainSamples.Count;
            int ok = mainSamples.Count(s => StackResolved(set, s.StackId));
            report.MainResolvedPct = mainSamples.Count > 0 ? 100.0 * ok / mainSamples.Count : 0;
            report.MainMeanIntervalMs = MeanInterval(mainSamples);
            report.Spikes = BuildSpikes(mainSamples, set, attribution, meta);
        }
        return report;
    }

    static bool StackResolved(SampleSet set, int stackId)
    {
        foreach (var f in set.Stacks[stackId]) if (set.Frames[f].Resolved) return true;
        return false;
    }

    static double MeanInterval(List<Sample> samples)
    {
        if (samples.Count < 2) return 0;
        return (samples[samples.Count - 1].TimeMs - samples[0].TimeMs) / (samples.Count - 1);
    }

    /// <summary>Each sample stands for the time until that thread's next sample; the last one gets the median gap.</summary>
    public static double[] Weights(List<Sample> samples)
    {
        var w = new double[samples.Count];
        if (samples.Count == 0) return w;
        var gaps = new List<double>(samples.Count);
        for (int i = 0; i + 1 < samples.Count; i++)
        {
            double gap = Math.Max(0, samples[i + 1].TimeMs - samples[i].TimeMs);
            w[i] = gap;
            gaps.Add(gap);
        }
        gaps.Sort();
        // An even count takes the lower middle gap so a lone outlier cannot become the last sample's weight.
        w[samples.Count - 1] = gaps.Count > 0 ? gaps[(gaps.Count - 1) / 2] : 0;
        return w;
    }

    public static bool IsIdleStack(int[] stack, List<FrameInfo> frames)
    {
        int depth = Math.Min(4, stack.Length);
        for (int i = 0; i < depth; i++)
        {
            var name = frames[stack[i]].Method;
            foreach (var mark in IdleLeaves)
                if (name.Contains(mark, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The capture's main thread: the render thread of a client, the tick thread of a server (found by its ServerMain stack when its OS id is unknown).</summary>
    public static ThreadReport? FindMain(Report r)
    {
        string role = CaptureSide.MainRole(r.Side);
        return r.Threads.FirstOrDefault(t => t.Role == role)
            ?? (CaptureSide.IsServer(r.Side) ? r.Threads.FirstOrDefault(t => t.Role == "server") : null);
    }

    static string? RoleOfStack(int[] stack, List<FrameInfo> frames)
    {
        foreach (var f in stack)
        {
            var name = frames[f].Method;
            foreach (var r in Roles)
                foreach (var mark in r.Marks)
                    if (name.Contains(mark, StringComparison.Ordinal)) return r.Role;
        }
        return null;
    }

    static string RoleOf(int threadId, List<Sample> samples, SampleSet set, CaptureMeta meta)
    {
        if (threadId != 0 && threadId == meta.MainThreadOsId) return CaptureSide.MainRole(meta.Side);
        if (threadId != 0 && threadId == meta.ServerThreadOsId) return "server";

        var roleByStack = new Dictionary<int, string?>();
        var counts = new Dictionary<string, int>();
        foreach (var s in samples)
        {
            if (!roleByStack.TryGetValue(s.StackId, out var role))
                roleByStack[s.StackId] = role = RoleOfStack(set.Stacks[s.StackId], set.Frames);
            if (role != null) counts[role] = counts.GetValueOrDefault(role) + 1;
        }
        return counts.Count == 0 ? "other" : counts.OrderByDescending(k => k.Value).First().Key;
    }

    static ThreadReport BuildThread(int threadId, List<Sample> samples, SampleSet set, Attribution attribution, CaptureMeta meta)
    {
        samples.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        var weights = Weights(samples);
        double total = weights.Sum();

        var self = new Dictionary<int, double>();
        var inclusive = new Dictionary<int, double>();
        var assemblySelf = new Dictionary<string, double>();
        var assemblyInclusive = new Dictionary<string, double>();
        var modSelf = new Dictionary<string, double>();
        var modInclusive = new Dictionary<string, double>();
        var paths = new Dictionary<string, double>();
        var pathCache = new Dictionary<int, string>();
        double external = 0, idle = 0;

        var seenFrames = new HashSet<int>();
        var seenAssemblies = new HashSet<string>();
        var seenOwners = new HashSet<string>();
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            double w = weights[i];
            var stack = set.Stacks[s.StackId];
            if (s.External) external += w;
            if (IsIdleStack(stack, set.Frames)) idle += w;
            if (stack.Length == 0) continue;

            Add(self, stack[0], w);
            Add(assemblySelf, Display(set.Frames[stack[0]].Module), w);
            Add(modSelf, attribution.OwnerOfStack(stack, set.Frames), w);

            // A method recursing or a mod appearing twice in one stack still counts this sample once.
            seenFrames.Clear();
            seenAssemblies.Clear();
            seenOwners.Clear();
            foreach (var f in stack)
            {
                if (!seenFrames.Add(f)) continue;
                Add(inclusive, f, w);
                var module = Display(set.Frames[f].Module);
                if (seenAssemblies.Add(module)) Add(assemblyInclusive, module, w);
                var owner = attribution.OwnerOf(set.Frames[f]);
                if (seenOwners.Add(owner)) Add(modInclusive, owner, w);
            }

            if (!pathCache.TryGetValue(s.StackId, out var path))
                pathCache[s.StackId] = path = PathOf(stack, set.Frames);
            Add(paths, path, w);
        }

        return new ThreadReport
        {
            OsThreadId = threadId,
            Role = RoleOf(threadId, samples, set, meta),
            Samples = samples.Count,
            DurationMs = total,
            ExternalPct = Pct(external, total),
            IdlePct = Pct(idle, total),
            MeanIntervalMs = MeanInterval(samples),
            Methods = inclusive
                .Select(kv =>
                {
                    var frame = set.Frames[kv.Key];
                    double selfMs = self.GetValueOrDefault(kv.Key);
                    return new MethodRow
                    {
                        Method = frame.Method,
                        Module = frame.Module,
                        Owner = attribution.OwnerOf(frame),
                        SelfMs = selfMs,
                        TotalMs = kv.Value,
                        SelfPct = Pct(selfMs, total),
                        TotalPct = Pct(kv.Value, total),
                    };
                })
                .OrderByDescending(r => r.SelfMs).ThenByDescending(r => r.TotalMs)
                .Take(MethodRowsKept)
                .ToList(),
            ByAssembly = Groups(assemblySelf, assemblyInclusive, total),
            ByMod = Groups(modSelf, modInclusive, total),
            TopPaths = paths.OrderByDescending(k => k.Value).Take(40)
                .Select(k => new PathRow { Path = k.Key, Ms = k.Value, Pct = Pct(k.Value, total) })
                .ToList(),
        };
    }

    static string Display(string module) => module.Length == 0 ? "(unresolved)" : module;

    static string PathOf(int[] stack, List<FrameInfo> frames)
    {
        var sb = new StringBuilder();
        for (int i = Math.Min(PathDepth, stack.Length) - 1; i >= 0; i--)
        {
            if (sb.Length > 0) sb.Append(" > ");
            sb.Append(ShortName(frames[stack[i]].Method));
        }
        return sb.ToString();
    }

    static List<GroupRow> Groups(Dictionary<string, double> self, Dictionary<string, double> inclusive, double total)
    {
        return inclusive
            .Select(kv => new GroupRow
            {
                Name = kv.Key,
                SelfMs = self.GetValueOrDefault(kv.Key),
                InclusiveMs = kv.Value,
                SelfPct = Pct(self.GetValueOrDefault(kv.Key), total),
                InclusivePct = Pct(kv.Value, total),
            })
            .OrderByDescending(r => r.SelfMs).ThenByDescending(r => r.InclusiveMs)
            .ToList();
    }

    static List<SpikeReport> BuildSpikes(List<Sample> main, SampleSet set, Attribution attribution, CaptureMeta meta)
    {
        var result = new List<SpikeReport>();
        if (meta.QpcFrequency <= 0) return result;
        var times = main.Select(s => s.TimeMs).ToArray();

        foreach (var spike in meta.Spikes)
        {
            double start = (spike.StartQpc - meta.StartQpc) * 1000.0 / meta.QpcFrequency;
            double end = start + spike.DurationMs;
            var row = new SpikeReport
            {
                AtSec = start / 1000.0,
                DurationMs = spike.DurationMs,
                Gen0 = spike.Gen0,
                Gen1 = spike.Gen1,
                Gen2 = spike.Gen2,
            };

            int first = Array.BinarySearch(times, start);
            if (first < 0) first = ~first;
            var votes = new Dictionary<int, int>();
            var ownerVotes = new Dictionary<string, int>();
            for (int i = first; i < main.Count && main[i].TimeMs <= end; i++)
            {
                row.SamplesInFrame++;
                var stack = set.Stacks[main[i].StackId];
                if (stack.Length == 0) continue;
                votes[stack[0]] = votes.GetValueOrDefault(stack[0]) + 1;
                var owner = attribution.OwnerOfStack(stack, set.Frames);
                ownerVotes[owner] = ownerVotes.GetValueOrDefault(owner) + 1;
            }

            double middle = (start + end) / 2;
            int nearest = NearestIndex(times, middle);
            if (nearest >= 0)
            {
                var stack = set.Stacks[main[nearest].StackId];
                row.NearestSampleMs = Math.Abs(times[nearest] - middle);
                row.NearestStack = PathOf(stack, set.Frames);
                if (votes.Count == 0 && stack.Length > 0)
                {
                    row.DominantMethod = "(no sample inside frame) " + ShortName(set.Frames[stack[0]].Method);
                    row.DominantOwner = attribution.OwnerOfStack(stack, set.Frames);
                }
            }
            if (votes.Count > 0)
            {
                row.DominantMethod = ShortName(set.Frames[votes.OrderByDescending(k => k.Value).First().Key].Method);
                row.DominantOwner = ownerVotes.OrderByDescending(k => k.Value).First().Key;
            }
            result.Add(row);
        }
        return result.OrderByDescending(s => s.DurationMs).ToList();
    }

    static int NearestIndex(double[] times, double t)
    {
        if (times.Length == 0) return -1;
        int i = Array.BinarySearch(times, t);
        if (i >= 0) return i;
        i = ~i;
        if (i == 0) return 0;
        if (i >= times.Length) return times.Length - 1;
        return t - times[i - 1] <= times[i] - t ? i - 1 : i;
    }

    static void Add<T>(Dictionary<T, double> map, T key, double w) where T : notnull
    {
        map[key] = map.GetValueOrDefault(key) + w;
    }

    static double Pct(double part, double total) => total > 0 ? part / total * 100.0 : 0;
}
