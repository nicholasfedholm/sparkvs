using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SparkLoad.AnalyzerTests
{
    internal sealed class Scenario
    {
        public string Name = "";
        public string Arm = "O";
        public string Mode = "default";
        public bool Passive;
        public double ShiftAt = 56000, Shift;
        public bool DropPair, ZeroHit;
        public double G1 = 12.5;
    }

    /// <summary>Generates a synthetic run folder (run.json, events.jsonl, samples.csv, logtail.csv).</summary>
    internal sealed class Synth
    {
        public static readonly string[] MarkerNames =
        {
            "client.boot.main", "client.boot.screenmanager", "client.boot.stage1", "client.boot.stage2", "client.boot.stage3", "client.boot.stage4", "client.boot.stage5",
            "server.launch", "server.phase", "mod.phase", "server.blocklist.wait", "server.blocklist.set", "server.remap.blocks", "server.remap.items",
            "server.save.load", "server.spawn.pregen", "column.pass", "column.load", "client.assets.handle", "client.assets.finish",
            "client.shapes.block", "client.shapes.item", "client.shapes.entity", "client.atlas.A", "client.atlas.B", "client.atlas.C",
            "client.handbook", "client.finalize.handle", "client.finalize.trigger", "frame",
        };

        public static readonly string[] PhaseNames = { "Start", "Initialization", "Configuration", "LoadAssets", "AssetsFinalize", "LoadGamePre", "GameReady", "WorldReady", "RunGame" };
        public static readonly double[] PhaseStarts = { 7000, 8000, 14000, 15000, 34000, 46000, 56000, 56500, 57000 };

        // Expected values, in ms of the logical (unshifted) script.
        public const int Columns = 49;
        public const double FinalizeLogMs = 120000;
        public const double SilenceStart = 80000, SilenceEnd = 86000;
        public int FramesInGap;
        public double PlayableMs;
        public double RunEndMs;

        private readonly Scenario _sc;
        private readonly List<(double T, int Ord, string Json)> _ev = new List<(double, int, string)>();
        private readonly List<(int Tid, double T, int Ord, bool Drop, Func<long, string> Mk, string Name)> _pend = new List<(int, double, int, bool, Func<long, string>, string)>();
        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>();
        private int _ord;

        public Synth(Scenario sc)
        {
            _sc = sc;
            for (int i = 0; i < MarkerNames.Length; i++) _ids[MarkerNames[i]] = i + 1;
        }

        private double M(double t) => t + (t >= _sc.ShiftAt ? _sc.Shift : 0);
        private double L(double real) => real < _sc.ShiftAt ? real : (real < _sc.ShiftAt + _sc.Shift ? _sc.ShiftAt : real - _sc.Shift);

        private static string Q(string s) => JsonSerializer.Serialize(s);
        private static string D(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        private void Add(double t, string src, string name, string fields) =>
            _ev.Add((t, _ord++, "{\"t_ms\":" + D(t) + ",\"src\":\"" + src + "\",\"name\":\"" + name + "\",\"fields\":{" + fields + "}}"));

        private void Marker(int tid, string name, double l0, double l1, long a = 0, long b = 0, long c = 0, string s = "", bool drop = false)
        {
            double t0 = M(l0), t1 = M(l1);
            int id = _ids[name];
            string f(long seq) => "\"markerId\":" + id + ",\"tid\":" + tid + ",\"seq\":" + seq + ",\"qpc\":0,\"a\":" + a + ",\"b\":" + b + ",\"c\":" + c + ",\"s\":" + Q(s);
            _pend.Add((tid, t0, _ord++, drop, f, "Begin"));
            _pend.Add((tid, t1, _ord++, drop, f, "End"));
        }

        /// <summary>Assign per-thread seq numbers in time order, as the hook does. Dropped events consume a seq.</summary>
        private void FlushPending()
        {
            foreach (var g in _pend.GroupBy(p => p.Tid))
            {
                long seq = 0;
                foreach (var p in g.OrderBy(x => x.T).ThenBy(x => x.Ord))
                {
                    seq++;
                    if (!p.Drop) Add(p.T, "sparkload", p.Name, p.Mk(seq));
                }
            }
        }

        public void Write(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            double end = M(130000);
            RunEndMs = end;
            if (!_sc.Passive) { BuildEvents(end); FlushPending(); }
            WriteRunJson(dir, end);
            if (!_sc.Passive)
            {
                File.WriteAllLines(Path.Combine(dir, "events.jsonl"), _ev.OrderBy(e => e.T).ThenBy(e => e.Ord).Select(e => e.Json), new UTF8Encoding(false));
                WriteSamples(dir, end);
            }
            WriteLogs(dir, end);
        }

        private void WriteRunJson(string dir, double end)
        {
            var sb = new StringBuilder();
            sb.Append("{\"run_id\":" + Q(_sc.Name) + ",\"arm\":" + Q(_sc.Arm) + ",\"mode\":" + Q(_sc.Mode) + ",\"timing_void\":false");
            sb.Append(",\"exe\":\"C:\\\\synthetic\\\\game.exe\",\"args\":\"\",\"data_path\":\"C:\\\\synthetic\\\\data\",\"launcher_version\":\"0.1.0\"");
            sb.Append(",\"t0_qpc\":1000000000,\"qpc_freq\":10000000,\"t0_utc_filetime\":134000000000000000,\"child_pid\":1234,\"kernel_create_filetime\":134000000000000000");
            sb.Append(",\"g1_delta_ms\":" + D(_sc.G1) + ",\"finalize_log_ms\":" + D(M(FinalizeLogMs)) + ",\"end_ms\":" + D(end));
            sb.Append(",\"exit_reason\":\"finalize\",\"present_mode\":\"immediate\"}");
            File.WriteAllText(Path.Combine(dir, "run.json"), sb.ToString(), new UTF8Encoding(false));
        }

        private void BuildEvents(double end)
        {
            const int main = 100, server = 200, worker = 201;
            Add(M(3000), "sparkload", "Hello", "\"pid\":1234,\"qpc\":0,\"qpcFreq\":10000000,\"hookVersion\":\"0.1.0-synth\",\"runtime\":\".NET 10 synthetic\"");
            for (int i = 0; i < MarkerNames.Length; i++)
                Add(M(3000) + 0.01 * (i + 1), "sparkload", "MarkerDef", "\"markerId\":" + (i + 1) + ",\"name\":" + Q(MarkerNames[i]));
            foreach (var (tid, nm) in new[] { (main, "VS Main"), (server, "ServerMain"), (worker, "WorldgenWorker") })
                Add(M(3001), "sparkload", "Thread", "\"tid\":" + tid + ",\"managedId\":" + (tid % 50) + ",\"name\":" + Q(nm));
            var patched = new[] { "client.boot.main", "server.phase", "mod.phase", "server.blocklist.wait", "server.blocklist.set", "server.remap.items", "column.pass", "column.load", "client.finalize.handle" };
            foreach (var p in patched)
                Add(M(3002), "sparkload", "Patch", "\"marker\":" + Q(p) + ",\"target\":" + Q("Vintagestory.Synth::" + p) + ",\"status\":\"ok\",\"detail\":\"\"");

            // Client main thread.
            Marker(main, "client.boot.main", 3000, 126000);
            Marker(main, "client.boot.stage1", 3100, 4000);
            Marker(main, "client.boot.stage2", 4000, 6000);
            Marker(main, "client.boot.stage3", 6000, 6900);
            for (int i = 0; i < 12; i++)
            {
                Marker(main, "mod.phase", 70000 + i * 500, 70000 + i * 500 + 50 + i * 30, 1, 0, 0, "clientmod" + i + "|client");
                Marker(main, "mod.phase", 70000 + i * 500 + 300, 70000 + i * 500 + 320, 2, 0, 0, "clientmod" + i + "|client");
            }
            Marker(main, "client.assets.handle", 86500, 90000);
            Marker(main, "client.shapes.block", 90100, 95000);
            Marker(main, "client.atlas.A", 95100, 98000);
            Marker(main, "client.atlas.B", 98000, 99000);
            Marker(main, "client.atlas.C", 101500, 104000);
            Marker(main, "client.finalize.handle", 119000, 121000);

            // Server main thread.
            Marker(server, "server.launch", 7000, 58000);
            for (int i = 0; i < PhaseNames.Length; i++)
                Marker(server, "server.phase", PhaseStarts[i], PhaseStarts[i] + 1, i);
            for (int i = 0; i < 8; i++)
                Marker(server, "mod.phase", 8200 + i * 500, 8200 + i * 500 + 100 + i * 10, 1, 0, 0, "servermod" + i + "|server");
            Marker(server, "server.blocklist.wait", 16000, 19000);
            Marker(server, "server.blocklist.set", 19000, 19010);
            Marker(server, "server.remap.blocks", 34100, 36000);
            if (!_sc.ZeroHit) Marker(server, "server.remap.items", 36000, 37500);
            Marker(server, "server.save.load", 46100, 55000);
            Marker(server, "server.spawn.pregen", 56000, 56400);

            // Worldgen worker: 49 columns over 6 passes.
            for (int i = 0; i < Columns; i++)
            {
                double s = 47000 + i * 150;
                int x = i % 7, z = i / 7;
                Marker(worker, "column.load", s - 6, s - 1, x, z, i % 5 == 0 ? 1 : 0, "", _sc.DropPair && i == 10);
                for (int p = 1; p <= 6; p++)
                    Marker(worker, "column.pass", s + (p - 1) * 22, s + (p - 1) * 22 + 20, x, z, p);
            }

            // Frames after finalize: 30 slow (80 ms) then fast (16 ms); playable after 60 consecutive fast frames.
            var t = 121000.0;
            int idx = 0;
            int framesInGap = 0;
            double playable = 0;
            for (; idx < 120; idx++)
            {
                double dur = idx < 30 ? 80 : 16;
                if (idx == 30) t = 123400;
                int fi = idx;
                _pend.Add((main, M(t), _ord++, false, seq => "\"tid\":" + main + ",\"seq\":" + seq + ",\"qpc\":0,\"index\":" + fi + ",\"durTicks\":" + (long)(dur * 10000) + ",\"dur_ms\":" + D(dur), "Frame"));
                if (idx == 89)
                {
                    playable = t;
                    _pend.Add((main, M(t), _ord++, false, seq => "\"tid\":" + main + ",\"seq\":" + seq + ",\"qpc\":0,\"frameIndex\":89", "Playable"));
                }
                if (idx <= 89) framesInGap++;
                t += dur;
            }
            PlayableMs = M(playable);
            FramesInGap = framesInGap;
            for (int k = 0; k < 3; k++)
            {
                _pend.Add((main, M(125000 + k * 1000), _ord++, false, seq => "\"tid\":" + main + ",\"seq\":" + seq + ",\"qpc\":0,\"count\":60,\"sumTicks\":9600000,\"maxTicks\":200000,\"over50\":0,\"sum_ms\":960,\"max_ms\":20", "FrameAgg"));
            }

            // Stats: hits as the hook would count them.
            foreach (var p in patched)
            {
                long hits = p == "server.phase" ? PhaseNames.Length : p == "mod.phase" ? 12 * 2 + 8 : p == "column.pass" ? Columns * 6
                    : p == "column.load" ? Columns : p == "server.remap.items" ? (_sc.ZeroHit ? 0 : 1) : 1;
                Add(M(124400), "sparkload", "Stats", "\"marker\":" + Q(p) + ",\"hits\":" + hits);
            }

            // GC, loader, exceptions.
            Add(M(60000), "gc", "GCSuspendBegin", "\"reason\":\"SuspendForGC\",\"gc\":0");
            Add(M(60001), "gc", "GCStart", "\"gc\":4,\"gen\":0,\"reason\":\"AllocSmall\",\"type\":\"NonConcurrentGC\"");
            Add(M(60012), "gc", "GCEnd", "\"gc\":4,\"gen\":0");
            Add(M(60015), "gc", "GCRestartEnd", "");
            Add(M(90000), "gc", "GCSuspendBegin", "\"reason\":\"SuspendForGC\",\"gc\":0");
            Add(M(90010), "gc", "GCStart", "\"gc\":5,\"gen\":2,\"reason\":\"AllocLarge\",\"type\":\"NonConcurrentGC\"");
            Add(M(90110), "gc", "GCEnd", "\"gc\":5,\"gen\":2");
            Add(M(90120), "gc", "GCRestartEnd", "");
            for (int i = 0; i < 40; i++) Add(M(1000 + i * 50), "loader", "AssemblyLoad", "\"assembly\":\"Synth.Asm" + i + ", Version=1.0.0.0\",\"id\":" + i);
            for (int i = 0; i < 3; i++) Add(M(5000 + i * 10), "exception", "ExceptionThrown", "\"type\":\"System.IO.IOException\",\"message\":\"synthetic\",\"hresult\":-2146232800");
        }

        private static double Rate(int tid, double lt)
        {
            switch (tid)
            {
                case 100: return 12.5;
                case 200: return lt > 7000 && lt <= 57000 ? 100 : 5;
                case 201: return lt > 47000 && lt <= 54500 ? 200 : 0;
                case 150: return lt > SilenceStart && lt <= SilenceEnd ? 250 : 0;
                case 300: return lt > 100000 && lt <= 101000 ? 0 : 75;
                case 400: return 12.5;
                default: return 0;
            }
        }

        private void WriteSamples(string dir, double end)
        {
            var tids = new[] { (100, "VS Main"), (200, "ServerMain"), (201, "WorldgenWorker"), (150, "SoundLoader"), (300, "BgWorker"), (400, "") };
            var sb = new StringBuilder();
            sb.AppendLine("t_ms,pid,kind,tid,name,cpu_ms,state,wait_reason,ctx,read_bytes,write_bytes,other_bytes,working_set,private_bytes,threads");
            var cum = tids.ToDictionary(x => x.Item1, _ => 0.0);
            var last = tids.ToDictionary(x => x.Item1, _ => -1.0);
            double rd = 0, wr = 0, ot = 0;
            for (double t = 0; t <= end + 1e-9; t += 250)
            {
                double lt = L(t);
                if (t > 0)
                {
                    bool silence = lt > SilenceStart && lt <= SilenceEnd;
                    rd += silence ? 400000 : 8000; wr += silence ? 100000 : 1000; ot += 1000;
                    foreach (var (tid, _) in tids) cum[tid] += Rate(tid, lt);
                }
                double proc = cum.Values.Sum();
                sb.AppendLine(D(t) + ",1234,P,,," + D(proc) + ",,,," + D(rd) + "," + D(wr) + "," + D(ot) + ",500000000,600000000," + tids.Length);
                foreach (var (tid, nm) in tids)
                {
                    if (t == 0 || cum[tid] != last[tid])
                    {
                        sb.AppendLine(D(t) + ",1234,T," + tid + "," + nm + "," + D(cum[tid]) + ",Wait,,0,,,,,," );
                        last[tid] = cum[tid];
                    }
                }
            }
            File.WriteAllText(Path.Combine(dir, "samples.csv"), sb.ToString(), new UTF8Encoding(false));
        }

        private void WriteLogs(string dir, double end)
        {
            var lines = new List<(double T, string File, string Text)>();
            lines.Add((M(1000), "client-main", "Client log opened"));
            lines.Add((M(3000), "client-main", "Client boot start, \"quoted\", with comma"));
            for (int i = 1; i < PhaseNames.Length; i++)
                lines.Add((M(PhaseStarts[i]) + 30 + 40 * i, "server-main", "12:00:00 [Notification] Entering runphase " + PhaseNames[i]));
            lines.Add((M(58000), "server-main", "12:00:51 [Notification] Singleplayer Server now running!"));
            lines.Add((M(FinalizeLogMs), "client-main", "12:02:00 [Notification] Received level finalize"));
            lines.Add((M(125000), "client-debug", "12:02:05 [Debug] Done level finalize"));
            lines.Add((M(SilenceStart), "client-main", "Sound load begin"));
            lines.Add((M(SilenceEnd), "client-main", "Sound load end"));
            double gapA = M(SilenceStart), gapB = M(SilenceEnd);
            for (double t = 500; t <= end; t += 1500)
            {
                if (t > gapA && t < gapB) continue;
                lines.Add((t, (int)(t / 1500) % 2 == 0 ? "client-debug" : "server-debug", "filler " + D(t)));
            }
            var sb = new StringBuilder();
            sb.AppendLine("t_ms,file,offset,len,text");
            long off = 0;
            foreach (var l in lines.OrderBy(x => x.T))
            {
                sb.AppendLine(D(l.T) + "," + l.File + "," + off + "," + l.Text.Length + "," + Csv(l.Text));
                off += l.Text.Length + 1;
            }
            File.WriteAllText(Path.Combine(dir, "logtail.csv"), sb.ToString(), new UTF8Encoding(false));
        }

        private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
