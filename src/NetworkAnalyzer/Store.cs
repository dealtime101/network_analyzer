using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NetworkAnalyzer;

/// <summary>Header of a session (what <c>&lt;id&gt;.meta.json</c> holds).</summary>
public sealed class SessionHeader
{
    public int Id { get; set; }
    public double Started { get; set; }
    public double? Ended { get; set; }
    public string Label { get; set; } = "";
    public string Link { get; set; } = "auto";
    public int PlannedS { get; set; }
    public SessionMeta Meta { get; set; } = new();
}

/// <summary>
/// Local storage, nothing leaves the machine. One session = two files in the data folder:
/// <c>sessions/&lt;id&gt;.meta.json</c> (header, rewritten rarely) and <c>sessions/&lt;id&gt;.jsonl</c> (one JSON array per line, appended):
/// ["s", t, series, value|null, ok(0/1), info] · ["m", t, kind, note] · ["p", name, t0, t1, {meta}] · ["tr", t, target, {result}].
/// Series: ping:&lt;target&gt;, dns:sys_hit|sys_miss|ref_miss, net:down_bps|up_bps, wifi:signal|rx|tx, load:down_bps|up_bps.
/// </summary>
public sealed class SessionStore
{
    readonly string dir;
    readonly object idLock = new();

    public string DataDir { get; }

