using System;
using System.Runtime.InteropServices;

namespace SparkLoad.Launch
{
    internal static class Native
    {
        public const uint CREATE_SUSPENDED = 0x00000004;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        public const int JobObjectBasicProcessIdList = 3;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 0x102;
        public const uint STILL_ACTIVE = 259;
        public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? lpApplicationName, char[] lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
            string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr h, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        public static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, int len);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int len, out int retLen);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

        [DllImport("kernel32.dll")]
        public static extern int GetThreadDescription(IntPtr hThread, out IntPtr name);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr p);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int pid);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetStdHandle(int which, IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);

        /// <summary>Make stdout/stderr usable when this WinExe was started from a console without redirection.</summary>
        public static void AttachParentConsole()
        {
            try
            {
                AttachConsole(-1);
                IntPtr o = GetStdHandle(-11);
                if (o == IntPtr.Zero || o == new IntPtr(-1))
                {
                    IntPtr con = CreateFileW("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (con != new IntPtr(-1))
                    {
                        SetStdHandle(-11, con);
                        SetStdHandle(-12, con);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
