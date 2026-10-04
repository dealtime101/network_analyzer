using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

/// <summary>Temporary folders that are removed (with their content) when this object is disposed.</summary>
sealed class TempFolders : IDisposable
{
    readonly System.Collections.Concurrent.ConcurrentBag<string> dirs = new();

    public string Dir()
    {
        var d = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "na-test-" + Guid.NewGuid().ToString("N"))).FullName;
        dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in dirs)
            try { Directory.Delete(d, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }  // best effort: a file still open
    }
}

static class Tmp
{
    static readonly TempFolders shared = Create();

    // every folder handed out is removed when the test process exits, so runs do not pile up folders in the temp directory
    static TempFolders Create()
    {
        var t = new TempFolders();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => t.Dispose();
        return t;
    }

    public static string Dir() => shared.Dir();
}

public class TempFolderTests
{
    [Fact]
    public void FoldersAreRemovedWithTheirContentOnDispose()
    {
        string a, b;
        using (var t = new TempFolders())
        {
            a = t.Dir(); b = t.Dir();
            Directory.CreateDirectory(Path.Combine(a, "sessions"));
            File.WriteAllText(Path.Combine(a, "sessions", "1.jsonl"), "x");
            Assert.True(Directory.Exists(a) && Directory.Exists(b));
        }
        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
    }
}

/// <summary>Fake Cloudflare-like speed server: GET /__down?bytes=N and POST /__up.</summary>
sealed class StubServer : IAsyncDisposable
{
    readonly WebApplication web;
    public string Url { get; }

    StubServer(WebApplication web, string url, int[] counters) { this.web = web; Url = url; this.counters = counters; }

    public static async Task<StubServer> StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = b.Build();
        app.MapGet("/__down", async (HttpContext c) =>
        {
            long n = long.Parse(c.Request.Query["bytes"]!);
            c.Response.ContentLength = n;
            var blk = new byte[65536];
            try { while (n > 0) { int k = (int)Math.Min(n, blk.Length); await c.Response.Body.WriteAsync(blk.AsMemory(0, k), c.RequestAborted); n -= k; } }
            catch (OperationCanceledException) { }
        });
        var counters = new int[4] { 0, 0, 0, 200 };   // started, exact, wrong, status to answer
        app.MapPost("/__up", async (HttpContext c) =>
        {
            Interlocked.Increment(ref counters[0]);
            var buf = new byte[65536];
            long read = 0, declared = c.Request.ContentLength ?? -1;
            try
            {
                int k;
                while ((k = await c.Request.Body.ReadAsync(buf, c.RequestAborted)) > 0) read += k;
                Interlocked.Increment(ref counters[read == declared ? 1 : 2]);
            }
            catch (OperationCanceledException) { }
            c.Response.StatusCode = Volatile.Read(ref counters[3]);
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new StubServer(app, url, counters);
    }

    readonly int[] counters;
    public int UploadsStarted => Volatile.Read(ref counters[0]);
    public int UploadsExact => Volatile.Read(ref counters[1]);
    public int UploadsWrong => Volatile.Read(ref counters[2]);
    public int UploadStatus { set => Volatile.Write(ref counters[3], value); }

    public async ValueTask DisposeAsync() { await web.StopAsync(); await web.DisposeAsync(); }
}

public class LoadTestTests
{
    static Recorder NewRec() => new(new SessionStore(Tmp.Dir()));

    static LoadConfig Cfg(string url, int capDown, int capUp) => new()
    {
        BaseUrl = url, Streams = 2, CapDownMb = capDown, CapUpMb = capUp,
        Phases = new() { new() { Name = "idle", DurationS = 1 }, new() { Name = "download", DurationS = 4, Direction = "down" }, new() { Name = "recovery1", DurationS = 1 }, new() { Name = "upload", DurationS = 4, Direction = "up" } },
    };

    [Fact]
    public async Task PhasesVolumeCapAndSeries()
    {
        await using var stub = await StubServer.StartAsync();
        var rec = NewRec();
        var lt = new LoadTest(rec, Cfg(stub.Url, 30, 30));
        lt.Start();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(40));
        Assert.Equal("done", lt.State);
        Assert.Equal(new[] { "idle", "download", "recovery1", "upload" }, rec.Status().Phases.Select(p => p.Name).ToArray());
        var res = lt.Status().Results;
        Assert.True(res[1].VolumeCapReached == true && res[3].VolumeCapReached == true);
        // the cap is a promise about the user's data: each read or write first reserves its bytes from one shared budget,
        // so the counted volume is exactly the cap, with several streams, in both directions
        const long cap = 30_000_000L;
        Assert.Equal(cap, res[1].Bytes!.Value);
        Assert.Equal(cap, res[3].Bytes!.Value);
        Assert.True(res[1].DurationS < 4.5);
        Assert.True(res[1].AvgMbps > 1);
        var live = rec.LiveSince(0);
        Assert.True(live.ContainsKey("load:down_bps") && live.ContainsKey("load:up_bps"));
    }

    [Fact]
    public async Task UploadBodiesMatchTheirContentLengthAndCauseNoErrors()
    {
        await using var stub = await StubServer.StartAsync();
        var cfg = new LoadConfig
        {
            BaseUrl = stub.Url, Streams = 1, CapDownMb = 100000, CapUpMb = 30,
            Phases = new() { new() { Name = "upload", DurationS = 4, Direction = "up" } },
        };
        var lt = new LoadTest(NewRec(), cfg);
        lt.Start();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(30));
        var r = lt.Status().Results.Single();
        Assert.True(r.Errors is null || r.Errors.Count == 0, string.Join(" | ", r.Errors ?? new()));
        Assert.True(stub.UploadsStarted >= 2, "several complete POSTs were expected");
        Assert.Equal(stub.UploadsStarted, stub.UploadsExact);   // every body had exactly the announced length
        Assert.Equal(0, stub.UploadsWrong);
    }

    sealed class TrackedHandler : DelegatingHandler
    {
        public TrackedHandler() : base(new SocketsHttpHandler()) { }
        public volatile bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task TheHttpClientIsReleasedWhenTheTestEnds()
    {
        var handler = new TrackedHandler();
        var cfg = new LoadConfig { BaseUrl = "http://127.0.0.1:1", Phases = new() { new() { Name = "idle", DurationS = 1 } } };
        var lt = new LoadTest(NewRec(), cfg, handler);
        lt.Start();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(handler.Disposed, "sockets and handler must be released at the end of the run");
        lt.Cancel();   // a late Cancel on a finished test must stay harmless
        Assert.Equal("done", lt.State);
    }

    [Fact]
    public async Task APhaseInterruptedBeforeItsFirstSampleStillHasARate()
    {
        await using var stub = await StubServer.StartAsync();
        var cfg = new LoadConfig
        {
            BaseUrl = stub.Url, Streams = 2, CapDownMb = 2000, CapUpMb = 2000,
            Phases = new() { new() { Name = "download", DurationS = 20, Direction = "down" } },
        };
        var lt = new LoadTest(NewRec(), cfg);
        lt.Start();
        await Task.Delay(600);        // before the first one-second sample
        lt.Cancel();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(20));
        var r = lt.Status().Results.Single();
        Assert.True(r.DurationS < 1.0, $"stopped after {r.DurationS} s: the test needs the stop to come before the first sample");
        Assert.True(r.Bytes > 0 && r.AvgMbps > 0);
        Assert.True(r.PeakMbps > 0, "peak is 0 although the phase moved bytes: its last, partial interval was dropped");   // red before
        Assert.True(r.SustainedMbps > 0);
    }

    [Fact]
    public async Task AnInstanceRunsOnceASecondStartIsRefused()
    {
        await using var stub = await StubServer.StartAsync();
        var cfg = new LoadConfig { BaseUrl = stub.Url, Phases = new() { new() { Name = "idle", DurationS = 1 } } };
        var lt = new LoadTest(NewRec(), cfg);
        lt.Start();
        var first = lt.Task;
        Assert.Throws<InvalidOperationException>(() => lt.Start());   // red before: a second run started on the same state
        Assert.Same(first, lt.Task);
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("done", lt.State);
        Assert.Throws<InvalidOperationException>(() => lt.Start());   // a finished instance is not restarted either: build a new one
        Assert.Equal("done", lt.State);
    }

    [Fact]
    public async Task CurrentRateFallsToZeroWhenALoadPhaseEnds()
    {
        await using var stub = await StubServer.StartAsync();
        var lt = new LoadTest(NewRec(), Cfg(stub.Url, 100000, 100000));
        lt.Start();
        double? seen = null;
        var until = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < until && seen is null)
        {
            var s = lt.Status();
            if (s.Phase == "recovery1") seen = s.CurrentMbps;
            await Task.Delay(50);
        }
        lt.Cancel();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0.0, seen);   // the recovery phase must not show the throughput of the load that just ended
    }

    [Fact]
    public async Task ARefusedUploadIsReportedNotCountedAsASuccess()
    {
        await using var stub = await StubServer.StartAsync();
        stub.UploadStatus = 429;
        var cfg = new LoadConfig
        {
            BaseUrl = stub.Url, Streams = 1, CapDownMb = 100000, CapUpMb = 30,
            Phases = new() { new() { Name = "upload", DurationS = 5, Direction = "up" } },
        };
        var lt = new LoadTest(NewRec(), cfg);
        lt.Start();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(30));
        var r = lt.Status().Results.Single();
        Assert.NotNull(r.Errors);
        Assert.Contains(r.Errors!, e => e.Contains("HTTP 429"));
    }

    [Fact]
    public async Task TimeLimitWhenNoCapReached()
    {
        await using var stub = await StubServer.StartAsync();
        var lt = new LoadTest(NewRec(), Cfg(stub.Url, 100000, 100000));
        lt.Start();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(40));
        var r = lt.Status().Results[1];
        Assert.True(r.DurationS < 6);
        Assert.False(r.VolumeCapReached);
    }

    [Fact]
    public async Task CancelStopsQuicklyAndSkipsLaterPhases()
    {
        await using var stub = await StubServer.StartAsync();
        var rec = NewRec();
        var lt = new LoadTest(rec, Cfg(stub.Url, 100000, 100000));
        lt.Start();
        // wait for the state we want to cancel in (the download phase is running), not for a fixed delay a slow machine could miss
        var until = DateTime.UtcNow.AddSeconds(30);
        while (lt.Status().Phase != "download" && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal("download", lt.Status().Phase);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lt.Cancel();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(sw.Elapsed.TotalSeconds < 8, $"cancelling took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("cancelled", lt.State);
        Assert.Contains(rec.Status().Phases, p => p.Meta.Cancelled == true);
        Assert.DoesNotContain(rec.Status().Phases, p => p.Name == "upload");
    }

    [Fact]
    public async Task UnreachableServerDoesNotCrash()
    {
        var lt = new LoadTest(NewRec(), Cfg("http://127.0.0.1:1", 600, 200));
        lt.Start();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(40));
        Assert.Equal("done", lt.State);
        Assert.NotNull(lt.Status().Results[1].Errors);
        Assert.Equal(0, lt.Status().Results[1].Bytes);
    }

    [Fact]
    public void EstimateShowsCapsAndTypical()
    {
        var e = LoadTest.Estimate(new LoadConfig(), 300, 30);
        Assert.Equal(800, e.MaxTotalMb);
        Assert.Equal(300.0 / 8 * 15, e.TypicalDownMb);
        Assert.Equal(30.0 / 8 * 15, e.TypicalUpMb);
        Assert.Null(LoadTest.Estimate(new LoadConfig()).TypicalTotalMb);
        Assert.Equal(60, e.DurationS);
    }
}