    public static string DefaultDataDir()
    {
        var d = Environment.GetEnvironmentVariable("NA_DATA");
        if (string.IsNullOrEmpty(d))
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            d = Path.Combine(baseDir, "NetworkAnalyzer");
        }
        return d;
    }

    public SessionStore(string? dataDir = null)
    {
        DataDir = dataDir ?? DefaultDataDir();
        dir = Path.Combine(DataDir, "sessions");
        Directory.CreateDirectory(dir);
    }

    string MetaPath(int id) => Path.Combine(dir, $"{id}.meta.json");
    string LinesPath(int id) => Path.Combine(dir, $"{id}.jsonl");

    public int Create(SessionHeader h)
    {
        lock (idLock)
        {
            // never below what was handed out before, even if that session was deleted (an export or a link may still name it)
            var counter = Path.Combine(dir, "last_id.txt");
            int last = 0;
            try { int.TryParse(File.ReadAllText(counter).Trim(), out last); } catch (IOException) { }
            int id = Math.Max(last, Directory.EnumerateFiles(dir, "*.meta.json").Select(f => int.TryParse(Path.GetFileName(f).Split('.')[0], out var n) ? n : 0).DefaultIfEmpty(0).Max()) + 1;
            while (true)  // reserve it atomically: another process on the same folder cannot get the same number
            {
                try
                {
                    // both files are created, never opened for writing over an old one: an id whose measurements exist is taken even without its header
                    using (new FileStream(LinesPath(id), FileMode.CreateNew, FileAccess.Write)) { }
                    try { using (new FileStream(MetaPath(id), FileMode.CreateNew, FileAccess.Write)) { } }
                    catch (IOException) { File.Delete(LinesPath(id)); throw; }   // we created the empty lines file just now: give it back
                    break;
                }
                catch (IOException) { id++; }
            }
            h.Id = id;
            File.WriteAllText(counter, id.ToString(CultureInfo.InvariantCulture));
            SaveHeader(h);
            return h.Id;
        }
    }

    public void SaveHeader(SessionHeader h)
    {
        DurableFile.WriteAllText(MetaPath(h.Id), JsonSerializer.Serialize(h, Json.Storage));
    }

    public SessionHeader? LoadHeader(int id)
    {
        try { return Json.From<SessionHeader>(File.ReadAllText(MetaPath(id))); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public List<SessionHeader> List()
    {
        var res = new List<SessionHeader>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.meta.json"))
        {
            if (!int.TryParse(Path.GetFileName(f).Split('.')[0], out var id)) continue;
            var h = LoadHeader(id);
            if (h != null) res.Add(h);
        }
        return res.OrderByDescending(h => h.Id).ToList();
    }

    public void Delete(int id)
    {
        foreach (var p in new[] { MetaPath(id), LinesPath(id) }) if (File.Exists(p)) File.Delete(p);
    }

    public SessionData? Load(int id)
    {
        var h = LoadHeader(id);
        if (h is null) return null;
        var d = new SessionData { Id = h.Id, Started = h.Started, Ended = h.Ended, Label = h.Label, Link = h.Link, PlannedS = h.PlannedS, Meta = h.Meta };
        var path = LinesPath(id);
        if (!File.Exists(path)) return d;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        bool brokenJson = false;   // the previous line did not parse: damage if more follows, a line still being written if it was the last
        while ((line = sr.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            if (brokenJson) { d.SkippedLines++; brokenJson = false; }
            try
            {
                using var doc = JsonDocument.Parse(line);
                var a = doc.RootElement;
                switch (a[0].GetString())
                {
                    case "s":
                    {
                        var name = a[2].GetString()!;
                        if (!d.Series.TryGetValue(name, out var list)) d.Series[name] = list = new List<Sample>();
                        double? v = a[3].ValueKind == JsonValueKind.Null ? null : a[3].GetDouble();
                        list.Add(new Sample(a[1].GetDouble(), v, a[4].GetInt32() != 0, a[5].GetString() ?? ""));
                        break;
                    }
                    case "m": d.Marks.Add(new Mark { T = a[1].GetDouble(), Kind = a[2].GetString()!, Note = a[3].GetString() ?? "" }); break;
                    case "p": d.Phases.Add(new Phase { Name = a[1].GetString()!, T0 = a[2].GetDouble(), T1 = a[3].GetDouble(), Meta = Json.From<PhaseMeta>(a[4].GetRawText()) ?? new() }); break;
                    case "tr": d.Traces.Add(new TraceRec { T = a[1].GetDouble(), Target = a[2].GetString()!, Data = Json.From<TraceResult>(a[3].GetRawText()) ?? new() }); break;
                }
            }
            // a line still being written (JsonException) or valid JSON of the wrong shape (too short, wrong type, not an array):
            // skip that line, never lose the whole session over it
            catch (JsonException) { brokenJson = true; }
            catch (Exception e) when (e is InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or FormatException or OverflowException or ArgumentException) { d.SkippedLines++; }
        }
        foreach (var l in d.Series.Values) l.Sort((x, y) => x.T.CompareTo(y.T));
        d.Marks.Sort((x, y) => x.T.CompareTo(y.T));
        d.Phases.Sort((x, y) => x.T0.CompareTo(y.T0));
        d.Traces.Sort((x, y) => x.T.CompareTo(y.T));
        return d;
    }

    public SessionWriter OpenWriter(int id) => new(LinesPath(id));

    /// <summary>Writes a whole session at once (import, simulations, tests). Returns its new id.</summary>
    public int SaveComplete(SessionData d)
    {
        var h = new SessionHeader { Started = d.Started, Ended = d.Ended, Label = d.Label, Link = d.Link, PlannedS = d.PlannedS, Meta = d.Meta };
        int id = Create(h);
        var sb = new StringBuilder();
        foreach (var (name, list) in d.Series)
            foreach (var s in list)
                sb.AppendLine(JsonSerializer.Serialize(new object?[] { "s", s.T, name, s.V, s.Ok ? 1 : 0, s.Info }));
        foreach (var m in d.Marks) sb.AppendLine(JsonSerializer.Serialize(new object?[] { "m", m.T, m.Kind, m.Note }));
        foreach (var p in d.Phases) sb.AppendLine(JsonSerializer.Serialize(new object?[] { "p", p.Name, p.T0, p.T1, JsonSerializer.SerializeToElement(p.Meta, Json.Options) }));
        foreach (var t in d.Traces) sb.AppendLine(JsonSerializer.Serialize(new object?[] { "tr", t.T, t.Target, JsonSerializer.SerializeToElement(t.Data, Json.Options) }));
        File.WriteAllText(LinesPath(id), sb.ToString(), new UTF8Encoding(false));
        return id;
    }
}

/// <summary>Single background writer: measurement threads only enqueue, the disk is touched every ~0.4 s.</summary>
public sealed class SessionWriter
{
    readonly Channel<string> ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task pump;

    public SessionWriter(string path)
    {
        pump = Task.Run(async () =>
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                await using var w = new StreamWriter(fs, new UTF8Encoding(false));
                var reader = ch.Reader;
                while (await reader.WaitToReadAsync())
                {
                    await Task.Delay(400);
                    while (reader.TryRead(out var line)) await w.WriteLineAsync(line);
                    await w.FlushAsync();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // disk full, file locked, folder gone: say so, and stop accepting lines so the queue cannot grow without limit
                Failure = e;
                ch.Writer.TryComplete(e);
            }
        });
    }

    /// <summary>Set when writing to disk failed; the measurements taken after that are no longer being saved.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>False once the writer stopped (finished or failed).</summary>
    public bool IsAccepting => Failure is null && !ch.Reader.Completion.IsCompleted;

    public void Sample(double t, string series, double? v, bool ok, string info) => ch.Writer.TryWrite(JsonSerializer.Serialize(new object?[] { "s", Math.Round(t, 3), series, v.HasValue && double.IsFinite(v.Value) ? Math.Round(v.Value, 3) : null, ok ? 1 : 0, info }));  // NaN/Infinity are not JSON numbers
    public void Mark(double t, string kind, string note) => ch.Writer.TryWrite(JsonSerializer.Serialize(new object?[] { "m", t, kind, note }));
    public void Phase(string name, double t0, double t1, PhaseMeta meta) => ch.Writer.TryWrite(JsonSerializer.Serialize(new object?[] { "p", name, t0, t1, JsonSerializer.SerializeToElement(meta, Json.Options) }));
    public void Trace(double t, string target, TraceResult r) => ch.Writer.TryWrite(JsonSerializer.Serialize(new object?[] { "tr", t, target, JsonSerializer.SerializeToElement(r, Json.Options) }));

    public async Task CompleteAsync()
    {
        ch.Writer.TryComplete();
        await pump;
    }
}
