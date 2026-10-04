using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace NetworkAnalyzer;

public static class Clock
{
    /// <summary>Unix time in seconds (wall clock: it keeps running during a system sleep, which is how pauses are detected).</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

public sealed class TargetState
{
    public string Mode { get; set; } = "icmp";
    public int? Port { get; set; }
    public bool NoResponse { get; set; }
}

public sealed class LiveTarget
{
    public string Label { get; set; } = "";
    public string Role { get; set; } = "";
    public string Host { get; set; } = "";
    public string Mode { get; set; } = "icmp";
    public int? Port { get; set; }
    public bool NoResponse { get; set; }
    public RttStats? Stats { get; set; }
}

public sealed class RecorderStatus
{
    public bool Running { get; set; }
    public int? Sid { get; set; }
    public double Started { get; set; }
    public int PlannedS { get; set; }
    public double RemainingS { get; set; }
    public List<string> Notes { get; set; } = new();
    public string? Phase { get; set; }
    public List<Mark> Marks { get; set; } = new();
    public List<Phase> Phases { get; set; } = new();
}

/// <summary>
/// One monitoring session = several measurement tasks running at the same time + one writer.
/// Traffic generated in normal monitoring: ~1 ping/s per target (≈ 100 bytes) + 1 DNS query / 5 s = a few KB/s.
/// </summary>
public sealed class Recorder
{
    public const double Interval = 1.0;
    public const double GapS = 8.0;      // abnormal delay between two measurements = sleep / freeze of the system
    const int LiveMax = 7200;
    public static readonly (string Id, string Label, string Host)[] InternetTargets =
    {
        ("cloudflare", "Cloudflare (1.1.1.1)", "1.1.1.1"), ("google", "Google (8.8.8.8)", "8.8.8.8"), ("quad9", "Quad9 (9.9.9.9)", "9.9.9.9"),
    };
    static readonly string[] HitNames = { "www.wikipedia.org", "www.microsoft.com", "www.cloudflare.com", "www.mozilla.org" };

    readonly SessionStore store;
    readonly object gate = new();
    readonly Dictionary<string, List<Sample>> live = new();
    readonly Dictionary<string, TargetState> targetState = new();
    readonly List<string> notes = new();
    readonly List<Phase> phases = new();
    readonly List<Mark> marks = new();
    CancellationTokenSource? cts;
    List<Task> tasks = new();
    SessionWriter? writer;
    SessionHeader? header;
    (string Name, double T0, PhaseMeta Meta)? curPhase;
    double lastGap;
    readonly List<Task> traceTasks = new();

    public Recorder(SessionStore store) => this.store = store;

    public bool Running { get; private set; }
    public int? Sid { get; private set; }
    public double Started { get; private set; }
    public int PlannedS { get; private set; }
    public List<Target> Targets { get; private set; } = new();
    public EnvInfo Env { get; private set; } = new();

    // ------------------------------------------------------------------ targets
    public static Target? ParseCustom(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        string host = text;
        int port = 443;
        if (text.Count(c => c == ':') == 1)
        {
            var parts = text.Split(':');
            host = parts[0];
            if (!int.TryParse(parts[1], out port) || port is < 1 or > 65535) throw new ArgumentException(Loc.T("err.invalid_port", parts[1]));
        }
        Probes.ValidateHost(host);
        return new Target { Id = "custom", Host = host, Role = "custom", Family = 0, TcpPort = port };
    }

    public static List<Target> BuildTargets(EnvInfo env, string? custom = "", string? gatewayOverride = "")
    {
        var a = env.Active;
        var t = new List<Target>();
        var gw = string.IsNullOrWhiteSpace(gatewayOverride) ? a?.Gw4 : gatewayOverride;
        if (!string.IsNullOrEmpty(gw))
            t.Add(new Target { Id = "gateway", Host = Probes.ValidateHost(gw), Role = "gateway", Family = 4 });
        else if (a?.Gw6 != null)
        {
            var scope = OperatingSystem.IsWindows() && a.Gw6.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && !a.Gw6.Contains('%') ? $"%{a.Index}" : "";
            t.Add(new Target { Id = "gateway", Host = a.Gw6 + scope, Role = "gateway", Family = 6 });
        }
        foreach (var (id, label, host) in InternetTargets)
            t.Add(new Target { Id = id, Host = host, Role = "internet", Family = 4, TcpPort = 443 });
        if (env.Ipv6Global)
            t.Add(new Target { Id = "cloudflare6", Host = "2606:4700:4700::1111", Role = "internet6", Family = 6, TcpPort = 443 });
        var c = ParseCustom(custom);
        if (c != null) t.Add(c);
        return t;
    }