public class ReportTests
{
    static string Html(string scn, AppConfig? cfg = null, Action<SessionData>? tweak = null)
    {
        var d = Simulator.Make(scn);
        tweak?.Invoke(d);
        return Report.Html(d, Diagnose.Analyze(d, cfg ?? new AppConfig()), cfg);
    }

    /// <summary>Everything a page would LOAD from outside itself: src/srcset/poster attributes, a stylesheet or preload link,
    /// CSS url() and @import, whether the address is http, https or protocol-relative (//host). data: URIs and #fragments are inside the document.
    /// Plain navigation links (a href) are not loads and are not reported.</summary>
    internal static List<string> ExternalLoads(string html)
    {
        var found = new List<string>();
        bool Outside(string v) { v = v.Trim().Trim('\'', '"'); return v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("//"); }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"\b(?:src|srcset|poster)\s*=\s*(""[^""]*""|'[^']*')", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            if (Outside(m.Groups[1].Value)) found.Add(m.Value);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"<link\b[^>]*\bhref\s*=\s*(""[^""]*""|'[^']*')", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            if (Outside(m.Groups[1].Value)) found.Add(m.Value);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"url\(\s*([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            if (Outside(m.Groups[1].Value)) found.Add(m.Value);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"@import\s+(?:url\()?\s*(""[^""]*""|'[^']*')", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            if (Outside(m.Groups[1].Value)) found.Add(m.Value);
        return found;
    }

    [Fact]
    public void TheDetectorOfExternalLoadsReallyDetects()
    {
        Assert.NotEmpty(ExternalLoads("<script src=\"https://cdn.example.com/x.js\"></script>"));
        Assert.NotEmpty(ExternalLoads("<script src='//cdn.example.com/x.js'></script>"));
        Assert.NotEmpty(ExternalLoads("<link rel=\"stylesheet\" href=\"https://fonts.example.com/f.css\">"));
        Assert.NotEmpty(ExternalLoads("<img src=\"http://tracker.example.com/p.gif\">"));
        Assert.NotEmpty(ExternalLoads("<style>@import url('https://x.example.com/a.css'); body{background:url(https://x.example.com/b.png)}</style>"));
        Assert.Empty(ExternalLoads("<img src=\"data:image/png;base64,AAAA\"><a href=\"https://www.tp-link.com/support/\">docs</a><svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"));
    }

    [Fact]
    public void SectionsAndNoCertainty()
    {
        foreach (var scn in new[] { "wifi_unstable", "bufferbloat", "isp", "dns", "healthy", "icmp_blocked" })
        {
            var html = Html(scn);
            foreach (var s in new[] { "Network diagnosis report", "Incident timeline", "<svg", "Measurements", "Environment and limits", "not confirmed causes" })
                Assert.True(html.Contains(s), $"{scn}: {s}");
            Assert.Empty(ExternalLoads(html));   // the report is private and works offline: nothing is fetched from outside
        }
    }

    [Fact]
    public void TheWebPageLoadsNothingFromOutsideEither()
    {
        var page = System.Text.Encoding.UTF8.GetString(Api.IndexBytes);
        Assert.True(page.Length > 10_000);
        Assert.Empty(ExternalLoads(page));
    }

    [Fact]
    public void HtmlEscapingOfUserText()
    {
        var html = Html("healthy", null, d => { d.Label = "<script>alert(1)</script>"; d.Targets[^1].Host = "<img src=x onerror=alert(1)>"; });
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
    }

    [Fact]
    public void RouterSectionHasProposalsAndProtocol()
    {
        var html = Html("bufferbloat", new AppConfig { Router = new RouterConfig { Model = "X", QosEnabled = true, QosType = "priority", LimitDown = 900, LimitUp = 90 } });
        Assert.Contains("PROPOSED changes", html);
        Assert.Contains("Rollback:", html);
        Assert.Contains("Before/after protocol", html);
    }

    [Fact]
    public void CsvAndJsonExports()
    {
        var d = Simulator.Make("wifi_unstable");
        var a = Diagnose.Analyze(d, new AppConfig());
        var rows = Report.ExportCsv(d).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("session,local_time,epoch_s,series,value,ok,info", rows[0]);
        Assert.True(rows.Length > 4000);
        Assert.Contains(rows, r => r.Contains(",mark:lag,"));
        var j = JsonNode.Parse(Report.ExportJson(d, a))!.AsObject();
        Assert.Equal(new[] { "analysis", "definitions", "marks", "measurements", "phases", "session", "traceroutes" }, j.Select(kv => kv.Key).OrderBy(x => x).ToArray());
        Assert.True(j["measurements"]!.AsObject().ContainsKey("ping:gateway"));
        Assert.True(j["definitions"]!.AsObject().ContainsKey("jitter"));
    }

    [Fact]
    public void CsvProtectsAgainstSpreadsheetFormulas()
    {
        var d = Simulator.Make("healthy");
        d.Series["ping:gateway"].Add(new Sample(Simulator.T0 + 1, null, false, "=HYPERLINK(\"http://x\")"));
        Assert.Contains("'=HYPERLINK", Report.ExportCsv(d));
    }

    [Fact]
    public void EveryReportChartHasAnAccessibleName()
    {
        var d = Simulator.Make("wifi_unstable");
        var html = Report.Html(d, Diagnose.Analyze(d, new AppConfig()));
        var charts = System.Text.RegularExpressions.Regex.Matches(html, "<svg [^>]*>");
        Assert.True(charts.Count >= 3);
        foreach (System.Text.RegularExpressions.Match m in charts)
            Assert.Matches("aria-label=\"[^\"]{5,}\"", m.Value);
        Assert.Contains("<title>", html);
    }

    /// <summary>A strict reader for the CSV the tool writes (RFC 4180 quoting): records and fields, quotes unescaped.</summary>
    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) { quoted = true; any = true; }
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); any = true; }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new(); any = false; }
            else field.Append(c);
        }
        if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    [Fact]
    public void EveryMarkKindComesBackAsOneRecordOfSevenFieldsWhenTheCsvIsParsed()
    {
        var d = Simulator.Make("healthy", 7, p => p.Minutes = 1);
        var kinds = new[] { "lag", "a,b", "say \"hi\"", "two\nlines", "cr\rlf", "=HYPERLINK(\"x\")", "-minus", "@at", "plain" };
        foreach (var (k, i) in kinds.Select((k, i) => (k, i))) d.Marks.Add(new Mark { T = Simulator.T0 + 10 + i, Kind = k, Note = $"note,{i}\n\"q\"" });
        var rows = ParseCsv(Report.ExportCsv(d));
        Assert.All(rows, r => Assert.Equal(7, r.Count));                                   // no row is split or shifted, header included
        var marks = rows.Where(r => r[3].StartsWith("mark:")).ToList();
        Assert.Equal(d.Marks.Count, marks.Count);                                           // one record per mark, the simulator's own included
        foreach (var (k, i) in kinds.Select((k, i) => (k, i)))                              // each of mine comes back intact, with its own note
            Assert.Contains(marks, m => m[3] == "mark:" + k && m[6] == $"note,{i}\n\"q\"");
    }

    [Fact]
    public void TimelineShowsTheDateOnlyWhenTheSessionSpansSeveralDays()
    {
        var d = Simulator.Make("healthy");
        var a = Diagnose.Analyze(d, new AppConfig());
        var timeCell = new System.Text.RegularExpressions.Regex(@"<tr><td>((\d{4}-\d\d-\d\d )?\d\d:\d\d:\d\d)</td><td>");
        var oneDay = timeCell.Matches(Report.Html(d, a));
        Assert.NotEmpty(oneDay);
        Assert.All(oneDay, m => Assert.False(m.Groups[2].Success));
        d.Ended = d.Started + 2 * 86400;
        var severalDays = timeCell.Matches(Report.Html(d, a));
        Assert.NotEmpty(severalDays);
        Assert.All(severalDays, m => Assert.True(m.Groups[2].Success));
    }

    [Fact]
    public void CsvMarkKindIsQuotedAsAWholeField()
    {
        var d = Simulator.Make("healthy");
        d.Marks.Add(new Mark { T = Simulator.T0 + 5, Kind = "a,\"b\"", Note = "n" });
        var row = Report.ExportCsv(d).Split('\n').Single(l => l.Contains("mark:a"));
        Assert.Contains(",\"mark:a,\"\"b\"\"\",,,n", row);
    }

    [Fact]
    public void QosTypeIsShownInTheReaderLanguageNotAsACode()
    {
        var d = Simulator.Make("healthy");
        var a = Diagnose.Analyze(d, new AppConfig());
        foreach (var (lang, shown) in new[] { ("en", "(type: Rate limit)"), ("fr", "(type : Limite de débit)"), ("fr", "(type : Inconnu)") })
            using (Loc.Scope(lang))
            {
                var typ = shown.Contains("Inconnu") ? "unknown" : "bandwidth_limit";
                var cfg = new AppConfig { Router = new RouterConfig { Model = "X", QosType = typ } };
                var html = System.Net.WebUtility.HtmlDecode(Report.Html(d, a, cfg));
                Assert.Contains(shown, html);
                Assert.DoesNotContain("bandwidth_limit", html);
            }
    }

    [Fact]
    public void IncreaseColumnHasACleanSign()
    {
        var d = Simulator.Make("bufferbloat");
        var a = Diagnose.Analyze(d, new AppConfig());
        var down = a.Bufferbloat!.Directions["down"];
        var up = a.Bufferbloat.Directions["up"];
        down.Delta = -3; down.GwDelta = -1;
        up.Delta = null; up.GwDelta = 4;
        var html = Report.Html(d, a);
        Assert.DoesNotContain("+-", html);
        Assert.DoesNotContain("+—", html);
        Assert.Contains("<td class='n'>-3 ms</td>", html);
        Assert.Contains("<td class='n'>-1 ms</td>", html);
        Assert.Contains("<td class='n'>+4 ms</td>", html);
    }

    [Fact]
    public void LateLossesAreDrawnToo()
    {
        var lost = Enumerable.Range(0, 600).Select(i => i + 0.5).ToList();   // a loss every second for 10 minutes
        var svg = Report.SvgChart(new() { new Report.ChartSeries { Name = "a", Pts = new() { (1, 5.0) }, Lost = lost } }, 0, 600);
        var ticks = System.Text.RegularExpressions.Regex.Matches(svg, "stroke-width=\"1.5\"");
        Assert.Equal(430, ticks.Count);   // one tick per chart column, from the first to the last
        Assert.Contains("x1=\"85" , svg);  // the right-hand end (x ≈ 854) is reached: the old cut-off stopped near x = 600
    }

    [Fact]
    public void YAxisLabelsKeepTheirDecimalsOnSmallScales()
    {
        string Chart(double v) => Report.SvgChart(new() { new Report.ChartSeries { Name = "a", Pts = new() { (1, v), (2, v / 2) } } }, 0, 10, unit: "Mbps");
        Assert.Contains(">0.43<", Chart(0.4));   // top label of a 0.43 scale, not "0"
        Assert.Contains(">1.6<", Chart(1.5));    // 1.62 scale: one decimal
        Assert.Contains(">108<", Chart(100));    // large scale: integers
    }

    [Fact]
    public void AChartMadeOnlyOfFailuresIsDrawnNotReportedAsNoData()
    {
        var onlyLoss = Report.SvgChart(new() { new Report.ChartSeries { Name = "gateway", Lost = new() { 1.0, 2.0, 5.0, 9.0 } } }, 0, 10);
        Assert.DoesNotContain("No data for this chart", onlyLoss);
        Assert.Contains("<svg", onlyLoss);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(onlyLoss, "stroke-width=\"1.5\"").Count);   // one tick per failed second
        Assert.Contains("No data for this chart", Report.SvgChart(new() { new Report.ChartSeries { Name = "a" } }, 0, 10));   // truly nothing: still says so
    }

    [Fact]
    public void ATotalOutageShowsItsFailuresInTheLatencyChartWhileOneFilteredTargetDoesNot()
    {
        // every target silent: a session of nothing but failures. The chart must show them, not claim there was no measurement.
        var outage = Simulator.Make("healthy", 7, p => { p.Minutes = 2; p.IcmpBlocked.AddRange(new[] { "gateway", "cloudflare", "google", "quad9", "custom" }); });
        var html = Report.Html(outage, Diagnose.Analyze(outage, new AppConfig()));
        var latency = html[html.IndexOf("<h3>Latency", StringComparison.Ordinal)..];
        latency = latency[..latency.IndexOf("</svg>", StringComparison.Ordinal)];
        Assert.Contains("stroke-width=\"1.5\"", latency);
        // one target that never answers while the others do is filtered ICMP, not loss: no ticks for it
        var oneBlocked = Simulator.Make("healthy", 7, p => { p.Minutes = 2; p.IcmpBlocked.Add("custom"); });
        var ticksBlocked = System.Text.RegularExpressions.Regex.Matches(Report.Html(oneBlocked, Diagnose.Analyze(oneBlocked, new AppConfig())), "stroke=\"#cf222e\" stroke-width=\"1.5\"").Count;   // #cf222e is the custom target's colour
        Assert.Equal(0, ticksBlocked);
    }

    [Fact]
    public void ASignedChangeHasNoSignWhenItIsZeroOrRoundsToZero()
    {
        var d = Simulator.Make("bufferbloat");
        var a = Diagnose.Analyze(d, new AppConfig());
        var down = a.Bufferbloat!.Directions["down"]; var up = a.Bufferbloat.Directions["up"];
        down.Delta = 0; down.GwDelta = 0.4;                  // exactly zero; and one that is shown as 0 ms
        up.Delta = 0.6; up.GwDelta = -0.4;                    // rounds to +1; and one that rounds to -0
        var html = Report.Html(d, a);
        Assert.DoesNotContain("+0 ms", html);
        Assert.DoesNotContain("-0 ms", html);
        Assert.DoesNotContain("+-", html);
        Assert.Contains("<td class='n'>0 ms</td>", html);
        Assert.Contains("<td class='n'>+1 ms</td>", html);
    }

    [Fact]
    public void TheTrafficChartAppearsWhenOnlyTheUploadWasMeasured()
    {
        var d = Simulator.Make("healthy", 7, p => p.Minutes = 2);
        d.Series.Remove("net:down_bps");                      // only upload samples exist
        var html = Report.Html(d, Diagnose.Analyze(d, new AppConfig()));
        var from = html.IndexOf("<h3>Traffic of this computer", StringComparison.Ordinal);
        Assert.True(from > 0, "the traffic chart is missing although upload samples exist");
        Assert.Contains("<polyline", html[from..html.IndexOf("</svg>", from, StringComparison.Ordinal)]);
        // and the mirror image, download only
        var e = Simulator.Make("healthy", 7, p => p.Minutes = 2);
        e.Series.Remove("net:up_bps");
        Assert.Contains("<h3>Traffic of this computer", Report.Html(e, Diagnose.Analyze(e, new AppConfig())));
        // with neither direction measured there is still no traffic chart
        var none = Simulator.Make("healthy", 7, p => p.Minutes = 2);
        none.Series.Remove("net:up_bps"); none.Series.Remove("net:down_bps");
        Assert.DoesNotContain("<h3>Traffic of this computer", Report.Html(none, Diagnose.Analyze(none, new AppConfig())));
    }

    [Fact]
    public void EveryChartCarriesATextAlternativeWithItsFigures()
    {
        var series = new List<Report.ChartSeries>
        {
            new() { Name = "Gateway", Pts = Enumerable.Range(0, 100).Select(i => ((double)i, i < 90 ? 2.0 : 40.0)).ToList(), Lost = new() { 10.0, 11.0, 12.0 } },
            new() { Name = "Silent", Pts = new(), Lost = new() },
        };
        string Svg(string lang) { using (Loc.Scope(lang)) return Report.SvgChart(series, 0, 100, title: "Latency", unit: "ms"); }
        var en = Svg("en");
        var m = System.Text.RegularExpressions.Regex.Match(en, "aria-describedby=\"([^\"]+)\"");
        Assert.True(m.Success, "the svg must point at its description");
        var desc = System.Text.RegularExpressions.Regex.Match(en, $"<desc id=\"{System.Text.RegularExpressions.Regex.Escape(m.Groups[1].Value)}\">([^<]*)</desc>");
        Assert.True(desc.Success, "and the description must exist under that id");
        Assert.Contains("Gateway: median 2 ms, maximum 40 ms, failed probes: 3", desc.Groups[1].Value);   // the figures of the curve and its failures
        Assert.DoesNotContain("Silent:", desc.Groups[1].Value);                                            // a curve with nothing in it is not described
        var fr = Svg("fr");
        Assert.Contains("Gateway : médiane 2 ms, maximum 40 ms, sondes échouées : 3", System.Net.WebUtility.HtmlDecode(fr));   // the markup encodes é as &#233;
        // two charts never share a description id (ids are unique in a document)
        var ids = Enumerable.Range(0, 3).Select(_ => System.Text.RegularExpressions.Regex.Match(Svg("en"), "aria-describedby=\"([^\"]+)\"").Groups[1].Value).ToList();
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void ACurveIsNotDrawnAcrossAHoleInTheMeasurements()
    {
        int Lines(string svg) => System.Text.RegularExpressions.Regex.Matches(svg, "<polyline").Count;
        List<(double, double)> Run(int a, int b) => Enumerable.Range(a, b - a).Select(i => ((double)i, 10.0 + i % 3)).ToList();
        string Chart(List<(double, double)> pts) => Report.SvgChart(new() { new Report.ChartSeries { Name = "x", Pts = pts } }, 0, 300);
        Assert.Equal(1, Lines(Chart(Run(0, 300))));                                          // continuous: one curve
        var holed = Run(0, 100).Concat(Run(200, 300)).ToList();                              // 100 s with nothing (a pause, or only failures)
        Assert.Equal(2, Lines(Chart(holed)));                                                // two curves, no line across the hole
        Assert.Equal(3, Lines(Chart(Run(0, 50).Concat(Run(100, 150)).Concat(Run(250, 300)).ToList())));
        // a small irregularity is not a hole: a missing second or two stays one curve
        Assert.Equal(1, Lines(Chart(Run(0, 100).Concat(Run(102, 300)).ToList())));
        // a series sampled slowly by design (every 5 s) is not cut into pieces
        Assert.Equal(1, Lines(Chart(Enumerable.Range(0, 60).Select(i => ((double)i * 5, 20.0)).ToList())));
        // an isolated point left alone between two holes is still shown
        Assert.Contains("<circle", Chart(Run(0, 50).Concat(new List<(double, double)> { (150, 12) }).Concat(Run(250, 300)).ToList()));
    }

    [Fact]
    public void LossesThatOnlyHappenAtTheEndOfALongSessionAreNotHidden()
    {
        // an hour of clean measurements, then a burst of 600 failures in the last ten minutes: the old cut-off kept the FIRST 400 in time order
        var lost = Enumerable.Range(0, 600).Select(i => 3000.0 + i).ToList();
        var svg = Report.SvgChart(new() { new Report.ChartSeries { Name = "gateway", Pts = Enumerable.Range(0, 3000).Select(i => ((double)i, 5.0)).ToList(), Lost = lost } }, 0, 3600);
        var xs = System.Text.RegularExpressions.Regex.Matches(svg, "<line x1=\"([0-9.]+)\" x2=\"[0-9.]+\" y1=\"[0-9.]+\" y2=\"[0-9.]+\" stroke=\"#555\" stroke-width=\"1.5\"")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(xs);
        Assert.True(xs.Min() > 0.8 * 860, $"the leftmost failure tick is at x={xs.Min()}: failures belong to the last sixth of the chart");
        Assert.True(xs.Max() > 0.95 * 860, $"the last tick is at x={xs.Max()}: the end of the burst must be there");
        Assert.InRange(xs.Count, 60, 85);     // one tick per chart column: 600 s of 3600 s over 430 columns is about 72, not 600 elements and not 400 early ones
    }

    [Fact]
    public void DnsFailuresAreChartedEvenWhenNoLookupEverSucceeded()
    {
        var d = Simulator.Make("healthy", 7, p => p.Minutes = 2);
        d.Series["dns:sys_hit"] = d.Series["dns:sys_hit"].Select(s => new Sample(s.T, null, false, "timeout")).ToList();   // DNS down all session
        var html = Report.Html(d, Diagnose.Analyze(d, new AppConfig()));
        var from = html.IndexOf("<h3>DNS resolution", StringComparison.Ordinal);
        Assert.True(from > 0, "the DNS chart section is missing");
        var section = html[from..];
        section = section[..section.IndexOf("</svg>", StringComparison.Ordinal)];
        Assert.Contains("stroke-width=\"1.5\"", section);   // the failed lookups are drawn
    }

    [Fact]
    public void ChartSurvivesEmptyAndSingleSeries()
    {
        Assert.Contains("No data for this chart", Report.SvgChart(new() { new Report.ChartSeries { Name = "a" } }, 0, 10));
        var lone = Report.SvgChart(new() { new Report.ChartSeries { Name = "a", Pts = new() { (1, 5.0) }, Lost = new() { 2.0 } } }, 0, 10);
        Assert.True(lone.Contains("<polyline") || lone.Contains("<circle"), "a single point must still be drawn (as a dot: a one-point line shows nothing)");
    }
}

