using System;
using System.Runtime.InteropServices;

namespace SparkLoad.Hook
{
    internal static class Native
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int infoClass, ref ProcessBasicInformation info, int size, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>Parent process id of this process, or -1 when it cannot be read.</summary>
        public static long GetParentPid()
        {
            try
            {
                var pbi = new ProcessBasicInformation();
                int st = NtQueryInformationProcess(GetCurrentProcess(), 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _);
                if (st != 0) return -1;
                return (long)pbi.InheritedFromUniqueProcessId;
            }
            catch { return -1; }
        }
    }
}
