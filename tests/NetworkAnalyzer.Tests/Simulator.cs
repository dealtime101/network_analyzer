using System.Text.Json;
using NetworkAnalyzer;

namespace NetworkAnalyzer.Tests;

/// <summary>One incident of a simulated session, from second <c>T0</c> to second <c>T1</c> after the start.
/// <para><c>Kind</c>: <c>gw_loss</c> (the gateway and everything behind it lose packets and slow down: local network),
/// <c>inet_all</c> (all Internet targets degrade, the gateway stays healthy: upstream), <c>inet_one</c> (a single target degrades:
/// path to it; its id is <c>Opt</c>), <c>custom_only</c> (only the custom destination degrades), <c>bg_traffic</c> (the PC itself
/// moves <c>Mbps</c> of traffic and latency to the Internet rises with it).</para>
/// <para><c>Opt</c>: the target id for <c>inet_one</c>, unused otherwise. <c>Mbps</c>: traffic of the PC, for <c>bg_traffic</c> only.</para></summary>
public sealed record SimEvent(string Kind, double T0, double T1, string? Opt = null, double Mbps = 0);

public sealed class LoadSim
{
    public double DownDelta = 25, UpDelta = 260, GwDown = 1, GwUp = 2, MbpsDown = 300, MbpsUp = 30;
}

public sealed class SimParams
{
    public int Minutes = 15;
    public string Link = "wifi";
    public Dictionary<string, double> Base = new() { ["gateway"] = 2.0, ["cloudflare"] = 18.0, ["google"] = 20.0, ["quad9"] = 22.0, ["custom"] = 35.0 };
    public double DnsHit = 15.0, DnsMiss = 70.0, RefMiss = 65.0, Signal = 78, Tx = 500.0, BgMbps = 0.2;
    public List<SimEvent> Events = new();
    public LoadSim? Load;
    public List<double> Marks = new();
    public List<double> Roam = new();
    public WifiNeighbors? Neighbors;
    public List<string> IcmpBlocked = new();
}

/// <summary>Deterministic simulated network incidents, to test the statistics and the diagnosis rules.
/// <see cref="Make"/> builds a whole session (pings, DNS, traffic, Wi-Fi, marks, an optional load test) for a named scenario and a seed:
/// the same pair always gives the same data. <see cref="Scenarios"/> lists the scenario names.</summary>
public static class Simulator
{
    public const double T0 = 1_760_000_000.0;

    /// <summary>The simulated targets. A fresh list of fresh objects on every call: a test that changes one cannot affect another test.</summary>
    public static IReadOnlyList<Target> Targets => new List<Target>
    {
        new() { Id = "gateway", Host = "192.168.0.1", Role = "gateway" },
        new() { Id = "cloudflare", Host = "1.1.1.1", Role = "internet" },
        new() { Id = "google", Host = "8.8.8.8", Role = "internet" },
        new() { Id = "quad9", Host = "9.9.9.9", Role = "internet" },
        new() { Id = "custom", Host = "game.example.net", Role = "custom" },
    };

    public static readonly string[] Scenarios =
        { "healthy", "wifi_unstable", "bufferbloat", "isp", "isp_single_path", "custom_only", "dns", "background", "saturation", "wired_router", "icmp_blocked" };

    static SimParams Scenario(string scn) => scn switch
    {
        "healthy" => new() { Marks = { 400 } },
        "wifi_unstable" => new() { Signal = 38, Events = { new("gw_loss", 200, 235), new("gw_loss", 520, 560) }, Marks = { 215 }, Roam = { 205 }, Neighbors = new WifiNeighbors { Total = 12, SameChannelStrong = 4 } },
        "bufferbloat" => new() { Load = new LoadSim(), Link = "ethernet" },
        "isp" => new() { Events = { new("inet_all", 300, 360), new("inet_all", 620, 660) }, Marks = { 320 }, Link = "ethernet" },
        "isp_single_path" => new() { Events = { new("inet_one", 300, 360, "google"), new("inet_one", 620, 660, "google") }, Marks = { 320 }, Link = "ethernet" },
        "custom_only" => new() { Events = { new("custom_only", 300, 350), new("custom_only", 600, 640) }, Marks = { 320 }, Link = "ethernet" },
        "dns" => new() { DnsHit = 190.0, DnsMiss = 800.0, RefMiss = 60.0, Link = "ethernet", Marks = { 300 } },
        "background" => new() { Events = { new("bg_traffic", 250, 290, Mbps: 45.0), new("bg_traffic", 520, 560, Mbps: 45.0) }, Marks = { 270 }, Link = "ethernet" },
        "saturation" => new() { Events = { new("bg_traffic", 250, 300, Mbps: 92.0), new("bg_traffic", 560, 610, Mbps: 92.0) }, Marks = { 270 }, Link = "ethernet" },
        "wired_router" => new() { Events = { new("gw_loss", 200, 240), new("gw_loss", 500, 540) }, Marks = { 220 }, Link = "ethernet" },
        "icmp_blocked" => new() { Link = "ethernet", IcmpBlocked = { "custom" } },
        _ => throw new ArgumentException(scn),
    };

    static readonly (string Name, int A, int B)[] PhaseSchedule = { ("idle", 30, 40), ("download", 60, 75), ("recovery1", 80, 90), ("upload", 100, 115), ("recovery2", 120, 130) };