public class StoreTests
{
    [Fact]
    public void SaveLoadRoundtripKeepsEverything()
    {
        var store = new SessionStore(Tmp.Dir());
        var d = Simulator.Make("bufferbloat");
        d.Traces.Add(new TraceRec { T = Simulator.T0 + 5, Target = "1.1.1.1", Data = new TraceResult { Hops = new() { new TraceHop { Hop = 1, Ip = "192.168.0.1", Rtts = new() { 0.5 }, Sent = 1 } }, Analysis = new TraceAnalysis { Reached = true } } });
        int id = store.SaveComplete(d);
        var l = store.Load(id)!;
        Assert.Equal(d.Series.Keys.OrderBy(x => x), l.Series.Keys.OrderBy(x => x));
        Assert.Equal(d.Series["ping:google"].Count, l.Series["ping:google"].Count);
        Assert.Equal(d.Phases.Count, l.Phases.Count);
        Assert.Equal("1.1.1.1", l.Traces[0].Target);
        Assert.Equal(d.Targets.Count, l.Targets.Count);
        Assert.Equal(Diagnose.Analyze(d, new AppConfig()).Hypotheses.Select(h => h.Id), Diagnose.Analyze(l, new AppConfig()).Hypotheses.Select(h => h.Id));
    }

    [Fact]
    public void TruncatedLastLineIsIgnoredAndDeleteRemovesBothFiles()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        int id = store.SaveComplete(Simulator.Make("healthy", 7, p => p.Minutes = 1));
        var before = store.Load(id)!;
        int samplesBefore = before.Series.Sum(kv => kv.Value.Count);
        Assert.True(samplesBefore > 100);   // there is something to lose
        File.AppendAllText(Path.Combine(dir, "sessions", $"{id}.jsonl"), "[\"s\", 12.5, \"ping:gat");
        var after = store.Load(id);
        Assert.NotNull(after);
        Assert.Equal(samplesBefore, after!.Series.Sum(kv => kv.Value.Count));          // the valid data is all still there
        Assert.Equal(before.Series.Keys.OrderBy(k => k), after.Series.Keys.OrderBy(k => k));   // no series lost, none invented from the half line
        Assert.Equal(before.Marks.Count, after.Marks.Count);
        store.Delete(id);
        Assert.Null(store.Load(id));
        // both session files are gone; only the id counter stays, on purpose (an id is never handed out again)
        Assert.Equal(new[] { "last_id.txt" }, Directory.GetFiles(Path.Combine(dir, "sessions")).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task WriterFlushesOnComplete()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        int id = store.Create(new SessionHeader { Started = 1000, Meta = new SessionMeta { Targets = Simulator.Targets.ToList() } });
        var w = store.OpenWriter(id);
        w.Sample(1001, "ping:gateway", 1.5, true, "");
        w.Mark(1002, "lag", "x");
        await w.CompleteAsync();
        var l = store.Load(id)!;
        Assert.Single(l.S("ping:gateway"));
        Assert.Single(l.Marks);
    }
}

