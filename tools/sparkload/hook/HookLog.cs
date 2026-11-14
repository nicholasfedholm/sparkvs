using System;
using System.IO;
using System.Text;

namespace SparkLoad.Hook
{
    internal static class HookLog
    {
        private static readonly object L = new object();
        private static string _runDir;

        public static void SetRunDir(string d) { _runDir = d; }

        public static void Line(string text) { WriteTo(_runDir, Environment.ProcessId + " " + text); }

        public static void WriteTo(string runDir, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(runDir)) return;
                lock (L)
                {
                    Directory.CreateDirectory(runDir);
                    using (var fs = new FileStream(Path.Combine(runDir, "hook.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        byte[] b = new UTF8Encoding(false).GetBytes(text + "\r\n");
                        fs.Write(b, 0, b.Length);
                    }
                }
            }
            catch
            {
                // logging must never reach the game
            }
        }
    }
}
