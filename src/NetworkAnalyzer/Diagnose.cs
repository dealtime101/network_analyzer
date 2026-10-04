using System.Globalization;
using System.Text.Json.Serialization;

namespace NetworkAnalyzer;

// ---------------------------------------------------------------------- output model (JSON names = snake_case of the properties)
public sealed class Hypothesis
{
    /// <summary>lan | bufferbloat | saturation | router_qos | isp | dns | background</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public double Score { get; set; }
    public List<string> Evidence { get; set; } = new();
    public List<string> Counter { get; set; } = new();
    public List<string> Limits { get; set; } = new();
    public string NextTest { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    /// <summary>low | medium | high</summary>
    public string Level { get; set; } = "low";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<RouterFinding>? Findings { get; set; }
    [JsonIgnore] public string? NotEvaluatedReason { get; set; }
}

public sealed class TargetFacts
{
    public string Role { get; set; } = "";
    public string Label { get; set; } = "";
    public RttStats? Stats { get; set; }
    public bool Bad { get; set; }
}

public sealed class WindowFacts
{
    public double T0 { get; set; }
    public double T1 { get; set; }
    public Dictionary<string, TargetFacts> Targets { get; set; } = new();
    public RttStats? DnsHit { get; set; }
    public RttStats? DnsMiss { get; set; }
    public double? NetDownMed { get; set; }
    public double? NetDownMax { get; set; }
    public double? NetUpMed { get; set; }
    public double? NetUpMax { get; set; }
    public double? WifiSignalMin { get; set; }
}

public sealed class TimelineItem
{
    /// <summary>lag | episode | gap | roam</summary>
    public string Type { get; set; } = "";
    public double T { get; set; }
    public double? T0 { get; set; }
    public double? T1 { get; set; }
    /// <summary>local | upstream | path | custom_path | dns | none | undetermined (null for gap and roam)</summary>
    public string? Zone { get; set; }
    public string ZoneText { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> Details { get; set; } = new();
    [JsonIgnore] public WindowFacts? Facts { get; set; }
}

public sealed class BloatTarget
{
    public string Role { get; set; } = "";
    public string Label { get; set; } = "";
    public double IdleMed { get; set; }
    public double LoadMed { get; set; }
    public double? LoadP95 { get; set; }
    public double Delta { get; set; }
    public double LossPct { get; set; }
}

public sealed class BloatRow
{
    public Dictionary<string, BloatTarget> Targets { get; set; } = new();
    public double? GwDelta { get; set; }
    public double? Delta { get; set; }
    public double? LoadP95 { get; set; }
    public double? IdleMed { get; set; }
    public double? LoadMed { get; set; }
    public double? LossPct { get; set; }
    public string? Grade { get; set; }
    public double? Mbps { get; set; }
    public bool Valid { get; set; }
}

public sealed class BloatResult
{
    public Dictionary<string, BloatRow> Directions { get; set; } = new();
}

public sealed class TargetRow
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>ok | tcp | no_response</summary>
    public string State { get; set; } = "ok";
    public string StateText { get; set; } = "";
    public RttStats? Stats { get; set; }
}

public sealed class DnsStats
{
    public RttStats? SysHit { get; set; }
    public RttStats? SysMiss { get; set; }
    public RttStats? RefMiss { get; set; }
}

public sealed class TrafficPart
{
    public double? Median { get; set; }
    public double? P95 { get; set; }
    public double? Max { get; set; }
}

public sealed class TrafficSummary
{
    public TrafficPart? Down { get; set; }
    public TrafficPart? Up { get; set; }
}

public sealed class WifiSummary
{
    public double? SignalMed { get; set; }
    public double? SignalMin { get; set; }
    public double? TxMed { get; set; }
    public double? TxMin { get; set; }
    public double? RxMed { get; set; }
    public int? Channel { get; set; }
    public string? Band { get; set; }
    public string? Radio { get; set; }
}

public sealed class PhaseRow
{
    public string Name { get; set; } = "";
    public double T0 { get; set; }
    public double T1 { get; set; }
    public PhaseMeta Meta { get; set; } = new();
    public Dictionary<string, RttStats?> Targets { get; set; } = new();
}

public sealed class StatsTables
{
    public List<TargetRow> Targets { get; set; } = new();
    public DnsStats Dns { get; set; } = new();
    public TrafficSummary Traffic { get; set; } = new();
    public WifiSummary? Wifi { get; set; }
    public List<PhaseRow> Phases { get; set; } = new();
}

public sealed class Metrics
{
    public double? InetMed { get; set; }
    public double? InetP95 { get; set; }
    public double? InetLoss { get; set; }
    public double? JitterInet { get; set; }
    public double? GwMed { get; set; }
    public double? GwP95 { get; set; }
    public double? GwLoss { get; set; }
    public double? DnsMed { get; set; }
    public double? BloatDown { get; set; }
    public double? BloatUp { get; set; }
    public double? DownMbps { get; set; }
    public double? UpMbps { get; set; }
    public double? WifiSignal { get; set; }
    public int Incidents { get; set; }
}

public sealed class UnlikelyItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Reasons { get; set; } = new();
}

public sealed class NotEvaluatedItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string NextTest { get; set; } = "";
}

public sealed class Analysis
{
    public int Session { get; set; }
    public List<string> Summary { get; set; } = new();
    public List<Hypothesis> Hypotheses { get; set; } = new();
    public List<UnlikelyItem> Unlikely { get; set; } = new();
    public List<NotEvaluatedItem> NotEvaluated { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public List<TimelineItem> Timeline { get; set; } = new();
    public StatsTables Stats { get; set; } = new();
    public BloatResult? Bufferbloat { get; set; }
    public List<string> GeneralLimits { get; set; } = new();
    public Dictionary<string, object> Thresholds { get; set; } = new();
    public Metrics Metrics { get; set; } = new();
}

public sealed class CompareRow
{
    public string Metric { get; set; } = "";
    public double A { get; set; }
    public double B { get; set; }
    public double Delta { get; set; }
    public double Variability { get; set; }
    public int CountA { get; set; }
    public int CountB { get; set; }
    /// <summary>improvement | degradation | indistinct | indicative</summary>
    public string Verdict { get; set; } = "";
    public string VerdictText { get; set; } = "";
}

/// <summary>
/// Diagnosis engine: measurements → ranked hypotheses, each with evidence, counter-evidence, limits and a next useful test.
/// A conclusion is always a HYPOTHESIS ("compatible with…"), never a confirmed cause. The confidence level (low / medium /
/// high) comes from a score accumulating weighted clues; the thresholds are gathered in <see cref="Th"/> for review.
/// </summary>
public static class Th
{
    public const double GwLossPct = 1.0, GwP95Ms = 30.0, GwMaxMs = 150.0, GwJitterMs = 8.0, GwCleanP95Ms = 15.0;
    public const double WifiWeakPct = 50, WifiVeryWeakPct = 30;
    public static readonly (double Lim, double Score)[] BloatDeltaMs = { (200, 7.0), (100, 6.0), (50, 4.5), (30, 3.0), (15, 1.0) };
    public const double BloatMinMbps = 1.0, BloatWarmupS = 3;
    public const double DnsMedMs = 100.0, DnsP95Ms = 300.0, DnsMissMedMs = 300.0, DnsFailPct = 2.0;
    public const double BgDownMbps = 3.0, BgUpMbps = 1.0, BgIdleMbps = 1.0, BgSpikeRatio = 3.0;
    public const double SatRatio = 0.8, BusyMbps = 5.0;
    public const int MaxEpisodes = 40, EpisodeGapS = 8, EpisodeMinEvents = 3, IncidentBeforeS = 45, IncidentAfterS = 30, MinSamples = 60;

