using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

// Tiny scripted emitter used to test step 1 of the SparkLoad analyzer with a real EventPipe trace.
// Defines EventSource "SparkLoad" with the SPEC section 3 events and emits a fixed sequence:
//   main thread:  Hello, MarkerDefs, Thread, Patch, client.boot.main Begin, server.phase a=1..3 (Begin/End),
//                 a 40 ms sleep, GC.Collect(2), a 40 ms sleep, mod.phase span with s="testmod|client",
//                 an exception thrown and caught, an assembly load (System.Xml.Linq), client.boot.main End,
//                 3 Frames, a FrameAgg, Playable, Stats.
//   worker thread: column.pass Begin/End for (x=3,z=4) passes 1..3 with a/b/c set.
namespace TraceGen
{
    [EventSource(Name = "SparkLoad")]
    internal sealed unsafe class SparkLoadSource : EventSource
    {
        public static readonly SparkLoadSource Log = new SparkLoadSource();

        // WriteEvent(id, params object[]) loses events in EventPipe sessions on .NET 10 (measured), so every event
        // is emitted with WriteEventCore. Field kinds: I = int32, L = int64, S = string.
        [NonEvent]
        private void Emit(int id, string kinds, long[] nums, string?[] strs)
        {
            if (!IsEnabled()) return;
            int n = kinds.Length;
            EventData* d = stackalloc EventData[n];
            long* store = stackalloc long[n];
            var handles = new System.Runtime.InteropServices.GCHandle[n];
            int ni = 0, si = 0;
            for (int i = 0; i < n; i++)
            {
                char k = kinds[i];
                if (k == 'S')
                {
                    string v = strs[si++] ?? "";
                    handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(v, System.Runtime.InteropServices.GCHandleType.Pinned);
                    d[i].DataPointer = handles[i].AddrOfPinnedObject();
                    d[i].Size = (v.Length + 1) * 2;
                }
                else
                {
                    store[i] = nums[ni++];
                    d[i].DataPointer = (IntPtr)(&store[i]);
                    d[i].Size = k == 'I' ? 4 : 8;
                }
            }
            WriteEventCore(id, n, d);
            for (int i = 0; i < n; i++) if (kinds[i] == 'S') handles[i].Free();
        }

        [Event(1, Level = EventLevel.Informational)]
        public void Hello(int pid, long qpc, long qpcFreq, string hookVersion, string runtime) => Emit(1, "ILLSS", new long[] { pid, qpc, qpcFreq }, new[] { hookVersion, runtime });
        [Event(2, Level = EventLevel.Informational)]
        public void MarkerDef(int markerId, string name) => Emit(2, "IS", new long[] { markerId }, new[] { name });
        [Event(3, Level = EventLevel.Informational)]
        public void Begin(int markerId, int tid, long seq, long qpc, long a, long b, long c, string s) => Emit(3, "IILLLLLS", new long[] { markerId, tid, seq, qpc, a, b, c }, new[] { s });
        [Event(4, Level = EventLevel.Informational)]
        public void End(int markerId, int tid, long seq, long qpc, long a, long b, long c, string s) => Emit(4, "IILLLLLS", new long[] { markerId, tid, seq, qpc, a, b, c }, new[] { s });
        [Event(5, Level = EventLevel.Informational)]
        public void Thread(int tid, int managedId, string name) => Emit(5, "IIS", new long[] { tid, managedId }, new[] { name });
        [Event(6, Level = EventLevel.Informational)]
        public void Patch(string marker, string target, string status, string detail) => Emit(6, "SSSS", Array.Empty<long>(), new[] { marker, target, status, detail });
        [Event(7, Level = EventLevel.Informational)]
        public void Frame(int tid, long seq, long qpc, long index, long durTicks) => Emit(7, "ILLLL", new long[] { tid, seq, qpc, index, durTicks }, Array.Empty<string?>());
        [Event(8, Level = EventLevel.Informational)]
        public void FrameAgg(int tid, long seq, long qpc, long count, long sumTicks, long maxTicks, long over50) => Emit(8, "ILLLLLL", new long[] { tid, seq, qpc, count, sumTicks, maxTicks, over50 }, Array.Empty<string?>());
        [Event(9, Level = EventLevel.Informational)]
        public void Playable(int tid, long seq, long qpc, long frameIndex) => Emit(9, "ILLL", new long[] { tid, seq, qpc, frameIndex }, Array.Empty<string?>());
        [Event(10, Level = EventLevel.Informational)]
        public void Stats(string marker, long hits) => Emit(10, "SL", new long[] { hits }, new[] { marker });
    }