public class SessionDataShapeTests
{
    [Fact]
    public void ComputedPropertiesAreNotSerialisedWithASession()
    {
        var json = Json.To(Simulator.Make("healthy", 7, p => p.Minutes = 1));
        Assert.DoesNotContain("end_or_last", json);
        // the targets appear once, inside meta
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"targets\":").Count);
    }
}

public class DynamicPortTests
{
    [Fact]
    public async Task PortZeroLetsTheSystemPickAFreePortAndTheServerAnswersOnIt()
    {
        var (web, port) = await Api.StartAsync(new App(Tmp.Dir()), 0);
        try
        {
            Assert.InRange(port, 1024, 65535);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var identity = await http.GetStringAsync("/api/identity");   // the Host header 127.0.0.1:<port> is accepted
            Assert.True(Launcher.IsNetworkAnalyzer(identity));
        }
        finally { await web.StopAsync(); await web.DisposeAsync(); }
    }

    [Fact]
    public async Task TwoServersStartedTogetherNeverShareAPort()
    {
        var starts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Api.StartAsync(new App(Tmp.Dir()), 0)));
        try { Assert.Equal(6, starts.Select(s => s.Port).Distinct().Count()); }
        finally { foreach (var (web, _) in starts) { await web.StopAsync(); await web.DisposeAsync(); } }
    }
}

public class StartupMessageTests
{
    [Fact]
    public async Task NoFreePortSaysSoInTheReadersLanguage()
    {
        int p = 0;
        var taken = new List<System.Net.Sockets.TcpListener>();
        try
        {
            // two ADJACENT free ports, found by asking the system rather than guessing a range
            for (int attempt = 0; attempt < 50 && taken.Count < 2; attempt++)
            {
                foreach (var l in taken) l.Stop();
                taken.Clear();
                var first = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); first.Start();
                taken.Add(first);
                p = ((IPEndPoint)first.LocalEndpoint).Port;
                try { var second = new System.Net.Sockets.TcpListener(IPAddress.Loopback, p + 1); second.Start(); taken.Add(second); }
                catch (System.Net.Sockets.SocketException) { }
            }
            Assert.Equal(2, taken.Count);
            var app = new App(Tmp.Dir());
            var en = await Assert.ThrowsAsync<InvalidOperationException>(() => Api.StartAsync(app, p, 2));
            Assert.Equal($"No free port between {p} and {p + 1}.", en.Message);
            using (Loc.Scope("fr"))
            {
                var fr = await Assert.ThrowsAsync<InvalidOperationException>(() => Api.StartAsync(app, p, 2));
                Assert.Equal($"Aucun port libre entre {p} et {p + 1}.", fr.Message);
            }
        }
        finally { foreach (var l in taken) l.Stop(); }
    }
}

public class RequestNumberTests
{
    [Theory]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("-Infinity")] [InlineData("1e999")]
    public void NonFiniteTextIsRejected(string text) =>
        Assert.Throws<ApiException>(() => App.Num(System.Text.Json.Nodes.JsonValue.Create(text)));

    [Fact]
    public void NonFiniteNumbersAreRejectedToo()
    {
        Assert.Throws<ApiException>(() => App.Num(System.Text.Json.Nodes.JsonValue.Create(double.NaN)));
        Assert.Throws<ApiException>(() => App.Num(System.Text.Json.Nodes.JsonValue.Create(double.PositiveInfinity)));
    }

    [Fact]
    public void OrdinaryNumbersStillWork()
    {
        Assert.Equal(12.5, App.Num(System.Text.Json.Nodes.JsonValue.Create("12.5")));
        Assert.Equal(-3, App.Num(System.Text.Json.Nodes.JsonValue.Create(-3.0)));
        Assert.Null(App.Num(null));
    }
}

public class RouterConfigValuesTests
{
    [Fact]
    public void UnexpectedValuesInAHandEditedFileBecomeUnknown()
    {
        var dir = Tmp.Dir();
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\"router\":{\"qos_type\":\"bandwith_limit\",\"sqm_available\":\"maybe\",\"qos_enabled\":true}}");
        var r = new ConfigStore(dir).Load().Router!;
        Assert.Equal("unknown", r.QosType);          // a typo is not a type the analysis has to cope with
        Assert.Equal("unknown", r.SqmAvailable);
        Assert.True(r.QosEnabled);                   // the rest is kept
    }

    [Theory]
    [InlineData("priority", "yes")] [InlineData("bandwidth_limit", "no")] [InlineData("sqm", "unknown")] [InlineData("unknown", null)]
    public void DocumentedValuesAreKept(string type, string? sqm)
    {
        var dir = Tmp.Dir();
        var store = new ConfigStore(dir);
        store.Save(new AppConfig { Router = new RouterConfig { QosType = type, SqmAvailable = sqm } });
        var r = store.Load().Router!;
        Assert.Equal(type, r.QosType);
        Assert.Equal(sqm, r.SqmAvailable);
    }

    [Fact]
    public void SavingAnInvalidSqmValueIsRefused()
    {
        var app = new App(Tmp.Dir());
        var body = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("{\"router\":{\"qos_type\":\"sqm\",\"sqm_available\":\"maybe\"}}")!;
        Assert.Throws<ApiException>(() => app.SaveConfig(body));
    }
}