    // ------------------------------------------------------------------ writing
    public void Emit(string series, double? value, bool ok, string info = "", double? t = null)
    {
        var tt = t ?? Clock.Now();
        lock (gate)
        {
            if (!live.TryGetValue(series, out var l)) live[series] = l = new List<Sample>();
            l.Add(new Sample(tt, value, ok, info));
            if (l.Count > LiveMax) l.RemoveRange(0, l.Count - LiveMax);
        }
        writer?.Sample(tt, series, value, ok, info);
    }

    public void MarkNow(string kind, string note = "", double? t = null)
    {
        var tt = t ?? Clock.Now();
        lock (gate)
        {
            if (kind == "gap")  // several tasks notice the same sleep
            {
                if (tt - lastGap < 3) return;
                lastGap = tt;
            }
            marks.Add(new Mark { T = tt, Kind = kind, Note = note });
        }
        writer?.Mark(tt, kind, note);
    }

    public void SetMeta(Action<SessionMeta> change)
    {
        lock (gate)
        {
            if (header is null) return;
            change(header.Meta);
            store.SaveHeader(header);
        }
    }

    void AddNote(string n) { lock (gate) notes.Add(n); }

    // ------------------------------------------------------------------ life cycle
    public int Start(EnvInfo env, List<Target> targets, double minutes, string label = "", string link = "auto", ConfigSnapshot? snap = null)
    {
        lock (gate)
        {
            if (Running) throw new InvalidOperationException(Loc.T("err.running"));
            if (link == "auto") link = env.Active?.Kind ?? "unknown";
            Started = Clock.Now();
            PlannedS = (int)(minutes * 60);
            header = new SessionHeader
            {
                Started = Started, Label = label, Link = link, PlannedS = PlannedS,
                Meta = new SessionMeta { Env = env, Targets = targets, IntervalS = Interval, CfgSnapshot = snap },
            };
            Sid = store.Create(header);
            writer = store.OpenWriter(Sid.Value);
            Env = env;
            Targets = targets;
            live.Clear(); notes.Clear(); targetState.Clear(); phases.Clear(); marks.Clear(); curPhase = null; lastGap = 0;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            tasks = new List<Task>();
            Running = true;
            var a = env.Active;
            foreach (var tg in targets) tasks.Add(Task.Run(() => PingLoop(tg, ct)));
            if (a?.Dns.Count > 0) tasks.Add(Task.Run(() => DnsLoop(a.Dns[0], ct)));
            else notes.Add("no_dns");
            tasks.Add(Task.Run(() => TrafficLoop(a?.Name, ct)));
            if (a?.Kind == "wifi") tasks.Add(Task.Run(() => WifiLoop(ct)));
            else notes.Add("not_wifi");
            tasks.Add(Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(PlannedS), ct); } catch (OperationCanceledException) { return; }
                _ = Task.Run(StopAsync);
            }));
            return Sid.Value;
        }
    }

    public async Task StopAsync()
    {
        Task[] pending;
        CancellationTokenSource? c;
        lock (gate)
        {
            if (!Running) return;
            Running = false;
            c = cts;
            pending = tasks.ToArray();
        }
        EndPhase(curPhase != null ? new PhaseMeta { Interrupted = true } : null);
        c?.Cancel();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8)); } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        try { await Task.WhenAll(traceTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        lock (gate)
        {
            if (header != null) { header.Ended = Clock.Now(); store.SaveHeader(header); }
        }
        if (writer != null) await writer.CompleteAsync();
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    // ------------------------------------------------------------------ phases (saturation test)
    public void BeginPhase(string name, PhaseMeta? meta = null)
    {
        lock (gate) curPhase = (name, Clock.Now(), meta ?? new PhaseMeta());
    }

    public void EndPhase(PhaseMeta? extra = null)
    {
        (string Name, double T0, PhaseMeta Meta)? p;
        lock (gate) { p = curPhase; curPhase = null; }
        if (p is null) return;
        var m = p.Value.Meta;
        if (extra != null) Merge(m, extra);
        var ph = new Phase { Name = p.Value.Name, T0 = p.Value.T0, T1 = Clock.Now(), Meta = m };
        lock (gate) phases.Add(ph);
        writer?.Phase(ph.Name, ph.T0, ph.T1, m);
    }

    static void Merge(PhaseMeta a, PhaseMeta b)
    {
        foreach (var pr in typeof(PhaseMeta).GetProperties())
        {
            var v = pr.GetValue(b);
            if (v != null) pr.SetValue(a, v);
        }
    }

    // ------------------------------------------------------------------ measurement loops
    void TickGap(double last)
    {
        if (Clock.Now() - last > GapS)
            MarkNow("gap", "", last);
    }

    async Task PingLoop(Target tg, CancellationToken ct)
    {
        try
        {
            var name = $"ping:{tg.Id}";
            var mode = "icmp";
            var pending = new List<(double T, string Info)>();
            bool decided = false;
            var sw = Stopwatch.StartNew();
            double next = 0, last = Clock.Now();
            IPAddress? addr = null;
            double resolvedAt = 0;
            var state = new TargetState { Mode = "icmp" };
            lock (gate) targetState[tg.Id] = state;
            while (!ct.IsCancellationRequested)
            {
                TickGap(last);
                var t = Clock.Now();
                ProbeResult r;
                if (mode == "icmp")
                {
                    if (addr is null || t - resolvedAt > 60)
                    {
                        try { addr = await Probes.ResolveAsync(tg.Host, tg.Family, ct); resolvedAt = t; }
                        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException) { addr = null; }
                    }
                    r = addr is null ? new ProbeResult(false, null, "error") : await Probes.PingAsync(addr, 1000);
                }
                else r = await Probes.TcpAsync(tg.Host, tg.TcpPort!.Value);
                last = Clock.Now();
                if (mode == "icmp" && !decided)
                {
                    // The first 8 seconds without ANY answer are held back before deciding whether ICMP is filtered.
                    if (r.Ok)
                    {
                        decided = true;
                        foreach (var (ft, fi) in pending) Emit(name, null, false, fi, ft);
                        pending.Clear();
                        Emit(name, r.Ms, true, r.Info, t);
                    }
                    else
                    {
                        pending.Add((t, r.Info));
                        if (pending.Count >= 8)
                        {
                            decided = true;
                            if (tg.TcpPort is int port && (await Probes.TcpAsync(tg.Host, port)).Ok)
                            {
                                mode = "tcp";
                                lock (gate) { state.Mode = "tcp"; state.Port = port; }
                            }
                            else lock (gate) state.NoResponse = true;
                            foreach (var (ft, _) in pending) Emit(name, null, false, "icmp_no_reply", ft);
                            pending.Clear();
                        }
                    }
                }
                else
                {
                    if (r.Ok) lock (gate) state.NoResponse = false;
                    Emit(name, r.Ms, r.Ok, r.Info, t);
                }
                next += Interval;
                double wait = next - sw.Elapsed.TotalSeconds;
                if (wait < -5) { next = sw.Elapsed.TotalSeconds; wait = 0; }
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task DnsLoop(string server, CancellationToken ct)
    {
        try
        {
            string? reference = server != "1.1.1.1" ? "1.1.1.1" : null;
            int i = 0;
            double last = Clock.Now();
            while (!ct.IsCancellationRequested)
            {
                TickGap(last);
                var t0 = Clock.Now();
                var r = await Probes.DnsQueryAsync(server, HitNames[i % HitNames.Length]);
                Emit("dns:sys_hit", r.Ms, r.Ok, r.Info, t0);
                if (i % 3 == 0)  // cold resolution: random name under example.com (reserved by IANA)
                {
                    var t1 = Clock.Now();
                    r = await Probes.DnsQueryAsync(server, $"na{Random.Shared.NextInt64():x}.example.com", acceptNxdomain: true);
                    Emit("dns:sys_miss", r.Ms, r.Ok, r.Info, t1);
                    if (reference != null)
                    {
                        var t2 = Clock.Now();
                        r = await Probes.DnsQueryAsync(reference, $"na{Random.Shared.NextInt64():x}.example.com", acceptNxdomain: true);
                        Emit("dns:ref_miss", r.Ms, r.Ok, r.Info, t2);
                    }
                }
                i++;
                last = Clock.Now();
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, 5 - (last - t0))), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Byte counters of ONE interface. Never the sum of several: bridges, VM switches and filter bindings count the same bytes again.</summary>
    static (long Rx, long Tx)? ReadCounters(string? nic)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !SysInfo.IsFilterInstance(n.Name)).ToList();
        var one = nic == null ? null : all.FirstOrDefault(n => n.Name == nic);
        // No known active interface: the busiest physical-looking one.
        one ??= all.Where(n => n.OperationalStatus == OperationalStatus.Up && SysInfo.Kind(n.Name, n.Description, n.NetworkInterfaceType) is "wifi" or "ethernet")
                   .OrderByDescending(n => { var s = n.GetIPv4Statistics(); return s.BytesReceived + s.BytesSent; }).FirstOrDefault();
        if (one is null) return null;
        var st = one.GetIPv4Statistics();
        return (st.BytesReceived, st.BytesSent);
    }

    async Task TrafficLoop(string? nic, CancellationToken ct)
    {
        try
        {
            var first = ReadCounters(nic);
            if (first is null) { AddNote("no_iface"); return; }
            var prev = first.Value;
            double pt = Clock.Now();
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                var now = Clock.Now();
                var cur0 = ReadCounters(nic);
                if (cur0 is null) continue;  // interface momentarily gone (cable pulled, adapter reset): no sample, no fake zero
                var cur = cur0.Value;
                double dt = now - pt;
                if (dt > GapS) { prev = cur; pt = now; continue; }  // sleep: the delta would cover the pause
                if (cur.Rx >= prev.Rx && cur.Tx >= prev.Tx && dt > 0)
                {
                    Emit("net:down_bps", (cur.Rx - prev.Rx) * 8 / dt, true, "", now);
                    Emit("net:up_bps", (cur.Tx - prev.Tx) * 8 / dt, true, "", now);
                }
                prev = cur; pt = now;
            }
        }
        catch (OperationCanceledException) { }
        catch (NetworkInformationException) { AddNote("counters_unreadable"); }
    }

    async Task WifiLoop(CancellationToken ct)
    {
        try
        {
            string? lastBssid = null;
            bool first = true;
            double last = Clock.Now();
            while (!ct.IsCancellationRequested)
            {
                TickGap(last);
                var w = await SysInfo.ReadWifiAsync();
                last = Clock.Now();
                if (w is null)
                {
                    if (first) AddNote("wifi_unavailable");
                }
                else
                {
                    var info = System.Text.Json.JsonSerializer.Serialize(new { channel = w.Channel, band = w.Band, bssid = w.Bssid, radio = w.Radio });
                    Emit("wifi:signal", w.Signal, true, info);
                    if (w.RxRate.HasValue) Emit("wifi:rx", w.RxRate, true);
                    if (w.TxRate.HasValue) Emit("wifi:tx", w.TxRate, true);
                    if (first)
                    {
                        SetMeta(m => m.Wifi = w);
                        var n = await SysInfo.ReadNeighborsAsync(w.Channel);
                        if (n != null) SetMeta(m => m.WifiNeighbors = n);
                    }
                    if (lastBssid != null && !string.IsNullOrEmpty(w.Bssid) && w.Bssid != lastBssid)
                        MarkNow("roam");
                    if (!string.IsNullOrEmpty(w.Bssid)) lastBssid = w.Bssid;
                }
                first = false;
                await Task.Delay(5000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ on-demand path diagnosis
    public void TraceAsync(IEnumerable<string> hosts)
    {
        var hs = hosts.ToList();
        var ct = cts?.Token ?? CancellationToken.None;
        var task = Task.Run(async () =>
        {
            foreach (var h in hs)
            {
                var res = await Probes.TracerouteAsync(h, 20, ct);
                writer?.Trace(Clock.Now(), h, res);
            }
        });
        lock (gate) traceTasks.Add(task);
    }

    // ------------------------------------------------------------------ reading for the UI
    public Dictionary<string, List<object?[]>> LiveSince(double since)
    {
        lock (gate)
            return live.ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => s.T > since).Select(s => new object?[] { s.T, s.V, s.Ok ? 1 : 0, s.Info }).ToList());
    }

    public Dictionary<string, LiveTarget> LiveStats(double seconds = 60)
    {
        var t1 = Clock.Now();
        Dictionary<string, List<Sample>> snap;
        lock (gate) snap = live.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var res = new Dictionary<string, LiveTarget>();
        foreach (var tg in Targets)
        {
            var smp = snap.GetValueOrDefault($"ping:{tg.Id}") ?? new List<Sample>();
            smp = smp.Where(s => s.T >= t1 - seconds && s.Info != "icmp_no_reply").ToList();
            TargetState? st;
            lock (gate) targetState.TryGetValue(tg.Id, out st);
            res[tg.Id] = new LiveTarget { Label = tg.Label, Role = tg.Role, Host = tg.Host, Mode = st?.Mode ?? "icmp", Port = st?.Port, NoResponse = st?.NoResponse ?? false, Stats = Stats.Rtt(smp) };
        }
        return res;
    }

    public RecorderStatus Status()
    {
        lock (gate)
            return new RecorderStatus
            {
                Running = Running, Sid = Sid, Started = Started, PlannedS = PlannedS,
                RemainingS = Running ? Math.Max(0, Started + PlannedS - Clock.Now()) : 0,
                Notes = notes.Select(c => Loc.T("note." + c)).ToList(), Phase = curPhase?.Name,
                Marks = marks.TakeLast(50).ToList(), Phases = phases.TakeLast(20).ToList(),
            };
    }
}
