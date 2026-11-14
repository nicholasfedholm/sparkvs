using System;
using System.IO;
using System.Runtime.CompilerServices;
using SparkLoad.Hook;

// DOTNET_STARTUP_HOOKS entry. Must never throw and never write to the console. No Harmony type is referenced
// here or in anything this method calls before the game role is confirmed.
internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            string runDir = Environment.GetEnvironmentVariable("SPARKLOAD_RUN_DIR");
            string exe = Environment.ProcessPath ?? "?";
            int pid = Environment.ProcessId;
            string reason = RoleCheck(exe);
            if (reason != null)
            {
                HookLog.WriteTo(runDir, pid + " " + exe + " role=noop " + reason);
                return;
            }
            Start(runDir, pid, exe);
        }
        catch
        {
            // never throw out of a startup hook
        }
    }

    // Returns null when this is the game process (target exe and parent is the launcher), else the noop reason.
    private static string RoleCheck(string exe)
    {
        string target = Environment.GetEnvironmentVariable("SPARKLOAD_TARGET_EXE");
        string launcher = Environment.GetEnvironmentVariable("SPARKLOAD_LAUNCHER_PID");
        if (string.IsNullOrEmpty(target)) return "no SPARKLOAD_TARGET_EXE";
        if (string.IsNullOrEmpty(launcher)) return "no SPARKLOAD_LAUNCHER_PID";
        string a, b;
        try { a = Path.GetFullPath(exe); b = Path.GetFullPath(target); }
        catch { a = exe; b = target; }
        if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return "exe is not the target";
        if (!long.TryParse(launcher, out long lp)) return "bad SPARKLOAD_LAUNCHER_PID";
        long parent = Native.GetParentPid();
        if (parent != lp) return "parent pid " + parent + " is not the launcher " + lp;
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start(string runDir, int pid, string exe)
    {
        HookLog.WriteTo(runDir, pid + " " + exe + " role=game");
        Core.Begin(runDir);
    }
}
