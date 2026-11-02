using System.Globalization;
using SparkVS.Core;

// sparkvs-report build <captureDir>                                     rebuild the three files outside the game
// sparkvs-report compare <a.summary.json> <b.summary.json> [role] [top]  delta rows in ms/s, B minus A
if (args.Length >= 2 && args[0] == "build")
{
    var r = ReportBuilder.Run(args[1], Console.WriteLine);
    Console.WriteLine($"samples={r.TotalSamples} resolved={r.SamplesWithResolvedStack} main={r.MainThreadSamples} mainResolved={r.MainResolvedPct:F1}% meanIntervalMs={r.MainMeanIntervalMs:F2} spikes={r.SpikesOverThreshold}");
    return 0;
}
if (args.Length >= 3 && args[0] == "compare")
{
    var a = SummaryWriter.FromJson(File.ReadAllText(args[1]))!;
    var b = SummaryWriter.FromJson(File.ReadAllText(args[2]))!;
    string? role = args.Length > 3 && args[3] != "all" ? args[3] : null;
    int top = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 5;
    foreach (var (title, byTotal) in new[] { ("inclusive", true), ("self", false) })
    {
        Console.WriteLine($"## top {top} {title} deltas, ms per s, role={role ?? "all"}");
        Console.WriteLine("| Row | A | B | B-A |\n|---|---:|---:|---:|");
        foreach (var d in SummaryWriter.Compare(a, b, role, byTotal, top))
            Console.WriteLine($"| {d.Key} | {d.A:F1} | {d.B:F1} | {d.B - d.A:+0.0;-0.0} |");
    }
    return 0;
}
if (args.Length >= 1 && args[0] == "selftest") return SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "sparkvs-selftest"));
Console.WriteLine("usage: build <captureDir> | compare <a.json> <b.json> [role|all] [top] | selftest [dir]");
return 2;
