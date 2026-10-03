using System.Globalization;
using System.Text.Json.Serialization;

namespace NetworkAnalyzer;

// ---------------------------------------------------------------------- output model (JSON names = snake_case of the properties)
public sealed class Hypothesis
{
    public string Id { get; set; } = "";
    public string Titre { get; set; } = "";
    public double Score { get; set; }
    public List<string> Preuves { get; set; } = new();
    public List<string> Contre { get; set; } = new();
    public List<string> Limites { get; set; } = new();
    public string ProchainTest { get; set; } = "";
    public List<string> Actions { get; set; } = new();
    public string Niveau { get; set; } = "faible";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<RouterFinding>? Findings { get; set; }
    [JsonIgnore] public string? NonEvalue { get; set; }
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
    public string Type { get; set; } = "";
    public double T { get; set; }
    public double? T0 { get; set; }
    public double? T1 { get; set; }
    public string? Zone { get; set; }
    public string ZoneTexte { get; set; } = "";
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
    public string State { get; set; } = "ok";
    public string Etat { get; set; } = "";
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

public sealed class Unlikely
{
    public string Id { get; set; } = "";
    public string Titre { get; set; } = "";
    public List<string> Raisons { get; set; } = new();
}

public sealed class NotEvaluated
{
    public string Id { get; set; } = "";
    public string Titre { get; set; } = "";
    public string Raison { get; set; } = "";
    public string ProchainTest { get; set; } = "";
}

public sealed class Analysis
{
    public int Session { get; set; }
    public List<string> Resume { get; set; } = new();
    public List<Hypothesis> Hypotheses { get; set; } = new();
    public List<Unlikely> PeuProbables { get; set; } = new();
    public List<NotEvaluated> NonEvalue { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public List<TimelineItem> Timeline { get; set; } = new();
    public StatsTables Stats { get; set; } = new();
    public BloatResult? Bufferbloat { get; set; }
    public List<string> LimitesGenerales { get; set; } = new();
    public Dictionary<string, object> Seuils { get; set; } = new();
    public Metrics Metrics { get; set; } = new();
}

public sealed class CompareRow
{
    public string Metrique { get; set; } = "";
    public double A { get; set; }
    public double B { get; set; }
    public double Delta { get; set; }
    public double Variabilite { get; set; }
    public int CountA { get; set; }
    public int CountB { get; set; }
    public string Verdict { get; set; } = "";
}

/// <summary>
/// Diagnosis engine: measurements → ranked hypotheses, each with evidence, counter-evidence, limits and a next useful test.
/// A conclusion is always a HYPOTHESIS ("compatible with…"), never a confirmed cause. The confidence level (faible / moyenne /
/// élevée) comes from a score accumulating weighted clues; the thresholds are gathered in <see cref="Th"/> for review.
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
    public const int EpisodeGapS = 8, EpisodeMinEvents = 3, IncidentBeforeS = 45, IncidentAfterS = 30, MinSamples = 60;

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

    public Ctx(SessionData data)
    {
        D = data;
        Excl = data.Phases.Where(p => p.Name != "repos").Select(p => (p.T0, p.T1)).ToList();
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
        Link = string.IsNullOrEmpty(data.Link) ? "inconnu" : data.Link;
    }

    public List<Sample> Series(string name) => Stats.Outside(D.S(name), Excl);
}

public static class Diagnose
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly (double Lim, string G)[] Grades = { (5, "A+"), (30, "A"), (60, "B"), (200, "C"), (400, "D") };

    static readonly Dictionary<string, string> ZoneText = new()
    {
        ["local"] = "réseau local / Wi-Fi / routeur : la passerelle est affectée",
        ["amont"] = "en amont du routeur (accès Internet, fournisseur ou trajet) : la passerelle est saine mais plusieurs destinations Internet sont affectées",
        ["trajet"] = "trajet vers une seule destination Internet : les autres destinations et la passerelle sont sains",
        ["trajet_custom"] = "trajet vers votre destination personnalisée : le reste d'Internet et la passerelle sont sains",
        ["dns"] = "résolution DNS : les pings sont sains mais le DNS est lent ou en échec",
        ["aucune"] = "aucune anomalie réseau mesurée sur ce créneau (le lag viendrait d'ailleurs : PC, jeu, serveur — non mesuré ici)",
        ["indetermine"] = "créneau sans mesure suffisante",
    };

    public static readonly string[] LimitesGenerales =
    {
        "Les pings ICMP peuvent être traités en basse priorité par un routeur ou filtrés : une perte ICMP n'est pas toujours une perte réelle de trafic.",
        "Les mesures viennent de CET ordinateur : elles ne représentent pas le trafic des autres appareils de la maison.",
        "Aucune capture du contenu des communications n'est faite : seules des métadonnées (temps, compteurs) sont utilisées.",
    };

    // ------------------------------------------------------------------ helpers
    static string F0(double x) => x.ToString("0", Inv);
    static string F1(double x) => x.ToString("0.0", Inv);
    static string F2(double x) => x.ToString("0.00", Inv);
    static string G(double x) => x.ToString("0.######", Inv);
    static string FmtMs(double? x) => x is null ? "—" : F0(x.Value) + " ms";
    static double? Mbps(double? bps) => bps is null ? null : bps / 1e6;
    static int Z(Dictionary<string, int> z, string k) => z.GetValueOrDefault(k);

    public static string Level(double score) => score >= 7 ? "élevée" : score >= 4 ? "moyenne" : "faible";

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

