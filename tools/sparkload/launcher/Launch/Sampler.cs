using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SparkLoad.Launch
{
    internal sealed class ThreadSnap
    {
        public uint Tid;
        public long KernelTime100ns;
        public long UserTime100ns;
        public uint ContextSwitches;
        public int ThreadState;
        public int WaitReason;
    }

    internal sealed class ProcSnap
    {
        public int Pid;
        public int ParentPid;
        public string ImageName = "";
        public int NumberOfThreads;
        public long CreateTime;
        public long UserTime100ns;
        public long KernelTime100ns;
        public long WorkingSet;
        public long PrivateBytes;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
        public List<ThreadSnap> Threads = new List<ThreadSnap>();
    }

    /// <summary>One NtQuerySystemInformation(SystemProcessInformation) call, parsed with the x64 offsets in SPEC.</summary>
    internal sealed class ProcessTable : IDisposable
    {
        const int SystemProcessInformation = 5;
        byte[] _buf = new byte[1 << 20];
        GCHandle _pin;

        public ProcessTable()
        {
            _pin = GCHandle.Alloc(_buf, GCHandleType.Pinned);
        }

        public void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }

        /// <summary>Takes one snapshot. wanted == null returns every process.</summary>
        public Dictionary<int, ProcSnap> Snapshot(HashSet<int>? wanted)
        {
            int status;
            int ret;
            while (true)
            {
                status = Native.NtQuerySystemInformation(SystemProcessInformation, _pin.AddrOfPinnedObject(), _buf.Length, out ret);
                if (status != Native.STATUS_INFO_LENGTH_MISMATCH) break;
                int want = Math.Max(ret + (256 << 10), _buf.Length * 2);
                _pin.Free();
                _buf = new byte[want];
                _pin = GCHandle.Alloc(_buf, GCHandleType.Pinned);
            }
            if (status < 0) throw new InvalidOperationException("NtQuerySystemInformation 0x" + status.ToString("X8", CultureInfo.InvariantCulture));

            long baseAddr = _pin.AddrOfPinnedObject().ToInt64();
            var result = new Dictionary<int, ProcSnap>();
            ReadOnlySpan<byte> b = _buf;
            int off = 0;
            while (true)
            {
                int next = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(off, 4));
                int pid = (int)BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off + 80, 8));
                if (wanted == null || wanted.Contains(pid))
                {
                    var p = new ProcSnap
                    {
                        Pid = pid,
                        NumberOfThreads = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(off + 4, 4)),
                        CreateTime = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off + 32, 8)),
                        UserTime100ns = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off + 40, 8)),
                        KernelTime100ns = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off + 48, 8)),
                        ParentPid = (int)BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off + 88, 8)),
                        WorkingSet = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off + 144, 8)),
                        PrivateBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off + 200, 8)),
                        ReadBytes = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off + 232, 8)),
                        WriteBytes = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off + 240, 8)),
                        OtherBytes = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off + 248, 8)),
                    };
                    int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 56, 2));
                    long namePtr = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(off + 64, 8));
                    if (nameLen > 0 && namePtr != 0)
                    {
                        long rel = namePtr - baseAddr;
                        if (rel >= 0 && rel + nameLen <= _buf.Length)
                            p.ImageName = Encoding.Unicode.GetString(_buf, (int)rel, nameLen);
                    }
                    else if (pid == 0)
                    {
                        p.ImageName = "Idle";
                    }
                    int tcount = p.NumberOfThreads;
                    int toff = off + 256;
                    for (int i = 0; i < tcount; i++)
                    {
                        int o = toff + i * 80;
                        if (o + 80 > _buf.Length) break;
                        p.Threads.Add(new ThreadSnap
                        {
                            KernelTime100ns = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o, 8)),
                            UserTime100ns = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o + 8, 8)),
                            Tid = (uint)BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o + 48, 8)),
                            ContextSwitches = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o + 64, 4)),
                            ThreadState = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o + 68, 4)),
                            WaitReason = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o + 72, 4)),
                        });
                    }
                    result[pid] = p;
                }
                if (next == 0) break;
                off += next;
            }
            return result;
        }
    }

    /// <summary>Reads OS thread descriptions. Cached per tid; re-queried every 2 s while empty.</summary>
    internal sealed class ThreadNamer
    {
        sealed class Entry
        {
            public string Name = "";
            public long NextQuery;
        }

        readonly Dictionary<uint, Entry> _cache = new Dictionary<uint, Entry>();
        readonly long _requeryTicks = 2 * Stopwatch.Frequency;

        public string Get(uint tid, long nowTicks)
        {
            if (!_cache.TryGetValue(tid, out var e))
            {
                e = new Entry();
                _cache[tid] = e;
                Query(tid, e, nowTicks);
            }
            else if (e.Name.Length == 0 && nowTicks >= e.NextQuery)
            {
                Query(tid, e, nowTicks);
            }
            return e.Name;
        }

        public static string QueryNow(uint tid)
        {
            var e = new Entry();
            new ThreadNamer().Query(tid, e, 0);
            return e.Name;
        }

        void Query(uint tid, Entry e, long nowTicks)
        {
            e.NextQuery = nowTicks + _requeryTicks;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
                if (h == IntPtr.Zero) return;
                int hr = Native.GetThreadDescription(h, out IntPtr p);
                if (hr >= 0 && p != IntPtr.Zero)
                {
                    e.Name = Marshal.PtrToStringUni(p) ?? "";
                    Native.LocalFree(p);
                }
            }
            catch
            {
            }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }
    }

    /// <summary>Samples the child and every other process in the job every 250 ms into samples.csv.</summary>
    internal sealed class Sampler
    {
        readonly string _path;
        readonly long _t0;
        readonly Func<HashSet<int>> _pids;
        readonly Action<string> _log;
        readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        Thread? _thread;
        public int Rows;
        public int Ticks;

        public Sampler(string path, long t0, Func<HashSet<int>> pids, Action<string> log)
        {
            _path = path;
            _t0 = t0;
            _pids = pids;
            _log = log;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "sparkload-sampler" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop.Set();
            _thread?.Join(5000);
        }

        static string Ms(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static string Csv(string s)
        {
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        void Run()
        {
            try
            {
                using var table = new ProcessTable();
                var namer = new ThreadNamer();
                var lastT = new Dictionary<(int, uint), (long cpu, uint ctx)>();
                using var w = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 1 << 16);
                w.NewLine = "\n";
                w.WriteLine("t_ms,pid,kind,tid,name,cpu_ms,state,wait_reason,ctx,read_bytes,write_bytes,other_bytes,working_set,private_bytes,threads");
                long period = Stopwatch.Frequency / 4;
                long next = Stopwatch.GetTimestamp();
                long lastFlush = next;
                while (!_stop.IsSet)
                {
                    long now = Stopwatch.GetTimestamp();
                    HashSet<int> want = _pids();
                    if (want.Count > 0)
                    {
                        Dictionary<int, ProcSnap> snap;
                        try
                        {
                            snap = table.Snapshot(want);
                            now = Stopwatch.GetTimestamp();
                        }
                        catch (Exception ex)
                        {
                            _log("sampler: " + ex.Message);
                            snap = new Dictionary<int, ProcSnap>();
                        }
                        string t = Ms((now - _t0) * 1000.0 / Stopwatch.Frequency);
                        foreach (var kv in snap)
                        {
                            ProcSnap p = kv.Value;
                            double pcpu = (p.UserTime100ns + p.KernelTime100ns) / 10000.0;
                            w.WriteLine(t + "," + p.Pid.ToString(CultureInfo.InvariantCulture) + ",P,," + Csv(p.ImageName) + "," + Ms(pcpu) + ",,,,"
                                + p.ReadBytes.ToString(CultureInfo.InvariantCulture) + "," + p.WriteBytes.ToString(CultureInfo.InvariantCulture) + ","
                                + p.OtherBytes.ToString(CultureInfo.InvariantCulture) + "," + p.WorkingSet.ToString(CultureInfo.InvariantCulture) + ","
                                + p.PrivateBytes.ToString(CultureInfo.InvariantCulture) + "," + p.NumberOfThreads.ToString(CultureInfo.InvariantCulture));
                            Rows++;
                            foreach (ThreadSnap th in p.Threads)
                            {
                                long cpu100 = th.UserTime100ns + th.KernelTime100ns;
                                var key = (p.Pid, th.Tid);
                                if (lastT.TryGetValue(key, out var prev) && prev.cpu == cpu100 && prev.ctx == th.ContextSwitches) continue;
                                lastT[key] = (cpu100, th.ContextSwitches);
                                string name = namer.Get(th.Tid, now);
                                w.WriteLine(t + "," + p.Pid.ToString(CultureInfo.InvariantCulture) + ",T," + th.Tid.ToString(CultureInfo.InvariantCulture) + ","
                                    + Csv(name) + "," + Ms(cpu100 / 10000.0) + "," + th.ThreadState.ToString(CultureInfo.InvariantCulture) + ","
                                    + th.WaitReason.ToString(CultureInfo.InvariantCulture) + "," + th.ContextSwitches.ToString(CultureInfo.InvariantCulture) + ",,,,,,");
                                Rows++;
                            }
                        }
                        Ticks++;
                    }
                    if (now - lastFlush > Stopwatch.Frequency)
                    {
                        w.Flush();
                        lastFlush = now;
                    }
                    next += period;
                    long cur = Stopwatch.GetTimestamp();
                    if (next < cur) next = cur;
                    int waitMs = (int)((next - cur) * 1000 / Stopwatch.Frequency);
                    if (waitMs > 0) _stop.Wait(waitMs);
                }
                w.Flush();
            }
            catch (Exception ex)
            {
                _log("sampler failed: " + ex);
            }
        }
    }
}
