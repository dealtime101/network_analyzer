using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetworkAnalyzer;

public sealed class ApiException : Exception
{
    public int Code { get; }
    public Dictionary<string, object?> Extra { get; }

    public ApiException(string msg, int code = 400, Dictionary<string, object?>? extra = null) : base(msg)
    {
        Code = code;
        Extra = extra ?? new();
    }
}

public static class AppVersion
{
    /// <summary>"1.0.0+abc1234" (version of the csproj + commit given at publish time).</summary>
    public static string Display { get; } = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    public static string Short => Display.Split('+')[0];
}

public sealed class SessionSummary
{
    public int Id { get; set; }
    public double Started { get; set; }
    public double? Ended { get; set; }
    public string Label { get; set; } = "";
    public string Link { get; set; } = "";
    public int PlannedS { get; set; }
    public bool Running { get; set; }
    public Metrics? Metrics { get; set; }
    public bool Loadtest { get; set; }
}

/// <summary>Application layer: sessions, saturation test, analyses, settings. No HTTP here (see <see cref="Api"/>).</summary>
public sealed class App
{
    static readonly Regex ShotRx = new(@"^[0-9a-f_]+\.(png|jpg|webp)$", RegexOptions.Compiled);
    static readonly Regex DataUriRx = new(@"^data:image/(png|jpeg|webp);base64,([A-Za-z0-9+/=]+)$", RegexOptions.Compiled);
    static readonly Regex UrlRx = new(@"^https?://[A-Za-z0-9.\-:]+$", RegexOptions.Compiled);
    const int MaxShot = 5_000_000;

    public SessionStore Store { get; }
    public ConfigStore Config { get; }
    public Recorder Rec { get; }
    public LoadTest? Load { get; private set; }
    EnvInfo? env;
    double envT;
    readonly object gate = new();

    public App(string? dataDir = null)
    {
        Store = new SessionStore(dataDir);
        Config = new ConfigStore(Store.DataDir);
        Rec = new Recorder(Store);
    }

    // ------------------------------------------------------------------ environment
    public EnvInfo GetEnv(bool refresh = false)
    {
        if (refresh || env is null || Clock.Now() - envT > 30) { env = SysInfo.Collect(); envT = Clock.Now(); }
        return env;
    }