    public static Dictionary<string, object> AsDictionary() => new()
    {
        ["gw_loss_pct"] = GwLossPct, ["gw_p95_ms"] = GwP95Ms, ["gw_max_ms"] = GwMaxMs, ["gw_jitter_ms"] = GwJitterMs, ["gw_clean_p95_ms"] = GwCleanP95Ms,
        ["wifi_weak_pct"] = WifiWeakPct, ["wifi_very_weak_pct"] = WifiVeryWeakPct,
        ["bloat_delta_ms"] = BloatDeltaMs.Select(b => new[] { b.Lim, b.Score }).ToList(), ["bloat_min_mbps"] = BloatMinMbps, ["bloat_warmup_s"] = BloatWarmupS,
        ["dns_med_ms"] = DnsMedMs, ["dns_p95_ms"] = DnsP95Ms, ["dns_miss_med_ms"] = DnsMissMedMs, ["dns_fail_pct"] = DnsFailPct,
        ["bg_down_mbps"] = BgDownMbps, ["bg_up_mbps"] = BgUpMbps, ["bg_idle_mbps"] = BgIdleMbps, ["bg_spike_ratio"] = BgSpikeRatio,
        ["sat_ratio"] = SatRatio, ["busy_mbps"] = BusyMbps, ["episode_gap_s"] = EpisodeGapS, ["episode_min_events"] = EpisodeMinEvents,
        ["incident_before_s"] = IncidentBeforeS, ["incident_after_s"] = IncidentAfterS, ["min_samples"] = MinSamples,
    };
}

public sealed class TargetCtx
{
    public Target Tg { get; init; } = new();
    public List<Sample> Raw { get; init; } = new();
    public List<Sample> Calm { get; init; } = new();
    public RttStats? Stats { get; init; }
    public string State { get; init; } = "ok";
    public double? Base { get; init; }
}

/// <summary>Prepared data of a session (outside load phases, except the idle one).</summary>
public sealed class Ctx
{
    public SessionData D { get; }
    public List<(double A, double B)> Excl { get; }
    public Dictionary<string, TargetCtx> T { get; } = new();
    public TargetCtx? Gw { get; }
    public List<TargetCtx> Inet { get; }
    public string Link { get; }
    /// <summary>Real episodes left out of the timeline because of Th.MaxEpisodes (set by BuildTimeline).</summary>
    public int EpisodesDropped { get; set; }

    public Ctx(SessionData data)
    {
        D = data;
        Excl = data.Phases.Where(p => p.Name != "idle").Select(p => (p.T0, p.T1)).ToList();
        foreach (var tg in data.Targets)
        {
            var raw = Diagnose.Usable(data.S($"ping:{tg.Id}"));
            var calm = Stats.Outside(raw, Excl);
            var ok = Stats.Values(calm);
            T[tg.Id] = new TargetCtx { Tg = tg, Raw = raw, Calm = calm, Stats = Stats.Rtt(calm), State = Diagnose.TargetState(raw), Base = ok.Count > 0 ? Stats.Median(ok) : null };
        }
        var gw = T.GetValueOrDefault("gateway");
        Gw = gw != null && gw.State != "no_response" ? gw : null;
        Inet = T.Values.Where(x => x.Tg.Role == "internet" && x.State != "no_response").ToList();
        Link = string.IsNullOrEmpty(data.Link) ? "unknown" : data.Link;
    }

    public List<Sample> Series(string name) => Stats.Outside(D.S(name), Excl);
}

public static partial class Diagnose
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly (double Lim, string G)[] Grades = { (5, "A+"), (30, "A"), (60, "B"), (200, "C"), (400, "D") };

    static string T(string key, params object?[] args) => Loc.T(key, args);

    // ------------------------------------------------------------------ helpers
    // numbers written into sentences follow the reader's language (decimal comma in French)
    static string F0(double x) => x.ToString("0", Loc.Fmt);
    static string F1(double x) => x.ToString("0.0", Loc.Fmt);
    static string F2(double x) => x.ToString("0.00", Loc.Fmt);
    static string G(double x) => x.ToString("0.######", Loc.Fmt);
    static string FmtF1(double? x) => x is null ? "—" : F1(x.Value);
    static string FmtMs(double? x) => x is null ? "—" : F0(x.Value) + " ms";
    static double? Mbps(double? bps) => bps is null ? null : bps / 1e6;
    static int Z(Dictionary<string, int> z, string k) => z.GetValueOrDefault(k);
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Cap and round the score first, then derive the level from the number the user will see.</summary>
    public static void Finalise(Hypothesis h)
    {
        h.Score = Math.Round(Math.Min(h.Score, 10.0), 1, MidpointRounding.ToEven);
        h.Level = Level(h.Score);
    }

    public static string Level(double score) => score >= 7 ? "high" : score >= 4 ? "medium" : "low";

    public static string Grade(double delta)
    {
        foreach (var (lim, g) in Grades) if (delta < lim) return g;
        return "F";
    }

    public static double Thr(string kind, double? baseMs)
    {
        double b = baseMs ?? 0;
        return kind == "gateway" ? Math.Max(Math.Max(20.0, b * 3), b + 15) : Math.Max(b * 2.0, b + 40);
    }

    /// <summary>Drops the first seconds "without any answer" when the target was finally measured over TCP.</summary>
    public static List<Sample> Usable(IReadOnlyList<Sample> s)
        => s.Any(x => x.Info == "tcp") ? s.Where(x => x.Info != "icmp_no_reply").ToList() : s.ToList();

    public static string TargetState(IReadOnlyList<Sample> s)
    {
        if (s.Any(x => x.Info == "tcp")) return "tcp";
        if (s.Count >= 8 && !s.Any(x => x.Ok)) return "no_response";
        return "ok";
    }

    static bool Degraded(Sample s, double limit) => !s.Ok || (s.V.HasValue && s.V.Value > limit);

    /// <summary>Degraded window: at least max(3, 10 %) samples lost or above the threshold.</summary>
    public static bool IsBad(IReadOnlyList<Sample> samples, double? baseMs, string kind)
    {
        if (samples.Count < 3) return false;
        double limit = Thr(kind, baseMs);
        return samples.Count(s => Degraded(s, limit)) >= Math.Max(3, 0.1 * samples.Count);
    }

    public static bool PcBusy(WindowFacts f) => (f.NetDownMax ?? 0) >= Th.BusyMbps || (f.NetUpMax ?? 0) >= Th.BusyMbps;

