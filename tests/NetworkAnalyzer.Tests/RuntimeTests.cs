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

static class Tmp
{
    public static string Dir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "na-test-" + Guid.NewGuid().ToString("N"))).FullName;
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
        Assert.True(res[1].Bytes < 30_000_000L + 4 * 25_000_000L);
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
        await Task.Delay(2500);
        var t0 = DateTime.UtcNow;
        lt.Cancel();
        await lt.Task!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True((DateTime.UtcNow - t0).TotalSeconds < 8);
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

    [Fact]
    public void SectionsAndNoCertainty()
    {
        foreach (var scn in new[] { "wifi_unstable", "bufferbloat", "isp", "dns", "healthy", "icmp_blocked" })
        {
            var html = Html(scn);
            foreach (var s in new[] { "Network diagnosis report", "Incident timeline", "<svg", "Measurements", "Environment and limits", "not confirmed causes" })
                Assert.True(html.Contains(s), $"{scn}: {s}");
            Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""));  // no external resource
        }
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
    public void ChartSurvivesEmptyAndSingleSeries()
    {
        Assert.Contains("No data for this chart", Report.SvgChart(new() { new Report.ChartSeries { Name = "a" } }, 0, 10));
        Assert.Contains("<polyline", Report.SvgChart(new() { new Report.ChartSeries { Name = "a", Pts = new() { (1, 5.0) }, Lost = new() { 2.0 } } }, 0, 10));
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
        File.AppendAllText(Path.Combine(dir, "sessions", $"{id}.jsonl"), "[\"s\", 12.5, \"ping:gat");
        Assert.NotNull(store.Load(id));
        store.Delete(id);
        Assert.Null(store.Load(id));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "sessions")));
    }

    [Fact]
    public async Task WriterFlushesOnComplete()
    {
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        int id = store.Create(new SessionHeader { Started = 1000, Meta = new SessionMeta { Targets = Simulator.Targets } });
        var w = store.OpenWriter(id);
        w.Sample(1001, "ping:gateway", 1.5, true, "");
        w.Mark(1002, "lag", "x");
        await w.CompleteAsync();
        var l = store.Load(id)!;
        Assert.Single(l.S("ping:gateway"));
        Assert.Single(l.Marks);
    }
}

public class StartupMessageTests
{
    [Fact]
    public async Task NoFreePortSaysSoInTheReadersLanguage()
    {
        int p = 19000 + Random.Shared.Next(500);
        var taken = new List<System.Net.Sockets.TcpListener>();
        try
        {
            for (int i = 0; i < 2; i++) { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, p + i); l.Start(); taken.Add(l); }
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
        (web, port) = await Api.StartAsync(app, 18000 + Random.Shared.Next(1000));
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task DisposeAsync()
    {
        await app.StopSessionAsync();
        await web.StopAsync();
        http.Dispose();
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
        var (code, _) = await Call("/api/config", new { router = new { model = "X", password = "hunter2", token = "t", cookie = "c", qos_type = "priority" } });
        Assert.Equal(200, code);
        var raw = File.ReadAllText(Path.Combine(dir, "config.json"));
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("password", raw);
        Assert.Contains("\"model\":\"X\"", raw);
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
        var (code, body) = await Call("/api/session/start", new { minutes = 1, label = "e2e", custom_target = "" });
        Assert.Equal(200, code);
        int sid = J(body)["sid"]!.GetValue<int>();
        Assert.Equal(409, (await Call("/api/session/start", new { minutes = 1 })).Code);  // already running
        await Task.Delay(4000);
        Assert.Equal(200, (await Call("/api/mark", new { note = "x" })).Code);
        var live = J((await Call("/api/live?since=0")).Body);
        Assert.True(live["status"]!["running"]!.GetValue<bool>());
        Assert.NotNull(live["series"]!["ping:cloudflare"]);
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
                return (n * 1_000L, n * 500L);
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
