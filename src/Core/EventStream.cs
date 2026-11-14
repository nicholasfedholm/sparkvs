using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace SparkVS.Core;

/// <summary>EventLog setting: off (no logging at all), capture (rows go into the bundle of a SparkVS capture), always (also a session file for as long as the world runs).</summary>
public enum EventStreamMode { Off, Capture, Always }

public static class EventStreamModes
{
    /// <summary>Case insensitive. Null means the key was absent and gives the default. Anything else unknown gives capture with valid false, so the caller can warn.</summary>
    public static EventStreamMode Parse(string? text, out bool valid)
    {
        valid = true;
        if (text == null) return EventStreamMode.Capture;
        switch (text.Trim().ToLowerInvariant())
        {
            case "off": return EventStreamMode.Off;
            case "capture": return EventStreamMode.Capture;
            case "always": return EventStreamMode.Always;
            default:
                valid = false;
                return EventStreamMode.Capture;
        }
    }

    public static string Name(EventStreamMode mode) => mode switch
    {
        EventStreamMode.Off => "off",
        EventStreamMode.Always => "always",
        _ => "capture",
    };
}

/// <summary>What each mode turns on.</summary>
public static class EventStreamPolicy
{
    /// <summary>The five game events and the log hook are subscribed in every mode except off.</summary>
    public static bool Subscribe(EventStreamMode mode) => mode != EventStreamMode.Off;

    /// <summary>The Harmony postfix on Entity.OnHurt needs logging on and the HurtLog switch.</summary>
    public static bool PatchHurt(EventStreamMode mode, bool hurtLog) => mode != EventStreamMode.Off && hurtLog;

    /// <summary>Only always mode writes a session file of its own.</summary>
    public static bool SessionFile(EventStreamMode mode) => mode == EventStreamMode.Always;
}

public static class EventStreamPaths
{
    public const string SessionFolder = "events";

    /// <summary>Same root as plain captures (OutputFolder, else ModData/sparkvs under the data path), then events/events-yyyyMMdd-HHmmss.jsonl.</summary>
    public static string SessionFile(string outputFolder, string dataPath, DateTime now) =>
        Path.Combine(CapturePaths.Root("", outputFolder, false, dataPath), SessionFolder, "events-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
}

public sealed record EventEntity(string Code, long Id, string Domain, string Name);

public sealed record EventAttacker(string Code, long Id);

/// <summary>One logged event, before it gets a time. Only the fields that apply to the row type are set.</summary>
public sealed class EventRow
{
    public const string Spawn = "spawn", Despawn = "despawn", Death = "death", Hurt = "hurt", Log = "log", Chat = "chat", Command = "command", Note = "note";

    public string Type { get; init; } = "";
    public string Side { get; init; } = "server";
    public EventEntity? Entity { get; init; }
    /// <summary>x, y, z; written with one decimal. Left out when null or not finite.</summary>
    public double[]? Pos { get; init; }
    public string? Reason { get; init; }
    public string? Source { get; init; }
    public string? DmgType { get; init; }
    public double? Damage { get; init; }
    public EventAttacker? Attacker { get; init; }
    public string? Level { get; init; }
    public string? Message { get; init; }
    public string? Player { get; init; }
    public string? Text { get; init; }

    /// <summary>A chat line becomes a chat row. A line starting with a slash becomes a command row that keeps the first token only (arguments are redacted). A blank line gives null.</summary>
    public static EventRow? FromChat(string? player, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.StartsWith('/'))
        {
            int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
            return new EventRow { Type = Command, Player = player ?? "?", Text = space < 0 ? trimmed : trimmed.Substring(0, space) };
        }
        return new EventRow { Type = Chat, Player = player ?? "?", Text = text };
    }
}

/// <summary>Hand built JSON lines, as in BundleWriter: invariant culture, escaped strings, optional fields left out.</summary>
public static class EventLine
{
    public const string FileName = "gameevents.jsonl";
    public const string Description = "Server side game events (spawn, despawn, death, hurt, warning and error log lines, chat, commands, notes) on the bundle clock.";

    static string Q(string? s) => "\"" + JsonEncodedText.Encode(s ?? "") + "\"";