    static string KindOf(Target t) => t.Role == "gateway" ? "gateway" : "internet";

    // ------------------------------------------------------------------ windows
    public static WindowFacts WindowFactsOf(Ctx cx, double t0, double t1)
    {
        var f = new WindowFacts { T0 = t0, T1 = t1 };
        foreach (var (tid, x) in cx.T)
        {
            var w = Stats.Window(x.Raw, t0, t1);
            if (x.State == "no_response") continue;
            f.Targets[tid] = new TargetFacts { Role = x.Tg.Role, Label = x.Tg.Label, Stats = Stats.Rtt(w), Bad = IsBad(w, x.Base, KindOf(x.Tg)) };
        }
        f.DnsHit = Stats.Rtt(Stats.Window(cx.D.S("dns:sys_hit"), t0, t1));
        f.DnsMiss = Stats.Rtt(Stats.Window(cx.D.S("dns:sys_miss"), t0, t1));
        var dn = Stats.Values(Stats.Window(cx.D.S("net:down_bps"), t0, t1));
        var up = Stats.Values(Stats.Window(cx.D.S("net:up_bps"), t0, t1));
        f.NetDownMed = Mbps(Stats.Median(dn)); f.NetDownMax = dn.Count > 0 ? Mbps(dn.Max()) : null;
        f.NetUpMed = Mbps(Stats.Median(up)); f.NetUpMax = up.Count > 0 ? Mbps(up.Max()) : null;
        var sig = Stats.Values(Stats.Window(cx.D.S("wifi:signal"), t0, t1));
        f.WifiSignalMin = sig.Count > 0 ? sig.Min() : null;
        return f;
    }

    static bool DnsBad(WindowFacts f)
    {
        var h = f.DnsHit;
        return h != null && h.N >= 2 && (h.LossPct >= Th.DnsFailPct || (h.Median ?? 0) >= Th.DnsMedMs);
    }

    /// <summary>Uncached ("cold") lookups: slow or failing, judged on at least 3 of them (they are sent only every 15 s,
    /// so a window holds few) against their own, higher threshold: a normal cold lookup takes tens of milliseconds.</summary>
    static bool DnsMissBad(WindowFacts f)
    {
        var m = f.DnsMiss;
        return m != null && m.N >= 3 && (m.LossPct >= Th.DnsFailPct || (m.Median ?? 0) >= Th.DnsMissMedMs);
    }

    public static string Localize(WindowFacts f)
    {
        var gw = f.Targets.Values.Where(v => v.Role == "gateway").ToList();
        var inet = f.Targets.Values.Where(v => v.Role == "internet").ToList();
        var cust = f.Targets.Values.Where(v => v.Role == "custom").ToList();
        var badInet = inet.Where(v => v.Bad).ToList();
        if (gw.Count > 0 && gw[0].Bad) return "local";
        if (badInet.Count >= 2 || (inet.Count == 1 && badInet.Count > 0)) return "upstream";
        if (badInet.Count > 0) return "path";
        if (cust.Any(v => v.Bad)) return "custom_path";  // any custom target, not only the first one
        if (DnsBad(f) || DnsMissBad(f)) return "dns";
        if (!f.Targets.Values.Any(v => v.Stats != null && v.Stats.N >= 3)) return "undetermined";
        return "none";
    }

    static string ZoneTextOf(string z, WindowFacts f)
    {
        var t = T("d.zone." + z);
        if (z is "upstream" or "path" && PcBusy(f)) t += T("d.zone.busy", G(Th.BusyMbps));
        return t;
    }

    static List<string> Describe(WindowFacts f)
    {
        var parts = new List<string>();
        foreach (var v in f.Targets.Values)
        {
            var s = v.Stats;
            if (s != null && s.N > 0)
                parts.Add(T("d.win.target", v.Label, FmtMs(s.Median), FmtMs(s.Max), F0(s.LossPct)) + (v.Bad ? " ⚠" : ""));
        }
        if (f.DnsHit != null) parts.Add(T("d.win.dns", FmtMs(f.DnsHit.Median), F0(f.DnsHit.LossPct)));
        if (f.NetDownMed != null || f.NetUpMed != null)
            parts.Add(T("d.win.traffic", FmtF1(f.NetDownMed), FmtF1(f.NetDownMax), FmtF1(f.NetUpMed), FmtF1(f.NetUpMax)));
        if (f.WifiSignalMin != null) parts.Add(T("d.win.wifi", G(f.WifiSignalMin.Value)));
        return parts;
    }

    // ------------------------------------------------------------------ episodes and incidents
    sealed record Episode(double T0, double T1, int Events, Dictionary<string, int> Targets);

    static List<Episode> DetectEpisodes(Ctx cx)
    {
        var ev = new List<(double T, string Tid)>();
        foreach (var (tid, x) in cx.T)
        {
            if (x.State == "no_response" || (x.Base is null && x.Calm.Count == 0)) continue;
            double limit = Thr(KindOf(x.Tg), x.Base);
            ev.AddRange(x.Calm.Where(s => Degraded(s, limit)).Select(s => (s.T, tid)));
        }
        ev = ev.OrderBy(e => e.T).ThenBy(e => e.Tid, StringComparer.Ordinal).ToList();
        var clusters = new List<List<(double T, string Tid)>>();
        var cur = new List<(double T, string Tid)>();
        foreach (var e in ev)
        {
            if (cur.Count > 0 && e.T - cur[^1].T > Th.EpisodeGapS) { clusters.Add(cur); cur = new(); }
            cur.Add(e);
        }
        if (cur.Count > 0) clusters.Add(cur);
        var res = new List<Episode>();
        foreach (var c in clusters)
        {
            if (c.Count < Th.EpisodeMinEvents) continue;
            var counts = new Dictionary<string, int>();
            foreach (var (_, tid) in c) counts[tid] = counts.GetValueOrDefault(tid) + 1;
            res.Add(new Episode(c[0].T - 3, c[^1].T + 3, c.Count, counts));
        }
        return res;
    }

    static List<TimelineItem> BuildTimeline(Ctx cx)
    {
        var items = new List<TimelineItem>();
        foreach (var m in cx.D.Marks)
        {
            if (m.Kind == "lag")
            {
                var f = WindowFactsOf(cx, m.T - Th.IncidentBeforeS, m.T + Th.IncidentAfterS);
                var z = Localize(f);
                items.Add(new TimelineItem { Type = "lag", T = m.T, T0 = f.T0, T1 = f.T1, Zone = z, ZoneText = ZoneTextOf(z, f), Note = m.Note, Details = Describe(f), Facts = f });
            }
            else if (m.Kind is "gap" or "roam")
                items.Add(new TimelineItem { Type = m.Kind, T = m.T, Zone = null, ZoneText = T("mark." + m.Kind) });
        }
        var episodes = new List<TimelineItem>();
        foreach (var e in DetectEpisodes(cx))
        {
            var f = WindowFactsOf(cx, e.T0 - 2, e.T1 + 2);
            var z = Localize(f);
            if (z is "none" or "undetermined") continue;  // isolated, scattered losses: noise, not an episode
            var names = string.Join(", ", e.Targets.Select(kv => $"{cx.T[kv.Key].Tg.Label} ({kv.Value})"));
            episodes.Add(new TimelineItem
            {
                Type = "episode", T = e.T0, T0 = e.T0, T1 = e.T1, Zone = z, ZoneText = ZoneTextOf(z, f),
                Note = T("d.episode.note", e.Events, names), Details = Describe(f), Facts = f,
            });
        }
        cx.EpisodesDropped = Math.Max(0, episodes.Count - Th.MaxEpisodes);
        items.AddRange(episodes.Take(Th.MaxEpisodes));
        return items.OrderBy(i => i.T).ToList();
    }

