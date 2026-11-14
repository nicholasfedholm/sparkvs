using System;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace SparkLoad.Hook
{
    // WriteEvent(id, params object[]) loses events in EventPipe sessions on .NET 10 (measured by the analyzer
    // agent), so every event is written with WriteEventCore. Field order follows SPEC section 3.
    [EventSource(Name = "SparkLoad")]
    internal sealed unsafe class SparkLoadSource : EventSource
    {
        // Field kinds: I = int32, L = int64, S = string. Used by the low-frequency events (arrays allocate, fine).
        [NonEvent]
        private void Emit(int id, string kinds, long[] nums, string[] strs)
        {
            int n = kinds.Length;
            EventData* d = stackalloc EventData[n];
            long* store = stackalloc long[n];
            var handles = new GCHandle[n];
            int ni = 0, si = 0;
            for (int i = 0; i < n; i++)
            {
                char k = kinds[i];
                if (k == 'S')
                {
                    string v = strs[si++] ?? "";
                    handles[i] = GCHandle.Alloc(v, GCHandleType.Pinned);
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

        // Hot path: Begin and End, no allocation.
        [NonEvent]
        private void WriteSpan(int id, int markerId, int tid, long seq, long qpc, long a, long b, long c, string s)
        {
            if (s == null) s = "";
            EventData* d = stackalloc EventData[8];
            int m = markerId, t = tid;
            long sq = seq, q = qpc, aa = a, bb = b, cc = c;
            fixed (char* p = s)
            {
                d[0].DataPointer = (IntPtr)(&m); d[0].Size = 4;
                d[1].DataPointer = (IntPtr)(&t); d[1].Size = 4;
                d[2].DataPointer = (IntPtr)(&sq); d[2].Size = 8;
                d[3].DataPointer = (IntPtr)(&q); d[3].Size = 8;
                d[4].DataPointer = (IntPtr)(&aa); d[4].Size = 8;
                d[5].DataPointer = (IntPtr)(&bb); d[5].Size = 8;
                d[6].DataPointer = (IntPtr)(&cc); d[6].Size = 8;
                d[7].DataPointer = (IntPtr)p; d[7].Size = (s.Length + 1) * 2;
                WriteEventCore(id, 8, d);
            }
        }

        [NonEvent]
        protected override void OnEventCommand(EventCommandEventArgs command)
        {
            // A session or listener that attaches after the hook started gets the definitions again.
            // During the base constructor Core.Ready is still false, so nothing is written then.
            try
            {
                if (Core.Ready && (command.Command == EventCommand.Enable || command.Command == EventCommand.Update))
                    Core.Replay();
            }
            catch
            {
                // never reach the game
            }
        }

        [Event(1, Level = EventLevel.Informational)]
        public void Hello(int pid, long qpc, long qpcFreq, string hookVersion, string runtime)
        {
            if (!IsEnabled()) return;
            Emit(1, "ILLSS", new long[] { pid, qpc, qpcFreq }, new[] { hookVersion, runtime });
        }

        [Event(2, Level = EventLevel.Informational)]
        public void MarkerDef(int markerId, string name)
        {
            if (!IsEnabled()) return;
            Emit(2, "IS", new long[] { markerId }, new[] { name });
        }

        [Event(3, Level = EventLevel.Informational)]
        public void Begin(int markerId, int tid, long seq, long qpc, long a, long b, long c, string s)
        {
            if (!IsEnabled()) return;
            WriteSpan(3, markerId, tid, seq, qpc, a, b, c, s);
        }

        [Event(4, Level = EventLevel.Informational)]
        public void End(int markerId, int tid, long seq, long qpc, long a, long b, long c, string s)
        {
            if (!IsEnabled()) return;
            WriteSpan(4, markerId, tid, seq, qpc, a, b, c, s);
        }

        [Event(5, Level = EventLevel.Informational)]
        public void Thread(int tid, int managedId, string name)
        {
            if (!IsEnabled()) return;
            Emit(5, "IIS", new long[] { tid, managedId }, new[] { name });
        }

        [Event(6, Level = EventLevel.Informational)]
        public void Patch(string marker, string target, string status, string detail)
        {
            if (!IsEnabled()) return;
            Emit(6, "SSSS", Array.Empty<long>(), new[] { marker, target, status, detail });
        }

        [Event(7, Level = EventLevel.Informational)]
        public void Frame(int tid, long seq, long qpc, long index, long durTicks)
        {
            if (!IsEnabled()) return;
            Emit(7, "ILLLL", new long[] { tid, seq, qpc, index, durTicks }, Array.Empty<string>());
        }

        [Event(8, Level = EventLevel.Informational)]
        public void FrameAgg(int tid, long seq, long qpc, long count, long sumTicks, long maxTicks, long over50)
        {
            if (!IsEnabled()) return;
            Emit(8, "ILLLLLL", new long[] { tid, seq, qpc, count, sumTicks, maxTicks, over50 }, Array.Empty<string>());
        }

        [Event(9, Level = EventLevel.Informational)]
        public void Playable(int tid, long seq, long qpc, long frameIndex)
        {
            if (!IsEnabled()) return;
            Emit(9, "ILLL", new long[] { tid, seq, qpc, frameIndex }, Array.Empty<string>());
        }

        [Event(10, Level = EventLevel.Informational)]
        public void Stats(string marker, long hits)
        {
            if (!IsEnabled()) return;
            Emit(10, "SL", new long[] { hits }, new[] { marker });
        }
    }
}