public class DurableFileTests
{
    [Fact]
    public void WritesReplacesAndLeavesNoTemporaryFile()
    {
        var dir = Tmp.Dir();
        var path = Path.Combine(dir, "a.json");
        DurableFile.WriteAllText(path, "{\"v\":1}");
        DurableFile.WriteAllText(path, "{\"v\":2,\"é\":\"ü\"}");
        Assert.Equal("{\"v\":2,\"é\":\"ü\"}", File.ReadAllText(path));
        Assert.Equal(new[] { "a.json" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        Assert.Equal(new byte[] { (byte)'{' }, File.ReadAllBytes(path)[..1]);   // UTF-8 without a BOM
    }

    [Fact]
    public void ConfigAndSessionHeadersGoThroughIt()
    {
        var dir = Tmp.Dir();
        new ConfigStore(dir).Save(new AppConfig { CustomTarget = "x.example.net" });
        var store = new SessionStore(dir);
        int id = store.Create(new SessionHeader { Started = Simulator.T0, Meta = new SessionMeta() });
        store.SaveHeader(store.LoadHeader(id)!);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal("x.example.net", new ConfigStore(dir).Load().CustomTarget);
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void ACorruptConfigIsKeptAsABackupBeforeItIsReplaced()
    {
        var dir = Tmp.Dir();
        const string broken = "{ \"custom_target\": \"game.example.net:27015\", \"router\": { \"model\": \"Archer";   // truncated hand edit
        File.WriteAllText(Path.Combine(dir, "config.json"), broken);
        var store = new ConfigStore(dir);
        Assert.Equal("", store.Load().CustomTarget);                       // the app still starts, on defaults
        var backup = Directory.GetFiles(dir, "config.json.corrupt-*").Single();
        Assert.Equal(broken, File.ReadAllText(backup));                    // what the user typed is not lost
        store.Update(c => c.CustomTarget = "new.example.net");             // the next save overwrites config.json…
        Assert.Equal(broken, File.ReadAllText(backup));                    // …but never the backup
        Assert.Single(Directory.GetFiles(dir, "config.json.corrupt-*"));   // one backup, not one per read
    }

    [Fact]
    public void AnUnreadableConfigFallsBackToDefaultsInsteadOfCrashing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;   // needs a file this account cannot read
        var dir = Tmp.Dir();
        var file = Path.Combine(dir, "config.json");
        File.WriteAllText(file, "{\"custom_target\":\"keep.example.net\"}");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            Assert.Equal("", new ConfigStore(dir).Load().CustomTarget);   // used to throw UnauthorizedAccessException
        }
        finally { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        Assert.Equal("keep.example.net", new ConfigStore(dir).Load().CustomTarget);   // the file itself was never touched
    }

    [Fact]
    public void AMissingConfigIsNormalAndLeavesNoBackup()
    {
        var dir = Tmp.Dir();
        Assert.Equal("", new ConfigStore(dir).Load().CustomTarget);
        Assert.Empty(Directory.GetFiles(dir, "config.json.corrupt-*"));
    }
}

public class SessionIdTests
{
    static SessionHeader H() => new() { Started = Simulator.T0, Meta = new SessionMeta() };

    [Fact]
    public void AnIdIsNeverReusedAfterTheLastSessionIsDeleted()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        int a = store.Create(H()), b = store.Create(H());
        Assert.Equal(a + 1, b);
        store.Delete(b);
        int c = store.Create(H());
        Assert.True(c > b, $"id {b} was handed out again ({c})");
        store.Delete(c);
        int d = new SessionStore(dir).Create(H());   // and not after a restart either
        Assert.True(d > c, $"id {c} was handed out again after a restart ({d})");
    }

    [Fact]
    public void ADataFolderWithoutTheCounterStillNumbersFromItsSessions()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        store.Create(H()); store.Create(H());
        File.Delete(Directory.EnumerateFiles(Path.Combine(dir, "sessions"), "last_id*").Single());   // a folder written by an older version
        Assert.Equal(3, new SessionStore(dir).Create(H()));
    }

    [Fact]
    public async Task ConcurrentCreationsGetDistinctIds()
    {
        var store = new SessionStore(Tmp.Dir());
        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.Create(H()))));
        Assert.Equal(20, ids.Distinct().Count());
    }
}

public class WriterFailureTests
{
    [Fact]
    public async Task ADiskWriteFailureIsReportedAndStopsTheBuffering()
    {
        // the folder does not exist: opening the file fails inside the writer's background task
        var w = new SessionWriter(Path.Combine(Tmp.Dir(), "no", "such", "folder", "1.jsonl"));
        w.Sample(1, "ping:x", 1.0, true, "");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (w.Failure is null && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.IsAssignableFrom<IOException>(w.Failure);
        Assert.False(w.IsAccepting, "after a failure nothing more is queued: memory must not grow without limit");
        for (int i = 0; i < 1000; i++) w.Sample(i, "ping:x", 1.0, true, "");   // harmless
        await w.CompleteAsync();   // and finishing does not throw
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public async Task ANonFiniteMeasurementDoesNotThrowOnTheMeasuringThread(double value)
    {
        var path = Path.Combine(Tmp.Dir(), "1.jsonl");
        var w = new SessionWriter(path);
        w.Sample(1, "net:down_bps", value, true, "");      // used to throw ArgumentException from System.Text.Json
        await w.CompleteAsync();
        var line = File.ReadAllLines(path).Single();
        Assert.Contains("null", line);

        var rec = new Recorder(new SessionStore(Tmp.Dir()));
        rec.Start(new EnvInfo(), new List<Target>(), 1);
        rec.Emit("net:down_bps", value, true);              // nor from the recorder, which also keeps it out of the live statistics
        var s = rec.LiveSince(0)["net:down_bps"].Single();
        Assert.Null(s[1]);
        Assert.Equal(0, s[2]);
        await rec.StopAsync();
    }

    [Fact]
    public async Task AHealthyWriterStaysHealthy()
    {
        var path = Path.Combine(Tmp.Dir(), "1.jsonl");
        var w = new SessionWriter(path);
        w.Sample(1, "ping:x", 1.0, true, "");
        await w.CompleteAsync();
        Assert.Null(w.Failure);
        Assert.Single(File.ReadAllLines(path));
    }
}

public class StoreToleranceTests
{
    [Fact]
    public void MalformedButValidJsonLinesAreSkippedNotFatal()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        var original = Simulator.Make("healthy", 7, p => p.Minutes = 1);
        int id = store.SaveComplete(original);
        var path = Path.Combine(dir, "sessions", $"{id}.jsonl");
        var good = File.ReadAllLines(path).ToList();
        var bad = new[] { "5", "[]", "[\"s\"]", "[\"s\", \"x\", 1, 2, 3, 4]", "{\"a\":1}", "[\"s\", 1.0, null, 2, 1, \"\"]", "[\"m\", 1.0]", "[\"p\", \"x\"]", "null", "\"text\"" };
        // bad lines mixed in the middle and at both ends
        var mixed = new List<string> { bad[0] };
        for (int i = 0; i < good.Count; i++) { mixed.Add(good[i]); if (i % 50 == 0) mixed.Add(bad[(i / 50) % bad.Length]); }
        mixed.AddRange(bad);
        File.WriteAllLines(path, mixed);
        var loaded = store.Load(id)!;
        Assert.Equal(original.Series.Sum(kv => kv.Value.Count), loaded.Series.Sum(kv => kv.Value.Count));   // every good line is still there
        Assert.Equal(original.Marks.Count, loaded.Marks.Count);
    }
}

public class SessionListTests
{
    [Fact]
    public void OneUnreadableSessionDoesNotHideTheOthers()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        int good = store.SaveComplete(Simulator.Make("healthy", 7, p => p.Minutes = 1));
        int bad = store.SaveComplete(Simulator.Make("healthy", 8, p => p.Minutes = 1));
        File.WriteAllText(Path.Combine(dir, "sessions", $"{bad}.jsonl"), "5\n");   // valid JSON, not a measurement line
        var list = new App(dir).Sessions();
        Assert.Equal(new[] { bad, good }, list.Select(s => s.Id).ToArray());
        Assert.NotNull(list.Single(s => s.Id == good).Metrics);
    }
}

public class LoadtestStartTests
{
    static System.Text.Json.Nodes.JsonObject Body() => new() { ["confirm"] = true, ["phase_s"] = 5, ["cap_down_mb"] = 10, ["cap_up_mb"] = 10 };

    [Fact]
    public async Task ASecondStartWaitsForTheFirstAndIsThenRefused()
    {
        var app = new App(Tmp.Dir());
        Task<LoadEstimate>? second = null;
        app.AfterLoadtestCheck = () =>
        {
            app.AfterLoadtestCheck = null;
            second = Task.Run(() => app.StartLoadtest(Body()));   // a simultaneous request, in the middle of the first one
            Thread.Sleep(400);
            Assert.False(second.IsCompleted, "the second start ran in parallel instead of waiting");
        };
        app.StartLoadtest(Body());
        var refused = await Assert.ThrowsAsync<ApiException>(() => second!);
        Assert.Equal(409, refused.Code);
        await app.StopSessionAsync();
    }
}