    // ------------------------------------------------------------------ bufferbloat
    static List<(double A, double B)> PhaseWindow(SessionData d, string name) => d.Phases.Where(p => p.Name == name).Select(p => (p.T0, p.T1)).ToList();

    static BloatResult? Bufferbloat(Ctx cx)
    {
        var d = cx.D;
        var down = PhaseWindow(d, "download");
        var up = PhaseWindow(d, "upload");
        var idle = PhaseWindow(d, "idle");
        if (down.Count == 0 && up.Count == 0) return null;
        var res = new BloatResult();
        foreach (var (name, wins) in new[] { ("download", down), ("upload", up) })
        {
            if (wins.Count == 0) continue;
            // every phase of this direction counts, each trimmed of its own warm-up (the app records one per direction,
            // but a session may hold several: the first must not decide for all the others)
            var loaded = wins.Select(w => (A: w.A + Th.BloatWarmupS, B: w.B)).ToList();
            var row = new BloatRow();
            foreach (var (tid, x) in cx.T)
            {
                if (x.State == "no_response" || x.Tg.Role is not ("gateway" or "internet")) continue;
                var idleS = idle.Count > 0 ? idle.SelectMany(w => Stats.Window(x.Raw, w.A, w.B)).ToList() : x.Calm;
                var loadS = loaded.SelectMany(w => Stats.Window(x.Raw, w.A, w.B)).ToList();
                var si = Stats.Rtt(idleS);
                var sl = Stats.Rtt(loadS);
                if (!(si != null && sl != null && si.Median != null && sl.Median != null && sl.N >= 3)) continue;
                row.Targets[tid] = new BloatTarget { Role = x.Tg.Role, Label = x.Tg.Label, IdleMed = si.Median.Value, LoadMed = sl.Median.Value, LoadP95 = sl.P95, Delta = sl.Median.Value - si.Median.Value, LossPct = sl.LossPct };
            }
            var inet = row.Targets.Values.Where(v => v.Role == "internet").ToList();
            var gw = row.Targets.Values.Where(v => v.Role == "gateway").ToList();
            if (inet.Count > 0)
            {
                row.Delta = Stats.Median(inet.Select(v => v.Delta));
                row.LoadP95 = Stats.Median(inet.Where(v => v.LoadP95.HasValue).Select(v => v.LoadP95!.Value));
                row.IdleMed = Stats.Median(inet.Select(v => v.IdleMed));
                row.LoadMed = Stats.Median(inet.Select(v => v.LoadMed));
                row.LossPct = inet.Max(v => v.LossPct);
                row.Grade = Grade(row.Delta!.Value);
            }
            if (gw.Count > 0) row.GwDelta = gw[0].Delta;
            var v2 = Stats.Values(loaded.SelectMany(w => Stats.Window(d.S(name == "download" ? "load:down_bps" : "load:up_bps"), w.A, w.B)));
            row.Mbps = v2.Count > 0 ? Mbps(Stats.Median(v2)) : null;
            row.Valid = inet.Count > 0 && row.Mbps != null && row.Mbps >= Th.BloatMinMbps;
            res.Directions[name == "download" ? "down" : "up"] = row;
        }
        return res;
    }

    static double BloatScore(double delta)
    {
        foreach (var (lim, s) in Th.BloatDeltaMs) if (delta >= lim) return s;
        return 0.0;
    }

    // ------------------------------------------------------------------ hypotheses
    static Hypothesis H(string id, string titleKey) => new() { Id = id, Title = T(titleKey) };

    static Dictionary<string, int> ZoneCounts(IEnumerable<TimelineItem> items)
    {
        var d = new Dictionary<string, int>();
        foreach (var i in items) if (i.Zone != null) d[i.Zone] = d.GetValueOrDefault(i.Zone) + 1;
        return d;
    }

    static Hypothesis RuleLan(Ctx cx, List<TimelineItem> timeline, BloatResult? bloat)
    {
        var h = H("lan", "lan.title");
        h.Limits.Add(T("lan.limit1"));
        h.Limits.Add(T("lan.limit2"));
        h.NextTest = T(cx.Link == "wifi" ? "lan.next_wifi" : "lan.next_wired");
        var gw = cx.Gw?.Stats;
        if (cx.T.ContainsKey("gateway") && cx.Gw is null) h.Limits.Add(T("lan.limit_gw_noicmp"));
        if (gw != null)
        {
            if (gw.LossPct >= Th.GwLossPct)
            {
                h.Score += gw.LossPct >= 5 ? 4 : 3;
                h.Evidence.Add(T("lan.ev.gw_loss", F1(gw.LossPct), gw.Lost, gw.N));
            }
            if (gw.P95 != null && gw.P95 >= Th.GwP95Ms)
            {
                h.Score += 2;
                h.Evidence.Add(T("lan.ev.gw_p95", FmtMs(gw.P95), FmtMs(gw.Max)));
            }
            else if (gw.Max != null && gw.Max >= Th.GwMaxMs)
            {
                h.Score += 1;
                h.Evidence.Add(T("lan.ev.gw_peaks", FmtMs(gw.Max)));
            }
            if (gw.Jitter != null && gw.Jitter >= Th.GwJitterMs)
            {
                h.Score += 1;
                h.Evidence.Add(T("lan.ev.gw_jitter", F1(gw.Jitter.Value)));
            }
            if (h.Score == 0 && gw.LossPct < 0.5 && (gw.P95 ?? 0) < Th.GwCleanP95Ms)
                h.Counter.Add(T("lan.counter.gw_stable", F1(gw.LossPct), FmtMs(gw.P95), FmtMs(gw.Max)));
        }
        var zones = ZoneCounts(timeline.Where(i => i.Type is "lag" or "episode"));
        int total = zones.Values.Sum();
        if (total > 0 && Z(zones, "local") > 0)
        {
            h.Score += 1 + ((double)Z(zones, "local") / total >= 0.5 ? 2 : 0);
            h.Evidence.Add(T("lan.ev.zones_local", Z(zones, "local"), total));
        }
        else if (total > 0) h.Counter.Add(T("lan.counter.zones_none", total));
        if (cx.Link == "wifi")
        {
            var sig = Stats.Values(cx.Series("wifi:signal"));
            if (sig.Count > 0)
            {
                double med = Stats.Median(sig)!.Value, mn = sig.Min();
                if (med < Th.WifiWeakPct)
                {
                    h.Score += 2;
                    h.Evidence.Add(T("lan.ev.wifi_weak", F0(med), G(mn)));
                }
                else if (mn < Th.WifiVeryWeakPct)
                {
                    h.Score += 1;
                    h.Evidence.Add(T("lan.ev.wifi_drops", G(mn), F0(med)));
                }
                else h.Counter.Add(T("lan.counter.wifi_ok", F0(med), G(mn)));
            }
            var tx = Stats.Values(cx.Series("wifi:tx"));
            if (tx.Count > 0 && Stats.Median(tx) is { } txm and > 0 && tx.Min() < 0.5 * txm)
            {
                h.Score += 1;
                h.Evidence.Add(T("lan.ev.wifi_link", F0(txm), F0(tx.Min())));
            }
            var roams = cx.D.Marks.Count(m => m.Kind == "roam");
            if (roams > 0)
            {
                h.Score += 1;
                h.Evidence.Add(T("lan.ev.roams", roams));
            }
            var nb = cx.D.Meta.WifiNeighbors;
            if (nb != null && nb.SameChannelStrong >= 3)
            {
                h.Score += 1;
                h.Evidence.Add(T("lan.ev.neighbors", nb.SameChannelStrong));
            }
            if (sig.Count == 0) h.Limits.Add(T("lan.limit_no_wifi"));
        }
        else if (cx.Link == "ethernet") h.Counter.Add(T("lan.counter.wired"));
        if (bloat != null)
        {
            var g = bloat.Directions.Values.Where(r => r.GwDelta.HasValue).Select(r => r.GwDelta!.Value).ToList();
            if (g.Count > 0 && g.Max() >= 30)
            {
                h.Score += 2;
                h.Evidence.Add(T("lan.ev.load_gw", F0(g.Max())));
            }
        }
        h.Actions = cx.Link == "wifi" ? new() { T("lan.action.wifi1"), T("lan.action.wifi2") } : new() { T("lan.action.wired") };
        return h;
    }