    public static List<Sample> Usable(IReadOnlyList<Sample> s)
        => s.Any(x => x.Info == "tcp") ? s.Where(x => x.Info != "icmp_sans_reponse").ToList() : s.ToList();

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

    public static string Localize(WindowFacts f)
    {
        var gw = f.Targets.Values.Where(v => v.Role == "gateway").ToList();
        var inet = f.Targets.Values.Where(v => v.Role == "internet").ToList();
        var cust = f.Targets.Values.Where(v => v.Role == "custom").ToList();
        var badInet = inet.Where(v => v.Bad).ToList();
        if (gw.Count > 0 && gw[0].Bad) return "local";
        if (badInet.Count >= 2 || (inet.Count == 1 && badInet.Count > 0)) return "amont";
        if (badInet.Count > 0) return "trajet";
        if (cust.Count > 0 && cust[0].Bad) return "trajet_custom";
        if (DnsBad(f)) return "dns";
        if (!f.Targets.Values.Any(v => v.Stats != null && v.Stats.N >= 3)) return "indetermine";
        return "aucune";
    }

    static string ZoneTextOf(string z, WindowFacts f)
    {
        var t = ZoneText[z];
        if (z is "amont" or "trajet" && PcBusy(f))
            t += $" — ATTENTION : ce PC échange ≥ {G(Th.BusyMbps)} Mbps au même moment, la hausse de latence peut venir de ce trafic lui‑même";
        return t;
    }