public class ConfigInputTests
{
    [Theory]
    [InlineData("{\"custom_target\": \"new.example.net\", \"minutes\": \"abc\"}")]
    [InlineData("{\"custom_target\": \"new.example.net\", \"link\": \"satellite\"}")]
    public void ARefusedStartLeavesTheSavedSettingsAlone(string json)
    {
        var app = new App(Tmp.Dir());
        var before = app.Config.Load().CustomTarget;
        Assert.Throws<ApiException>(() => app.StartSession((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!));
        Assert.Equal(before, app.Config.Load().CustomTarget);
    }

    [Theory]
    [InlineData("{\"custom_target\": 5}")] [InlineData("{\"gateway_override\": true}")] [InlineData("{\"custom_target\": {\"a\": 1}}")]
    public void ANonTextAddressIsABadRequestNotAServerError(string json) =>
        Assert.Throws<ApiException>(() => new App(Tmp.Dir()).SaveConfig((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!));

    [Fact]
    public void TextAndNullStillWork()
    {
        var app = new App(Tmp.Dir());
        var c = app.SaveConfig((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("{\"custom_target\": \" game.example.net:27015 \", \"gateway_override\": null}")!);
        Assert.Equal("game.example.net:27015", c.CustomTarget);
        Assert.Equal("", c.GatewayOverride);
    }
}

public class ShotSizeTests
{
    [Fact]
    public void AnOversizedScreenshotIsRefusedWithoutDecodingOrCopyingIt()
    {
        var app = new App(Tmp.Dir());
        var huge = "data:image/png;base64," + new string('A', 40_000_000);   // about 30 MB once decoded
        app.AddShot("data:image/png;base64,AAAA");                          // warm up: the first call JITs and builds the regex
        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ApiException>(() => app.AddShot(huge));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Loc.T("err.image_too_large"), ex.Message);
        Assert.True(allocated < 1_000_000, $"{allocated} bytes allocated to refuse a screenshot");   // red before: decoded and copied tens of MB
    }

    [Fact]
    public void AScreenshotJustUnderTheLimitIsStillAccepted()
    {
        var app = new App(Tmp.Dir());
        var raw = new byte[4_999_999];
        var name = app.AddShot("data:image/png;base64," + Convert.ToBase64String(raw));
        Assert.Equal(raw.Length, app.ReadShot(name)!.Value.Bytes.Length);
    }
}

public class ShotNameTests
{
    [Fact]
    public void TwoScreenshotsAddedInTheSameSecondKeepTheirOwnNames()
    {
        var app = new App(Tmp.Dir());
        var uri = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var names = Enumerable.Range(0, 20).Select(_ => app.AddShot(uri)).ToList();
        Assert.Equal(20, names.Distinct().Count());
        Assert.All(names, n => Assert.NotNull(app.ReadShot(n)));
        Assert.DoesNotContain(names, n => n.Contains("_00000000."));
    }
}

public class ServerTests : IAsyncLifetime
{
    App app = null!;
    WebApplication web = null!;
    HttpClient http = null!;
    string dir = "";
    int port;

    public async Task InitializeAsync()
    {
        dir = Tmp.Dir();
        app = new App(dir);
        (web, port) = await Api.StartAsync(app, 0);   // the system picks a free port: no collision with parallel tests or other services
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task DisposeAsync()
    {
        await app.StopSessionAsync();
        await web.StopAsync();
        await web.DisposeAsync();
        http.Dispose();
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // best effort; Tmp sweeps what is left at exit
    }

    [Fact]
    public async Task ASessionStillRunningNeverReachesThePageWithoutAnEndTime()
    {
        int id = app.Store.SaveComplete(Simulator.Make("healthy", 7, p => p.Minutes = 2));
        var h = app.Store.LoadHeader(id)!;
        h.Ended = null;                       // as a header is while the recording is still going
        app.Store.SaveHeader(h);
        var detail = J((await Call($"/api/session/{id}")).Body)["session"]!;
        var series = J((await Call($"/api/session/{id}/series")).Body);
        double started = detail["started"]!.GetValue<double>();
        double ended = detail["ended"]!.GetValue<double>();                       // the last measurement, not null (and not epoch 0)
        Assert.True(ended > started + 60, $"ended={ended} started={started}");
        Assert.Equal(ended, series["ended"]!.GetValue<double>());
        // the page also copes if a null ever arrived: it shows 'running' and draws up to now
        var page = System.Text.Encoding.UTF8.GetString(Api.IndexBytes);
        Assert.Contains("s.ended == null", page);
        Assert.Contains("sd.ended ?? ", page);
    }

    [Fact]
    public async Task TheFixtureReleasesTheServerAndItsFolder()
    {
        var probe = new ServerTests();
        await probe.InitializeAsync();
        var (web, dir) = (probe.web, probe.dir);
        Assert.True(Directory.Exists(dir));
        await probe.DisposeAsync();
        Assert.False(Directory.Exists(dir));
        Assert.Throws<ObjectDisposedException>(() => web.Services.GetService(typeof(Microsoft.Extensions.Hosting.IHostApplicationLifetime)));
    }

    async Task<(int Code, string Body)> Call(string path, object? body = null, string? host = null, string ctype = "application/json", string? lang = null)
    {
        var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (host != null) req.Headers.Host = host;
        if (lang != null) req.Headers.Add("X-Lang", lang);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, ctype);
        var r = await http.SendAsync(req);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    static JsonNode J(string s) => JsonNode.Parse(s)!;

    [Fact]
    public async Task Guards()
    {
        Assert.Equal(403, (await Call("/api/env", host: "evil.example")).Code);
        Assert.Equal(404, (await Call("/api/session/stop")).Code);  // GET on a POST route
        Assert.Equal(415, (await Call("/api/session/stop", new { }, ctype: "text/plain")).Code);
        Assert.Equal(400, (await Call("/api/session/start", new { minutes = "x" })).Code);
        Assert.Equal(400, (await Call("/api/session/start", new { custom_target = "-n 5" })).Code);
        Assert.Equal(400, (await Call("/api/session/start", new { link = "satellite" })).Code);
        Assert.False(app.Rec.Running);
    }

    [Theory]
    [InlineData("/api/session/99999999999")]
    [InlineData("/api/session/99999999999/series")]
    [InlineData("/api/session/99999999999/export.csv")]
    [InlineData("/api/session/99999999999/report.html")]
    public async Task ASessionIdTooBigForAnIntIsNotFoundNotAServerError(string path)
    {
        var r = await Call(path);
        Assert.Equal(404, r.Code);
        Assert.DoesNotContain("Overflow", r.Body);
    }

    [Theory]
    [InlineData("/api/session/99999999999/delete")]
    [InlineData("/api/session/99999999999/label")]
    public async Task AnActionOnATooBigSessionIdIsNotFoundToo(string path)
    {
        var r = await Call(path, new { label = "x" });
        Assert.Equal(404, r.Code);
        Assert.DoesNotContain("Overflow", r.Body);
    }

    [Fact]
    public async Task ThePageIsReadFromTheResourceOnceAndServedFromMemory()
    {
        Assert.Same(Api.IndexBytes, Api.IndexBytes);   // the same buffer, not a new read + encode per call
        var r = await http.GetByteArrayAsync("/");
        Assert.Equal(Api.IndexBytes, r);
        Assert.Contains("<html", System.Text.Encoding.UTF8.GetString(r), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/session/1/foo")] [InlineData("/api/session/1/export.xml")] [InlineData("/api/session/1/report.csv")]
    public async Task AnUnknownSubResourceIsRejectedBeforeAnySessionWork(string path)
    {
        // no session 1 exists here: if the session were loaded and analysed first, the answer would be "Session not found."
        var r = await Call(path);
        Assert.Equal(404, r.Code);
        Assert.Equal("Not found.", J(r.Body)["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheCsvExportWorksWithoutTheAnalysis()
    {
        int id = app.Store.SaveComplete(Simulator.Make("healthy", 7, p => p.Minutes = 1));
        var r = await Call($"/api/session/{id}/export.csv");
        Assert.Equal(200, r.Code);
        Assert.StartsWith("session,local_time", r.Body);
    }

    [Fact]
    public async Task TheEstimateReadsDecimalAndNegativeQueryValuesLikeNumbers()
    {
        // streams 3.7 -> 3, cap_down_mb -5 -> clamped to 10, cap_up_mb 55.5 -> 55, phase_s 7.5 -> 7
        var e = J((await Call("/api/loadtest/estimate?streams=3.7&cap_down_mb=-5&cap_up_mb=55.5&phase_s=7.5")).Body);
        Assert.Equal(3, e["streams"]!.GetValue<int>());
        Assert.Equal(10 * 1 + 55 * 1, e["max_total_mb"]!.GetValue<int>());
        var plain = J((await Call("/api/loadtest/estimate?streams=3&cap_down_mb=10&cap_up_mb=55&phase_s=7")).Body);
        Assert.Equal(plain["duration_s"]!.GetValue<int>(), e["duration_s"]!.GetValue<int>());
    }

    [Fact]
    public async Task SimultaneousRequestsEachKeepTheirOwnLanguage()
    {
        // 80 requests in flight at once, alternating languages: none may answer in the other one
        var calls = Enumerable.Range(0, 80).Select(async i =>
        {
            var fr = i % 2 == 0;
            var r = await Call("/api/session/999999", lang: fr ? "fr" : "en");
            return (fr, Msg: J(r.Body)["error"]!.GetValue<string>());
        }).ToList();
        foreach (var (fr, msg) in await Task.WhenAll(calls))
            Assert.Equal(fr ? "Session introuvable." : "Session not found.", msg);
    }

    [Fact]
    public async Task LanguageIsEnglishByDefaultThenHeaderThenQuery()
    {
        string Err(string json) => J(json)["error"]!.GetValue<string>();
        var bad = new { link = "satellite" };
        Assert.Equal("Invalid link type.", Err((await Call("/api/session/start", bad)).Body));
        Assert.Equal("Type de liaison invalide.", Err((await Call("/api/session/start", bad, lang: "fr")).Body));
        Assert.Equal("Type de liaison invalide.", Err((await Call("/api/session/start?lang=fr", bad)).Body));
        Assert.Equal("Invalid link type.", Err((await Call("/api/session/start?lang=en", bad, lang: "fr")).Body));  // the query wins over the header
        Assert.Equal("Invalid link type.", Err((await Call("/api/session/start", bad, lang: "de")).Body));          // unknown language → English
        Assert.Equal("Hôte refusé.", Err((await Call("/api/env", host: "evil.example", lang: "fr")).Body));
        Assert.Equal("Host refused.", Err((await Call("/api/env", host: "evil.example")).Body));
        // report and exports follow the language too
        int id = app.Store.SaveComplete(Simulator.Make("wifi_unstable", 7, p => p.Minutes = 2));
        Assert.Contains("Network diagnosis report", (await Call($"/api/session/{id}/report.html")).Body);
        Assert.Contains("Rapport de diagnostic r", (await Call($"/api/session/{id}/report.html?lang=fr")).Body);
        var fr = J((await Call($"/api/session/{id}", lang: "fr")).Body)["analysis"]!["stats"]!["targets"]![0]!["label"]!.GetValue<string>();
        Assert.Equal("Passerelle (routeur)", fr);
        var en = J((await Call($"/api/session/{id}")).Body)["analysis"]!["stats"]!["targets"]![0]!["label"]!.GetValue<string>();
        Assert.Equal("Gateway (router)", en);
        Assert.Equal("NetworkAnalyzer", J((await Call("/api/identity")).Body)["app"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListensOnLoopbackOnly()
    {
        var addrs = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.All(addrs, a => Assert.StartsWith("http://127.0.0.1:", a));
    }

    [Fact]
    public async Task LoadTestRequiresConfirmation()
    {
        var (code, body) = await Call("/api/loadtest/start", new { });
        Assert.Equal(412, code);
        Assert.Equal(800, J(body)["estimate"]!["max_total_mb"]!.GetValue<int>());
        Assert.False(app.Rec.Running);  // nothing started
    }

    [Fact]
    public async Task ConfigNeverStoresCredentials()
    {
        // distinctive secrets (a one-letter value would match anything), under the three usual credential keys, plus the same values under unrelated keys
        var secrets = new[] { "pw-Zx81-secret", "tok-9f3a-secret", "cookie-7b21-secret" };
        var (code, _) = await Call("/api/config", new { router = new { model = "X", password = secrets[0], token = secrets[1], cookie = secrets[2], notes = "plain note", qos_type = "priority" } });
        Assert.Equal(200, code);
        var raw = File.ReadAllText(Path.Combine(dir, "config.json"));
        foreach (var s in secrets) Assert.DoesNotContain(s, raw);                  // none of the values anywhere in the file
        foreach (var key in new[] { "password", "token", "cookie" }) Assert.DoesNotContain($"\"{key}\"", raw);   // nor a field for them
        var router = System.Text.Json.JsonDocument.Parse(raw).RootElement.GetProperty("router");
        Assert.Equal("X", router.GetProperty("model").GetString());                // the legitimate fields are kept
        Assert.Equal("plain note", router.GetProperty("notes").GetString());
        // and what the API gives back never carries them either
        Assert.DoesNotContain("secret", (await Call("/api/config")).Body);
        Assert.DoesNotContain("secret", (await Call("/api/router")).Body);
    }

    [Fact]
    public async Task InvalidRouterValuesRejected()
    {
        Assert.Equal(400, (await Call("/api/config", new { router = new { unit = "Tbps" } })).Code);
        Assert.Equal(400, (await Call("/api/config", new { router = new { qos_type = "magic" } })).Code);
    }

    [Fact]
    public async Task ScreenshotValidationAndPathSafety()
    {
        Assert.Equal(400, (await Call("/api/router/shot", new { data = "data:text/html;base64,PGI+" })).Code);
        var (code, body) = await Call("/api/router/shot", new { data = "data:image/png;base64,iVBORw0KGgo=" });
        Assert.Equal(200, code);
        var name = J(body)["name"]!.GetValue<string>();
        Assert.Equal(200, (await Call($"/api/router/shot/{name}")).Code);
        Assert.Equal(404, (await Call("/api/router/shot/..%2f..%2fconfig.json")).Code);
        Assert.Equal(400, (await Call("/api/router/shot/delete", new { name = "../config.json" })).Code);
        Assert.Equal(200, (await Call("/api/router/shot/delete", new { name })).Code);
        Assert.Equal(404, (await Call($"/api/router/shot/{name}")).Code);
    }

    [Fact]
    public async Task RealShortSessionEndToEnd()
    {
        // Loopback stands in for the gateway and for a custom destination, so this test needs no Internet at all:
        // the Cloudflare/Google/Quad9 targets are still measured, but nothing below depends on them answering.
        await Call("/api/config", new { gateway_override = "127.0.0.1" });
        var (code, body) = await Call("/api/session/start", new { minutes = 1, label = "e2e", custom_target = "127.0.0.1:443" });
        Assert.Equal(200, code);
        int sid = J(body)["sid"]!.GetValue<int>();
        Assert.Equal(409, (await Call("/api/session/start", new { minutes = 1 })).Code);  // already running
        await Task.Delay(4000);
        Assert.Equal(200, (await Call("/api/mark", new { note = "x" })).Code);
        var live = J((await Call("/api/live?since=0")).Body);
        Assert.True(live["status"]!["running"]!.GetValue<bool>());
        Assert.NotNull(live["series"]!["ping:custom"]);    // answered by the loopback
        Assert.NotNull(live["series"]!["ping:gateway"]);
        Assert.Equal(409, (await Call($"/api/session/{sid}/delete", new { })).Code);  // no deletion while measuring
        Assert.Equal(200, (await Call("/api/session/stop", new { })).Code);
        var res = J((await Call($"/api/session/{sid}")).Body);
        Assert.True(res["analysis"]!["stats"]!["targets"]![0]!["stats"]!["n"]!.GetValue<int>() > 1);
        Assert.Equal(200, (await Call($"/api/session/{sid}/report.html")).Code);
        Assert.Equal(200, (await Call($"/api/session/{sid}/export.csv")).Code);
        Assert.Equal(200, (await Call($"/api/session/{sid}/export.json")).Code);
        Assert.Contains(res["analysis"]!["timeline"]!.AsArray(), t => t!["type"]!.GetValue<string>() == "lag");
        Assert.Equal(404, (await Call("/api/session/999")).Code);
        var sessions = J((await Call("/api/sessions")).Body).AsArray();
        Assert.Equal("e2e", sessions[0]!["label"]!.GetValue<string>());
        Assert.NotNull(sessions[0]!["metrics"]);
        Assert.Equal(200, (await Call($"/api/session/{sid}/label", new { label = "renamed" })).Code);
        Assert.Equal("renamed", J((await Call("/api/sessions")).Body)[0]!["label"]!.GetValue<string>());
        Assert.Equal(200, (await Call($"/api/session/{sid}/delete", new { })).Code);
        Assert.Equal(404, (await Call($"/api/session/{sid}")).Code);
    }

    [Fact]
    public async Task MarkWithoutSessionAutostartsOne()
    {
        var (code, body) = await Call("/api/mark", new { note = "sans session" });
        Assert.Equal(200, code);
        Assert.True(J(body)["auto_started"]!.GetValue<bool>());
        Assert.True(app.Rec.Running);
        await app.StopSessionAsync();
    }

    [Fact]
    public async Task CompareEndpoint()
    {
        var good = (Action<SimParams>)(p => p.Load = new LoadSim { DownDelta = 10, UpDelta = 30, GwDown = 1, GwUp = 1 });
        var ids = new List<int>();
        foreach (var s in new[] { 1, 2 }) ids.Add(app.Store.SaveComplete(Simulator.Make("bufferbloat", s)));
        foreach (var s in new[] { 3, 4 }) ids.Add(app.Store.SaveComplete(Simulator.Make("bufferbloat", s, good)));
        var rows = J((await Call("/api/compare", new { a = ids.Take(2), b = ids.Skip(2) })).Body)["rows"]!.AsArray();
        var up = rows.First(r => r!["metric"]!.GetValue<string>() == "Latency increase during upload (ms)")!;
        Assert.Equal("improvement", up["verdict"]!.GetValue<string>());
        Assert.Equal("improvement", up["verdict_text"]!.GetValue<string>());
    }

    [Fact]
    public async Task RouterViewUsesMeasuredSpeeds()
    {
        app.Store.SaveComplete(Simulator.Make("bufferbloat", 1, p => p.Load = new LoadSim()));
        // the simulator does not store the load-test meta: give it, as the recorder does
        var h = app.Store.List()[0];
        h.Meta.Loadtest = new LoadMeta { Server = "x" };
        app.Store.SaveHeader(h);
        await Call("/api/config", new { router = new { qos_enabled = true, qos_type = "priority", limit_down = 900, limit_up = 900, unit = "Mbps" } });
        var v = J((await Call("/api/router")).Body);
        Assert.Equal(h.Id, v["measures_from_session"]!.GetValue<int>());
        Assert.True(v["measured"]!["down"]!.GetValue<double>() > 250);
        Assert.Contains(v["analysis"]!["findings"]!.AsArray(), f => f!["text"]!.GetValue<string>().Contains("limits nothing"));
    }
}

public class RecorderTests
{
    [Fact]
    public void BuildTargetsRolesAndCustomParsing()
    {
        var env = new EnvInfo { Active = new AdapterInfo { Gw4 = "192.168.0.1", Index = 3 }, Ipv6Global = true };
        var t = Recorder.BuildTargets(env, "game.example.net:27015");
        Assert.Equal(new[] { "gateway", "internet", "internet", "internet", "internet6", "custom" }, t.Select(x => x.Role).ToArray());
        Assert.Equal(27015, t[^1].TcpPort);
        Assert.Equal(3, t.Where(x => x.Role == "internet").Select(x => x.Host).Distinct().Count());  // independent destinations
        Assert.Equal("2001:db8::1", Recorder.ParseCustom("2001:db8::1")!.Host);
        Assert.Null(Recorder.ParseCustom("  "));
        Assert.Throws<ArgumentException>(() => Recorder.ParseCustom("-oops"));
        Assert.Throws<ArgumentException>(() => Recorder.ParseCustom("host:99999"));
    }

    [Fact]
    public async Task EndingAPhaseKeepsWhatWasAlreadyRecordedInIt()
    {
        var rec = new Recorder(new SessionStore(Tmp.Dir()));
        rec.Start(new EnvInfo(), new List<Target>(), 1);
        rec.BeginPhase("download", new PhaseMeta { Bytes = 1234, VolumeCapReached = true, Streams = 4 });
        rec.EndPhase(new PhaseMeta { Interrupted = true });
        await rec.StopAsync();
        var m = rec.Status().Phases.Single().Meta;
        Assert.Equal(1234, m.Bytes);
        Assert.True(m.VolumeCapReached);
        Assert.Equal(4, m.Streams);
        Assert.True(m.Interrupted);
    }

    [Fact]
    public void EveryPhaseMetaPropertyCanBeUnset()
    {
        // Recorder merges PhaseMeta by copying the non-null properties: a non-nullable value type would always
        // overwrite (false / 0). This fails if someone adds one.
        foreach (var p in typeof(PhaseMeta).GetProperties())
            Assert.True(!p.PropertyType.IsValueType || Nullable.GetUnderlyingType(p.PropertyType) != null, p.Name);
    }

    [Fact]
    public void CustomTargetAcceptsABracketedIpv6WithAPort()
    {
        var t = Recorder.ParseCustom("[2001:db8::1]:8443")!;
        Assert.Equal("2001:db8::1", t.Host);
        Assert.Equal(8443, t.TcpPort);
        Assert.Equal(443, Recorder.ParseCustom("[2001:db8::1]")!.TcpPort);
        Assert.Equal("2001:db8::1", Recorder.ParseCustom("[2001:db8::1]")!.Host);
        Assert.Throws<ArgumentException>(() => Recorder.ParseCustom("[2001:db8::1"));
        Assert.Throws<ArgumentException>(() => Recorder.ParseCustom("[2001:db8::1]:99999"));
        Assert.Throws<ArgumentException>(() => Recorder.ParseCustom("[2001:db8::1]x"));
    }

    [Fact]
    public void GatewayOverrideAndIpv6GatewayScope()
    {
        var t = Recorder.BuildTargets(new EnvInfo { Active = new AdapterInfo { Gw4 = "10.0.0.1" } }, "", "192.168.1.254");
        Assert.Equal("192.168.1.254", t[0].Host);
        t = Recorder.BuildTargets(new EnvInfo { Active = new AdapterInfo { Gw6 = "fe80::1", Index = 12 } }, "");
        Assert.Equal(6, t[0].Family);
        Assert.Equal("fe80::1%12", t[0].Host);   // a link-local gateway needs its zone, on every platform
        Assert.Equal("fe80::1%3", Recorder.BuildTargets(new EnvInfo { Active = new AdapterInfo { Gw6 = "fe80::1%3", Index = 12 } }, "")[0].Host);
        Assert.Equal("fe80::1", Recorder.BuildTargets(new EnvInfo { Active = new AdapterInfo { Gw6 = "fe80::1", Index = 0 } }, "")[0].Host);   // unknown index: no bogus zone
    }

    [Fact]
    public async Task TraceIsRefusedWithoutARunningSessionAndAcceptedDuringOne()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store);
        Assert.False(rec.TraceAsync(new[] { "127.0.0.1" }));   // never started: no token, no writer
        rec.Start(new EnvInfo(), new List<Target>(), 1);
        Assert.True(rec.TraceAsync(new[] { "127.0.0.1" }));
        await rec.StopAsync();
        Assert.False(rec.TraceAsync(new[] { "127.0.0.1" }));   // stopped: the old token is cancelled, the writer closed
    }

    [Fact]
    public void AnInterfaceThatWasAskedForAndIsGoneGivesNoMeasurementNotAnotherInterface()
    {
        Assert.Null(Recorder.ReadCounters("this-interface-does-not-exist"));                  // used to fall back to the busiest other one
        var any = Recorder.ReadCounters(null);                                               // with no preference the busiest is still chosen...
        if (any != null) Assert.False(string.IsNullOrEmpty(any.Value.Name));                  // ...and its identity comes with the numbers
    }

    [Fact]
    public async Task SwitchingInterfaceBetweenTwoReadingsDoesNotInventARateSpike()
    {
        int calls = 0;
        var rec = new Recorder(new SessionStore(Tmp.Dir()))
        {
            TrafficPollMs = 40,
            CounterReader = _ =>
            {
                int n = Interlocked.Increment(ref calls);
                // two readings on interface "a" (about 2 KB), then the busiest interface becomes "b" whose counters are 50 MB:
                // subtracting a's counter from b's would read as ~12 Gbps
                if (n <= 2) return ("a", n * 1_000L, n * 500L);
                return ("b", 50_000_000L + (n - 3) * 1_000L, 40_000_000L + (n - 3) * 500L);
            },
        };
        rec.Start(new EnvInfo { Active = new AdapterInfo { Name = "" } }, new List<Target>(), 1);
        await Task.Delay(900);
        await rec.StopAsync();
        var down = rec.LiveSince(0)["net:down_bps"].Select(s => (double)s[1]!).ToList();
        Assert.True(down.Count >= 5);
        Assert.True(down.Max() < 1e6, $"a rate of {down.Max():0} bps was recorded: counters of two interfaces were subtracted");
    }

    [Fact]
    public async Task LiveStatisticsKeepTheInitialFailuresUnlessATcpFallbackReallyTookOver()
    {
        var rec = new Recorder(new SessionStore(Tmp.Dir()));
        var t = new Target { Id = "ghost", Host = "192.0.2.1", Role = "internet", Family = 4, TcpPort = 443 };
        rec.Start(new EnvInfo(), new List<Target> { t }, 1);
        double now = Clock.Now();
        // 8 failures flushed with no TCP fallback (both probes failed): real unavailability, they count
        for (int i = 0; i < 8; i++) rec.Emit("ping:ghost", null, false, "icmp_no_reply", now - 8 + i);
        var noFallback = rec.LiveStats()["ghost"].Stats!;
        Assert.Equal((8, 100.0), (noFallback.N, noFallback.LossPct));
        // the TCP fallback then worked: the ICMP failures are not relevant to the new mode any more, and are left out
        for (int i = 0; i < 5; i++) rec.Emit("ping:ghost", 20.0 + i, true, "tcp", now + i * 0.1);
        var fallback = rec.LiveStats()["ghost"].Stats!;
        Assert.Equal((5, 0.0), (fallback.N, fallback.LossPct));
        await rec.StopAsync();
    }

    [Fact]
    public async Task FailuresHeldBackWhileWaitingToDecideAboutIcmpAreSavedWhenTheSessionStopsEarly()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store);
        // a target that never answers (TEST-NET-1, reserved for documentation): within a 3.5 s session fewer than the 8 failures needed to decide
        var ghost = new Target { Id = "ghost", Host = "192.0.2.1", Role = "internet", Family = 4 };
        int sid = rec.Start(new EnvInfo(), new List<Target> { ghost }, 1);
        await Task.Delay(3500);
        await rec.StopAsync();
        var samples = store.Load(sid)!.S("ping:ghost");
        Assert.True(samples.Count >= 2, $"only {samples.Count} samples saved: the early failures were lost");
        Assert.All(samples, s => { Assert.False(s.Ok); Assert.NotEqual("icmp_no_reply", s.Info); });   // as they were observed, not relabelled
        // and they do not leak into the next session
        int next = rec.Start(new EnvInfo(), new List<Target>(), 1);
        await rec.StopAsync();
        Assert.Empty(store.Load(next)!.S("ping:ghost"));
    }

    [Fact]
    public async Task AMeasurementTaskThatCrashedNeverPreventsTheSessionFromBeingFinalised()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store) { WifiPollMs = 20, WifiReader = () => throw new InvalidOperationException("the reader crashed") };
        int sid = rec.Start(new EnvInfo { Active = new AdapterInfo { Kind = "wifi" } }, new List<Target>(), 1);
        await Task.Delay(300);                                   // the Wi-Fi task has faulted by now
        await rec.StopAsync();                                    // used to throw the task's exception before closing anything
        Assert.NotNull(store.Load(sid)!.Ended);                   // the header was closed...
        Assert.False(rec.Running);
        Assert.Contains(rec.Status().Notes, n => n == Loc.T("note.task_failed"));   // ...and the failure is not silent: it is noted
        int next = rec.Start(new EnvInfo(), new List<Target>(), 1);                // and the recorder is usable again
        Assert.True(next > sid);
        await rec.StopAsync();
    }

    [Fact]
    public async Task ANewSessionCannotStartWhileThePreviousOneIsStillBeingClosed()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store)
        {
            WifiPollMs = 20,
            // a Wi-Fi reading that takes 1.5 s keeps the stop waiting for that task, so the window between "stopping" and "stopped" is wide
            WifiReader = async () => { await Task.Delay(1500); return null; },
        };
        var env = new EnvInfo { Active = new AdapterInfo { Kind = "wifi" } };
        int first = rec.Start(env, new List<Target>(), 1);
        await Task.Delay(300);
        var stopping = rec.StopAsync();
        Assert.Throws<InvalidOperationException>(() => rec.Start(env, new List<Target>(), 1));   // not while the first is closing
        await stopping;
        Assert.NotNull(store.Load(first)!.Ended);                                                // the first session was finalised, with its own header
        int second = rec.Start(new EnvInfo(), new List<Target>(), 1);                           // and once it is closed a new one starts normally
        Assert.True(second > first);
        await rec.StopAsync();
        Assert.NotNull(store.Load(second)!.Ended);
    }

    [Fact]
    public async Task ATransientCounterErrorDoesNotEndTrafficMeasurement()
    {
        int calls = 0;
        var rec = new Recorder(new SessionStore(Tmp.Dir()))
        {
            TrafficPollMs = 40,
            CounterReader = _ =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n is 3 or 4) throw new System.Net.NetworkInformation.NetworkInformationException();   // adapter being reset
                return ("eth", n * 1_000L, n * 500L);
            },
        };
        rec.Start(new EnvInfo { Active = new AdapterInfo { Name = "eth" } }, new List<Target>(), 1);
        await Task.Delay(900);
        await rec.StopAsync();
        var down = rec.LiveSince(0)["net:down_bps"];
        Assert.True(down.Count >= 5, $"only {down.Count} samples: the loop stopped at the error");
        Assert.Single(rec.Status().Notes, n => n == Loc.T("note.counters_unreadable"));   // noted once, not on every failed read
    }

    [Fact]
    public async Task AWifiSessionDoesNotStartWithAFakeSleepMark()
    {
        // the neighbour scan runs once, right after the first reading; its duration is not a system sleep
        var rec = new Recorder(new SessionStore(Tmp.Dir()))
        {
            GapThresholdS = 0.6, WifiPollMs = 50,
            WifiReader = () => Task.FromResult<WifiInfo?>(new WifiInfo { Connected = true, Signal = 80, Channel = 6, Bssid = "aa:bb" }),
            NeighborReader = async (_, _) => { await Task.Delay(900); return new WifiNeighbors { Total = 3 }; },
        };
        rec.Start(new EnvInfo { Active = new AdapterInfo { Kind = "wifi" } }, new List<Target>(), 1);
        await Task.Delay(1800);
        await rec.StopAsync();
        Assert.DoesNotContain(rec.Status().Marks, m => m.Kind == "gap");
        Assert.True(rec.LiveSince(0).ContainsKey("wifi:signal"));
    }

    [Fact]
    public async Task WifiThatBecomesReadableAfterAnUnavailableFirstReadingStillGetsItsDetails()
    {
        var store = new SessionStore(Tmp.Dir());
        int calls = 0;
        var rec = new Recorder(store)
        {
            WifiPollMs = 30,
            WifiReader = () => Task.FromResult<WifiInfo?>(++calls == 1 ? null : new WifiInfo { Connected = true, Signal = 80, Channel = 6, Bssid = "aa:bb" }),
            NeighborReader = (_, _) => Task.FromResult<WifiNeighbors?>(new WifiNeighbors { Total = 3 }),
        };
        rec.Start(new EnvInfo { Active = new AdapterInfo { Kind = "wifi" } }, new List<Target>(), 1);
        await Task.Delay(600);
        var id = rec.Status().Sid!.Value;
        await rec.StopAsync();
        var meta = store.LoadHeader(id)!.Meta;
        Assert.NotNull(meta.Wifi);                          // red before: stayed null for the whole session
        Assert.Equal(3, meta.WifiNeighbors?.Total);
        Assert.Single(rec.Status().Notes, n => n == Loc.T("note.wifi_unavailable"));   // the first miss is still noted, once
    }

    [Fact]
    public async Task ALateMeasurementOfAStoppedSessionIsDropped()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store);
        rec.Start(new EnvInfo(), new List<Target>(), 1);
        using var old = new CancellationTokenSource();
        old.Cancel();                                       // the token of a task that outlived its session
        rec.Emit("ping:late", 12.0, true, "", null, old.Token);
        rec.MarkNow("gap", "", 50.0, old.Token);
        rec.Emit("ping:fresh", 1.0, true);
        Assert.False(rec.LiveSince(0).ContainsKey("ping:late"));
        Assert.True(rec.LiveSince(0).ContainsKey("ping:fresh"));
        Assert.Empty(rec.Status().Marks);
        await rec.StopAsync();
    }

    [Fact]
    public async Task AFailedTraceNeverPreventsTheSessionFromBeingFinalised()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store);
        int sid = rec.Start(new EnvInfo(), new List<Target>(), 1);
        Assert.True(rec.TraceAsync(new[] { "-not a host" }));   // makes the trace task throw
        await Task.Delay(300);
        await rec.StopAsync();
        Assert.NotNull(store.Load(sid)!.Ended);
        // and the finished trace is not waited for again by the next session
        rec.Start(new EnvInfo(), new List<Target>(), 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rec.StopAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GapMarksAreDeduplicated()
    {
        var store = new SessionStore(Tmp.Dir());
        var rec = new Recorder(store);
        rec.MarkNow("gap", "a", 100.0);
        rec.MarkNow("gap", "b", 101.0);
        rec.MarkNow("gap", "c", 110.0);
        Assert.Equal(2, rec.Status().Marks.Count);
        await Task.CompletedTask;
    }
}