    public static string Build(EventRow r, double tMs, string tsLocal)
    {
        var sb = new StringBuilder(192);
        sb.Append("{\"t_ms\":").Append(BundleWriter.N(tMs, 3))
          .Append(",\"ts_local\":").Append(Q(tsLocal))
          .Append(",\"type\":").Append(Q(r.Type))
          .Append(",\"side\":").Append(Q(r.Side));
        if (r.Entity != null)
            sb.Append(",\"entity\":{\"code\":").Append(Q(r.Entity.Code)).Append(",\"id\":").Append(r.Entity.Id.ToString(CultureInfo.InvariantCulture))
              .Append(",\"domain\":").Append(Q(r.Entity.Domain)).Append(",\"name\":").Append(Q(r.Entity.Name)).Append('}');
        if (r.Pos != null && r.Pos.Length == 3 && r.Pos.All(double.IsFinite))
            sb.Append(",\"pos\":[").Append(BundleWriter.N(r.Pos[0], 1)).Append(',').Append(BundleWriter.N(r.Pos[1], 1)).Append(',').Append(BundleWriter.N(r.Pos[2], 1)).Append(']');
        if (r.Reason != null) sb.Append(",\"reason\":").Append(Q(r.Reason));
        if (r.Source != null) sb.Append(",\"source\":").Append(Q(r.Source));
        if (r.DmgType != null) sb.Append(",\"dmg_type\":").Append(Q(r.DmgType));
        if (r.Damage is double d && double.IsFinite(d)) sb.Append(",\"damage\":").Append(BundleWriter.N(d, 2));
        if (r.Attacker != null)
            sb.Append(",\"attacker\":{\"code\":").Append(Q(r.Attacker.Code)).Append(",\"id\":").Append(r.Attacker.Id.ToString(CultureInfo.InvariantCulture)).Append('}');
        if (r.Level != null) sb.Append(",\"level\":").Append(Q(r.Level));
        if (r.Message != null) sb.Append(",\"message\":").Append(Q(r.Message));
        if (r.Player != null) sb.Append(",\"player\":").Append(Q(r.Player));
        if (r.Text != null) sb.Append(",\"text\":").Append(Q(r.Text));
        return sb.Append('}').ToString();
    }

    public static string Footer(int rows, int dropped) => "{\"type\":\"footer\",\"rows\":" + rows.ToString(CultureInfo.InvariantCulture) + ",\"dropped\":" + dropped.ToString(CultureInfo.InvariantCulture) + "}";

    public static string HeaderLine(BundleClock clock) => BundleWriter.Header("game_events", clock, Description, new JsonObject
    {
        ["type"] = "spawn | despawn | death | hurt | log | chat | command | note",
        ["side"] = "server",
        ["entity"] = "{code, id, domain, name} of the entity the row is about (spawn, despawn, death, hurt)",
        ["pos"] = "[x, y, z] of that entity, one decimal",
        ["reason"] = "despawn only: the EnumDespawnReason (deaths are death rows, not despawn rows)",
        ["source"] = "hurt and death: DamageSource.Source",
        ["dmg_type"] = "hurt and death: DamageSource.Type",
        ["damage"] = "hurt only: damage amount, two decimals",
        ["attacker"] = "hurt and death: {code, id} of the causing entity when there is one",
        ["level"] = "log only: warning | error | fatal",
        ["message"] = "log only: the formatted log line",
        ["player"] = "chat, command, note: player name, or console",
        ["text"] = "chat and note: the text; command: the first token only",
    });
}

/// <summary>
/// One output: the bundle copy of a capture, or the always-mode session file. It keeps its own clock, so t_ms and ts_local
/// of every row come from the same origin as the rest of that bundle.
/// </summary>
public sealed class EventStreamSink
{
    public const int RowCap = 200_000;

    readonly object gate = new();
    readonly List<string>? lines;
    readonly StreamWriter? writer;
    readonly int cap;
    int rows, dropped;
    bool closed;
    string? error;

    public BundleClock Clock { get; }
    public string? Path { get; }
    public bool IsSession => writer != null;

