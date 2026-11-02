using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SparkVS.Core;

/// <summary>Writes the Speedscope "sampled" file format, one profile per reported thread, chronological.</summary>
public static class Speedscope
{
    public static void Write(Stream output, SampleSet set, Report report, string name)
    {
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteString("$schema", "https://www.speedscope.app/file-format-schema.json");
        json.WriteString("exporter", "SparkVS " + BuildInfo.SparkVsVersion);
        json.WriteString("name", name);
        json.WriteNumber("activeProfileIndex", 0);

        json.WriteStartObject("shared");
        json.WriteStartArray("frames");
        foreach (var f in set.Frames)
        {
            json.WriteStartObject();
            json.WriteString("name", f.Method.Length == 0 ? "?" : f.Method);
            if (f.Module.Length > 0) json.WriteString("file", f.Module);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        var byThread = set.Samples.GroupBy(s => s.ThreadId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.TimeMs).ToList());

        json.WriteStartArray("profiles");
        foreach (var thread in report.Threads)
        {
            if (!byThread.TryGetValue(thread.OsThreadId, out var samples) || samples.Count == 0) continue;
            WriteProfile(json, samples, set, thread.Role + " (thread " + thread.OsThreadId + ")");
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    static void WriteProfile(Utf8JsonWriter json, List<Sample> samples, SampleSet set, string name)
    {
        var weights = Analyzer.Weights(samples);
        double start = samples[0].TimeMs;

        json.WriteStartObject();
        json.WriteString("type", "sampled");
        json.WriteString("name", name);
        json.WriteString("unit", "milliseconds");
        json.WriteNumber("startValue", start);
        json.WriteNumber("endValue", start + weights.Sum());

        // Runs of one stack merge into one entry, which keeps long captures small.
        var runStacks = new List<int>();
        var runWeights = new List<double>();
        for (int i = 0; i < samples.Count; i++)
        {
            if (runStacks.Count > 0 && runStacks[runStacks.Count - 1] == samples[i].StackId)
                runWeights[runWeights.Count - 1] += weights[i];
            else
            {
                runStacks.Add(samples[i].StackId);
                runWeights.Add(weights[i]);
            }
        }

        json.WriteStartArray("samples");
        foreach (var stackId in runStacks)
        {
            json.WriteStartArray();
            var stack = set.Stacks[stackId];
            for (int i = stack.Length - 1; i >= 0; i--) json.WriteNumberValue(stack[i]);  // root first
            json.WriteEndArray();
        }
        json.WriteEndArray();

        json.WriteStartArray("weights");
        foreach (var w in runWeights) json.WriteNumberValue(w);
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
