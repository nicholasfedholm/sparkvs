using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SparkLoad.Launch
{
    internal sealed class RunLog : IDisposable
    {
        StreamWriter? _file;
        readonly object _lock = new object();

        public void Open(string path)
        {
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public void Line(string s)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s;
            lock (_lock)
            {
                try { _file?.WriteLine(stamped); } catch { }
                try { Console.Out.WriteLine(stamped); Console.Out.Flush(); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _file?.Dispose(); } catch { }
                _file = null;
            }
        }
    }

    internal static class Guard
    {
        public sealed class Found
        {
            public int Pid;
            public string Name = "";
            public string Start = "?";
        }

        public static List<Found> Scan(string[] names)
        {
            var l = new List<Found>();
            int self = Environment.ProcessId;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self) continue;
                    string n = p.ProcessName;
                    foreach (string g in names)
                    {
                        if (g.Length > 0 && n.StartsWith(g, StringComparison.OrdinalIgnoreCase))
                        {
                            string st = "?";
                            try { st = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); } catch { }
                            l.Add(new Found { Pid = p.Id, Name = n, Start = st });
                            break;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
            return l;
        }

        public static int Preflight(string[] names, Action<string> print)
        {
            var found = Scan(names);
            if (found.Count == 0)
            {
                print("preflight: no guarded process running (" + string.Join(",", names) + ")");
                return 0;
            }
            foreach (var f in found)
                print("preflight: foreign process pid=" + f.Pid.ToString(CultureInfo.InvariantCulture) + " name=" + f.Name + " start=" + f.Start);
            return 3;
        }
    }

    internal static class Launcher
    {
        public const string Version = "0.1.0";

        static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', bs * 2 + 1);
                    sb.Append('"');
                    bs = 0;
                    continue;
                }
                sb.Append('\\', bs);
                bs = 0;
                sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static SortedDictionary<string, string> BuildEnv(RunOptions o, string runDir, string? pipeName, string exeFull)
        {
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                string k = (string)de.Key;
                if (k.Length == 0) continue;
                env[k] = (string?)de.Value ?? "";
            }
            if (o.Mode != "passive" && pipeName != null)
            {
                env["DOTNET_DiagnosticPorts"] = pipeName + ",suspend"; // Windows: the runtime prepends the pipe namespace itself
                if (!string.IsNullOrEmpty(o.Hook))
                {
                    string hook = Path.GetFullPath(o.Hook);
                    env["DOTNET_STARTUP_HOOKS"] = env.TryGetValue("DOTNET_STARTUP_HOOKS", out var cur) && cur.Length > 0 ? cur + ";" + hook : hook;
                }
                env["SPARKLOAD_RUN_DIR"] = runDir;
                env["SPARKLOAD_LAUNCHER_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
                env["SPARKLOAD_TARGET_EXE"] = exeFull;
                env["SPARKLOAD_OFF"] = o.Off;
            }
            return env;
        }

        static IntPtr EnvBlock(SortedDictionary<string, string> env)
        {
            var sb = new StringBuilder();
            foreach (var kv in env)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            char[] chars = sb.ToString().ToCharArray();
            IntPtr p = Marshal.AllocHGlobal(chars.Length * 2);
            Marshal.Copy(chars, 0, p, chars.Length);
            return p;
        }

        static HashSet<int> JobPids(IntPtr job, int childPid)
        {
            var set = new HashSet<int> { childPid };
            int size = 8 + 8 * 4096;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.QueryInformationJobObject(job, Native.JobObjectBasicProcessIdList, buf, size, out _))
                {
                    int n = Marshal.ReadInt32(buf, 4);
                    for (int i = 0; i < n && i < 4096; i++)
                        set.Add((int)Marshal.ReadInt64(buf, 8 + i * 8));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return set;
        }

        static int JobProcessCount(IntPtr job)
        {
            int size = 8 + 8 * 4096;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.QueryInformationJobObject(job, Native.JobObjectBasicProcessIdList, buf, size, out _))
                    return Marshal.ReadInt32(buf, 0);
                return -1;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static IntPtr MakeJob(ulong processMemoryBytes)
        {
            IntPtr job = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            SetJobLimits(job, true, processMemoryBytes);
            return job;
        }

        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, x64, 144 bytes: BasicLimitInformation 0..63 (LimitFlags at 16),
        // IO_COUNTERS 64..111, ProcessMemoryLimit at 112, JobMemoryLimit 120, PeakProcessMemoryUsed 128, PeakJobMemoryUsed 136.
        // The whole struct is rewritten on every call, so each caller passes both limits (the detach path keeps the
        // memory limit and clears only kill-on-close). processMemoryBytes = 0 means no memory limit.
        const int LimitFlagsOffset = 16;
        const int ProcessMemoryLimitOffset = 112;

        static bool SetJobLimits(IntPtr job, bool killOnClose, ulong processMemoryBytes)
        {
            IntPtr info = Marshal.AllocHGlobal(144);
            try
            {
                for (int i = 0; i < 144; i += 8) Marshal.WriteInt64(info, i, 0);
                uint flags = (killOnClose ? Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE : 0) | (processMemoryBytes > 0 ? Native.JOB_OBJECT_LIMIT_PROCESS_MEMORY : 0);
                Marshal.WriteInt32(info, LimitFlagsOffset, (int)flags);
                Marshal.WriteInt64(info, ProcessMemoryLimitOffset, (long)processMemoryBytes);
                return Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, info, 144);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        static double Round3(double v) => Math.Round(v, 3);

        static void WriteRunJson(string path, Dictionary<string, object?> d)
        {
            string json = JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true });
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json + "\n", new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }

        public static int Run(RunOptions o, RunLog log)
        {
            // 1. preflight guard: nothing is created, nothing is launched
            string[] guard = RunOptions.SplitGuard(o.GuardNames);
            int g = Guard.Preflight(guard, s => log.Line(s));
            if (g != 0)
            {
                log.Line("abort: foreign game process present, nothing launched, nothing killed");
                return 3;
            }

            string exeFull;
            try { exeFull = Path.GetFullPath(o.Exe); } catch { log.Line("bad --exe path"); return 2; }
            string arm = o.Arm ?? (o.Mode == "passive" ? "V" : o.Mode == "deep" ? "D" : "O");
            string runId = o.RunId ?? (arm + "-" + o.Mode + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            string runDir = Path.GetFullPath(Path.Combine(o.Out, runId));
            try
            {
                if (Directory.Exists(runDir) && Directory.GetFileSystemEntries(runDir).Length > 0)
                {
                    log.Line("run folder exists and is not empty: " + runDir);
                    return 2;
                }
                Directory.CreateDirectory(runDir);
                log.Open(Path.Combine(runDir, "launcher.log"));
            }
            catch (Exception ex)
            {
                log.Line("cannot create run folder: " + ex.Message);
                return 2;
            }
            bool deep = o.Mode == "deep";
            bool traced = o.Mode != "passive";
            log.Line("sparkload " + Version + " run " + runId + " arm=" + arm + " mode=" + o.Mode + " exe=" + exeFull);

            Regex pattern = new Regex(o.FinalizePattern, RegexOptions.CultureInvariant);
            var tailer = new LogTailer(Path.Combine(runDir, "logtail.csv"), o.Data, o.LauncherLog, pattern, o.FinalizeFile, s => log.Line(s));
            tailer.Prepare();

            DiagServer? diag = null;
            string? pipeName = null;
            if (traced)
            {
                pipeName = "sparkload-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                // Wire LogLevel: 4 = Informational (5 = Verbose), as SPEC section 2 asks.
                var providers = new List<TraceProvider>
                {
                    new TraceProvider { Name = "SparkLoad", Keywords = ulong.MaxValue, Level = 4 },
                    new TraceProvider { Name = "Microsoft-Windows-DotNETRuntime", Keywords = 0x1UL | 0x8UL | 0x8000UL | (deep ? 0x10UL | 0x20UL | 0x40000000UL : 0), Level = 4 },
                };
                if (deep) providers.Add(new TraceProvider { Name = "Microsoft-DotNETCore-SampleProfiler", Keywords = ulong.MaxValue, Level = 4 });
                diag = new DiagServer(pipeName, Stopwatch.GetTimestamp(), Path.Combine(runDir, "ipc.log"), Path.Combine(runDir, "raw.nettrace"),
                    providers, deep, deep, 256, true, s => log.Line(s));
                diag.HoldBeforeResumeSeconds = (int)Math.Ceiling(o.DebugHoldBeforeResume);
                diag.MaxTraceBytes = o.MaxTraceMb > 0 ? (long)(o.MaxTraceMb * 1048576.0) : 0;
                try
                {
                    diag.Start();
                }
                catch (Exception ex)
                {
                    log.Line("cannot start diagnostic server: " + ex.Message);
                    diag.Dispose();
                    return 4;
                }
                log.Line("diagnostic pipe " + pipeName);
            }

            var env = BuildEnv(o, runDir, pipeName, exeFull);
            IntPtr envBlock = EnvBlock(env);
            string cmdText = QuoteArg(exeFull);
            foreach (string a in o.GameArgs) cmdText += " " + QuoteArg(a);
            char[] cmd = (cmdText + "\0").ToCharArray();
            string cwd = Path.GetDirectoryName(exeFull) ?? Environment.CurrentDirectory;

            ulong memLimitBytes = o.MaxChildGb > 0 ? (ulong)(o.MaxChildGb * 1073741824.0) : 0;
            IntPtr job = MakeJob(memLimitBytes);
            if (job == IntPtr.Zero)
            {
                log.Line("CreateJobObject failed, error " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                diag?.Dispose();
                return 4;
            }
            log.Line("job: kill-on-close, process memory limit " + (memLimitBytes > 0
                ? o.MaxChildGb.ToString("0.##", CultureInfo.InvariantCulture) + " GB (flags 0x" + (Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Native.JOB_OBJECT_LIMIT_PROCESS_MEMORY).ToString("X", CultureInfo.InvariantCulture) + ", bytes " + memLimitBytes.ToString(CultureInfo.InvariantCulture) + ")"
                : "off (flags 0x" + Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.ToString("X", CultureInfo.InvariantCulture) + ")")
                + "; trace size cap " + (o.MaxTraceMb > 0 ? o.MaxTraceMb.ToString("0.##", CultureInfo.InvariantCulture) + " MB" : "off"));

            uint flags = Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT | (o.NoWindow ? Native.CREATE_NO_WINDOW : 0);
            var si = new Native.STARTUPINFOW();
            si.cb = Marshal.SizeOf<Native.STARTUPINFOW>();

            // t0: QPC read immediately before CreateProcessW
            long t0 = Stopwatch.GetTimestamp();
            Native.GetSystemTimePreciseAsFileTime(out long t0Utc);
            bool created = Native.CreateProcessW(exeFull, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, cwd, ref si, out var pi);
            int createErr = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(envBlock);
            if (!created)
            {
                log.Line("CreateProcess failed, error " + createErr.ToString(CultureInfo.InvariantCulture) + " for " + exeFull);
                diag?.Dispose();
                Native.CloseHandle(job);
                var fail = new Dictionary<string, object?> { ["run_id"] = runId, ["arm"] = arm, ["mode"] = o.Mode, ["exe"] = exeFull, ["exit_reason"] = "create-failed", ["launcher_version"] = Version };
                try { WriteRunJson(Path.Combine(runDir, "run.json"), fail); } catch { }
                return 4;
            }
            diag?.SetT0(t0);
            diag?.SetChild(pi.dwProcessId);
            if (!Native.AssignProcessToJobObject(job, pi.hProcess))
            {
                log.Line("AssignProcessToJobObject failed, error " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ", terminating child");
                Native.TerminateProcess(pi.hProcess, 1);
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
                Native.CloseHandle(job);
                diag?.Dispose();
                return 4;
            }
            int childPid = pi.dwProcessId;
            Native.GetProcessTimes(pi.hProcess, out long kernelCreate, out _, out _, out _);
            double g1 = (kernelCreate - t0Utc) / 10000.0;

            tailer.Start(t0);
            Sampler? sampler = null;
            if (traced)
            {
                sampler = new Sampler(Path.Combine(runDir, "samples.csv"), t0, () => JobPids(job, childPid), s => log.Line(s));
                sampler.Start();
            }

            var rj = new Dictionary<string, object?>
            {
                ["run_id"] = runId,
                ["arm"] = arm,
                ["mode"] = o.Mode,
                ["timing_void"] = deep,
                ["exe"] = exeFull,
                ["args"] = o.GameArgs.ToArray(),
                ["data_path"] = o.Data,
                ["launcher_version"] = Version,
                ["t0_qpc"] = t0,
                ["qpc_freq"] = Stopwatch.Frequency,
                ["t0_utc_filetime"] = t0Utc,
                ["child_pid"] = childPid,
                ["kernel_create_filetime"] = kernelCreate,
                ["g1_delta_ms"] = Round3(g1),
                ["finalize_log_ms"] = null,
                ["end_ms"] = null,
                ["exit_reason"] = "running",
                ["present_mode"] = null,
                ["launcher_pid"] = Environment.ProcessId,
                ["finalize_pattern"] = o.FinalizePattern,
                ["finalize_file"] = o.FinalizeFile,
                ["tail_seconds"] = o.TailSeconds,
                ["trace_bytes"] = null,
                ["trace_end"] = null,
                ["child_exit_code"] = null,
                ["max_trace_mb"] = o.MaxTraceMb,
                ["max_child_gb"] = o.MaxChildGb,
            };
            string runJsonPath = Path.Combine(runDir, "run.json");
            try { WriteRunJson(runJsonPath, rj); } catch { }

            uint rs = Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
            log.Line("child pid " + childPid.ToString(CultureInfo.InvariantCulture) + " created, g1_delta_ms=" + g1.ToString("0.000", CultureInfo.InvariantCulture)
                + ", primary thread resumed (previous suspend count " + rs.ToString(CultureInfo.InvariantCulture) + ")");

            // 7. wait for finalize
            string reason = "";
            long maxTicks = t0 + (long)(o.MaxSeconds * Stopwatch.Frequency);
            long finalizeTicks = 0;
            while (true)
            {
                finalizeTicks = tailer.FinalizeTicks;
                if (finalizeTicks != 0) break;
                if (Native.WaitForSingleObject(pi.hProcess, 0) == Native.WAIT_OBJECT_0)
                {
                    Thread.Sleep(250); // let the tailer see a last line written just before exit
                    finalizeTicks = tailer.FinalizeTicks;
                    if (finalizeTicks == 0) reason = "child-exit-before-finalize";
                    break;
                }
                if (Stopwatch.GetTimestamp() >= maxTicks) { reason = "max-seconds"; break; }
                Thread.Sleep(20);
            }
            double? finalizeMs = null;
            if (finalizeTicks != 0)
            {
                finalizeMs = (finalizeTicks - t0) * 1000.0 / Stopwatch.Frequency;
                log.Line("finalize line seen at t_ms=" + finalizeMs.Value.ToString("0.000", CultureInfo.InvariantCulture) + ", collecting " + o.TailSeconds.ToString(CultureInfo.InvariantCulture) + " s more");
                long until = Stopwatch.GetTimestamp() + (long)(o.TailSeconds * Stopwatch.Frequency);
                reason = "finalize";
                while (Stopwatch.GetTimestamp() < until)
                {
                    if (Native.WaitForSingleObject(pi.hProcess, 0) == Native.WAIT_OBJECT_0) { reason = "finalize-then-child-exit"; break; }
                    Thread.Sleep(20);
                }
            }
            else
            {
                log.Line("no finalize: " + reason);
            }

            // StopTracing, close files
            bool traceClean = false;
            if (diag != null)
            {
                traceClean = diag.StopTracing();
                if (!diag.WaitCopyDone(0))
                {
                    // The copy thread must not be writing raw.nettrace while the analyzer reads it: close the pipes and wait.
                    log.Line("trace: copy thread still running after StopTracing, closing the diagnostic pipes");
                    diag.Dispose();
                    if (!diag.WaitCopyDone(10000)) log.Line("trace: copy thread did not finish within 10 s");
                }
                log.Line("trace: " + diag.CopyEnd + ", " + diag.TraceBytes.ToString(CultureInfo.InvariantCulture) + " bytes, clean=" + traceClean.ToString().ToLowerInvariant()
                    + ", session=" + diag.SessionOk.ToString().ToLowerInvariant());
            }
            sampler?.Stop();
            tailer.Stop();
            if (sampler != null) log.Line("sampler: " + sampler.Ticks.ToString(CultureInfo.InvariantCulture) + " ticks, " + sampler.Rows.ToString(CultureInfo.InvariantCulture) + " rows");
            log.Line("logtail: " + tailer.Lines.ToString(CultureInfo.InvariantCulture) + " lines");
            double endMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            uint exitCode = 0;
            bool childGone = Native.WaitForSingleObject(pi.hProcess, 0) == Native.WAIT_OBJECT_0;
            if (childGone) Native.GetExitCodeProcess(pi.hProcess, out exitCode);
            rj["finalize_log_ms"] = finalizeMs.HasValue ? Round3(finalizeMs.Value) : null;
            rj["end_ms"] = Round3(endMs);
            rj["exit_reason"] = reason;
            rj["trace_bytes"] = diag != null ? diag.TraceBytes : null;
            rj["trace_end"] = diag != null ? diag.CopyEnd : null;
            rj["child_exit_code"] = childGone ? (object)(int)exitCode : null;
            try { WriteRunJson(runJsonPath, rj); } catch (Exception ex) { log.Line("run.json write failed: " + ex.Message); }

            // --then kill: the game goes down after StopTracing and the trace copy, BEFORE the analyzer, so a big
            // analysis never runs next to a game that holds 20+ GB.
            bool killed = false;
            if (o.Then == "kill")
            {
                log.Line("then kill: terminating job before the analyzer");
                Native.TerminateJobObject(job, 1);
                Native.WaitForSingleObject(pi.hProcess, 5000);
                killed = true;
            }

            // analyzer
            int analyzerRc = 0;
            try
            {
                var tw = new LineWriter(s => log.Line("analyzer: " + s));
                int rc = SparkLoad.Analyzer.AnalyzerMain.Run(runDir, o.Compare, tw);
                tw.Flush();
                if (rc != 0) { analyzerRc = 6; log.Line("analyzer returned " + rc.ToString(CultureInfo.InvariantCulture)); }
            }
            catch (Exception ex)
            {
                analyzerRc = 6;
                log.Line("analyzer failed: " + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(o.ManifestCmd))
            {
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c " + o.ManifestCmd)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = runDir,
                    };
                    psi.Environment["SPARKLOAD_RUN_DIR"] = runDir;
                    using var mp = Process.Start(psi);
                    if (mp != null)
                    {
                        string so = mp.StandardOutput.ReadToEnd();
                        string se = mp.StandardError.ReadToEnd();
                        mp.WaitForExit();
                        log.Line("manifest-cmd exit " + mp.ExitCode.ToString(CultureInfo.InvariantCulture) + (so.Length > 0 ? " out: " + so.Trim() : "") + (se.Length > 0 ? " err: " + se.Trim() : ""));
                    }
                }
                catch (Exception ex)
                {
                    log.Line("manifest-cmd failed: " + ex.Message);
                }
            }

            // --then
            switch (o.Then)
            {
                case "kill":
                    if (!killed)
                    {
                        log.Line("then kill: terminating job");
                        Native.TerminateJobObject(job, 1);
                        Native.WaitForSingleObject(pi.hProcess, 5000);
                    }
                    break;
                case "detach":
                    log.Line("then detach: clearing kill-on-close" + (memLimitBytes > 0 ? ", keeping the process memory limit" : ""));
                    SetJobLimits(job, false, memLimitBytes);
                    break;
                default:
                    log.Line("then wait: waiting for the job to empty");
                    while (true)
                    {
                        int n = JobProcessCount(job);
                        if (n <= 0) break;
                        Thread.Sleep(250);
                    }
                    break;
            }
            diag?.Dispose();
            Native.CloseHandle(pi.hProcess);
            Native.CloseHandle(job);
            if (finalizeTicks == 0) return 5;
            return analyzerRc;
        }

        sealed class LineWriter : TextWriter
        {
            readonly Action<string> _emit;
            readonly StringBuilder _sb = new StringBuilder();
            public LineWriter(Action<string> emit) { _emit = emit; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value)
            {
                if (value == '\n') { Flush(); return; }
                if (value != '\r') _sb.Append(value);
            }
            public override void Flush()
            {
                if (_sb.Length > 0) { _emit(_sb.ToString()); _sb.Clear(); }
            }
        }
    }
}
