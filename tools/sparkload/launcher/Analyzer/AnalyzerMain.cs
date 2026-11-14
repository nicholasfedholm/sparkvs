using System;
using System.IO;
using System.Text;

namespace SparkLoad.Analyzer
{
    public static class AnalyzerMain
    {
        /// <summary>
        /// Analyzes one run folder: step 1 (raw.nettrace to events.jsonl, when a trace exists) and step 2
        /// (loading.csv and loading.md). With compareRunDir, loading.md gains a moved-bottleneck section.
        /// Returns 0 on success, non-zero only when the inputs are unreadable.
        /// </summary>
        public static int Run(string runDir, string? compareRunDir, TextWriter log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
                {
                    log.WriteLine("analyzer: run folder not found: " + Txt.Clean(runDir));
                    return 1;
                }
                runDir = Path.GetFullPath(runDir);

                RunData d;
                try { d = RunData.Load(runDir, log, true); }
                catch (Exception ex) when (ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    log.WriteLine("analyzer: cannot read run.json: " + Txt.Clean(ex.Message));
                    return 1;
                }

                Result? cmp = null;
                if (!string.IsNullOrWhiteSpace(compareRunDir))
                {
                    try
                    {
                        var cd = RunData.Load(Path.GetFullPath(compareRunDir), log, true);
                        cmp = Result.Build(cd);
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine("analyzer: compare run unreadable, diff skipped: " + Txt.Clean(ex.Message));
                    }
                }

                var r = Result.Build(d);
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(runDir, "loading.csv"), Report.BuildCsv(r), utf8);
                File.WriteAllText(Path.Combine(runDir, "loading.md"), Report.BuildMarkdown(r, cmp), utf8);
                foreach (var g in r.Gates) log.WriteLine("analyzer: " + g);
                log.WriteLine("analyzer: wrote loading.csv and loading.md in " + Txt.Clean(runDir));
                return 0;
            }
            catch (Exception ex)
            {
                log.WriteLine("analyzer: failed: " + Txt.Clean(ex.GetType().Name + ": " + ex.Message));
                return 1;
            }
        }
    }
}