    // ------------------------------------------------------------------ request helpers
    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        // NaN and Infinity parse fine but slip through Math.Min/Max bounds and (int) casts: only finite numbers pass
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : throw new ApiException(Loc.T("err.invalid_number"));
        if (v.TryGetValue<string>(out var s)) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) ? x : throw new ApiException(Loc.T("err.invalid_number"));
        return null;
    }

    static string? Str(JsonObject b, string k) => b[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ------------------------------------------------------------------ sessions
    public int StartSession(JsonObject body, string labelDefault = "", double minutesDefault = 15)
    {
        lock (gate)
        {
            if (Rec.Running) throw new ApiException(Loc.T("err.running"), 409);
            var cfg = Config.Load();
            var custom = Str(body, "custom_target")?.Trim() ?? cfg.CustomTarget;
            try { Recorder.ParseCustom(custom); }
            catch (ArgumentException e) { throw new ApiException(e.Message); }
            double minutes;
            try { minutes = Math.Min(180.0, Math.Max(1.0, Num(body["minutes"]) ?? minutesDefault)); }
            catch (ApiException) { throw new ApiException(Loc.T("err.invalid_duration")); }
            var link = Str(body, "link") ?? "auto";
            if (link is not ("auto" or "wifi" or "ethernet")) throw new ApiException(Loc.T("err.invalid_link"));
            var e2 = GetEnv(refresh: true);
            List<Target> targets;
            try { targets = Recorder.BuildTargets(e2, custom, cfg.GatewayOverride); }
            catch (ArgumentException e) { throw new ApiException(e.Message); }
            var snap = new ConfigSnapshot { Router = cfg.Router, PlanDownMbps = cfg.PlanDownMbps, PlanUpMbps = cfg.PlanUpMbps };
            var label = Str(body, "label");
            label = string.IsNullOrEmpty(label) ? labelDefault : label;
            if (label.Length > 80) label = label[..80];
            if (custom != cfg.CustomTarget) Config.Update(c => c.CustomTarget = custom);  // only once everything else is accepted
            Load = null;
            try { return Rec.Start(e2, targets, minutes, label, link, snap); }
            catch (InvalidOperationException e) { throw new ApiException(e.Message, 409); }  // still closing the previous session: a clean 409, not a server error
        }
    }

    public async Task<int?> StopSessionAsync()
    {
        if (Load is { State: "running" })
        {
            Load.Cancel();
            // whatever the load test ends with (timeout, cancellation, a fault), the recording below must still be stopped and saved
            if (Load.Task != null) try { await Load.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
        var sid = Rec.Sid;
        bool was = Rec.Running;
        await Rec.StopAsync();
        if (was && sid.HasValue) PersistMetrics(sid.Value);
        return sid;
    }

    public object Mark(string? note)
    {
        bool auto = false;
        if (!Rec.Running) { StartSession(new JsonObject(), Loc.T("label.auto_mark")); auto = true; }
        Rec.MarkNow("lag", (note ?? "").Length > 200 ? note![..200] : note ?? "");
        var hosts = Rec.Targets.Where(t => t.Id is "cloudflare" or "custom").Select(t => t.Host).ToList();
        Rec.TraceAsync(hosts);
        return new { auto_started = auto, traceroute = hosts };
    }

    // ------------------------------------------------------------------ saturation test
    public LoadConfig LoadCfg(JsonObject body)
    {
        var c = new LoadConfig();
        int Clamp(string k, int lo, int hi, int cur) => Num(body[k]) is { } v ? (int)Math.Min(hi, Math.Max(lo, v)) : cur;
        try
        {
            c.Streams = Clamp("streams", 1, 8, c.Streams);
            c.CapDownMb = Clamp("cap_down_mb", 10, 2000, c.CapDownMb);
            c.CapUpMb = Clamp("cap_up_mb", 10, 1000, c.CapUpMb);
            if (body["phase_s"] != null) c.Phases = LoadConfig.DefaultPhases(Clamp("phase_s", 5, 30, 15));
        }
        catch (ApiException) { throw new ApiException(Loc.T("err.invalid_loadtest")); }
        var url = Str(body, "base_url");
        if (!string.IsNullOrEmpty(url))
        {
            if (!UrlRx.IsMatch(url)) throw new ApiException(Loc.T("err.invalid_server_url"));
            c.BaseUrl = url;
        }
        return c;
    }

    public LoadEstimate Estimate(JsonObject body)
    {
        var cfg = Config.Load();
        return LoadTest.Estimate(LoadCfg(body), cfg.PlanDownMbps, cfg.PlanUpMbps);
    }

    public LoadEstimate StartLoadtest(JsonObject body)
    {
        var lc = LoadCfg(body);
        var live = Config.Load();
        var est = LoadTest.Estimate(lc, live.PlanDownMbps, live.PlanUpMbps);
        if (!(body["confirm"] is JsonValue cv && cv.TryGetValue<bool>(out var ok) && ok))
            throw new ApiException(Loc.T("err.confirm_required"), 412, new() { ["estimate"] = est });
        lock (gate)  // check, session start and Load assignment are one step: two simultaneous requests cannot both start a test
        {
            if (Load is { State: "running" }) throw new ApiException(Loc.T("err.test_running"), 409);
            AfterLoadtestCheck?.Invoke();
            if (!Rec.Running)
                StartSession(new JsonObject { ["minutes"] = est.DurationS / 60.0 + 1, ["link"] = Str(body, "link") ?? "auto", ["label"] = Str(body, "label") ?? Loc.T("label.saturation_test") }, Loc.T("label.saturation_test"));
            else if (Rec.Status().RemainingS < est.DurationS + 5)
                throw new ApiException(Loc.T("err.session_too_short", est.DurationS), 409);
            Load = new LoadTest(Rec, lc);
            Load.Start();
            return est;
        }
    }

    /// <summary>Test seam: called under the lock, right after the "already running" check.</summary>
    public Action? AfterLoadtestCheck { get; set; }

    // ------------------------------------------------------------------ analyses
    public AppConfig CfgFor(SessionData d) => Diagnose.ConfigFor(d, Config.Load());

    public (SessionData Data, Analysis A) AnalysisOf(int sid)
    {
        var d = Store.Load(sid) ?? throw new ApiException(Loc.T("err.session_not_found"), 404);
        return (d, Diagnose.Analyze(d, CfgFor(d)));
    }

    public Metrics PersistMetrics(int sid)
    {
        var (_, a) = AnalysisOf(sid);
        var h = Store.LoadHeader(sid);
        if (h != null) { h.Meta.Metrics = a.Metrics; Store.SaveHeader(h); }
        return a.Metrics;
    }

    public List<SessionSummary> Sessions()
    {
        var res = new List<SessionSummary>();
        foreach (var h in Store.List())
        {
            bool running = Rec.Running && Rec.Sid == h.Id;
            var m = h.Meta.Metrics;
            if (m is null && !running && h.Ended != null)
            {
                try { m = PersistMetrics(h.Id); }
                catch (Exception) { }  // one unreadable session must not hide all the others: it is listed without metrics
            }
            res.Add(new SessionSummary { Id = h.Id, Started = h.Started, Ended = h.Ended, Label = h.Label, Link = h.Link, PlannedS = h.PlannedS, Running = running, Metrics = m, Loadtest = h.Meta.Loadtest != null });
        }
        return res;
    }

    public object SeriesPayload(int sid)
    {
        var d = Store.Load(sid) ?? throw new ApiException(Loc.T("err.session_not_found"), 404);
        return new
        {
            started = d.Started, ended = d.EndOrLast, targets = d.Targets, marks = d.Marks, phases = d.Phases,
            series = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => new object?[] { s.T, s.V, s.Ok ? 1 : 0 }).ToList()),
        };
    }

    public object SessionDetail(int sid)
    {
        var (d, a) = AnalysisOf(sid);
        var meta = new SessionMeta { Env = d.Meta.Env, Targets = d.Meta.Targets, IntervalS = d.Meta.IntervalS, Wifi = d.Meta.Wifi, WifiNeighbors = d.Meta.WifiNeighbors, Loadtest = d.Meta.Loadtest, Metrics = d.Meta.Metrics };
        return new { session = new { id = d.Id, started = d.Started, ended = d.EndOrLast, label = d.Label, link = d.Link, planned_s = d.PlannedS, meta }, analysis = a };
    }

    public object RouterView()
    {
        var cfg = Config.Load();
        (double? Down, double? Up) meas = (null, null);
        double worst = 0;
        int? sid = null;
        foreach (var s in Sessions())
        {
            var m = s.Metrics;
            if (s.Loadtest && m != null && (m.DownMbps is > 0 || m.UpMbps is > 0))
            {
                meas = (m.DownMbps, m.UpMbps);
                worst = Math.Max(m.BloatDown ?? 0, m.BloatUp ?? 0);
                sid = s.Id;
                break;
            }
        }
        return new { config = cfg.Router, analysis = RouterQos.Analysis(cfg.Router, meas, cfg, worst), measures_from_session = sid, measured = new { down = meas.Down, up = meas.Up }, qos_types = RouterQos.QosTypes };
    }

    public object Compare(JsonObject body)
    {
        var byId = Sessions().ToDictionary(s => s.Id, s => s.Metrics ?? new Metrics());
        List<Metrics> Group(string k) => (body[k] as JsonArray ?? new JsonArray()).Select(n => (int?)Num(n)).Where(i => i.HasValue && byId.ContainsKey(i.Value)).Select(i => byId[i!.Value]).ToList();
        var a = Group("a");
        var b = Group("b");
        return new Dictionary<string, object> { ["rows"] = Diagnose.Compare(a, b), ["n_a"] = a.Count, ["n_b"] = b.Count };
    }

    public void DeleteOrLabel(int sid, string action, JsonObject body)
    {
        if (Rec.Running && Rec.Sid == sid) throw new ApiException(Loc.T("err.stop_first"), 409);
        if (action == "delete") Store.Delete(sid);
        else
        {
            var h = Store.LoadHeader(sid) ?? throw new ApiException(Loc.T("err.session_not_found"), 404);
            var l = Str(body, "label") ?? "";
            h.Label = l.Length > 80 ? l[..80] : l;
            Store.SaveHeader(h);
        }
    }

    // ------------------------------------------------------------------ router screenshots
    string ShotsDir() => Directory.CreateDirectory(Path.Combine(Store.DataDir, "router_shots")).FullName;

    public string AddShot(string? dataUri)
    {
        var m = DataUriRx.Match(dataUri ?? "");
        if (!m.Success) throw new ApiException(Loc.T("err.invalid_image"));
        byte[] raw;
        try { raw = Convert.FromBase64String(m.Groups[2].Value); }
        catch (FormatException) { throw new ApiException(Loc.T("err.invalid_image")); }
        if (raw.Length > MaxShot) throw new ApiException(Loc.T("err.image_too_large"));
        var name = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{RandomNumberGenerator.GetHexString(8, lowercase: true)}.{(m.Groups[1].Value == "jpeg" ? "jpg" : m.Groups[1].Value)}";
        File.WriteAllBytes(Path.Combine(ShotsDir(), name), raw);
        Config.Update(c => { c.Router ??= new RouterConfig(); c.Router.Screenshots.Add(name); });
        return name;
    }

    public void DelShot(string? name)
    {
        if (name is null || !ShotRx.IsMatch(name)) throw new ApiException(Loc.T("err.invalid_name"));
        Config.Update(c => c.Router?.Screenshots.RemoveAll(n => n == name));
        try { File.Delete(Path.Combine(ShotsDir(), name)); } catch (IOException) { }
    }

    public (byte[] Bytes, string Mime)? ReadShot(string name)
    {
        if (!ShotRx.IsMatch(name)) return null;
        var p = Path.Combine(ShotsDir(), name);
        if (!File.Exists(p)) return null;
        return (File.ReadAllBytes(p), "image/" + (name.EndsWith(".png") ? "png" : name.EndsWith(".jpg") ? "jpeg" : "webp"));
    }

    public List<(string Name, string Uri)> ShotsForReport()
    {
        var res = new List<(string, string)>();
        foreach (var n in Config.Load().Router?.Screenshots ?? new())
            if (ReadShot(n) is { } s) res.Add((n, $"data:{s.Mime};base64,{Convert.ToBase64String(s.Bytes)}"));
        return res;
    }

    // ------------------------------------------------------------------ settings
    public AppConfig SaveConfig(JsonObject body)
    {
        return Config.Update(c =>
        {
            foreach (var k in new[] { "custom_target", "gateway_override" })
            {
                if (!body.ContainsKey(k)) continue;
                var raw = body[k];
                if (raw is not null && !(raw is JsonValue rv && rv.TryGetValue<string>(out _))) throw new ApiException(Loc.T("err.invalid_host", raw.ToJsonString()));
                var v = (raw?.GetValue<string>() ?? "").Trim();
                if (k == "custom_target")
                {
                    try { Recorder.ParseCustom(v); } catch (ArgumentException e) { throw new ApiException(e.Message); }
                    c.CustomTarget = v;
                }
                else c.GatewayOverride = v;
            }
            foreach (var k in new[] { "plan_down_mbps", "plan_up_mbps" })
            {
                if (!body.ContainsKey(k)) continue;
                double? v = body[k] is JsonValue jv && jv.TryGetValue<string>(out var s) && s == "" ? null : Num(body[k]);
                if (k == "plan_down_mbps") c.PlanDownMbps = v; else c.PlanUpMbps = v;
            }
            if (body["router"] is JsonObject r)
            {
                RouterConfig rc;
                try { rc = Json.From<RouterConfig>(r.ToJsonString()) ?? new RouterConfig(); }
                catch (JsonException) { throw new ApiException(Loc.T("err.invalid_router")); }
                rc.QosType ??= "unknown";
                rc.Unit ??= "Mbps";
                if (!RouterQos.QosTypes.Contains(rc.QosType) || rc.Unit is not ("Kbps" or "Mbps" or "Gbps")
                    || (!string.IsNullOrEmpty(rc.SqmAvailable) && !RouterConfig.SqmValues.Contains(rc.SqmAvailable))) throw new ApiException(Loc.T("err.invalid_qos_value"));
                rc.PriorityDevices ??= new();
                rc.BandwidthRules ??= new();
                rc.Screenshots = c.Router?.Screenshots ?? new();
                c.Router = rc;
            }
        });
    }
}