    static List<string> Describe(WindowFacts f)
    {
        var parts = new List<string>();
        foreach (var v in f.Targets.Values)
        {
            var s = v.Stats;
            if (s != null && s.N > 0)
                parts.Add($"{v.Label} : médiane {FmtMs(s.Median)}, max {FmtMs(s.Max)}, perte {F0(s.LossPct)} %" + (v.Bad ? " ⚠" : ""));
        }
        if (f.DnsHit != null) parts.Add($"DNS : médiane {FmtMs(f.DnsHit.Median)}, échecs {F0(f.DnsHit.LossPct)} %");
        if (f.NetDownMed != null)
            parts.Add($"Trafic du PC : ↓ {F1(f.NetDownMed.Value)} Mbps (max {F1(f.NetDownMax!.Value)}), ↑ {F1(f.NetUpMed!.Value)} Mbps (max {F1(f.NetUpMax!.Value)})");
        if (f.WifiSignalMin != null) parts.Add($"Signal Wi‑Fi min {G(f.WifiSignalMin.Value)} %");
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
        return res.Take(40).ToList();
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
                items.Add(new TimelineItem { Type = "lag", T = m.T, T0 = f.T0, T1 = f.T1, Zone = z, ZoneTexte = ZoneTextOf(z, f), Note = m.Note, Details = Describe(f), Facts = f });
            }
            else if (m.Kind is "gap" or "roam")
                items.Add(new TimelineItem { Type = m.Kind, T = m.T, Zone = null, ZoneTexte = m.Note });
        }
        foreach (var e in DetectEpisodes(cx))
        {
            var f = WindowFactsOf(cx, e.T0 - 2, e.T1 + 2);
            var z = Localize(f);
            if (z is "aucune" or "indetermine") continue;  // isolated, scattered losses: noise, not an episode
            var names = string.Join(", ", e.Targets.Select(kv => $"{cx.T[kv.Key].Tg.Label} ({kv.Value})"));
            items.Add(new TimelineItem
            {
                Type = "episode", T = e.T0, T0 = e.T0, T1 = e.T1, Zone = z, ZoneTexte = ZoneTextOf(z, f),
                Note = $"Épisode détecté automatiquement : {e.Events} mesures dégradées ({names}).", Details = Describe(f), Facts = f,
            });
        }
        return items.OrderBy(i => i.T).ToList();
    }

    // ------------------------------------------------------------------ bufferbloat
    static List<(double A, double B)> PhaseWindow(SessionData d, string name) => d.Phases.Where(p => p.Name == name).Select(p => (p.T0, p.T1)).ToList();

    static BloatResult? Bufferbloat(Ctx cx)
    {
        var d = cx.D;
        var down = PhaseWindow(d, "download");
        var up = PhaseWindow(d, "upload");
        var idle = PhaseWindow(d, "repos");
        if (down.Count == 0 && up.Count == 0) return null;
        var res = new BloatResult();
        foreach (var (name, wins) in new[] { ("download", down), ("upload", up) })
        {
            if (wins.Count == 0) continue;
            double t0 = wins[0].A + Th.BloatWarmupS, t1 = wins[0].B;
            var row = new BloatRow();
            foreach (var (tid, x) in cx.T)
            {
                if (x.State == "no_response" || x.Tg.Role is not ("gateway" or "internet")) continue;
                var idleS = idle.Count > 0 ? idle.SelectMany(w => Stats.Window(x.Raw, w.A, w.B)).ToList() : x.Calm;
                var loadS = Stats.Window(x.Raw, t0, t1);
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
            var v2 = Stats.Values(Stats.Window(d.S(name == "download" ? "load:down_bps" : "load:up_bps"), t0, t1));
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
    static Hypothesis H(string id, string titre) => new() { Id = id, Titre = titre };

    static Hypothesis RuleLan(Ctx cx, List<TimelineItem> timeline, BloatResult? bloat)
    {
        var h = H("lan", "Instabilité du Wi‑Fi ou du réseau local");
        h.Limites.Add("Le routeur peut répondre lentement aux pings tout en acheminant bien le trafic (ICMP en basse priorité) : " +
                      "une gigue vers la passerelle seule ne prouve pas un problème de réseau.");
        h.Limites.Add("Le « signal % » de Windows est une échelle grossière ; l'application ne mesure ni l'occupation du canal ni les interférences.");
        h.ProchainTest = cx.Link == "wifi"
            ? "Refaire exactement la même surveillance en Ethernet (câble). Si les épisodes disparaissent, le Wi‑Fi est en cause ; s'ils persistent, regarder le câble, le port ou le routeur."
            : "Brancher un autre appareil en Ethernet sur un autre port du routeur et comparer, puis tester un autre câble.";
        var gw = cx.Gw?.Stats;
        if (cx.T.ContainsKey("gateway") && cx.Gw is null)
            h.Limites.Add("La passerelle ne répond pas aux pings ICMP : le réseau local n'a pas pu être évalué par cette méthode.");
        if (gw != null)
        {
            if (gw.LossPct >= Th.GwLossPct)
            {
                h.Score += gw.LossPct >= 5 ? 4 : 3;
                h.Preuves.Add($"Perte vers la passerelle : {F1(gw.LossPct)} % ({gw.Lost} sur {gw.N} requêtes).");
            }
            if (gw.P95 != null && gw.P95 >= Th.GwP95Ms)
            {
                h.Score += 2;
                h.Preuves.Add($"Latence vers la passerelle p95 = {FmtMs(gw.P95)} (max {FmtMs(gw.Max)}) ; un réseau local sain reste généralement sous quelques ms (Ethernet) à ~20 ms (Wi‑Fi).");
            }
            else if (gw.Max != null && gw.Max >= Th.GwMaxMs)
            {
                h.Score += 1;
                h.Preuves.Add($"Pics ponctuels vers la passerelle jusqu'à {FmtMs(gw.Max)}.");
            }
            if (gw.Jitter != null && gw.Jitter >= Th.GwJitterMs)
            {
                h.Score += 1;
                h.Preuves.Add($"Gigue vers la passerelle élevée : {F1(gw.Jitter.Value)} ms.");
            }
            if (h.Score == 0 && gw.LossPct < 0.5 && (gw.P95 ?? 0) < Th.GwCleanP95Ms)
                h.Contre.Add($"Passerelle stable : perte {F1(gw.LossPct)} %, p95 {FmtMs(gw.P95)}, max {FmtMs(gw.Max)}.");
        }
        var zones = ZoneCounts(timeline.Where(i => i.Type is "lag" or "episode"));
        int total = zones.Values.Sum();
        if (total > 0 && Z(zones, "local") > 0)
        {
            h.Score += 1 + ((double)Z(zones, "local") / total >= 0.5 ? 2 : 0);
            h.Preuves.Add($"{Z(zones, "local")} épisode(s)/incident(s) sur {total} touchent d'abord la passerelle (donc en deçà d'Internet).");
        }
        else if (total > 0) h.Contre.Add($"Aucun des {total} épisode(s)/incident(s) ne dégrade la passerelle.");
        if (cx.Link == "wifi")
        {
            var sig = Stats.Values(cx.Series("wifi:signal"));
            if (sig.Count > 0)
            {
                double med = Stats.Median(sig)!.Value, mn = sig.Min();
                if (med < Th.WifiWeakPct)
                {
                    h.Score += 2;
                    h.Preuves.Add($"Signal Wi‑Fi faible : médiane {F0(med)} % (min {G(mn)} %).");
                }
                else if (mn < Th.WifiVeryWeakPct)
                {
                    h.Score += 1;
                    h.Preuves.Add($"Chutes du signal Wi‑Fi jusqu'à {G(mn)} % (médiane {F0(med)} %).");
                }
                else h.Contre.Add($"Signal Wi‑Fi correct : médiane {F0(med)} %, min {G(mn)} %.");
            }
            var tx = Stats.Values(cx.Series("wifi:tx"));
            if (tx.Count > 0 && Stats.Median(tx) is { } txm and > 0 && tx.Min() < 0.5 * txm)
            {
                h.Score += 1;
                h.Preuves.Add($"Le débit de LIAISON Wi‑Fi (≠ débit Internet) chute de {F0(txm)} à {F0(tx.Min())} Mbit/s.");
            }
            var roams = cx.D.Marks.Count(m => m.Kind == "roam");
            if (roams > 0)
            {
                h.Score += 1;
                h.Preuves.Add($"{roams} changement(s) de point d'accès Wi‑Fi pendant la session.");
            }
            var nb = cx.D.Meta.WifiNeighbors;
            if (nb != null && nb.SameChannelStrong >= 3)
            {
                h.Score += 1;
                h.Preuves.Add($"{nb.SameChannelStrong} réseaux voisins avec un signal notable sur le même canal (indicatif ; les interférences non Wi‑Fi — micro‑ondes, Bluetooth — ne sont pas mesurables ici).");
            }
            if (sig.Count == 0) h.Limites.Add("Aucune mesure Wi‑Fi disponible (Windows ne les a pas exposées).");
        }
        else if (cx.Link == "ethernet") h.Contre.Add("Connexion filaire : le Wi‑Fi est hors de cause pour cette session.");
        if (bloat != null)
        {
            var g = bloat.Directions.Values.Where(r => r.GwDelta.HasValue).Select(r => r.GwDelta!.Value).ToList();
            if (g.Count > 0 && g.Max() >= 30)
            {
                h.Score += 2;
                h.Preuves.Add($"Sous charge, la latence vers la passerelle monte aussi (+{F0(g.Max())} ms) : le lien local lui‑même sature (Wi‑Fi ou file du routeur côté LAN).");
            }
        }
        h.Actions = cx.Link == "wifi"
            ? new() { "Rapprocher l'ordinateur du routeur ou utiliser un câble Ethernet pour comparer.", "Si Wi‑Fi : changer de canal/bande (5 GHz) et éloigner le routeur des sources d'interférences." }
            : new() { "Vérifier câble et port du routeur ; redémarrer le routeur puis refaire une session pour comparer." };
        return h;
    }

    static Dictionary<string, int> ZoneCounts(IEnumerable<TimelineItem> items)
    {
        var d = new Dictionary<string, int>();
        foreach (var i in items) if (i.Zone != null) d[i.Zone] = d.GetValueOrDefault(i.Zone) + 1;
        return d;
    }

    static Hypothesis RuleBufferbloat(Ctx cx, BloatResult? bloat)
    {
        var h = H("bufferbloat", "Bufferbloat (latence qui explose quand la connexion est chargée)");
        h.Limites.Add("Le débit obtenu peut être borné par le serveur de test, le Wi‑Fi ou le PC : résultat INDICATIF.");
        h.Limites.Add("Le ping ICMP n'est pas forcément traité comme votre trafic de jeu (une file QoS peut les séparer).");
        h.ProchainTest = "Refaire le test de saturation en Ethernet, puis après un seul changement de réglage (limite QoS/SQM), avec le protocole avant/après.";
        if (bloat is null)
        {
            h.NonEvalue = "Aucun test de saturation dans cette session : lancez l'onglet « Test de saturation ».";
            return h;
        }
        foreach (var (dkey, dname) in new[] { ("down", "téléchargement"), ("up", "envoi") })
        {
            if (!bloat.Directions.TryGetValue(dkey, out var r)) continue;
            var cap = char.ToUpperInvariant(dname[0]) + dname[1..];
            if (!r.Valid)
            {
                var ph = cx.D.Phases.FirstOrDefault(p => p.Name == (dkey == "down" ? "download" : "upload"));
                bool early = ph?.Meta.PlafondVolumeAtteint == true;
                h.Limites.Add($"Phase {dname} non concluante (débit {(r.Mbps != null ? F1(r.Mbps.Value) + " Mbps" : "nul")} ou cibles muettes) : la ligne n'a pas été réellement chargée."
                              + (early ? $" Le plafond de volume a été atteint en {G(ph!.Meta.DureeS ?? 0)} s : relancez avec un plafond plus élevé." : ""));
                continue;
            }
            double delta = r.Delta!.Value;
            double s = BloatScore(delta);
            double localPart = r.GwDelta.HasValue && delta >= 30 ? (r.GwDelta.Value) / delta : 0.0;
            if (localPart >= 0.5 && r.GwDelta >= 10) s = 1.0;   // most of the delay already shows before the router: it is not the line's queue
            else if (localPart >= 0.2 && r.GwDelta >= 10) s -= 1.5;
            h.Score = Math.Max(h.Score, s);
            h.Preuves.Add($"{cap} : latence Internet médiane {F0(r.IdleMed!.Value)} → {F0(r.LoadMed!.Value)} ms (+{F0(delta)} ms, note indicative {r.Grade}), " +
                          $"p95 sous charge {FmtMs(r.LoadP95)}, perte max {F1(r.LossPct!.Value)} %, débit soutenu {F0(r.Mbps!.Value)} Mbps.");
            if (r.GwDelta.HasValue && delta >= 30)
            {
                if (r.GwDelta < 10)
                    h.Preuves.Add($"{cap} : la passerelle reste stable (+{F0(r.GwDelta.Value)} ms) alors qu'Internet monte : la file d'attente se forme au‑delà du réseau local (sortie du routeur, modem ou ligne) — compatible avec du bufferbloat.");
                else
                    h.Contre.Add($"{cap} : la latence vers la passerelle monte aussi (+{F0(r.GwDelta.Value)} ms) : le lien local sature, on ne peut pas attribuer (tout) le retard à la ligne Internet.");
            }
            if (delta < Th.BloatDeltaMs[^1].Lim) h.Contre.Add($"{cap} : latence stable sous charge (+{F0(delta)} ms).");
        }
        if (!bloat.Directions.Values.Any(r => r.Valid))
        {
            h.NonEvalue = "Le test de saturation n'a pas chargé la ligne de façon exploitable (voir les limites) : bufferbloat non évalué.";
            return h;
        }
        if (cx.Link == "wifi" && h.Score > 0)
        {
            h.Score = Math.Max(0.0, h.Score - 1);
            h.Limites.Add("Test réalisé en Wi‑Fi : le Wi‑Fi peut ajouter de la latence sous charge ; refaire en Ethernet pour isoler la ligne.");
        }
        h.Score = Math.Max(h.Score, 0.0);
        h.Actions = new() { "Activer une gestion de file (SQM/Smart Queue) si votre routeur la propose, sinon limiter manuellement débit montant/descendant à ~90–95 % du débit mesuré." };
        return h;
    }

    static Hypothesis RuleSaturation(Ctx cx, List<TimelineItem> timeline, BloatResult? bloat, AppConfig cfg)
    {
        var h = H("saturation", "Saturation du téléchargement ou de l'envoi");
        h.Limites.Add("Seul le trafic de CET ordinateur est visible ; les autres appareils de la maison peuvent saturer la ligne sans apparaître ici.");
        h.Limites.Add("Sans débit annoncé ni test de saturation, la capacité de la ligne est inconnue.");
        h.ProchainTest = "Pendant un lag, noter ce que font les autres appareils (streaming, mises à jour) ; renseigner le débit annoncé ou lancer le test pour connaître la capacité.";
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
        if (nSat > 0)
        {
            h.Score += 3 + ((double)nSat / total >= 0.5 ? 2 : 0);
            h.Preuves.Add($"Pendant {nSat} des {total} épisode(s)/incident(s), le trafic de ce PC dépasse {(int)(Th.SatRatio * 100)} % de la capacité connue (↓ {CapTxt("down")} / ↑ {CapTxt("up")} Mbps).");
        }
        foreach (var (d, name) in new[] { ("down", "descendant"), ("up", "montant") })
        {
            var v = Stats.Values(cx.Series($"net:{d}_bps"));
            if (v.Count > 0 && cap[d] is > 0)
            {
                double p95 = Stats.Percentile(v.OrderBy(x => x).ToList(), 95)! .Value / 1e6;
                if (p95 >= Th.SatRatio * cap[d]!.Value)
                {
                    h.Score += 1;
                    h.Preuves.Add($"Le trafic {name} du PC atteint ≥ {(int)(Th.SatRatio * 100)} % de la capacité (p95 {F0(p95)} Mbps).");
                }
            }
        }
        var foyer = timeline.Where(i => i.Type is "lag" or "episode" && i.Zone is "amont" or "trajet" && (i.Facts!.NetDownMed ?? 0) < 2 && (i.Facts.NetUpMed ?? 0) < 2).ToList();
        if (foyer.Count > 0 && nSat == 0)
        {
            h.Score += 1;
            h.Preuves.Add($"{foyer.Count} épisode(s) avec latence Internet élevée alors que ce PC échange peu de données : un autre appareil du foyer pourrait saturer la ligne (indice faible, non vérifiable depuis ce PC).");
        }
        if (total > 0 && nSat == 0 && cap["down"] is > 0 && foyer.Count == 0) h.Contre.Add("Le trafic de ce PC reste loin de la capacité pendant les épisodes.");
        h.Actions = new()
        {
            "Mettre en pause les téléchargements/mises à jour/streaming pendant le jeu, puis refaire une session pour comparer.",
            "Consulter la page d'état du routeur (appareils connectés / trafic) pendant un lag, si elle existe sur votre modèle.",
        };
        return h;
    }

    static Hypothesis RuleRouter(Ctx cx, BloatResult? bloat, AppConfig cfg)
    {
        var h = H("routeur_qos", "Problème de routeur ou de configuration QoS");
        h.Limites.Add("L'application ne lit pas le routeur automatiquement : elle ne connaît que ce que vous avez saisi dans l'onglet Routeur & QoS.");
        h.Limites.Add("Priorisation QoS ≠ gestion de file (SQM) : ne pas supposer que votre modèle propose SQM (à vérifier dans la documentation officielle de votre modèle et firmware).");
        h.ProchainTest = "Renseigner la configuration QoS, appliquer UN seul changement proposé, puis refaire le test de saturation selon le protocole avant/après.";
        var gw = cx.Gw?.Stats;
        if (cx.Link == "ethernet" && gw != null && (gw.LossPct >= Th.GwLossPct || (gw.P95 ?? 0) >= Th.GwP95Ms))
        {
            h.Score += 3;
            h.Preuves.Add($"Connexion filaire mais la passerelle est instable (perte {F1(gw.LossPct)} %, p95 {FmtMs(gw.P95)}) : le routeur (charge, firmware), le câble ou le port sont à examiner.");
        }
        var rcfg = cfg.Router;
        (double? Down, double? Up) meas = (bloat?.Directions.GetValueOrDefault("down")?.Mbps, bloat?.Directions.GetValueOrDefault("up")?.Mbps);
        double worst = (bloat?.Directions.Values.Where(r => r.Valid && r.Delta.HasValue).Select(r => r.Delta!.Value) ?? Enumerable.Empty<double>()).DefaultIfEmpty(0).Max();
        var findings = RouterQos.Check(rcfg, meas, cfg, worst);
        h.Findings = findings;
        foreach (var f in findings)
        {
            if (f.Severite == "probleme") { h.Score += 2; h.Preuves.Add(f.Texte); }
            else if (f.Severite == "attention") { h.Score += 1; h.Preuves.Add(f.Texte); }
            else if (f.Severite == "ok") h.Contre.Add(f.Texte);
        }
        h.Score = Math.Min(h.Score, 8.0);
        if (rcfg is null) h.Limites.Add("Aucune configuration de routeur saisie : l'analyse QoS n'a pas pu comparer les limites aux débits mesurés.");
        h.Actions = RouterQos.Propose(rcfg, meas, worst).Select(p => p.Changement).ToList();
        if (h.Actions.Count == 0)
            h.Actions.Add("Redémarrer le routeur, vérifier sur le site du constructeur si un firmware plus récent existe pour votre modèle/version matérielle, essayer un autre câble ou port.");
        return h;
    }

    static Hypothesis RuleIsp(Ctx cx, List<TimelineItem> timeline)
    {
        var h = H("fai", "Problème chez le fournisseur Internet ou sur un trajet Internet");
        h.Limites.Add("Les cibles par défaut sont des résolveurs DNS publics (anycast) : elles ne passent pas forcément par le même chemin que votre jeu ou votre site.");
        h.Limites.Add("Un traceroute ponctuel ne montre qu'un instant et trois sondes par saut.");
        h.ProchainTest = "Refaire une session avec votre serveur de jeu comme destination personnalisée et cliquer « Je lag maintenant » pendant le lag (traceroute automatique) ; comparer à une autre heure.";
        var items = timeline.Where(i => i.Type is "lag" or "episode").ToList();
        var busy = items.Where(i => i.Zone is "amont" or "trajet" && PcBusy(i.Facts!)).ToList();
        var zones = ZoneCounts(items.Where(i => !busy.Contains(i)));  // an episode where the PC itself loads the line proves nothing about the ISP
        int total = items.Count;
        if (busy.Count > 0)
            h.Limites.Add($"{busy.Count} épisode(s) coïncident avec un trafic soutenu (≥ {G(Th.BusyMbps)} Mbps) de ce PC : la hausse de latence peut venir de ce trafic lui‑même, ils ne sont pas retenus contre le fournisseur.");
        if (Z(zones, "amont") > 0)
        {
            h.Score += (double)Z(zones, "amont") / total >= 0.5 ? 6 : 4;
            h.Preuves.Add($"{Z(zones, "amont")} épisode(s)/incident(s) sur {total} : la passerelle est saine mais plusieurs destinations Internet indépendantes sont dégradées en même temps, sans trafic notable du PC.");
        }
        if (Z(zones, "trajet") > 0 && Z(zones, "amont") == 0)
        {
            h.Score += 3;
            h.Preuves.Add($"{Z(zones, "trajet")} épisode(s) n'affectent qu'UNE destination Internet (les autres et la passerelle restent sains) : plutôt le trajet vers cette destination que la connexion entière.");
        }
        if (Z(zones, "trajet_custom") > 0)
        {
            h.Score += 3;
            h.Preuves.Add($"{Z(zones, "trajet_custom")} épisode(s) n'affectent que votre destination personnalisée : problème possible sur le trajet ou chez ce serveur.");
        }
        if (total > 0 && Z(zones, "amont") + Z(zones, "trajet") + Z(zones, "trajet_custom") == 0)
            h.Contre.Add("Aucun épisode ne dégrade Internet tout en épargnant la passerelle.");
        if (Z(zones, "local") > 0 && Z(zones, "amont") > 0)
        {
            h.Score = Math.Min(h.Score, Math.Max(1.0, h.Score - 2));
            h.Limites.Add("Une partie des épisodes touche aussi la passerelle : le réseau local peut expliquer une partie de la dégradation vers Internet.");
        }
        foreach (var tr in cx.D.Traces)
        {
            var a = tr.Data.Analysis;
            if (a is null) continue;
            if (a.Step != null)
            {
                h.Score += 2;
                h.Preuves.Add($"Traceroute vers {tr.Target} : la latence monte de {F0(a.Step.FromMs)} à {F0(a.Step.ToMs)} ms au saut {a.Step.Hop} ({a.Step.Ip ?? "adresse inconnue"}) et persiste jusqu'à la destination.");
            }
            if (a.IntermediateLoss.Count > 0 && !(a.DestLossPct is > 0))
                h.Contre.Add($"Traceroute vers {tr.Target} : perte(s) sur des sauts intermédiaires ({string.Join(", ", a.IntermediateLoss)}) NON retrouvée(s) à destination → limitation ICMP des routeurs, pas une perte réelle.");
            else if (a.DestLossPct is > 0)
            {
                h.Score += 1;
                h.Preuves.Add($"Traceroute vers {tr.Target} : perte à la destination ({F0(a.DestLossPct.Value)} %).");
            }
        }
        h.Actions = new()
        {
            "Noter heure et fréquence des épisodes et contacter le fournisseur avec le rapport exporté si les épisodes « en amont » se répètent.",
            "Tester avec un autre appareil/ordinateur en Ethernet branché directement au modem pour exclure le routeur.",
        };
        return h;
    }

    static Hypothesis RuleDns(Ctx cx)
    {
        var h = H("dns", "Problème DNS");
        h.Limites.Add("La mesure interroge directement votre résolveur en UDP/53 ; un navigateur qui utilise DNS‑sur‑HTTPS contourne ce résolveur.");
        h.Limites.Add("Le test « à froid » utilise des noms aléatoires sous example.com (réservé à cet usage).");
        h.ProchainTest = "Changer le DNS du PC (ex. 1.1.1.1 ou 9.9.9.9), refaire une session et comparer les temps de résolution.";
        var hit = Stats.Rtt(cx.Series("dns:sys_hit"));
        var miss = Stats.Rtt(cx.Series("dns:sys_miss"));
        var rf = Stats.Rtt(cx.Series("dns:ref_miss"));
        if (hit is null)
        {
            h.Limites.Add("Aucune mesure DNS disponible.");
            return h;
        }
        if (hit.LossPct >= Th.DnsFailPct)
        {
            h.Score += 4;
            h.Preuves.Add($"Échecs de résolution : {F1(hit.LossPct)} % ({hit.Lost} sur {hit.N}) sur le DNS configuré.");
        }
        if (hit.Median != null && hit.Median >= Th.DnsMedMs)
        {
            h.Score += 3;
            h.Preuves.Add($"Résolution lente (noms courants) : médiane {FmtMs(hit.Median)}.");
        }
        if (hit.P95 != null && hit.P95 >= Th.DnsP95Ms)
        {
            h.Score += 2;
            h.Preuves.Add($"Pics de résolution : p95 {FmtMs(hit.P95)}, max {FmtMs(hit.Max)}.");
        }
        if (miss?.Median != null && miss.Median >= Th.DnsMissMedMs)
        {
            h.Score += 2;
            h.Preuves.Add($"Résolution à froid lente : médiane {FmtMs(miss.Median)}.");
        }
        if (rf?.Median != null && miss?.Median != null && rf.Median < 0.5 * miss.Median && miss.Median - rf.Median >= 80)
        {
            h.Score += 2;
            h.Preuves.Add($"Le résolveur public 1.1.1.1 résout à froid bien plus vite ({FmtMs(rf.Median)}) que votre DNS ({FmtMs(miss.Median)}).");
        }
        if (h.Score == 0) h.Contre.Add($"DNS rapide et fiable : médiane {FmtMs(hit.Median)}, échecs {F1(hit.LossPct)} %.");
        h.Actions = new() { "Essayer un autre résolveur (1.1.1.1, 9.9.9.9 ou 8.8.8.8) dans les réglages réseau de Windows." };
        return h;
    }

    static Hypothesis RuleBackground(Ctx cx, List<TimelineItem> timeline)
    {
        var h = H("fond", "Trafic de fond sur mon ordinateur");
        h.Limites.Add("Méthode : compteurs de l'interface réseau (totaux du PC). Aucune attribution par processus n'est faite car il n'existe pas de méthode fiable sans droits administrateur ni capture : " +
                      "l'application ne vous désigne donc PAS un programme coupable.");
        h.Limites.Add("Le trafic des autres appareils de la maison n'est pas visible.");
        h.ProchainTest = "Ouvrir le Gestionnaire des tâches (onglet Performances/Processus, colonne Réseau) ou le Moniteur de ressources (resmon › Réseau) pendant un lag, puis refaire une session avec les applications de fond fermées.";
        var down = Stats.Values(cx.Series("net:down_bps"));
        var up = Stats.Values(cx.Series("net:up_bps"));
        if (down.Count == 0)
        {
            h.Limites.Add("Trafic du PC indisponible (interface réseau non lue).");
            return h;
        }
        double md = Mbps(Stats.Median(down))!.Value, mu = Mbps(Stats.Median(up)) ?? 0;
        if (md >= Th.BgDownMbps || mu >= Th.BgUpMbps)
        {
            h.Score += md >= 10 || mu >= 5 ? 3 : 2;
            h.Preuves.Add($"Trafic de fond notable hors test : médiane ↓ {F1(md)} Mbps, ↑ {F1(mu)} Mbps.");
        }
        var idle = PhaseWindow(cx.D, "repos");
        if (idle.Count > 0)
        {
            var iv = Stats.Values(idle.SelectMany(w => Stats.Window(cx.D.S("net:down_bps"), w.A, w.B).Concat(Stats.Window(cx.D.S("net:up_bps"), w.A, w.B))));
            if (iv.Count > 0 && Mbps(Stats.Median(iv))!.Value >= Th.BgIdleMbps / 2)
            {
                h.Score += 3;
                h.Preuves.Add($"Pendant la phase de repos du test (rien ne devait circuler), le PC échange déjà {F1(Mbps(Stats.Median(iv))!.Value * 2)} Mbps (↓+↑ cumulés).");
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
                h.Preuves.Add($"Aux instants dégradés, le trafic du PC est {F1(a / b)}× plus élevé que le reste du temps ({F1(a)} contre {F1(b)} Mbps) : corrélation compatible avec une activité de fond (une corrélation n'est pas une preuve).");
            }
        }
        var hot = timeline.Count(i => i.Type is "lag" or "episode" && ((i.Facts!.NetDownMax ?? 0) >= 5 || (i.Facts.NetUpMax ?? 0) >= 5));
        if (hot > 0)
        {
            h.Score += 1;
            h.Preuves.Add($"{hot} épisode(s)/incident(s) coïncident avec un trafic du PC ≥ 5 Mbps.");
        }
        if (h.Score == 0) h.Contre.Add($"Trafic du PC faible hors test : médiane ↓ {F2(md)} Mbps, ↑ {F2(mu)} Mbps.");
        h.Actions = new() { "Fermer sauvegardes cloud, mises à jour, lanceurs de jeux, onglets de streaming pendant le jeu, puis refaire une session pour comparer." };
        return h;
    }

    // ------------------------------------------------------------------ assembly
    static StatsTables BuildStats(Ctx cx)
    {
        var res = new StatsTables();
        foreach (var (tid, x) in cx.T)
        {
            var etat = x.State switch
            {
                "ok" => "mesuré (ICMP)",
                "tcp" => "mesuré en TCP (l'ICMP ne répond pas)",
                _ => "ne répond pas à l'ICMP (cible exclue du diagnostic)",
            };
            res.Targets.Add(new TargetRow { Id = tid, Label = x.Tg.Label, Role = x.Tg.Role, Host = x.Tg.Host, State = x.State, Etat = etat, Stats = x.Stats });
        }
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
        return new AppConfig { CustomTarget = live.CustomTarget, GatewayOverride = live.GatewayOverride, PlanDownMbps = snap.PlanDownMbps, PlanUpMbps = snap.PlanUpMbps, Router = snap.Router };
    }

    public static Analysis Analyze(SessionData data, AppConfig? cfg = null)
    {
        cfg ??= new AppConfig();
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
        foreach (var h in hyps)
        {
            h.Niveau = Level(h.Score);
            h.Score = Math.Round(Math.Min(h.Score, 10.0), 1, MidpointRounding.ToEven);
        }
        var shown = hyps.Where(h => h.Score >= 2 && h.Preuves.Count > 0).OrderByDescending(h => h.Score).ToList();
        var unlikely = hyps.Where(h => h.Score < 2 && h.Contre.Count > 0).Select(h => new Unlikely { Id = h.Id, Titre = h.Titre, Raisons = h.Contre }).ToList();
        var notEval = hyps.Where(h => h.NonEvalue != null).Select(h => new NotEvaluated { Id = h.Id, Titre = h.Titre, Raison = h.NonEvalue!, ProchainTest = h.ProchainTest }).ToList();
        var stat = BuildStats(cx);
        bool sparse = Math.Max(gwN, inetN) < Th.MinSamples;
        bool everythingDead = cx.T.Count > 0 && cx.T.Values.All(x => x.State == "no_response");
        var incidents = timeline.Where(i => i.Type == "lag").ToList();
        var resume = new List<string>();
        var actions = new List<string>();
        if (cx.T.Count == 0) resume.Add("Aucune mesure enregistrée.");
        else if (everythingDead) resume.Add("Aucune cible n'a répondu : connexion coupée pendant toute la session, ou ICMP bloqué sur cette machine/ce réseau. Rien ne peut être conclu sur le lag.");
        else if (sparse) resume.Add($"Données insuffisantes ({Math.Max(gwN, inetN)} mesures calmes ; il en faut au moins {Th.MinSamples}). Laissez tourner 10 à 30 minutes.");
        if (shown.Count > 0 && !everythingDead)
        {
            var top = shown[0];
            resume.Add($"Hypothèse la plus compatible avec les mesures : « {top.Titre} » (confiance {top.Niveau}).");
            if (shown.Count > 1) resume.Add("Autres pistes : " + string.Join(" ; ", shown.Skip(1).Take(3).Select(h => $"{h.Titre} ({h.Niveau})")) + ".");
            resume.Add("Ce sont des hypothèses déduites de mesures, pas des causes confirmées : voir pour chacune les preuves, les limites et le test suivant.");
        }
        else if (!sparse && !everythingDead)
            resume.Add("Aucune dégradation nette mesurée pendant cette session : le lag n'a probablement pas été capturé. Relancez la surveillance et cliquez « Je lag maintenant » pendant un épisode.");
        foreach (var x in cx.T.Values)
            if (x.State == "no_response" && !everythingDead)
                resume.Add($"« {x.Tg.Label} » ne répond pas à l'ICMP (ping) : cible exclue du diagnostic. Précisez un port TCP (ex. {x.Tg.Host}:443) pour la mesurer autrement.");
        int quiet = incidents.Count(i => i.Zone == "aucune");
        if (incidents.Count > 0 && quiet > 0)
            resume.Add($"Pour {quiet} signalement(s) sur {incidents.Count}, aucune anomalie réseau n'est mesurée autour de l'instant : piste hors réseau (ordinateur, jeu, serveur).");
        foreach (var h in shown) foreach (var a in h.Actions) if (!actions.Contains(a)) actions.Add(a);
        return new Analysis
        {
            Session = data.Id, Resume = resume, Hypotheses = shown, PeuProbables = unlikely, NonEvalue = notEval, Actions = actions, Timeline = timeline,
            Stats = stat, Bufferbloat = bloat, LimitesGenerales = LimitesGenerales.ToList(), Seuils = Th.AsDictionary(),
            Metrics = ComputeMetrics(stat, bloat, incidents.Count),
        };
    }

    // ------------------------------------------------------------------ session comparison
    public static Metrics ComputeMetrics(StatsTables stat, BloatResult? bloat, int nIncidents)
    {
        var inet = stat.Targets.Where(t => t.Role == "internet" && t.Stats != null && t.State != "no_response").Select(t => t.Stats!).ToList();
        var gw = stat.Targets.FirstOrDefault(t => t.Role == "gateway")?.Stats;
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

    static readonly (string Label, int Better, Func<Metrics, double?> Get)[] MetricDefs =
    {
        ("Latence Internet p95 (ms)", -1, m => m.InetP95), ("Perte Internet (%)", -1, m => m.InetLoss), ("Latence passerelle p95 (ms)", -1, m => m.GwP95),
        ("Gigue Internet (ms)", -1, m => m.JitterInet), ("DNS médiane (ms)", -1, m => m.DnsMed), ("Hausse de latence en téléchargement (ms)", -1, m => m.BloatDown),
        ("Hausse de latence en envoi (ms)", -1, m => m.BloatUp), ("Débit descendant soutenu (Mbps)", +1, m => m.DownMbps), ("Débit montant soutenu (Mbps)", +1, m => m.UpMbps),
    };

    /// <summary>
    /// Compares two groups of sessions. Cautious verdict: a difference is kept only when it exceeds the variability observed
    /// INSIDE each group (repeat each condition 2–3 times).
    /// </summary>
    public static List<CompareRow> Compare(IReadOnlyList<Metrics> groupA, IReadOnlyList<Metrics> groupB)
    {
        var rows = new List<CompareRow>();
        foreach (var (label, better, get) in MetricDefs)
        {
            var a = groupA.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var b = groupB.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (a.Count == 0 || b.Count == 0) continue;
            double ma = Stats.Median(a)!.Value, mb = Stats.Median(b)!.Value, delta = mb - ma;
            double spread = Math.Max(a.Max() - a.Min(), b.Max() - b.Min());
            string verdict = a.Count < 2 || b.Count < 2 ? "indicatif (une seule mesure par groupe : répéter pour juger)"
                : Math.Abs(delta) <= spread ? "indistinct (dans la variabilité entre répétitions)"
                : delta * better > 0 ? "amélioration" : "dégradation";
            rows.Add(new CompareRow { Metrique = label, A = ma, B = mb, Delta = delta, Variabilite = spread, CountA = a.Count, CountB = b.Count, Verdict = verdict });
        }
        return rows;
    }
}
