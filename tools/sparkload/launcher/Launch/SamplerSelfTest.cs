using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SparkLoad.Launch
{
    /// <summary>Hidden `sparkload sampler-selftest`: checks the x64 SYSTEM_PROCESS_INFORMATION offsets against this very process.</summary>
    internal static class SamplerSelfTest
    {
        public static int Run(TextWriter o)
        {
            int fails = 0;
            void Check(string name, bool ok, string detail)
            {
                o.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + detail);
                if (!ok) fails++;
            }

            // a named worker thread that burns CPU, so thread CPU, ctx and names have something to show
            uint workerTid = 0;
            var ready = new ManualResetEventSlim(false);
            var done = new ManualResetEventSlim(false);
            var worker = new Thread(() =>
            {
                workerTid = Native.GetCurrentThreadId();
                ready.Set();
                var sw = Stopwatch.StartNew();
                double x = 0;
                while (sw.ElapsedMilliseconds < 400) x += Math.Sqrt(x + 1);
                GC.KeepAlive(x);
                done.Wait();
            }) { Name = "sl-selftest-worker", IsBackground = true };
            worker.Start();
            ready.Wait();
            uint mainTid = Native.GetCurrentThreadId();
            var sw2 = Stopwatch.StartNew();
            double y = 0;
            while (sw2.ElapsedMilliseconds < 300) y += Math.Sqrt(y + 1);
            GC.KeepAlive(y);
            Thread.Sleep(200);

            using var table = new ProcessTable();
            var me = Process.GetCurrentProcess();
            var wanted = new HashSet<int> { Environment.ProcessId };
            var snap = table.Snapshot(wanted);
            TimeSpan cpuApi = me.TotalProcessorTime;
            long ws = me.WorkingSet64;
            int threadsApi = me.Threads.Count;
            if (!snap.TryGetValue(Environment.ProcessId, out var p))
            {
                Check("self-found", false, "pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " not in snapshot");
                return 1;
            }
            Check("self-found", true, "pid " + p.Pid.ToString(CultureInfo.InvariantCulture));
            Check("image-name", p.ImageName.Equals("sparkload.exe", StringComparison.OrdinalIgnoreCase), "'" + p.ImageName + "'");

            // parent pid via NtQueryInformationProcess(ProcessBasicInformation): InheritedFromUniqueProcessId at offset 40
            IntPtr pbi = Marshal.AllocHGlobal(48);
            try
            {
                int st = Native.NtQueryInformationProcess(me.Handle, 0, pbi, 48, out _);
                long parent = Marshal.ReadInt64(pbi, 40);
                Check("parent-pid", st == 0 && parent == p.ParentPid, "snapshot " + p.ParentPid.ToString(CultureInfo.InvariantCulture) + " vs NtQueryInformationProcess " + parent.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                Marshal.FreeHGlobal(pbi);
            }

            double cpuSnap = (p.UserTime100ns + p.KernelTime100ns) / 10000.0;
            double diff = Math.Abs(cpuSnap - cpuApi.TotalMilliseconds);
            Check("process-cpu", diff < 40 && cpuSnap > 300, "snapshot " + cpuSnap.ToString("0.0", CultureInfo.InvariantCulture) + " ms vs TotalProcessorTime " + cpuApi.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms, diff " + diff.ToString("0.0", CultureInfo.InvariantCulture));
            double userApi = me.UserProcessorTime.TotalMilliseconds;
            double userSnap = p.UserTime100ns / 10000.0;
            Check("process-user-cpu", Math.Abs(userSnap - userApi) < 40, "snapshot " + userSnap.ToString("0.0", CultureInfo.InvariantCulture) + " vs API " + userApi.ToString("0.0", CultureInfo.InvariantCulture));
            Check("create-time", Math.Abs(p.CreateTime - me.StartTime.ToFileTime()) < 20000000, "snapshot " + p.CreateTime.ToString(CultureInfo.InvariantCulture) + " vs StartTime " + me.StartTime.ToFileTime().ToString(CultureInfo.InvariantCulture));
            Check("working-set", Math.Abs(p.WorkingSet - ws) < Math.Max(8L << 20, ws / 5), "snapshot " + (p.WorkingSet >> 20).ToString(CultureInfo.InvariantCulture) + " MB vs API " + (ws >> 20).ToString(CultureInfo.InvariantCulture) + " MB");
            Check("private-bytes", p.PrivateBytes > (1 << 20) && p.PrivateBytes < (64L << 30), p.PrivateBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
            Check("thread-count", p.Threads.Count == p.NumberOfThreads && Math.Abs(p.NumberOfThreads - threadsApi) <= 3, "snapshot " + p.NumberOfThreads.ToString(CultureInfo.InvariantCulture) + " parsed " + p.Threads.Count.ToString(CultureInfo.InvariantCulture) + " vs API " + threadsApi.ToString(CultureInfo.InvariantCulture));

            ThreadSnap? tMain = p.Threads.Find(t => t.Tid == mainTid);
            ThreadSnap? tWork = p.Threads.Find(t => t.Tid == workerTid);
            Check("current-thread-found", tMain != null, "tid " + mainTid.ToString(CultureInfo.InvariantCulture));
            if (tMain != null)
            {
                double c = (tMain.UserTime100ns + tMain.KernelTime100ns) / 10000.0;
                Check("current-thread-cpu", c >= 250 && c < 3000, c.ToString("0.0", CultureInfo.InvariantCulture) + " ms (burned 300 ms)");
                Check("current-thread-ctx", tMain.ContextSwitches > 0 && tMain.ThreadState >= 0 && tMain.ThreadState <= 9, "ctx " + tMain.ContextSwitches.ToString(CultureInfo.InvariantCulture) + " state " + tMain.ThreadState.ToString(CultureInfo.InvariantCulture) + " wait " + tMain.WaitReason.ToString(CultureInfo.InvariantCulture));
            }
            Check("worker-thread-found", tWork != null, "tid " + workerTid.ToString(CultureInfo.InvariantCulture));
            if (tWork != null)
            {
                double c = (tWork.UserTime100ns + tWork.KernelTime100ns) / 10000.0;
                Check("worker-thread-cpu", c >= 250 && c < 3000, c.ToString("0.0", CultureInfo.InvariantCulture) + " ms (burned about 400 ms)");
                string nm = ThreadNamer.QueryNow(workerTid);
                Check("thread-name", nm == "sl-selftest-worker", "'" + nm + "'");
            }
            done.Set();
            o.WriteLine(fails == 0 ? "SAMPLER-SELFTEST PASS" : "SAMPLER-SELFTEST FAIL (" + fails.ToString(CultureInfo.InvariantCulture) + ")");
            return fails == 0 ? 0 : 1;
        }
    }
}
