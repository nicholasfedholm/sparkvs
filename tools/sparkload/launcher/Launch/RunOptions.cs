using System;
using System.Collections.Generic;
using System.Globalization;

namespace SparkLoad.Launch
{
    internal sealed class RunOptions
    {
        public string Exe = "";
        public string Mode = "default";
        public string Out = "";
        public string? RunId;
        public string? Arm;
        public string? Data;
        public string? LauncherLog;
        public string? Hook;
        public string Off = "";
        public double TailSeconds = 75;
        public double MaxSeconds = 900;
        public string FinalizePattern = "Received level finalize";
        public string FinalizeFile = "client-main";
        public string Then = "wait";
        public string GuardNames = "Vintagestory,Optimum";
        public string? Compare;
        public string? ManifestCmd;
        public bool NoWindow;
        public double DebugHoldBeforeResume;
        public double MaxTraceMb = 1536;   // trace size cap, 0 = off
        public double MaxChildGb = 20;     // per-process commit limit of the job, 0 = off
        public List<string> GameArgs = new List<string>();

        public static string[] SplitGuard(string s)
        {
            var l = new List<string>();
            foreach (string p in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                l.Add(p);
            return l.ToArray();
        }

        /// <summary>Parses `run` options. Returns an error text or null.</summary>
        public static string? Parse(string[] args, int start, RunOptions o)
        {
            for (int i = start; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--")
                {
                    for (int j = i + 1; j < args.Length; j++) o.GameArgs.Add(args[j]);
                    break;
                }
                string? Next()
                {
                    if (i + 1 >= args.Length) return null;
                    return args[++i];
                }
                string? v;
                switch (a)
                {
                    case "--no-window": o.NoWindow = true; break;
                    case "--exe": v = Next(); if (v == null) return "--exe needs a value"; o.Exe = v; break;
                    case "--mode": v = Next(); if (v == null) return "--mode needs a value"; o.Mode = v.ToLowerInvariant(); break;
                    case "--out": v = Next(); if (v == null) return "--out needs a value"; o.Out = v; break;
                    case "--run-id": v = Next(); if (v == null) return "--run-id needs a value"; o.RunId = v; break;
                    case "--arm": v = Next(); if (v == null) return "--arm needs a value"; o.Arm = v.ToUpperInvariant(); break;
                    case "--data": v = Next(); if (v == null) return "--data needs a value"; o.Data = v; break;
                    case "--launcher-log": v = Next(); if (v == null) return "--launcher-log needs a value"; o.LauncherLog = v; break;
                    case "--hook": v = Next(); if (v == null) return "--hook needs a value"; o.Hook = v; break;
                    case "--off": v = Next(); if (v == null) return "--off needs a value"; o.Off = v; break;
                    case "--tail-seconds":
                        v = Next();
                        if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.TailSeconds) || o.TailSeconds < 0) return "--tail-seconds needs a number";
                        break;
                    case "--max-seconds":
                        v = Next();
                        if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.MaxSeconds) || o.MaxSeconds <= 0) return "--max-seconds needs a number";
                        break;
                    case "--finalize-pattern": v = Next(); if (v == null) return "--finalize-pattern needs a value"; o.FinalizePattern = v; break;
                    case "--finalize-file": v = Next(); if (v == null) return "--finalize-file needs a value"; o.FinalizeFile = v; break;
                    case "--then": v = Next(); if (v == null) return "--then needs a value"; o.Then = v.ToLowerInvariant(); break;
                    case "--guard-names": v = Next(); if (v == null) return "--guard-names needs a value"; o.GuardNames = v; break;
                    case "--compare": v = Next(); if (v == null) return "--compare needs a value"; o.Compare = v; break;
                    case "--manifest-cmd": v = Next(); if (v == null) return "--manifest-cmd needs a value"; o.ManifestCmd = v; break;
                    case "--max-trace-mb":
                        v = Next();
                        if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.MaxTraceMb) || o.MaxTraceMb < 0) return "--max-trace-mb needs a number >= 0 (0 = off)";
                        break;
                    case "--max-child-gb":
                        v = Next();
                        if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.MaxChildGb) || o.MaxChildGb < 0) return "--max-child-gb needs a number >= 0 (0 = off)";
                        break;
                    case "--debug-hold-before-resume":
                        v = Next();
                        if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.DebugHoldBeforeResume) || o.DebugHoldBeforeResume < 0) return "--debug-hold-before-resume needs a number";
                        break;
                    default:
                        return "unknown option " + a;
                }
            }
            if (o.Exe.Length == 0) return "--exe is required";
            if (o.Out.Length == 0) return "--out is required";
            if (o.Mode != "passive" && o.Mode != "default" && o.Mode != "deep") return "--mode must be passive, default or deep";
            if (o.Then != "kill" && o.Then != "wait" && o.Then != "detach") return "--then must be kill, wait or detach";
            if (o.Arm != null && o.Arm != "V" && o.Arm != "O" && o.Arm != "D") return "--arm must be V, O or D";
            try
            {
                _ = new System.Text.RegularExpressions.Regex(o.FinalizePattern);
            }
            catch (ArgumentException)
            {
                return "--finalize-pattern is not a valid regex";
            }
            return null;
        }
    }
}
