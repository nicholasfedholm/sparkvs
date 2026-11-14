using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SparkLoad.Launch
{
    /// <summary>Provider list for the CollectTracing command.</summary>
    internal sealed class TraceProvider
    {
        public string Name = "";
        public ulong Keywords;
        public uint Level;
    }

    /// <summary>
    /// Hand-rolled reverse diagnostic server (SPEC 2.1). Multi-accept: always one pipe instance listening, every runtime that
    /// advertises is resumed, the child gets a tracing session first.
    /// </summary>
    internal sealed class DiagServer : IDisposable
    {
        static readonly byte[] IpcMagic = Encoding.ASCII.GetBytes("DOTNET_IPC_V1\0");
        static readonly byte[] AdvrMagic = Encoding.ASCII.GetBytes("ADVR_V1\0");

        readonly string _pipeName;
        long _t0;
        readonly Action<string> _log;
        readonly StreamWriter _ipcLog;
        readonly string _tracePath;
        readonly List<TraceProvider> _providers;
        readonly bool _rundown;
        readonly bool _stackwalk;
        readonly uint _bufferMb;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly object _lock = new object();
        readonly ManualResetEventSlim _childKnown = new ManualResetEventSlim(false);
        readonly Dictionary<int, NamedPipeServerStream> _idle = new Dictionary<int, NamedPipeServerStream>();
        readonly HashSet<int> _resumedForeign = new HashSet<int>();
        readonly List<NamedPipeServerStream> _open = new List<NamedPipeServerStream>();
        int _childPid;
        int _stage; // 0 need session, 1 need resume, 2 resumed
        int _attempt;
        ulong _sessionId;
        bool _sessionOk;
        Thread? _copyThread;
        long _traceBytes;
        volatile bool _copyDone;
        string _copyEnd = "not started";
        volatile bool _childResumed;
        public int HoldBeforeResumeSeconds;
        /// <summary>Trace size cap in bytes, 0 = off. Set before Start().</summary>
        public long MaxTraceBytes;
        volatile bool _capHit;
        int _stopSent;
        public bool CapHit => _capHit;
        public bool SessionOk => _sessionOk;
        public bool ChildResumed => _childResumed;
        public long TraceBytes => Interlocked.Read(ref _traceBytes);
        public string CopyEnd => _copyEnd;
        public string PipeName => _pipeName;

        public DiagServer(string pipeName, long t0, string ipcLogPath, string tracePath, List<TraceProvider> providers,
            bool rundown, bool stackwalk, uint bufferMb, bool traceEnabled, Action<string> log)
        {
            _pipeName = pipeName;
            _t0 = t0;
            _log = log;
            _tracePath = tracePath;
            _providers = providers;
            _rundown = rundown;
            _stackwalk = stackwalk;
            _bufferMb = bufferMb;
            _ipcLog = new StreamWriter(new FileStream(ipcLogPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            if (!traceEnabled) _stage = 1;
        }

        NamedPipeServerStream NewInstance()
        {
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        }

        NamedPipeServerStream? _listening;

        public void Start()
        {
            _listening = NewInstance();
            var t = new Thread(() => AcceptLoop().GetAwaiter().GetResult()) { IsBackground = true, Name = "sparkload-accept" };
            t.Start();
        }

        public void SetT0(long t0) { _t0 = t0; }

        public void SetChild(int pid)
        {
            _childPid = pid;
            _childKnown.Set();
        }

        string Stamp() => ((Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture);

        void IpcLog(int pid, Guid cookie, string action, string? extra = null)
        {
            lock (_ipcLog)
            {
                _ipcLog.WriteLine(Stamp() + " " + pid.ToString(CultureInfo.InvariantCulture) + " " + cookie.ToString("D") + " " + action + (extra != null ? " " + extra : ""));
            }
        }

        async Task AcceptLoop()
        {
            NamedPipeServerStream current = _listening!;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await current.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    current.Dispose();
                    return;
                }
                NamedPipeServerStream conn = current;
                try
                {
                    current = NewInstance();
                    _listening = current;
                }
                catch (Exception ex)
                {
                    _log("diag: cannot create next pipe instance: " + ex.Message);
                    conn.Dispose();
                    return;
                }
                lock (_lock) _open.Add(conn);
                var ct = new Thread(() => Handle(conn)) { IsBackground = true, Name = "sparkload-diag-conn" };
                ct.Start();
            }
        }

        static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        static byte[] BuildCommand(byte set, byte id, byte[] payload)
        {
            int size = 20 + payload.Length;
            byte[] m = new byte[size];
            Buffer.BlockCopy(IpcMagic, 0, m, 0, 14);
            m[14] = (byte)(size & 0xFF);
            m[15] = (byte)(size >> 8);
            m[16] = set;
            m[17] = id;
            Buffer.BlockCopy(payload, 0, m, 20, payload.Length);
            return m;
        }

        /// <summary>Reads a response. Returns (ok, payload, hresult).</summary>
        static bool ReadResponse(Stream s, out byte[] payload, out uint hresult)
        {
            payload = Array.Empty<byte>();
            hresult = 0;
            byte[] hdr = new byte[20];
            if (!ReadExact(s, hdr, 20)) return false;
            int size = hdr[14] | (hdr[15] << 8);
            byte cmdId = hdr[17];
            if (size > 20)
            {
                payload = new byte[size - 20];
                if (!ReadExact(s, payload, payload.Length)) return false;
            }
            if (hdr[16] == 0xFF && cmdId == 0x00) return true;
            hresult = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0xFFFFFFFF;
            return false;
        }

        static void PutString(MemoryStream ms, string? s)
        {
            if (string.IsNullOrEmpty(s))
            {
                ms.Write(BitConverter.GetBytes(0u));
                return;
            }
            ms.Write(BitConverter.GetBytes((uint)(s.Length + 1)));
            ms.Write(Encoding.Unicode.GetBytes(s));
            ms.Write(new byte[2]);
        }

        byte[] BuildCollectPayload(bool v3)
        {
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(_bufferMb));
            ms.Write(BitConverter.GetBytes(1u));
            ms.WriteByte((byte)(_rundown ? 1 : 0));
            if (v3) ms.WriteByte((byte)(_stackwalk ? 1 : 0));
            ms.Write(BitConverter.GetBytes((uint)_providers.Count));
            foreach (var p in _providers)
            {
                ms.Write(BitConverter.GetBytes(p.Keywords));
                ms.Write(BitConverter.GetBytes(p.Level));
                PutString(ms, p.Name);
                PutString(ms, null);
            }
            return ms.ToArray();
        }

        void Handle(NamedPipeServerStream conn)
        {
            bool keep = false;
            try
            {
                byte[] adv = new byte[34];
                if (!ReadExact(conn, adv, 34)) return;
                for (int i = 0; i < 8; i++) if (adv[i] != AdvrMagic[i]) { _log("diag: bad advertise magic"); return; }
                Guid cookie = new Guid(new ReadOnlySpan<byte>(adv, 8, 16));
                int pid = (int)BitConverter.ToUInt64(adv, 24);
                _childKnown.Wait(5000);
                int childPid = _childPid;

                if (pid != childPid)
                {
                    bool first;
                    lock (_lock) first = _resumedForeign.Add(pid);
                    if (first)
                    {
                        string name = "?";
                        try { name = Process.GetProcessById(pid).ProcessName; } catch { }
                        IpcLog(pid, cookie, "resume-foreign", name);
                        SendResume(conn, pid, cookie, false);
                        return;
                    }
                    keep = StoreIdle(pid, cookie, conn);
                    return;
                }

                int action; // 0 session, 1 resume, 2 idle
                int attempt;
                lock (_lock)
                {
                    action = _stage;
                    attempt = _attempt;
                }
                if (action == 0)
                {
                    keep = DoSession(conn, pid, cookie, attempt);
                }
                else if (action == 1)
                {
                    if (HoldBeforeResumeSeconds > 0)
                    {
                        _log("diag: holding resume for " + HoldBeforeResumeSeconds.ToString(CultureInfo.InvariantCulture) + " s (debug)");
                        Thread.Sleep(HoldBeforeResumeSeconds * 1000);
                    }
                    lock (_lock) _stage = 2;
                    IpcLog(pid, cookie, "resume");
                    SendResume(conn, pid, cookie, true);
                }
                else
                {
                    keep = StoreIdle(pid, cookie, conn);
                }
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) _log("diag: connection handler: " + ex.Message);
            }
            finally
            {
                if (!keep)
                {
                    lock (_lock) _open.Remove(conn);
                    try { conn.Dispose(); } catch { }
                }
            }
        }

        bool StoreIdle(int pid, Guid cookie, NamedPipeServerStream conn)
        {
            NamedPipeServerStream? old;
            lock (_lock)
            {
                _idle.TryGetValue(pid, out old);
                _idle[pid] = conn;
                if (old != null) _open.Remove(old);
            }
            if (old != null) { try { old.Dispose(); } catch { } }
            IpcLog(pid, cookie, "idle");
            return true;
        }

        void SendResume(NamedPipeServerStream conn, int pid, Guid cookie, bool isChild)
        {
            byte[] cmd = BuildCommand(0x04, 0x01, Array.Empty<byte>());
            conn.Write(cmd, 0, cmd.Length);
            conn.Flush();
            bool ok = ReadResponse(conn, out _, out uint hr);
            if (!ok) _log("diag: ResumeRuntime pid " + pid.ToString(CultureInfo.InvariantCulture) + " response error 0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
            if (isChild) _childResumed = true;
        }

        bool DoSession(NamedPipeServerStream conn, int pid, Guid cookie, int attempt)
        {
            bool v3 = attempt == 0;
            byte[] cmd = BuildCommand(0x02, v3 ? (byte)0x04 : (byte)0x03, BuildCollectPayload(v3));
            conn.Write(cmd, 0, cmd.Length);
            conn.Flush();
            bool ok = ReadResponse(conn, out byte[] payload, out uint hr);
            if (!ok || payload.Length < 8)
            {
                _log("diag: " + (v3 ? "CollectTracing3" : "CollectTracing2") + " failed hr=0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                lock (_lock)
                {
                    _attempt++;
                    if (_attempt >= 2)
                    {
                        _stage = 1;
                        _log("diag: no tracing session, resuming without trace");
                    }
                }
                return false;
            }
            _sessionId = BitConverter.ToUInt64(payload, 0);
            lock (_lock)
            {
                _sessionOk = true;
                _stage = 1;
            }
            IpcLog(pid, cookie, "session", "id=" + _sessionId.ToString(CultureInfo.InvariantCulture));
            _copyEnd = "running";
            _copyThread = new Thread(() => CopyLoop(conn)) { IsBackground = true, Name = "sparkload-trace-copy", Priority = ThreadPriority.AboveNormal };
            _copyThread.Start();
            return true;
        }

        void CopyLoop(NamedPipeServerStream conn)
        {
            try
            {
                using var fs = new FileStream(_tracePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                byte[] buf = new byte[1 << 18];
                // After the cap is hit the session is stopped (StopTracing on the idle connection) and the stream is drained
                // to its end, so the file stays a valid nettrace. If the stream still runs on 512 MB past the cap, give up.
                long hardLimit = MaxTraceBytes > 0 ? MaxTraceBytes + (512L << 20) : long.MaxValue;
                while (true)
                {
                    int n = conn.Read(buf, 0, buf.Length);
                    if (n <= 0) { _copyEnd = _capHit ? "size_cap" : "end of stream"; break; }
                    fs.Write(buf, 0, n);
                    long total = Interlocked.Add(ref _traceBytes, n);
                    if (MaxTraceBytes > 0 && !_capHit && total >= MaxTraceBytes)
                    {
                        _capHit = true;
                        _log("trace: size cap reached (" + total.ToString(CultureInfo.InvariantCulture) + " bytes >= " + MaxTraceBytes.ToString(CultureInfo.InvariantCulture)
                            + "), stopping the tracing session, trace_end=size_cap");
                        var stopper = new Thread(() => { try { SendStopSession(); } catch (Exception ex) { _log("diag: cap stop failed: " + ex.Message); } })
                        { IsBackground = true, Name = "sparkload-cap-stop" };
                        stopper.Start();
                    }
                    if (total >= hardLimit)
                    {
                        _copyEnd = "size_cap";
                        _log("trace: stream did not end after the stop, closing at " + total.ToString(CultureInfo.InvariantCulture) + " bytes");
                        break;
                    }
                }
                fs.Flush();
            }
            catch (Exception ex)
            {
                _copyEnd = "read error: " + ex.Message;
            }
            finally
            {
                lock (_lock) _open.Remove(conn);
                try { conn.Dispose(); } catch { }
                _copyDone = true;
            }
        }

        /// <summary>Sends StopTracing on an idle connection of the child and waits for the copy thread. Returns true when the stream ended cleanly.</summary>
        public bool StopTracing()
        {
            if (!_sessionOk) return false;
            if (_copyDone) return CleanEnd();
            SendStopSession();
            for (int i = 0; i < 300 && !_copyDone; i++) Thread.Sleep(50);
            if (!_copyDone)
            {
                _log("diag: trace stream did not end within 15 s, closing");
                return false;
            }
            return CleanEnd();
        }

        bool CleanEnd() => _copyEnd == "end of stream" || _copyEnd == "size_cap";

        /// <summary>Waits for the copy thread to finish (the trace file is closed before it reports done).</summary>
        public bool WaitCopyDone(int ms)
        {
            for (int waited = 0; !_copyDone && waited < ms; waited += 20) Thread.Sleep(20);
            return _copyDone;
        }

        /// <summary>Sends StopTracing once (the first caller wins) on the idle connection of the child.</summary>
        void SendStopSession()
        {
            if (Interlocked.Exchange(ref _stopSent, 1) != 0) return;
            NamedPipeServerStream? idle = null;
            for (int i = 0; i < 60 && idle == null; i++)
            {
                lock (_lock)
                {
                    if (_idle.TryGetValue(_childPid, out idle)) _idle.Remove(_childPid);
                }
                if (idle == null)
                {
                    if (_copyDone) break;
                    Thread.Sleep(50);
                }
            }
            if (idle != null)
            {
                try
                {
                    var ms = new MemoryStream();
                    ms.Write(BitConverter.GetBytes(_sessionId));
                    byte[] cmd = BuildCommand(0x02, 0x01, ms.ToArray());
                    IpcLog(_childPid, Guid.Empty, "stop");
                    idle.Write(cmd, 0, cmd.Length);
                    idle.Flush();
                    bool ok = ReadResponse(idle, out _, out uint hr);
                    if (!ok) _log("diag: StopTracing error 0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    _log("diag: StopTracing failed: " + ex.Message);
                }
                finally
                {
                    lock (_lock) _open.Remove(idle);
                    try { idle.Dispose(); } catch { }
                }
            }
            else
            {
                _log("diag: no idle connection for StopTracing");
            }
        }

        public void LogIdleSummary()
        {
            lock (_lock)
            {
                foreach (var kv in _idle) _log("diag: idle connection kept for pid " + kv.Key.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            List<NamedPipeServerStream> all;
            lock (_lock)
            {
                all = new List<NamedPipeServerStream>(_open);
                _open.Clear();
                _idle.Clear();
            }
            foreach (var s in all) { try { s.Dispose(); } catch { } }
            try { _listening?.Dispose(); } catch { }
            try { _ipcLog.Dispose(); } catch { }
        }
    }
}
