using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SparkLoadTestTarget
{
    // Stand-in for the real hook: an EventSource named SparkLoad so the trace has that provider.
    [EventSource(Name = "SparkLoad")]
    internal sealed class SparkLoadSource : EventSource
    {
        public static readonly SparkLoadSource Log = new SparkLoadSource();

        [Event(1, Level = EventLevel.Informational)]
        public void Hello(int pid, long qpc, long qpcFreq) { if (IsEnabled()) WriteEvent(1, pid, qpc, qpcFreq); }

        [Event(3, Level = EventLevel.Informational)]
        public void Begin(int markerId, int tid, long seq, long qpc) { if (IsEnabled()) WriteEvent(3, markerId, tid, seq, qpc); }

        [Event(4, Level = EventLevel.Informational)]
        public void End(int markerId, int tid, long seq, long qpc) { if (IsEnabled()) WriteEvent(4, markerId, tid, seq, qpc); }

        [Event(10, Level = EventLevel.Informational)]
        public void Stats(string marker, long hits) { if (IsEnabled()) WriteEvent(10, marker, hits); }
    }

    internal static class Program
    {
        static int Main(string[] args)
        {
            long mainQpc = Stopwatch.GetTimestamp();
            string? logs = null;
            double sleep = 0;
            bool spawn = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--logs": logs = args[++i]; break;
                    case "--sleep": sleep = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--spawn-child": spawn = true; break;
                }
            }

            int pid = Environment.ProcessId;
            SparkLoadSource.Log.Hello(pid, mainQpc, Stopwatch.Frequency);

            if (logs != null)
            {
                Directory.CreateDirectory(logs);
                var names = new List<string>();
                foreach (DictionaryEntry de in Environment.GetEnvironmentVariables()) names.Add((string)de.Key);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(Path.Combine(logs, "env-names.txt"), string.Join("\n", names) + "\n");
                var info = new StringBuilder();
                info.Append("pid=").Append(pid).Append('\n');
                info.Append("ppid_hint=").Append(Environment.GetEnvironmentVariable("SPARKLOAD_LAUNCHER_PID") ?? "").Append('\n');
                info.Append("main_qpc=").Append(mainQpc).Append('\n');
                info.Append("qpc_freq=").Append(Stopwatch.Frequency).Append('\n');
                info.Append("process=").Append(Process.GetCurrentProcess().ProcessName).Append('\n');
                File.WriteAllText(Path.Combine(logs, "target-info.txt"), info.ToString());
            }

            int grandPid = 0;
            Process? grand = null;
            if (spawn)
            {
                string self = Environment.ProcessPath ?? "";
                var psi = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("--sleep");
                psi.ArgumentList.Add("1");
                if (logs != null)
                {
                    psi.ArgumentList.Add("--logs");
                    psi.ArgumentList.Add(Path.Combine(logs, "grandchild"));
                }
                grand = Process.Start(psi);
                grandPid = grand?.Id ?? 0;
                if (logs != null) File.AppendAllText(Path.Combine(logs, "target-info.txt"), "grandchild_pid=" + grandPid + "\n");
            }

            // some allocation and a forced GC so the runtime provider has GC events
            var keep = new List<byte[]>();
            for (int i = 0; i < 64; i++) keep.Add(new byte[256 * 1024]);
            GC.Collect(2, GCCollectionMode.Forced, true);
            keep.Clear();
            GC.Collect();

            long seq = 0;
            if (logs != null)
            {
                string logPath = Path.Combine(logs, "client-main.log");
                using var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                string[] lines =
                {
                    "12.11.2026 10:00:00 [Notification] Test target starting",
                    "12.11.2026 10:00:00 [Notification] Café über test line with non-ascii bytes",
                    "12.11.2026 10:00:01 [Notification] Loading mods",
                    "12.11.2026 10:00:01 [Event] Received level finalize",
                    "12.11.2026 10:00:02 [Notification] Level ready",
                    "12.11.2026 10:00:02 [Notification] A partial line follows the next write",
                };
                var enc = new UTF8Encoding(false);
                for (int i = 0; i < lines.Length; i++)
                {
                    SparkLoadSource.Log.Begin(1, Environment.CurrentManagedThreadId, ++seq, Stopwatch.GetTimestamp());
                    string text = lines[i] + (i % 2 == 0 ? "\n" : "\r\n");
                    byte[] b = enc.GetBytes(text);
                    // write the last line in two halves to exercise the partial-line path
                    if (i == lines.Length - 1)
                    {
                        fs.Write(b, 0, b.Length / 2);
                        fs.Flush();
                        Thread.Sleep(300);
                        fs.Write(b, b.Length / 2, b.Length - b.Length / 2);
                    }
                    else
                    {
                        fs.Write(b, 0, b.Length);
                    }
                    fs.Flush();
                    SparkLoadSource.Log.End(1, Environment.CurrentManagedThreadId, ++seq, Stopwatch.GetTimestamp());
                    Thread.Sleep(400);
                }
            }
            SparkLoadSource.Log.Stats("test", seq);

            if (grand != null)
            {
                grand.WaitForExit();
                if (logs != null) File.AppendAllText(Path.Combine(logs, "target-info.txt"), "grandchild_exit=" + grand.ExitCode + "\n");
            }
            if (sleep > 0) Thread.Sleep((int)(sleep * 1000));
            if (logs != null) File.AppendAllText(Path.Combine(logs, "target-info.txt"), "done=1\n");
            return 0;
        }
    }
}