    static Hypothesis RuleBufferbloat(Ctx cx, BloatResult? bloat)
    {
        var h = H("bufferbloat", "bloat.title");
        h.Limits.Add(T("bloat.limit1"));
        h.Limits.Add(T("bloat.limit2"));
        h.NextTest = T("bloat.next");
        if (bloat is null)
        {
            h.NotEvaluatedReason = T("bloat.not_eval.none");
            return h;
        }
        var invalid = new List<string>();
        foreach (var dkey in new[] { "down", "up" })
        {
            if (!bloat.Directions.TryGetValue(dkey, out var r)) continue;
            var dname = T("d.dir." + dkey);
            var cap = Cap(dname);
            if (!r.Valid)
            {
                var ph = cx.D.Phases.FirstOrDefault(p => p.Name == (dkey == "down" ? "download" : "upload"));
                bool early = ph?.Meta.VolumeCapReached == true;
                // two different reasons, said differently: the line was not loaded enough, or it was but the latency could not be measured
                bool rateTooLow = r.Mbps == null || r.Mbps < Th.BloatMinMbps;
                var rate = r.Mbps != null ? F1(r.Mbps.Value) + " Mbps" : T("bloat.none");
                var why = T(rateTooLow ? "bloat.limit_invalid" : "bloat.limit_silent", dname, rate) + (early ? T("bloat.cap_early", G(ph!.Meta.DurationS ?? 0)) : "");
                h.Limits.Add(why);
                invalid.Add(why);
                continue;
            }
            double delta = r.Delta!.Value;
            double s = BloatScore(delta);
            double localPart = r.GwDelta.HasValue && delta >= 30 ? (r.GwDelta.Value) / delta : 0.0;
            if (localPart >= 0.5 && r.GwDelta >= 10) s = 1.0;   // most of the delay already shows before the router: it is not the line's queue
            else if (localPart >= 0.2 && r.GwDelta >= 10) s -= 1.5;
            h.Score = Math.Max(h.Score, s);
            h.Evidence.Add(T("bloat.ev", cap, F0(r.IdleMed!.Value), F0(r.LoadMed!.Value), F0(delta), r.Grade, FmtMs(r.LoadP95), F1(r.LossPct!.Value), F0(r.Mbps!.Value)));
            if (r.GwDelta.HasValue && delta >= 30)
            {
                if (r.GwDelta < 10) h.Evidence.Add(T("bloat.ev_gw_stable", cap, F0(r.GwDelta.Value)));
                else h.Counter.Add(T("bloat.counter_gw", cap, F0(r.GwDelta.Value)));
            }
            if (delta < Th.BloatDeltaMs[^1].Lim) h.Counter.Add(T("bloat.counter_stable", cap, F0(delta)));
        }
        if (!bloat.Directions.Values.Any(r => r.Valid))
        {
            // the user sees this reason (the hypothesis itself is not shown): say, per direction, why the phase could not be used
            h.NotEvaluatedReason = T("bloat.not_eval.unusable") + (invalid.Count > 0 ? " " + string.Join(" ", invalid) : "");
            return h;
        }
        if (cx.Link == "wifi" && h.Score > 0)
        {
            h.Score = Math.Max(0.0, h.Score - 1);
            h.Limits.Add(T("bloat.limit_wifi"));
        }
        h.Score = Math.Max(h.Score, 0.0);
        h.Actions = new() { T("bloat.action") };
        return h;
    }

    static Hypothesis RuleSaturation(Ctx cx, List<TimelineItem> timeline, BloatResult? bloat, AppConfig cfg)
    {
        var h = H("saturation", "sat.title");
        h.Limits.Add(T("sat.limit1"));
        h.Limits.Add(T("sat.limit2"));
        h.NextTest = T("sat.next");
        double? Capacity(string d, double? plan)
        {
            double? measured = bloat?.Directions.GetValueOrDefault(d)?.Mbps;
            return plan is > 0 ? plan : measured is > 0 ? measured : null;
        }
        var cap = new Dictionary<string, double?> { ["down"] = Capacity("down", cfg.PlanDownMbps), ["up"] = Capacity("up", cfg.PlanUpMbps) };
        int nSat = 0, total = 0;
        foreach (var i in timeline)
        {
            if (i.Type is not ("lag" or "episode")) continue;
            total++;
            var f = i.Facts!;
            bool Sat(string d, double? med) => cap[d] is > 0 && med != null && med >= Th.SatRatio * cap[d]!.Value;
            if (Sat("down", f.NetDownMed) || Sat("up", f.NetUpMed)) nSat++;
        }
        string CapTxt(string d) => cap[d] is { } v ? G(v) : "?";
        int pct = (int)(Th.SatRatio * 100);
        if (nSat > 0)
        {
            h.Score += 3 + ((double)nSat / total >= 0.5 ? 2 : 0);
            h.Evidence.Add(T("sat.ev.pc_over", nSat, total, pct, CapTxt("down"), CapTxt("up")));
        }
        foreach (var d in new[] { "down", "up" })
        {
            var v = Stats.Values(cx.Series($"net:{d}_bps"));
            if (v.Count > 0 && cap[d] is > 0)
            {
                double p95 = Stats.Percentile(v.OrderBy(x => x).ToList(), 95)!.Value / 1e6;
                if (p95 >= Th.SatRatio * cap[d]!.Value)
                {
                    h.Score += 1;
                    h.Evidence.Add(T("sat.ev.p95", T("d.adj." + d), pct, F0(p95)));
                }
            }
        }
        var household = timeline.Where(i => i.Type is "lag" or "episode" && i.Zone is "upstream" or "path" && (i.Facts!.NetDownMed ?? 0) < 2 && (i.Facts.NetUpMed ?? 0) < 2).ToList();
        if (household.Count > 0 && nSat == 0)
        {
            h.Score += 1;
            h.Evidence.Add(T("sat.ev.household", household.Count));
        }
        if (total > 0 && nSat == 0 && cap["down"] is > 0 && household.Count == 0) h.Counter.Add(T("sat.counter.far"));
        h.Actions = new() { T("sat.action1"), T("sat.action2") };
        return h;
    }

