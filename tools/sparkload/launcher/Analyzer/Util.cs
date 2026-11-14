using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SparkLoad.Analyzer
{
    /// <summary>Text helpers. Every string that leaves the analyzer goes through Clean (no em or en dashes).</summary>
    internal static class Txt
    {
        public const string Na = "n/a";

        private static readonly char[] Dashes = { '\u2014', '\u2013', '\u2015', '\u2012', '\u2212' };

        public static string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(Dashes) < 0) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(Array.IndexOf(Dashes, c) >= 0 ? '-' : c);
            return sb.ToString();
        }

        public static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        public static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        public static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
        public static string F0(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
        public static string Sec(double ms) => F2(ms / 1000.0);
        public static string Sec(double? ms) => ms.HasValue ? F2(ms.Value / 1000.0) : Na;
        public static string Pct(double v) => F1(v) + "%";
        public static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>CSV field quoting.</summary>
        public static string Csv(string? s)
        {
            s = Clean(s);
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>Markdown table cell: no pipes, no newlines.</summary>
        public static string Md(string? s)
        {
            s = Clean(s);
            return s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        public static string Cut(string? s, int n)
        {
            s ??= "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }

    /// <summary>Minimal RFC 4180 reader (quoted fields, doubled quotes, embedded newlines).</summary>
    internal static class CsvReader
    {
        public static IEnumerable<List<string>> Rows(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, new UTF8Encoding(false), true, 1 << 16);
            var row = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            bool any = false;
            int ci;
            while ((ci = sr.Read()) >= 0)
            {
                char c = (char)ci;
                if (inQ)
                {
                    if (c == '"')
                    {
                        if (sr.Peek() == '"') { sr.Read(); sb.Append('"'); }
                        else inQ = false;
                    }
                    else sb.Append(c);
                    continue;
                }
                if (c == '"') { inQ = true; any = true; }
                else if (c == ',') { row.Add(sb.ToString()); sb.Clear(); any = true; }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && sr.Peek() == '\n') sr.Read();
                    if (any || sb.Length > 0)
                    {
                        row.Add(sb.ToString());
                        yield return row;
                        row = new List<string>();
                    }
                    sb.Clear();
                    any = false;
                }
                else { sb.Append(c); any = true; }
            }
            if (any || sb.Length > 0)
            {
                row.Add(sb.ToString());
                yield return row;
            }
        }
    }

    internal static class Num
    {
        public static bool TryD(string? s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        public static double D(string? s, double dflt = 0) => TryD(s, out var v) ? v : dflt;

        public static long L(string? s, long dflt = 0) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (TryD(s, out var d) ? (long)d : dflt);
    }
}