    internal sealed class ConsoleListener : EventListener
    {
        protected override void OnEventSourceCreated(EventSource es)
        {
            if (es.Name == "SparkLoad") EnableEvents(es, EventLevel.Verbose, EventKeywords.All);
        }
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            Console.Out.WriteLine("EV " + e.EventId + " " + e.EventName + " " + (e.Message ?? "") + " " + (e.Payload == null ? "" : string.Join(",", e.Payload)));
        }
    }

    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static long Q => Stopwatch.GetTimestamp();

        private sealed class Seq
        {
            public long N;
            public long Next() => ++N;
        }

        private static void B(Seq sq, int mid, long a = 0, long b = 0, long c = 0, string s = "") =>
            SparkLoadSource.Log.Begin(mid, (int)GetCurrentThreadId(), sq.Next(), Q, a, b, c, s);

        private static void E(Seq sq, int mid, long a = 0, long b = 0, long c = 0, string s = "") =>
            SparkLoadSource.Log.End(mid, (int)GetCurrentThreadId(), sq.Next(), Q, a, b, c, s);

        private static int Main(string[] args)
        {
            // Give the EventPipe session a moment to be fully attached (it is already, with a suspended runtime start).
            if (args.Length > 0 && args[0] == "--listen") { _ = new ConsoleListener(); }
            var log = SparkLoadSource.Log;
            var main = new Seq();
            int tid = (int)GetCurrentThreadId();
            string[] names = { "", "client.boot.main", "server.phase", "column.pass", "mod.phase" };
            log.Hello(Environment.ProcessId, Q, Stopwatch.Frequency, "test-1.0", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            for (int i = 1; i < names.Length; i++) log.MarkerDef(i, names[i]);
            log.Thread(tid, Environment.CurrentManagedThreadId, "main-test");
            log.Patch("client.boot.main", "Vintagestory.Client.Boot::Main", "ok", "");
            log.Patch("server.phase", "Vintagestory.Server.EnterRunPhase", "mismatch", "sig");

            B(main, 1);
            for (int ph = 1; ph <= 3; ph++)
            {
                B(main, 2, ph);
                E(main, 2, ph);
                Thread.Sleep(20);
            }

            // worker thread with its own seq
            var worker = new Thread(() =>
            {
                var wq = new Seq();
                int wtid = (int)GetCurrentThreadId();
                log.Thread(wtid, Environment.CurrentManagedThreadId, "worker-test");
                for (int pass = 1; pass <= 3; pass++)
                {
                    B(wq, 3, 3, 4, pass);
                    Thread.Sleep(5);
                    E(wq, 3, 3, 4, pass);
                }
            });
            worker.Start();
            worker.Join();

            Thread.Sleep(40);
            B(main, 4, 1, 0, 0, "gcmarker|client");   // marks the start of the window that holds the GC
            Thread.Sleep(40);
            GC.Collect(2, GCCollectionMode.Forced, true);
            Thread.Sleep(40);
            E(main, 4, 1, 0, 0, "gcmarker|client");   // end of the window

            B(main, 4, 2, 0, 0, "testmod|server");
            try { throw new InvalidOperationException("tracegen test exception"); } catch (InvalidOperationException) { }
            var doc = System.Xml.Linq.XDocument.Parse("<a/>");
            if (doc.Root == null) return 3;
            E(main, 4, 2, 0, 0, "testmod|server");
            E(main, 1);

            for (int i = 0; i < 3; i++)
                log.Frame(tid, main.Next(), Q, i, 160000 + i * 1000);
            log.FrameAgg(tid, main.Next(), Q, 60, 9600000, 200000, 2);
            log.Playable(tid, main.Next(), Q, 2);
            log.Stats("client.boot.main", 1);
            log.Stats("server.phase", 0);
            Thread.Sleep(300);
            Console.Out.WriteLine("tracegen done tid=" + tid);
            return 0;
        }
    }
}
