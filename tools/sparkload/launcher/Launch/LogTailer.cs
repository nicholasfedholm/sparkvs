using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SparkLoad.Launch
{
    /// <summary>Polls log files every 50 ms and writes one logtail.csv row per complete line (offset = byte offset of line start).</summary>
    internal sealed class LogTailer
    {
        sealed class Tracked
        {
            public string Key = "";
            public string Path = "";
            public long Offset;
            public bool Seen;
            public DateTime Created;
            public byte[] Pending = Array.Empty<byte>();
        }

        readonly string _csvPath;
        long _t0;
        readonly List<Tracked> _files = new List<Tracked>();
        readonly Regex _pattern;
        readonly string _finalizeKey;
        readonly Action<string> _log;
        readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        Thread? _thread;
        long _finalizeTicks;
        public long Lines;

        public LogTailer(string csvPath, string? dataPath, string? launcherLog, Regex pattern, string finalizeFile, Action<string> log)
        {
            _csvPath = csvPath;
            _pattern = pattern;
            _log = log;
            string fk = finalizeFile;
            if (fk.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) fk = fk.Substring(0, fk.Length - 4);
            _finalizeKey = fk;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string logs = System.IO.Path.Combine(dataPath, "Logs");
                foreach (string k in new[] { "client-main", "client-debug", "server-main", "server-debug" })
                    _files.Add(new Tracked { Key = k, Path = System.IO.Path.Combine(logs, k + ".log") });
            }
            if (!string.IsNullOrEmpty(launcherLog))
                _files.Add(new Tracked { Key = "optimum-launcher", Path = launcherLog! });
        }

        /// <summary>Stopwatch ticks of the first finalize line arrival, or 0 while not seen.</summary>
        public long FinalizeTicks => Interlocked.Read(ref _finalizeTicks);

        public void Prepare()
        {
            // Files that already exist hold output from earlier sessions: start after their current end.
            foreach (var f in _files)
            {
                try
                {
                    var fi = new FileInfo(f.Path);
                    if (fi.Exists)
                    {
                        f.Offset = fi.Length;
                        f.Created = fi.CreationTimeUtc;
                        f.Seen = true;
                        _log("tail: " + f.Key + " exists, starting at byte " + f.Offset.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch
                {
                }
            }
        }

        public void Start(long t0)
        {
            _t0 = t0;
            _thread = new Thread(Run) { IsBackground = true, Name = "sparkload-logtail" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop.Set();
            _thread?.Join(5000);
        }

        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

        void Run()
        {
            try
            {
                using var w = new StreamWriter(new FileStream(_csvPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 1 << 16);
                w.NewLine = "\n";
                w.WriteLine("t_ms,file,offset,len,text");
                w.Flush();
                long lastFlush = Stopwatch.GetTimestamp();
                while (true)
                {
                    bool stopping = _stop.IsSet;
                    foreach (var f in _files)
                    {
                        try
                        {
                            Poll(f, w);
                        }
                        catch (Exception ex)
                        {
                            _log("tail: " + f.Key + ": " + ex.Message);
                        }
                    }
                    long now = Stopwatch.GetTimestamp();
                    if (now - lastFlush > Stopwatch.Frequency / 4)
                    {
                        w.Flush();
                        lastFlush = now;
                    }
                    if (stopping) break;
                    _stop.Wait(50);
                }
                w.Flush();
            }
            catch (Exception ex)
            {
                _log("tail failed: " + ex);
            }
        }

        void Poll(Tracked f, StreamWriter w)
        {
            var fi = new FileInfo(f.Path);
            if (!fi.Exists)
            {
                if (f.Seen)
                {
                    f.Seen = false;
                    f.Offset = 0;
                    f.Pending = Array.Empty<byte>();
                }
                return;
            }
            long len = fi.Length;
            DateTime created = fi.CreationTimeUtc;
            if (!f.Seen)
            {
                f.Seen = true;
                f.Created = created;
            }
            else if (created != f.Created || len < f.Offset + f.Pending.Length)
            {
                // recreated or truncated: restart from the top
                f.Created = created;
                f.Offset = 0;
                f.Pending = Array.Empty<byte>();
            }
            long readFrom = f.Offset + f.Pending.Length;
            if (len <= readFrom) return;
            using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(readFrom, SeekOrigin.Begin);
            int want = (int)Math.Min(len - readFrom, 8 << 20);
            byte[] chunk = new byte[want];
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(chunk, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            long arrival = Stopwatch.GetTimestamp();
            if (got == 0) return;
            byte[] data;
            if (f.Pending.Length > 0)
            {
                data = new byte[f.Pending.Length + got];
                Buffer.BlockCopy(f.Pending, 0, data, 0, f.Pending.Length);
                Buffer.BlockCopy(chunk, 0, data, f.Pending.Length, got);
            }
            else
            {
                data = got == chunk.Length ? chunk : chunk.AsSpan(0, got).ToArray();
            }
            int start = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'\n') continue;
                int end = i;
                if (end > start && data[end - 1] == (byte)'\r') end--;
                long lineOffset = f.Offset + start;
                string text = Encoding.UTF8.GetString(data, start, end - start);
                if (lineOffset == 0 && text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
                int lineLen = i + 1 - start;
                string shown = text.Length > 400 ? text.Substring(0, 400) : text;
                double tms = (arrival - _t0) * 1000.0 / Stopwatch.Frequency;
                w.WriteLine(tms.ToString("0.000", CultureInfo.InvariantCulture) + "," + f.Key + "," + lineOffset.ToString(CultureInfo.InvariantCulture) + ","
                    + lineLen.ToString(CultureInfo.InvariantCulture) + "," + Csv(shown));
                Lines++;
                if (Interlocked.Read(ref _finalizeTicks) == 0 && f.Key == _finalizeKey && _pattern.IsMatch(text))
                {
                    Interlocked.Exchange(ref _finalizeTicks, arrival);
                    w.Flush();
                }
                start = i + 1;
            }
            f.Offset += start;
            f.Pending = start >= data.Length ? Array.Empty<byte>() : data.AsSpan(start).ToArray();
        }
    }
}