    static Hypothesis RuleRouter(Ctx cx, BloatResult? bloat, AppConfig cfg)
    {
        var h = H("router_qos", "router.title");
        h.Limits.Add(T("rq.limit1"));
        h.Limits.Add(T("rq.limit2"));
        h.NextTest = T("rq.next");
        var gw = cx.Gw?.Stats;
        if (cx.Link == "ethernet" && gw != null && (gw.LossPct >= Th.GwLossPct || (gw.P95 ?? 0) >= Th.GwP95Ms))
        {
            h.Score += 3;
            h.Evidence.Add(T("rq.ev.wired", F1(gw.LossPct), FmtMs(gw.P95)));
        }
        var rcfg = cfg.Router;
        (double? Down, double? Up) meas = (bloat?.Directions.GetValueOrDefault("down")?.Mbps, bloat?.Directions.GetValueOrDefault("up")?.Mbps);
        double worst = (bloat?.Directions.Values.Where(r => r.Valid && r.Delta.HasValue).Select(r => r.Delta!.Value) ?? Enumerable.Empty<double>()).DefaultIfEmpty(0).Max();
        var findings = RouterQos.Check(rcfg, meas, cfg, worst);
        h.Findings = findings;
        foreach (var f in findings)
        {
            if (f.Severity == "problem") { h.Score += 2; h.Evidence.Add(f.Text); }
            else if (f.Severity == "warning") { h.Score += 1; h.Evidence.Add(f.Text); }
            else if (f.Severity == "ok") h.Counter.Add(f.Text);
        }
        h.Score = Math.Min(h.Score, 8.0);
        if (rcfg is null) h.Limits.Add(T("rq.limit_noconfig"));
        h.Actions = RouterQos.Propose(rcfg, meas, worst).Select(p => p.Change).ToList();
        if (h.Actions.Count == 0) h.Actions.Add(T("rq.action.default"));
        return h;
    }

    static Hypothesis RuleIsp(Ctx cx, List<TimelineItem> timeline)
    {
        var h = H("isp", "isp.title");
        h.Limits.Add(T("isp.limit1"));
        h.Limits.Add(T("isp.limit2"));
        h.NextTest = T("isp.next");
        var items = timeline.Where(i => i.Type is "lag" or "episode").ToList();
        var busy = items.Where(i => i.Zone is "upstream" or "path" && PcBusy(i.Facts!)).ToList();
        var zones = ZoneCounts(items.Where(i => !busy.Contains(i)));  // an episode where the PC itself loads the line proves nothing about the ISP
        int total = items.Count;
        if (busy.Count > 0) h.Limits.Add(T("isp.limit_busy", busy.Count, G(Th.BusyMbps)));
        if (Z(zones, "upstream") > 0)
        {
            h.Score += (double)Z(zones, "upstream") / total >= 0.5 ? 6 : 4;
            h.Evidence.Add(T("isp.ev.upstream", Z(zones, "upstream"), total));
        }
        if (Z(zones, "path") > 0 && Z(zones, "upstream") == 0)
        {
            h.Score += 3;
            h.Evidence.Add(T("isp.ev.path", Z(zones, "path")));
        }
        if (Z(zones, "custom_path") > 0)
        {
            h.Score += 3;
            h.Evidence.Add(T("isp.ev.custom", Z(zones, "custom_path")));
        }
        if (total > 0 && busy.Count == 0 && Z(zones, "upstream") + Z(zones, "path") + Z(zones, "custom_path") == 0) h.Counter.Add(T("isp.counter.none"));
        if (Z(zones, "local") > 0 && Z(zones, "upstream") > 0)
        {
            h.Score = Math.Min(h.Score, Math.Max(1.0, h.Score - 2));
            h.Limits.Add(T("isp.limit_partial_local"));
        }
        foreach (var tr in cx.D.Traces)
        {
            var a = tr.Data.Analysis;
            if (a is null) continue;
            if (a.Step != null)
            {
                h.Score += 2;
                h.Evidence.Add(T("isp.ev.trace_step", tr.Target, F0(a.Step.FromMs), F0(a.Step.ToMs), a.Step.Hop, a.Step.Ip ?? T("isp.unknown_ip")));
            }
            if (a.IntermediateLoss.Count > 0 && !(a.DestLossPct is > 0))
                h.Counter.Add(T("isp.counter.trace_icmp", tr.Target, string.Join(", ", a.IntermediateLoss)));
            else if (a.DestLossPct is > 0)
            {
                h.Score += 1;
                h.Evidence.Add(T("isp.ev.trace_destloss", tr.Target, F0(a.DestLossPct.Value)));
            }
        }
        h.Actions = new() { T("isp.action1"), T("isp.action2") };
        return h;
    }

    static Hypothesis RuleDns(Ctx cx)
    {
        var h = H("dns", "dnsr.title");
        h.Limits.Add(T("dnsr.limit1"));
        h.Limits.Add(T("dnsr.limit2"));
        h.NextTest = T("dnsr.next");
        var hit = Stats.Rtt(cx.Series("dns:sys_hit"));
        var miss = Stats.Rtt(cx.Series("dns:sys_miss"));
        var rf = Stats.Rtt(cx.Series("dns:ref_miss"));
        // each series is judged on its own: the absence of the cached one must not hide slow uncached lookups
        if (hit is null && miss is null && rf is null)
        {
            h.Limits.Add(T("dnsr.limit_none"));
            return h;
        }
        if (hit is null) h.Limits.Add(T("dnsr.limit_nohit"));
        if (hit != null && hit.LossPct >= Th.DnsFailPct)
        {
            h.Score += 4;
            h.Evidence.Add(T("dnsr.ev.fail", F1(hit.LossPct), hit.Lost, hit.N));
        }
        if (hit?.Median != null && hit.Median >= Th.DnsMedMs)
        {
            h.Score += 3;
            h.Evidence.Add(T("dnsr.ev.slow", FmtMs(hit.Median)));
        }
        if (hit?.P95 != null && hit.P95 >= Th.DnsP95Ms)
        {
            h.Score += 2;
            h.Evidence.Add(T("dnsr.ev.peaks", FmtMs(hit.P95), FmtMs(hit.Max)));
        }
        if (miss?.Median != null && miss.Median >= Th.DnsMissMedMs)
        {
            h.Score += 2;
            h.Evidence.Add(T("dnsr.ev.cold", FmtMs(miss.Median)));
        }
        if (rf?.Median != null && miss?.Median != null && rf.Median < 0.5 * miss.Median && miss.Median - rf.Median >= 80)
        {
            h.Score += 2;
            h.Evidence.Add(T("dnsr.ev.ref", FmtMs(rf.Median), FmtMs(miss.Median)));
        }
        if (h.Score == 0 && hit != null) h.Counter.Add(T("dnsr.counter.fast", FmtMs(hit.Median), F1(hit.LossPct)));
        h.Actions = new() { T("dnsr.action") };
        return h;
    }