    /// <summary>Without a path this is a capture sink that holds its rows until WriteCapture. With a path it is a session sink that appends and flushes every row, so a crash keeps what was written.</summary>
    public EventStreamSink(BundleClock clock, string? sessionPath = null, int cap = RowCap)
    {
        Clock = clock;
        this.cap = cap;
        Path = sessionPath;
        if (sessionPath == null) { lines = new List<string>(); return; }
        string? dir = System.IO.Path.GetDirectoryName(sessionPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var fs = new FileStream(sessionPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(EventLine.HeaderLine(clock));
        writer.Flush();
    }

    public int Rows { get { lock (gate) return rows; } }
    public int Dropped { get { lock (gate) return dropped; } }
    public string? Error { get { lock (gate) return error; } }

    /// <summary>Rows held by a capture sink, for tests.</summary>
    public IReadOnlyList<string> Lines() { lock (gate) return lines == null ? Array.Empty<string>() : lines.ToArray(); }

    public bool Add(EventRow row, long qpc)
    {
        double t = Clock.MsSince(qpc);
        string line = EventLine.Build(row, t, Clock.IsoAt(t));
        lock (gate)
        {
            if (closed) return false;
            if (rows >= cap) { dropped++; return false; }
            if (lines != null) lines.Add(line);
            else
            {
                try { writer!.WriteLine(line); writer.Flush(); }
                catch (Exception ex) { error ??= ex.GetType().Name + ": " + ex.Message; dropped++; return false; }
            }
            rows++;
            return true;
        }
    }

    /// <summary>Capture sink: header, every row, footer. Returns the row count. Closes the sink.</summary>
    public int WriteCapture(string path)
    {
        lock (gate)
        {
            closed = true;
            var sb = new StringBuilder(256 + (lines?.Count ?? 0) * 192);
            sb.Append(EventLine.HeaderLine(Clock)).Append('\n');
            if (lines != null) foreach (var l in lines) sb.Append(l).Append('\n');
            sb.Append(EventLine.Footer(rows, dropped)).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return rows;
        }
    }

    /// <summary>Session sink: writes the footer and closes the file. Safe to call twice. Returns the row count.</summary>
    public int Close()
    {
        lock (gate)
        {
            if (closed) return rows;
            closed = true;
            if (writer != null)
            {
                try { writer.WriteLine(EventLine.Footer(rows, dropped)); writer.Flush(); }
                catch (Exception ex) { error ??= ex.GetType().Name + ": " + ex.Message; }
                try { writer.Dispose(); } catch { }
            }
            return rows;
        }
    }
}

/// <summary>
/// Process wide: an integrated server runs the client and server ModSystems in one process, and the capture is owned by one
/// of them while the events come from the other. Holds at most one capture sink and one session sink.
/// </summary>
public static class EventStreamHub
{
    static readonly object gate = new();
    static volatile EventStreamSink? capture;
    static volatile EventStreamSink? session;
    [ThreadStatic] static bool recording;

    /// <summary>True while the server side subscriptions exist in this process (set by the hooks). A client that joins a remote server has none, so its captures do not bind.</summary>
    public static volatile bool Attached;

    /// <summary>Called inside Record, under the re-entrancy guard, for every row offered. A diagnostic seam for tests.</summary>
    public static Action<EventRow>? Probe { get; set; }

    public static bool Active => capture != null || session != null;
    public static bool CaptureOpen => capture != null;
    public static bool SessionOpen => session != null;
    public static string? SessionPath => session?.Path;

    /// <summary>Opens the bundle sink on the clock the bundle uses. A capture left open by an earlier failure is dropped.</summary>
    public static void BeginCapture(BundleClock clock)
    {
        lock (gate) capture = new EventStreamSink(clock);
    }

    /// <summary>Writes gameevents.jsonl into the bundle folder and closes the capture sink. Returns the row count, or 0 with no file when no capture was open.</summary>
    public static int EndCapture(string bundleDir)
    {
        EventStreamSink? sink;
        lock (gate) { sink = capture; capture = null; }
        return sink == null ? 0 : sink.WriteCapture(System.IO.Path.Combine(bundleDir, EventLine.FileName));
    }

    /// <summary>False when a session is already open. Throws when the file cannot be created.</summary>
    public static bool StartSession(string path, BundleClock clock)
    {
        lock (gate)
        {
            if (session != null) return false;
            session = new EventStreamSink(clock, path);
            return true;
        }
    }

    /// <summary>Row count of the closed session, or -1 when none was open.</summary>
    public static int StopSession()
    {
        EventStreamSink? sink;
        lock (gate) { sink = session; session = null; }
        return sink == null ? -1 : sink.Close();
    }

    /// <summary>Offers a row to every open sink. Returns how many took it. A call made while this thread is already inside Record is ignored, so our own log lines never loop back through the log hook.</summary>
    public static int Record(EventRow row, long qpc)
    {
        if (recording || !Active) return 0;
        recording = true;
        try
        {
            Probe?.Invoke(row);
            int taken = 0;
            var c = capture;
            var s = session;
            if (c != null && c.Add(row, qpc)) taken++;
            if (s != null && s.Add(row, qpc)) taken++;
            return taken;
        }
        catch { return 0; }
        finally { recording = false; }
    }

    public static string Status()
    {
        var c = capture;
        var s = session;
        var parts = new List<string>();
        if (c != null) parts.Add($"capture bundle open ({c.Rows} rows, {c.Dropped} dropped)");
        if (s != null) parts.Add($"session open at {s.Path} ({s.Rows} rows, {s.Dropped} dropped{(s.Error != null ? ", write error " + s.Error : "")})");
        return parts.Count == 0 ? "no sink open" : "sinks: " + string.Join("; ", parts);
    }

    /// <summary>Closes everything and clears the flags. Tests and shutdown only.</summary>
    public static void Reset()
    {
        lock (gate)
        {
            session?.Close();
            session = null;
            capture = null;
        }
        Attached = false;
        Probe = null;
    }
}

/// <summary>Reply texts and behaviour of /testlog and /testlogh, kept free of game types so they can be tested.</summary>
public static class EventStreamCommands
{
    public const string BoundMessage = "event logging is bound to SparkVS captures (.sparkvs start / /sparkvs start)";
    public const string OffMessage = "SparkVS EventStream: EventLog is off in SparkVS.json (set it to capture or always and restart).";
    public const string NothingOpenMessage = "SparkVS EventStream: not recorded: no capture or session open";

    public static (bool Ok, string Text) Status(EventStreamMode mode, bool hurtLog, bool hurtLive)
    {
        string text = $"SparkVS EventStream: mode {EventStreamModes.Name(mode)}; HurtLog {(hurtLog ? "on" : "off")}, hurt patch {(hurtLive ? "live" : "not live")}; {EventStreamHub.Status()}.";
        if (mode == EventStreamMode.Capture) text += " In capture mode " + BoundMessage + ".";
        return (true, text);
    }

    public static (bool Ok, string Text) Start(EventStreamMode mode, Func<string> sessionPath)
    {
        if (mode == EventStreamMode.Off) return (false, OffMessage);
        if (mode == EventStreamMode.Capture) return (true, "SparkVS EventStream: nothing to start, " + BoundMessage + ".");
        string path = sessionPath();
        try
        {
            if (!EventStreamHub.StartSession(path, BundleClock.FromStamp(Stopwatch.GetTimestamp())))
                return (false, "SparkVS EventStream: a session is already open at " + EventStreamHub.SessionPath + ". Use stop first.");
        }
        catch (Exception ex) { return (false, "SparkVS EventStream: could not open the session file: " + ex.Message); }
        return (true, "SparkVS EventStream: session started, writing to " + path);
    }

    public static (bool Ok, string Text) Stop(EventStreamMode mode)
    {
        if (mode == EventStreamMode.Off) return (false, OffMessage);
        if (mode == EventStreamMode.Capture) return (true, "SparkVS EventStream: nothing to stop, " + BoundMessage + ".");
        string? path = EventStreamHub.SessionPath;
        int rows = EventStreamHub.StopSession();
        return rows < 0 ? (false, "SparkVS EventStream: no session is open.") : (true, $"SparkVS EventStream: session stopped, {rows} rows in {path}");
    }

    public static (bool Ok, string Text) Note(EventStreamMode mode, string? player, string? text, long qpc)
    {
        if (mode == EventStreamMode.Off) return (false, OffMessage);
        if (!EventStreamHub.Active) return (false, NothingOpenMessage);
        int taken = EventStreamHub.Record(new EventRow { Type = EventRow.Note, Player = string.IsNullOrEmpty(player) ? "console" : player, Text = text ?? "" }, qpc);
        return taken > 0 ? (true, $"SparkVS EventStream: note recorded in {taken} sink(s)") : (false, NothingOpenMessage);
    }
}

/// <summary>Reads gameevents.jsonl back for the bundle summary.</summary>
public sealed class EventSummary
{
    public int Rows { get; private set; }
    public int Dropped { get; private set; }
    public double? FirstMs { get; private set; }
    public double? LastMs { get; private set; }
    public SortedDictionary<string, int> ByType { get; } = new(StringComparer.Ordinal);
    public List<KeyValuePair<string, int>> TopHurts { get; private set; } = new();
    public List<KeyValuePair<string, int>> TopDeaths { get; private set; } = new();

    const int Top = 10;

    public static EventSummary FromJsonl(string path)
    {
        var s = new EventSummary();
        var hurts = new Dictionary<string, int>(StringComparer.Ordinal);
        var deaths = new Dictionary<string, int>(StringComparer.Ordinal);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() ?? "" : "";
                if (type == "header") continue;
                if (type == "footer")
                {
                    if (root.TryGetProperty("dropped", out var dr) && dr.TryGetInt32(out int dv)) s.Dropped = dv;
                    continue;
                }
                s.Rows++;
                s.ByType[type] = s.ByType.GetValueOrDefault(type) + 1;
                if (root.TryGetProperty("t_ms", out var tm) && tm.TryGetDouble(out double t))
                {
                    if (s.FirstMs == null || t < s.FirstMs) s.FirstMs = t;
                    if (s.LastMs == null || t > s.LastMs) s.LastMs = t;
                }
                if ((type == EventRow.Hurt || type == EventRow.Death) && root.TryGetProperty("entity", out var en) && en.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.String)
                {
                    var map = type == EventRow.Hurt ? hurts : deaths;
                    string code = cd.GetString() ?? "?";
                    map[code] = map.GetValueOrDefault(code) + 1;
                }
            }
            catch (JsonException) { }
        }
        s.TopHurts = Rank(hurts);
        s.TopDeaths = Rank(deaths);
        return s;
    }

