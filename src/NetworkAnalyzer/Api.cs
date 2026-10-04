using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetworkAnalyzer;

public sealed record Raw(byte[] Body, string ContentType, Dictionary<string, string>? Headers = null);

/// <summary>
/// Local web server (127.0.0.1 only): interface + JSON API. Protections: loopback listener only; Host header checked
/// (anti DNS-rebinding); requests that change something require Content-Type: application/json (another web site cannot send
/// that without a CORS grant, which this server never gives).
/// </summary>
public static class Api
{
    const int MaxBody = 8_000_000;
    static readonly Regex SessionRx = new(@"^/api/session/(\d+)(?:/(\w+)(?:\.(\w+))?)?$", RegexOptions.Compiled);
    static readonly Regex ShotRx = new(@"^/api/router/shot/([0-9a-f_]+\.(?:png|jpg|webp))$", RegexOptions.Compiled);
    static readonly Regex SessionActionRx = new(@"^/api/session/(\d+)/(delete|label)$", RegexOptions.Compiled);

    static readonly Lazy<byte[]> indexBytes = new(() =>
    {
        // the page is an embedded resource: it cannot change while the program runs
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    /// <summary>The web page, as served (UTF-8), read once.</summary>
    public static byte[] IndexBytes => indexBytes.Value;

    public static async Task<(WebApplication Web, int Port)> StartAsync(App app, int port, int tries = 20)
    {
        Exception? last = null;
        for (int p = port; p < port + tries && p <= 65535; p++)  // never past the last valid port
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, p));
            var web = builder.Build();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{p}", $"localhost:{p}", $"[::1]:{p}" };
            web.Run(ctx => Handle(app, allowed, ctx));
            try
            {
                await web.StartAsync();
                return (web, p);
            }
            catch (IOException e) { last = e; await web.DisposeAsync(); }
        }
        throw new InvalidOperationException(Loc.T("err.no_free_port", port.ToString(), (port + tries - 1).ToString()), last);
    }

    static async Task Write(HttpContext ctx, int code, object body, string ctype = "application/json; charset=utf-8", Dictionary<string, string>? extra = null)
    {
        byte[] bytes = body switch
        {
            byte[] b => b,
            string s => Encoding.UTF8.GetBytes(s),
            _ => JsonSerializer.SerializeToUtf8Bytes(body, Json.Options),
        };
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = ctype;
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        foreach (var (k, v) in extra ?? new()) ctx.Response.Headers[k] = v;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    static async Task Handle(App app, HashSet<string> allowed, HttpContext ctx)
    {
        Loc.Lang = ctx.Request.Query["lang"].FirstOrDefault() ?? ctx.Request.Headers["X-Lang"].FirstOrDefault() ?? Loc.Default;  // English unless French is asked for
        if (!allowed.Contains(ctx.Request.Host.Value ?? "")) { await Write(ctx, 403, new { error = Loc.T("err.forbidden_host") }); return; }
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "/";
        var q = ctx.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var body = new JsonObject();
        if (method == "POST")
        {
            if (!(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { await Write(ctx, 415, new { error = Loc.T("err.content_type") }); return; }
            if ((ctx.Request.ContentLength ?? 0) > MaxBody) { await Write(ctx, 413, new { error = Loc.T("err.too_large") }); return; }
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                if (ms.Length > MaxBody) { await Write(ctx, 413, new { error = Loc.T("err.too_large") }); return; }
                var node = ms.Length == 0 ? new JsonObject() : JsonNode.Parse(ms.ToArray());
                if (node is not JsonObject o) { await Write(ctx, 400, new { error = Loc.T("err.object_expected") }); return; }
                body = o;
            }
            catch (JsonException) { await Write(ctx, 400, new { error = Loc.T("err.invalid_json") }); return; }
        }
        else if (method != "GET") { await Write(ctx, 405, new { error = Loc.T("err.method") }); return; }
        try
        {
            var res = await Route(app, method, path, q, body);
            if (res is Raw r) await Write(ctx, 200, r.Body, r.ContentType, r.Headers);
            else await Write(ctx, 200, res);
        }
        catch (ApiException e)
        {
            var d = new Dictionary<string, object?> { ["error"] = e.Message };
            foreach (var (k, v) in e.Extra) d[k] = v;
            await Write(ctx, e.Code, d);
        }
        catch (Exception e)  // a request error must never stop the server
        {
            await Write(ctx, 500, new { error = $"{e.GetType().Name}: {e.Message}" });
        }
    }

    static double ParseDouble(string? s, double dflt) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;

    public static async Task<object> Route(App app, string method, string path, Dictionary<string, string> q, JsonObject body)
    {
        var rec = app.Rec;
        if (method == "GET")
        {
            if (path == "/") return new Raw(IndexBytes, "text/html; charset=utf-8");
            if (path == "/favicon.ico") return new Raw(Array.Empty<byte>(), "image/x-icon");
            if (path == "/api/identity") return new { app = "NetworkAnalyzer", version = AppVersion.Display };
            if (path == "/api/env")
            {
                var e = app.GetEnv(q.GetValueOrDefault("refresh") == "1");
                return new { interfaces = e.Interfaces, active = e.Active, vpn = e.Vpn, ipv6_global = e.Ipv6Global, platform = e.Platform, notes = SysInfo.Notes(e) };
            }
            if (path == "/api/live")
            {
                double since = ParseDouble(q.GetValueOrDefault("since"), 0);
                return new
                {
                    now = Clock.Now(), status = rec.Status(), stats = rec.Running ? rec.LiveStats(60) : new Dictionary<string, LiveTarget>(),
                    series = rec.LiveSince(since), loadtest = app.Load?.Status(), targets = rec.Targets, version = AppVersion.Short, data_dir = app.Store.DataDir,
                };
            }
            if (path == "/api/config") return app.Config.Load();
            if (path == "/api/loadtest/estimate")
            {
                var b = new JsonObject();
                foreach (var (k, v) in q) b[k] = JsonValue.Create(v);  // App.Num reads numeric strings (decimal, negative) itself
                return app.Estimate(b);
            }
            if (path == "/api/sessions") return app.Sessions();
            if (path == "/api/router") return app.RouterView();
            var ms = ShotRx.Match(path);
            if (ms.Success)
            {
                var s = app.ReadShot(ms.Groups[1].Value) ?? throw new ApiException(Loc.T("err.not_found"), 404);
                return new Raw(s.Bytes, s.Mime);
            }
            var m = SessionRx.Match(path);
            if (m.Success)
            {
                if (!int.TryParse(m.Groups[1].Value, out int sid)) throw new ApiException(Loc.T("err.session_not_found"), 404);  // too big for an id: no such session
                string? sub = m.Groups[2].Success ? m.Groups[2].Value : null, ext = m.Groups[3].Success ? m.Groups[3].Value : null;
                if (sub is null) return app.SessionDetail(sid);
                if (sub == "series") return app.SeriesPayload(sid);
                // the route is checked first: an unknown one costs nothing, and the CSV (raw data) needs no analysis
                if ((sub, ext) is not (("export", "csv") or ("export", "json") or ("report", "html"))) throw new ApiException(Loc.T("err.not_found"), 404);
                if (sub == "export" && ext == "csv")
                {
                    var raw = app.Store.Load(sid) ?? throw new ApiException(Loc.T("err.session_not_found"), 404);
                    return new Raw(Encoding.UTF8.GetBytes(Report.ExportCsv(raw)), "text/csv; charset=utf-8", new() { ["Content-Disposition"] = $"attachment; filename=\"measurements_session_{sid}.csv\"" });
                }
                var (d, a) = app.AnalysisOf(sid);
                if (sub == "export" && ext == "json")
                    return new Raw(Encoding.UTF8.GetBytes(Report.ExportJson(d, a)), "application/json; charset=utf-8", new() { ["Content-Disposition"] = $"attachment; filename=\"session_{sid}.json\"" });
                if (sub == "report" && ext == "html")
                    return new Raw(Encoding.UTF8.GetBytes(Report.Html(d, a, app.CfgFor(d), app.ShotsForReport(), AppVersion.Short)), "text/html; charset=utf-8",
                        new() { ["Content-Disposition"] = $"inline; filename=\"report_session_{sid}.html\"" });
            }
            throw new ApiException(Loc.T("err.not_found"), 404);
        }

        switch (path)
        {
            case "/api/session/start": return new { sid = app.StartSession(body) };
            case "/api/session/stop": return new { sid = await app.StopSessionAsync() };
            case "/api/mark": return app.Mark(body["note"] is JsonValue nv && nv.TryGetValue<string>(out var note) ? note : null);
            case "/api/loadtest/start": return new { estimate = app.StartLoadtest(body) };
            case "/api/loadtest/cancel": app.Load?.Cancel(); return new { ok = true };
            case "/api/config": return app.SaveConfig(body);
            case "/api/router/shot": return new { name = app.AddShot(body["data"] is JsonValue dv && dv.TryGetValue<string>(out var data) ? data : null) };
            case "/api/router/shot/delete": app.DelShot(body["name"] is JsonValue nm && nm.TryGetValue<string>(out var name) ? name : null); return new { ok = true };
            case "/api/compare": return app.Compare(body);
        }
        var ma = SessionActionRx.Match(path);
        if (ma.Success)
        {
            if (!int.TryParse(ma.Groups[1].Value, out int id)) throw new ApiException(Loc.T("err.session_not_found"), 404);
            app.DeleteOrLabel(id, ma.Groups[2].Value, body);
            return new { ok = true };
        }
        throw new ApiException(Loc.T("err.not_found"), 404);
    }
}