    public static SessionData Make(string scn, int seed = 7, Action<SimParams>? tweak = null)
    {
        var p = Scenario(scn);
        tweak?.Invoke(p);
        var r = new Random(seed);
        double Gauss(double sd) { double u1 = 1 - r.NextDouble(), u2 = r.NextDouble(); return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sd; }
        double Choice(params double[] v) => v[r.Next(v.Length)];
        double Uniform(double a, double b) => a + (b - a) * r.NextDouble();
        (double Add, double Loss) Effect(string tid, string role, double t)
        {
            double add = 0, loss = 0.002;
            foreach (var ev in p.Events)
            {
                if (t < ev.T0 || t > ev.T1) continue;
                switch (ev.Kind)
                {
                    case "gw_loss":
                        add += role != "custom" ? Choice(0, 40, 120, 300) : 0;
                        add += role == "custom" ? Choice(0, 60, 200) : 0;
                        loss = Math.Max(loss, 0.22);
                        break;
                    case "inet_all" when role is "internet" or "custom": add += Choice(0, 90, 180, 400); loss = Math.Max(loss, 0.15); break;
                    case "inet_one" when tid == ev.Opt: add += Choice(0, 120, 300); loss = Math.Max(loss, 0.2); break;
                    case "custom_only" when tid == "custom": add += Choice(0, 150, 350); loss = Math.Max(loss, 0.2); break;
                    case "bg_traffic" when role is "internet" or "custom": add += Choice(0, 60, 150, 250); break;
                }
            }
            return (add, loss);
        }
        string? PhaseAt(int t) => p.Load is null ? null : PhaseSchedule.Where(x => x.A <= t && t < x.B).Select(x => x.Name).FirstOrDefault();

        int n = p.Minutes * 60;
        var series = new Dictionary<string, List<Sample>>();
        void Add(string name, double t, double? v, bool ok = true, string info = "")
        {
            if (!series.TryGetValue(name, out var l)) series[name] = l = new List<Sample>();
            l.Add(new Sample(T0 + t, v, ok, info));
        }
        for (int t = 0; t < n; t++)
        {
            var ph = PhaseAt(t);
            foreach (var tg in Targets)
            {
                if (p.IcmpBlocked.Contains(tg.Id)) { Add($"ping:{tg.Id}", t, null, false, "timeout"); continue; }
                var (eAdd, eLoss) = Effect(tg.Id, tg.Role, t);
                double bas = p.Base[tg.Id], extra = 0;
                if (p.Load != null && ph == "download") extra = tg.Role == "gateway" ? p.Load.GwDown : p.Load.DownDelta;
                else if (p.Load != null && ph == "upload") extra = tg.Role == "gateway" ? p.Load.GwUp : p.Load.UpDelta;
                if (r.NextDouble() < eLoss) Add($"ping:{tg.Id}", t, null, false, "timeout");
                else Add($"ping:{tg.Id}", t, bas + Math.Abs(Gauss(bas * 0.08)) + eAdd + extra * Uniform(0.8, 1.2));
            }
            if (t % 5 == 0)
            {
                bool slow = p.Events.Any(ev => ev.Kind == "bg_traffic" && ev.T0 <= t && t <= ev.T1);
                Add("dns:sys_hit", t, p.DnsHit * Uniform(0.7, 1.4) * (slow ? 3 : 1));
                if (t % 15 == 0)
                {
                    Add("dns:sys_miss", t, p.DnsMiss * Uniform(0.7, 1.4));
                    Add("dns:ref_miss", t, p.RefMiss * Uniform(0.7, 1.4));
                }
            }
            double down = p.BgMbps * Uniform(0.5, 1.5), up = down;
            foreach (var ev in p.Events)
                if (ev.Kind == "bg_traffic" && ev.T0 <= t && t <= ev.T1) { down = ev.Mbps * Uniform(0.9, 1.05); up = ev.Mbps * 0.1; }
            if (p.Load != null && ph == "download") { down = p.Load.MbpsDown * Uniform(0.95, 1.05); Add("load:down_bps", t, down * 1e6); }
            if (p.Load != null && ph == "upload") { up = p.Load.MbpsUp * Uniform(0.95, 1.05); Add("load:up_bps", t, up * 1e6); }
            Add("net:down_bps", t, down * 1e6);
            Add("net:up_bps", t, up * 1e6);
            if (p.Link == "wifi" && t % 5 == 0)
            {
                double sig = p.Signal;
                if (p.Events.Any(ev => ev.Kind == "gw_loss" && ev.T0 <= t && t <= ev.T1)) { sig = Math.Max(10, sig - 25); Add("wifi:tx", t, p.Tx * 0.3); }
                else Add("wifi:tx", t, p.Tx);
                Add("wifi:signal", t, sig + r.Next(-3, 4));
            }
        }
        var marks = p.Marks.Select(m => new Mark { T = T0 + m, Kind = "lag" }).Concat(p.Roam.Select(m => new Mark { T = T0 + m, Kind = "roam" })).OrderBy(m => m.T).ToList();
        var phases = p.Load != null ? PhaseSchedule.Select(x => new Phase { Name = x.Name, T0 = T0 + x.A, T1 = T0 + x.B }).ToList() : new List<Phase>();
        return new SessionData
        {
            Id = 1, Started = T0, Ended = T0 + n, Label = scn, Link = p.Link, PlannedS = n,
            Meta = new SessionMeta { Targets = Targets.Select(t => new Target { Id = t.Id, Host = t.Host, Role = t.Role }).ToList(), WifiNeighbors = p.Neighbors },
            Series = series, Marks = marks, Phases = phases,
        };
    }

    public static string ToJson(object o) => JsonSerializer.Serialize(o, Json.Options);
}
