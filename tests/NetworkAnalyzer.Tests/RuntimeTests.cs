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

    StubServer(WebApplication web, string url) { this.web = web; Url = url; }

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
        app.MapPost("/__up", async (HttpContext c) =>
        {
            var buf = new byte[65536];
            try { while (await c.Request.Body.ReadAsync(buf, c.RequestAborted) > 0) { } } catch (OperationCanceledException) { }
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new StubServer(app, url);
    }

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
    public void GatewayOverrideAndIpv6GatewayScope()
    {
        var t = Recorder.BuildTargets(new EnvInfo { Active = new AdapterInfo { Gw4 = "10.0.0.1" } }, "", "192.168.1.254");
        Assert.Equal("192.168.1.254", t[0].Host);
        t = Recorder.BuildTargets(new EnvInfo { Active = new AdapterInfo { Gw6 = "fe80::1", Index = 12 } }, "");
        Assert.Equal(6, t[0].Family);
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