    static List<KeyValuePair<string, int>> Rank(Dictionary<string, int> map) =>
        map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(Top).ToList();

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Game events").AppendLine();
        sb.AppendLine("Label: measured. Server side game events recorded while the capture ran, one row each in gameevents.jsonl on the bundle clock.").AppendLine();
        if (Rows == 0)
        {
            sb.AppendLine($"- No events were recorded in this capture (dropped over the cap: {Dropped}).").AppendLine();
            return sb.ToString();
        }
        sb.AppendLine($"- Rows: {Rows} (dropped over the {EventStreamSink.RowCap} row cap: {Dropped}). First at t_ms {BundleWriter.N(FirstMs ?? 0, 1)}, last at t_ms {BundleWriter.N(LastMs ?? 0, 1)}.").AppendLine();
        sb.AppendLine("| Type | Rows |").AppendLine("|---|---:|");
        foreach (var kv in ByType) sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        sb.AppendLine();
        AppendTop(sb, "Top entities by hurts", "Hurts", TopHurts);
        AppendTop(sb, "Top entities by deaths", "Deaths", TopDeaths);
        return sb.ToString();
    }

    static void AppendTop(StringBuilder sb, string title, string column, List<KeyValuePair<string, int>> top)
    {
        if (top.Count == 0) return;
        sb.AppendLine(title + ":").AppendLine();
        sb.AppendLine($"| Entity | {column} |").AppendLine("|---|---:|");
        foreach (var kv in top) sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        sb.AppendLine();
    }

    public JsonObject ToJson()
    {
        var byType = new JsonObject();
        foreach (var kv in ByType) byType[kv.Key] = kv.Value;
        JsonArray Arr(List<KeyValuePair<string, int>> top) => new(top.Select(kv => (JsonNode?)new JsonObject { ["entity"] = kv.Key, ["count"] = kv.Value }).ToArray());
        return new JsonObject
        {
            ["label"] = "measured",
            ["detail_file"] = EventLine.FileName,
            ["rows"] = Rows,
            ["dropped"] = Dropped,
            ["first_t_ms"] = FirstMs == null ? null : Math.Round(FirstMs.Value, 3),
            ["last_t_ms"] = LastMs == null ? null : Math.Round(LastMs.Value, 3),
            ["by_type"] = byType,
            ["top_hurts"] = Arr(TopHurts),
            ["top_deaths"] = Arr(TopDeaths),
        };
    }
}