    static Hypothesis RuleBackground(Ctx cx, List<TimelineItem> timeline)
    {
        var h = H("background", "bg.title");
        h.Limits.Add(T("bg.limit1"));
        h.Limits.Add(T("bg.limit2"));
        h.NextTest = T("bg.next");
        var down = Stats.Values(cx.Series("net:down_bps"));
        var up = Stats.Values(cx.Series("net:up_bps"));
        if (down.Count == 0)
        {
            h.Limits.Add(T("bg.limit_unavailable"));
            return h;
        }
        double md = Mbps(Stats.Median(down))!.Value, mu = Mbps(Stats.Median(up)) ?? 0;
        if (md >= Th.BgDownMbps || mu >= Th.BgUpMbps)
        {
            h.Score += md >= 10 || mu >= 5 ? 3 : 2;
            h.Evidence.Add(T("bg.ev.notable", F1(md), F1(mu)));
        }
        var idle = PhaseWindow(cx.D, "idle");
        if (idle.Count > 0)
        {
            // down and up are sampled at the same instant: add them second by second
            var perSecond = idle.SelectMany(w => Stats.Window(cx.D.S("net:down_bps"), w.A, w.B).Concat(Stats.Window(cx.D.S("net:up_bps"), w.A, w.B)))
                .Where(s => s.V != null).GroupBy(s => s.T).Select(g => g.Sum(s => s.V!.Value)).ToList();
            var idleMbps = Mbps(Stats.Median(perSecond));
            if (idleMbps != null && idleMbps.Value >= Th.BgIdleMbps)
            {
                h.Score += 3;
                h.Evidence.Add(T("bg.ev.idle", F1(idleMbps.Value)));
            }
        }
        // correlation: mean traffic at degraded seconds vs the other seconds
        var badS = new HashSet<long>();
        foreach (var x in cx.T.Values)
        {
            if (x.State == "no_response" || x.Tg.Role is not ("gateway" or "internet")) continue;
            double lim = Thr(KindOf(x.Tg), x.Base);
            foreach (var s in x.Calm) if (Degraded(s, lim)) badS.Add((long)s.T);
        }
        var tot = new Dictionary<long, double>();
        foreach (var name in new[] { "net:down_bps", "net:up_bps" })
            foreach (var s in cx.Series(name)) tot[(long)s.T] = tot.GetValueOrDefault((long)s.T) + (s.V ?? 0);
        var inb = tot.Where(kv => badS.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        var outb = tot.Where(kv => !badS.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (inb.Count >= 5 && outb.Count > 0)
        {
            double a = inb.Sum() / inb.Count / 1e6, b = Math.Max(outb.Sum() / outb.Count / 1e6, 0.01);
            if (a >= 2 && a / b >= Th.BgSpikeRatio)
            {
                h.Score += 3;
                h.Evidence.Add(T("bg.ev.corr", F1(a / b), F1(a), F1(b)));
            }
        }
        var hot = timeline.Count(i => i.Type is "lag" or "episode" && PcBusy(i.Facts!));
        if (hot > 0)
        {
            h.Score += 1;
            h.Evidence.Add(T("bg.ev.hot", hot, G(Th.BusyMbps)));
        }
        if (h.Score == 0) h.Counter.Add(T("bg.counter.low", F2(md), F2(mu)));
        h.Actions = new() { T("bg.action") };
        return h;
    }

    // ------------------------------------------------------------------ assembly
    static StatsTables BuildStats(Ctx cx)
    {
        var res = new StatsTables();
        foreach (var (tid, x) in cx.T)
            res.Targets.Add(new TargetRow { Id = tid, Label = x.Tg.Label, Role = x.Tg.Role, Host = x.Tg.Host, State = x.State, StateText = T("d.state." + x.State), Stats = x.Stats });
        res.Dns = new DnsStats { SysHit = Stats.Rtt(cx.Series("dns:sys_hit")), SysMiss = Stats.Rtt(cx.Series("dns:sys_miss")), RefMiss = Stats.Rtt(cx.Series("dns:ref_miss")) };
        TrafficPart? Part(string d)
        {
            var v = Stats.Values(cx.Series($"net:{d}_bps"));
            return v.Count == 0 ? null : new TrafficPart { Median = Mbps(Stats.Median(v)), P95 = Mbps(Stats.Percentile(v.OrderBy(x => x).ToList(), 95)), Max = Mbps(v.Max()) };
        }
        res.Traffic = new TrafficSummary { Down = Part("down"), Up = Part("up") };
        var sig = Stats.Values(cx.Series("wifi:signal"));
        var tx = Stats.Values(cx.Series("wifi:tx"));
        var rx = Stats.Values(cx.Series("wifi:rx"));
        if (sig.Count > 0)
        {
            var w = cx.D.Meta.Wifi;
            res.Wifi = new WifiSummary
            {
                SignalMed = Stats.Median(sig), SignalMin = sig.Min(), TxMed = tx.Count > 0 ? Stats.Median(tx) : null, TxMin = tx.Count > 0 ? tx.Min() : null,
                RxMed = rx.Count > 0 ? Stats.Median(rx) : null, Channel = w?.Channel, Band = w?.Band, Radio = w?.Radio,
            };
        }
        foreach (var p in cx.D.Phases)
        {
            var row = new PhaseRow { Name = p.Name, T0 = p.T0, T1 = p.T1, Meta = p.Meta };
            foreach (var (tid, x) in cx.T)
                if (x.State != "no_response" && x.Tg.Role is "gateway" or "internet")
                    row.Targets[tid] = Stats.Rtt(Stats.Window(x.Raw, p.T0 + (p.Name is "download" or "upload" ? Th.BloatWarmupS : 0), p.T1));
            res.Phases.Add(row);
        }
        return res;
    }

    /// <summary>Settings seen by the analysis: the snapshot taken at session start wins over the live settings.</summary>
    public static AppConfig ConfigFor(SessionData d, AppConfig live)
    {
        var snap = d.Meta.CfgSnapshot;
        if (snap is null) return live;
        // the targets of a session are the ones stored with it (Meta.Targets): the live target settings play no part here
        return new AppConfig { PlanDownMbps = snap.PlanDownMbps, PlanUpMbps = snap.PlanUpMbps, Router = snap.Router };
    }

    static List<string> GeneralLimits(Ctx cx)
    {
        var l = Loc.List("d.general", 3);
        if (cx.EpisodesDropped > 0) l.Add(T("d.general.episodes_capped", Th.MaxEpisodes + cx.EpisodesDropped, Th.MaxEpisodes));
        return l;
    }

    public static Analysis Analyze(SessionData data, AppConfig? cfg = null)
    {
        cfg = ConfigFor(data, cfg ?? new AppConfig());  // the settings the session was recorded with take priority, whoever calls
        var cx = new Ctx(data);
        int gwN = cx.Gw?.Stats?.N ?? 0;
        int inetN = cx.Inet.Where(x => x.Stats != null).Select(x => x.Stats!.N).DefaultIfEmpty(0).Max();
        var timeline = BuildTimeline(cx);
        var bloat = Bufferbloat(cx);
        var hyps = new List<Hypothesis>
        {
            RuleLan(cx, timeline, bloat), RuleBufferbloat(cx, bloat), RuleSaturation(cx, timeline, bloat, cfg), RuleRouter(cx, bloat, cfg),
            RuleIsp(cx, timeline), RuleDns(cx), RuleBackground(cx, timeline),
        };
        foreach (var h in hyps) Finalise(h);
        var shown = hyps.Where(h => h.Score >= 2 && h.Evidence.Count > 0).OrderByDescending(h => h.Score).ToList();
        var unlikely = hyps.Where(h => h.Score < 2 && h.Counter.Count > 0).Select(h => new UnlikelyItem { Id = h.Id, Title = h.Title, Reasons = h.Counter }).ToList();
        var notEval = hyps.Where(h => h.NotEvaluatedReason != null).Select(h => new NotEvaluatedItem { Id = h.Id, Title = h.Title, Reason = h.NotEvaluatedReason!, NextTest = h.NextTest }).ToList();
        var stat = BuildStats(cx);
        bool sparse = Math.Max(gwN, inetN) < Th.MinSamples;
        bool everythingDead = cx.T.Count > 0 && cx.T.Values.All(x => x.State == "no_response");
        var incidents = timeline.Where(i => i.Type == "lag").ToList();
        var summary = new List<string>();
        var actions = new List<string>();
        if (cx.T.Count == 0) summary.Add(T("sum.nodata"));
        else if (everythingDead) summary.Add(T("sum.dead"));
        else if (sparse) summary.Add(T("sum.sparse", Math.Max(gwN, inetN), Th.MinSamples));
        if (shown.Count > 0 && !everythingDead)
        {
            var top = shown[0];
            summary.Add(T("sum.top", top.Title, T("d.level." + top.Level)));
            if (shown.Count > 1) summary.Add(T("sum.others", string.Join(" ; ", shown.Skip(1).Take(3).Select(h => $"{h.Title} ({T("d.level." + h.Level)})"))));
            summary.Add(T("sum.disclaimer"));
        }
        else if (!sparse && !everythingDead) summary.Add(T("sum.none"));
        foreach (var x in cx.T.Values)
            if (x.State == "no_response" && !everythingDead)
                summary.Add(T("sum.noicmp", x.Tg.Label, x.Tg.Host));
        int quiet = incidents.Count(i => i.Zone == "none");
        if (incidents.Count > 0 && quiet > 0) summary.Add(T("sum.quiet", quiet, incidents.Count));
        foreach (var h in shown) foreach (var a in h.Actions) if (!actions.Contains(a)) actions.Add(a);
        return new Analysis
        {
            Session = data.Id, Summary = summary, Hypotheses = shown, Unlikely = unlikely, NotEvaluated = notEval, Actions = actions, Timeline = timeline,
            Stats = stat, Bufferbloat = bloat, GeneralLimits = GeneralLimits(cx), Thresholds = Th.AsDictionary(),
            Metrics = ComputeMetrics(stat, bloat, incidents.Count),
        };
    }

    // ------------------------------------------------------------------ session comparison
    public static Metrics ComputeMetrics(StatsTables stat, BloatResult? bloat, int nIncidents)
    {
        var inet = stat.Targets.Where(t => t.Role == "internet" && t.Stats != null && t.State != "no_response").Select(t => t.Stats!).ToList();
        var gw = stat.Targets.FirstOrDefault(t => t.Role == "gateway" && t.Stats != null && t.State != "no_response")?.Stats;
        double? Med(Func<RttStats, double?> f) { var v = inet.Select(f).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count > 0 ? Stats.Median(v) : null; }
        var d = bloat?.Directions ?? new();
        BloatRow? down = d.GetValueOrDefault("down"), up = d.GetValueOrDefault("up");
        return new Metrics
        {
            InetMed = Med(s => s.Median), InetP95 = Med(s => s.P95), InetLoss = Med(s => s.LossPct), JitterInet = Med(s => s.Jitter),
            GwMed = gw?.Median, GwP95 = gw?.P95, GwLoss = gw?.LossPct, DnsMed = stat.Dns.SysHit?.Median,
            BloatDown = down is { Valid: true } ? down.Delta : null, BloatUp = up is { Valid: true } ? up.Delta : null,
            DownMbps = down?.Mbps, UpMbps = up?.Mbps, WifiSignal = stat.Wifi?.SignalMed, Incidents = nIncidents,
        };
    }

    static readonly (string Key, int Better, Func<Metrics, double?> Get)[] MetricDefs =
    {
        ("cmp.inet_p95", -1, m => m.InetP95), ("cmp.inet_loss", -1, m => m.InetLoss), ("cmp.gw_p95", -1, m => m.GwP95),
        ("cmp.jitter", -1, m => m.JitterInet), ("cmp.dns", -1, m => m.DnsMed), ("cmp.bloat_down", -1, m => m.BloatDown),
        ("cmp.bloat_up", -1, m => m.BloatUp), ("cmp.down_mbps", +1, m => m.DownMbps), ("cmp.up_mbps", +1, m => m.UpMbps),
    };

    /// <summary>
    /// Compares two groups of sessions. Cautious verdict: a difference is kept only when it exceeds the variability observed
    /// INSIDE each group (repeat each condition 2–3 times).
    /// </summary>
    public static List<CompareRow> Compare(IReadOnlyList<Metrics> groupA, IReadOnlyList<Metrics> groupB)
    {
        var rows = new List<CompareRow>();
        foreach (var (key, better, get) in MetricDefs)
        {
            var a = groupA.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var b = groupB.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (a.Count == 0 || b.Count == 0) continue;
            double ma = Stats.Median(a)!.Value, mb = Stats.Median(b)!.Value, delta = mb - ma;
            double spread = Math.Max(a.Max() - a.Min(), b.Max() - b.Min());
            string verdict = a.Count < 2 || b.Count < 2 ? "indicative" : Math.Abs(delta) <= spread ? "indistinct" : delta * better > 0 ? "improvement" : "degradation";
            rows.Add(new CompareRow { Metric = T(key), A = ma, B = mb, Delta = delta, Variability = spread, CountA = a.Count, CountB = b.Count, Verdict = verdict, VerdictText = T("cmp.verdict." + verdict) });
        }
        return rows;
    }
}
