using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NetworkAnalyzer;

/// <summary>Exports (CSV, JSON) and self-contained HTML report (no external resource, charts in SVG). Texts follow the current language.</summary>
public static class Report
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string T(string key, params object?[] args) => Loc.T(key, args);

    static readonly Dictionary<string, string> Colors = new()
    {
        ["gateway"] = "#1a7f37", ["cloudflare"] = "#0969da", ["google"] = "#bc4c00", ["quad9"] = "#8250df", ["custom"] = "#cf222e", ["cloudflare6"] = "#0a7ea4",
    };

    public static string Ts(double t, string fmt = "HH:mm:ss") => DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).ToLocalTime().ToString(fmt, Inv);
    static string Num(double? x, int d = 0, string unit = "") => x is null ? "—" : x.Value.ToString("F" + d, Loc.Fmt) + unit;

    /// <summary>A change with an explicit sign: +4, -3, 0; an em dash when there is no value.</summary>
    static string Signed(double? x, int d = 0, string unit = "")
    {
        if (x is null) return "—";
        double rounded = Math.Round(x.Value, d);
        if (rounded == 0) return Num(0.0, d, unit);   // a change that shows as 0 has no sign: not "+0", not "-0"
        return (rounded > 0 ? "+" : "") + Num(x, d, unit);
    }

    // ------------------------------------------------------------------ exports
    static string Csv(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;  // spreadsheet formula protection
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>One row per measurement. Column names and series names are fixed English identifiers (data, not display text).</summary>
    public static string ExportCsv(SessionData d)
    {
        var sb = new StringBuilder("session,local_time,epoch_s,series,value,ok,info\n");
        foreach (var (name, s) in d.Series.SelectMany(kv => kv.Value.Select(s => (kv.Key, s))).OrderBy(x => x.s.T))
            sb.Append(d.Id).Append(',').Append(Ts(s.T, "yyyy-MM-dd HH:mm:ss")).Append(',').Append(s.T.ToString("F3", Inv)).Append(',').Append(Csv(name)).Append(',')
              .Append(s.V.HasValue ? s.V.Value.ToString("F3", Inv) : "").Append(',').Append(s.Ok ? 1 : 0).Append(',').Append(Csv(s.Info)).Append('\n');
        foreach (var m in d.Marks)
            sb.Append(d.Id).Append(',').Append(Ts(m.T, "yyyy-MM-dd HH:mm:ss")).Append(',').Append(m.T.ToString("F3", Inv)).Append(',').Append(Csv("mark:" + m.Kind)).Append(",,,").Append(Csv(m.Note)).Append('\n');
        return sb.ToString();
    }

    public static string ExportJson(SessionData d, Analysis a) => JsonSerializer.Serialize(new
    {
        session = new { id = d.Id, start = d.Started, end = d.Ended, label = d.Label, link = d.Link, meta = d.Meta },
        definitions = Stats.Definitions(),
        measurements = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => new object?[] { s.T, s.V, s.Ok ? 1 : 0, s.Info }).ToList()),
        marks = d.Marks, phases = d.Phases, traceroutes = d.Traces, analysis = a,
    }, Json.Indented);

    // ------------------------------------------------------------------ SVG charts
    public sealed class ChartSeries
    {
        public string Name { get; init; } = "";
        public string Color { get; init; } = "#555";
        public List<(double T, double V)> Pts { get; init; } = new();
        public List<double> Lost { get; init; } = new();
    }

    static int chartSeq;

    /// <summary>Curve = maximum per column (spikes stay visible).</summary>
    public static string SvgChart(List<ChartSeries> series, double t0, double t1, IEnumerable<Mark>? marks = null, IEnumerable<Phase>? phases = null, int height = 170, string unit = "ms", double? clip = null, string title = "")
    {
        const int W = 860, L = 46, B = 18, cols = 430;
        int H = height;
        double span = Math.Max(1.0, t1 - t0);
        var allv = series.SelectMany(s => s.Pts.Select(p => p.V)).ToList();
        // nothing at all is "no data"; failures alone are a chart (a total outage is a run of failures, not an absence of measurement)
        if (allv.Count == 0 && !series.Any(s => s.Lost.Count > 0)) return $"<p class=\"muted\">{E(T("rep.nochart"))}</p>";
        double ymax = Math.Max(clip ?? (allv.Count > 0 ? allv.Max() : 100.0), 1e-9) * 1.08;   // no value to scale on: a default scale
        double X(double t) => L + (t - t0) / span * (W - L - 6);
        double Y(double v) => 4 + (1 - Math.Min(v, ymax) / ymax) * (H - B - 4);
        string N1(double x) => x.ToString("0.0", Inv);
        // accessible name: what the chart shows, then the curves it holds
        var name = E((title.Length > 0 ? title + ": " : "") + string.Join(", ", series.Select(s => s.Name)) + " (" + unit + ")");
        // text alternative: what the curves say, figure by figure (a screen reader cannot read a drawing)
        string F(double v) => v.ToString("0.#", Loc.Fmt);
        var desc = E(string.Join(" ", series.Where(s => s.Pts.Count > 0 || s.Lost.Count > 0).Select(s => s.Pts.Count > 0
            ? T("rep.chart_desc", s.Name, F(Stats.Median(s.Pts.Select(p => p.V)) ?? 0), unit, F(s.Pts.Max(p => p.V)), s.Lost.Count)
            : T("rep.chart_desc_loss", s.Name, s.Lost.Count))));
        var descId = $"chart-desc-{Interlocked.Increment(ref chartSeq)}";   // ids are unique in a document, and a report holds several charts
        var o = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" class=\"chart\" aria-label=\"{name}\" aria-describedby=\"{descId}\"><title>{name}</title><desc id=\"{descId}\">{desc}</desc>");
        foreach (var p in phases ?? Enumerable.Empty<Phase>())
            o.Append($"<rect x=\"{N1(X(p.T0))}\" y=\"4\" width=\"{N1(Math.Max(1, X(p.T1) - X(p.T0)))}\" height=\"{H - B - 4}\" fill=\"#8884\" /><text x=\"{N1(X(p.T0) + 2)}\" y=\"14\" font-size=\"9\" fill=\"#666\">{E(p.Name)}</text>");
        for (int i = 0; i < 5; i++)
        {
            double y = 4 + i * (H - B - 4) / 4.0;
            o.Append($"<line x1=\"{L}\" x2=\"{W - 6}\" y1=\"{N1(y)}\" y2=\"{N1(y)}\" stroke=\"#ddd\"/><text x=\"{L - 4}\" y=\"{N1(y + 3)}\" font-size=\"9\" text-anchor=\"end\" fill=\"#666\">{(ymax * (1 - i / 4.0)).ToString(ymax < 1 ? "0.00" : ymax < 10 ? "0.0" : "0", Loc.Fmt)}</text>");
        }
        for (int i = 0; i <= 6; i++)
        {
            double t = t0 + span * i / 6;
            o.Append($"<text x=\"{N1(X(t))}\" y=\"{H - 4}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#666\">{Ts(t, "HH:mm")}</text>");
        }
        foreach (var s in series)
        {
            var buckets = new SortedDictionary<int, double>();
            foreach (var (t, v) in s.Pts)
            {
                int k = (int)((t - t0) / span * cols);
                buckets[k] = Math.Max(buckets.GetValueOrDefault(k), v);
            }
            // One polyline per run of measurements: a hole (a pause, or a stretch with only failures) is not crossed by a line
            // that would suggest a continuous measured evolution. "Hole" = more than 4 usual sampling steps (the series' own
            // median spacing: a second or two of jitter is not a hole) and more than 2 chart columns.
            var times = s.Pts.Select(p => p.T).OrderBy(x => x).ToList();
            var steps = times.Zip(times.Skip(1), (a, b) => b - a).Where(x => x > 0).ToList();
            double holeS = Math.Max(4 * (Stats.Median(steps) ?? 1.0), 2 * span / cols);
            var run = new List<(double T, double V)>();
            void Flush()
            {
                if (run.Count == 1) o.Append($"<circle cx=\"{N1(X(run[0].T))}\" cy=\"{N1(Y(run[0].V))}\" r=\"1.6\" fill=\"{s.Color}\"/>");
                else if (run.Count > 1) o.Append($"<polyline fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"1.2\" points=\"{string.Join(" ", run.Select(p => $"{N1(X(p.T))},{N1(Y(p.V))}"))}\"/>");
                run.Clear();
            }
            foreach (var kv in buckets)
            {
                double tk = t0 + kv.Key / (double)cols * span;
                if (run.Count > 0 && tk - run[^1].T > holeS) Flush();
                run.Add((tk, kv.Value));
            }
            Flush();
            // one tick per chart column: a long session keeps the losses of its end too
            foreach (var k in s.Lost.Select(t => (int)((t - t0) / span * cols)).Distinct().Order())
                o.Append($"<line x1=\"{N1(X(t0 + k / (double)cols * span))}\" x2=\"{N1(X(t0 + k / (double)cols * span))}\" y1=\"{H - B - 6}\" y2=\"{H - B}\" stroke=\"{s.Color}\" stroke-width=\"1.5\"/>");
        }
        foreach (var m in marks ?? Enumerable.Empty<Mark>())
            o.Append($"<line x1=\"{N1(X(m.T))}\" x2=\"{N1(X(m.T))}\" y1=\"4\" y2=\"{H - B}\" stroke=\"#cf222e\" stroke-dasharray=\"3 3\"/>");
        o.Append($"<text x=\"2\" y=\"10\" font-size=\"9\" fill=\"#666\">{E(unit)}</text></svg>");
        return o.ToString();
    }

    static string Legend(List<ChartSeries> series) => "<p class=\"legend\">" + string.Join(" ", series.Select(s => $"<span><i style=\"background:{s.Color}\"></i>{E(s.Name)}</span>")) + "</p>";

    static List<ChartSeries> LatencySeries(SessionData d)
    {
        var res = new List<ChartSeries>();
        // a target that never answers while others do is filtered ICMP (not a loss), so its failures are not drawn;
        // but when NOTHING answered anywhere it is an outage, and the failures are the whole story
        bool anyAnswered = d.Targets.Any(t => d.S($"ping:{t.Id}").Any(s => s.Ok));
        foreach (var tg in d.Targets)
        {
            var raw = d.S($"ping:{tg.Id}").ToList();
            if (raw.Any(s => s.Info == "tcp")) raw = raw.Where(s => s.Info != "icmp_no_reply").ToList();
            bool answered = raw.Any(s => s.Ok) || !anyAnswered;
            res.Add(new ChartSeries
            {
                Name = tg.Label, Color = Colors.GetValueOrDefault(tg.Id, "#555"),
                Pts = raw.Where(s => s.Ok && s.V.HasValue).Select(s => (s.T, s.V!.Value)).ToList(),
                Lost = answered ? raw.Where(s => !s.Ok).Select(s => s.T).ToList() : new List<double>(),
            });
        }
        return res;
    }

    // ------------------------------------------------------------------ HTML report
    const string Css = @"body{font:15px/1.5 system-ui,Segoe UI,sans-serif;max-width:980px;margin:24px auto;padding:0 16px;color:#1f2328}
h1{font-size:26px;margin:.2em 0}h2{font-size:19px;margin:1.6em 0 .5em;border-bottom:1px solid #d0d7de;padding-bottom:4px}h3{font-size:16px;margin:.8em 0 .2em}
table{border-collapse:collapse;width:100%;font-size:13px;margin:.4em 0}th,td{border:1px solid #d0d7de;padding:4px 7px;text-align:left}th{background:#f6f8fa}
td.n{text-align:right;font-variant-numeric:tabular-nums}.muted{color:#656d76}.card{border:1px solid #d0d7de;border-radius:8px;padding:10px 14px;margin:10px 0;break-inside:avoid}
.badge{display:inline-block;border-radius:10px;padding:1px 9px;font-size:12px;color:#fff}.b-low{background:#9a6700}.b-medium{background:#bc4c00}.b-high{background:#cf222e}
.warn{background:#fff8c5;border:1px solid #d4a72c;border-radius:6px;padding:6px 10px}ul{margin:.2em 0 .4em 1.2em;padding:0}.chart{width:100%;height:auto;background:#fff;border:1px solid #d0d7de}
.legend span{margin-right:14px;font-size:12px}.legend i{display:inline-block;width:10px;height:10px;margin-right:4px;border-radius:2px}img.shot{max-width:100%;border:1px solid #d0d7de}
@media print{body{max-width:none}}";

    static string Ul(IEnumerable<string> items)
    {
        var l = items.ToList();
        return l.Count == 0 ? "" : "<ul>" + string.Concat(l.Select(i => $"<li>{E(i)}</li>")) + "</ul>";
    }

    static string StatRow(string label, string state, RttStats? s)
    {
        if (s is null) return $"<tr><td>{E(label)}</td><td>{E(state)}</td>" + string.Concat(Enumerable.Repeat("<td class='n'>—</td>", 7)) + "</tr>";
        return $"<tr><td>{E(label)}</td><td>{E(state)}</td><td class='n'>{s.N}</td><td class='n'>{s.LossPct.ToString("0.0", Loc.Fmt)}</td><td class='n'>{Num(s.Median, 1)}</td>" +
               $"<td class='n'>{Num(s.P95, 1)}</td><td class='n'>{Num(s.Max, 1)}</td><td class='n'>{Num(s.Jitter, 1)}</td><td class='n'>{Num(s.Min, 1)}</td></tr>";
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string Html(SessionData d, Analysis a, AppConfig? cfg = null, IEnumerable<(string Name, string Uri)>? shots = null, string version = "")
    {
        cfg ??= new AppConfig();
        shots ??= Enumerable.Empty<(string, string)>();
        var env = d.Meta.Env;
        var act = env.Active;
        double end = d.EndOrLast;
        var o = new StringBuilder();
        o.Append($"<!doctype html><html lang='{Loc.Lang}'><head><meta charset='utf-8'><title>{E(T("rep.title"))} — #{d.Id}</title><style>{Css}</style></head><body>");
        o.Append($"<h1>{E(T("rep.title"))}</h1><p class='muted'>{E(T("rep.session", d.Id, string.IsNullOrEmpty(d.Label) ? T("rep.nolabel") : d.Label, Ts(d.Started, "yyyy-MM-dd HH:mm"), Ts(end, "HH:mm"), ((end - d.Started) / 60).ToString("0", Inv), d.Link))}" + (version.Length > 0 ? E(T("rep.version", version)) : "") + "</p>");
        o.Append($"<p class='warn'>{T("rep.disclaimer")}</p>");
        o.Append($"<h2>{E(T("rep.h.summary"))}</h2>").Append(string.Concat(a.Summary.Select(r => $"<p>{E(r)}</p>")));
        o.Append($"<h2>{E(T("rep.h.hypotheses"))}</h2>");
        if (a.Hypotheses.Count == 0) o.Append($"<p>{E(T("rep.nohyp"))}</p>");
        foreach (var h in a.Hypotheses)
            o.Append($"<div class='card'><h3>{E(h.Title)} <span class='badge b-{h.Level}'>{E(T("rep.confidence", T("d.level." + h.Level)))}</span></h3><b>{E(T("rep.evidence"))}</b>{Ul(h.Evidence)}" +
                     (h.Counter.Count > 0 ? $"<b>{E(T("rep.counter"))}</b>{Ul(h.Counter)}" : "") + $"<b>{E(T("rep.limits"))}</b>{Ul(h.Limits)}<p><b>{E(T("rep.next"))}</b> {E(h.NextTest)}</p></div>");
        if (a.Unlikely.Count > 0)
            o.Append($"<h3>{E(T("rep.h.unlikely"))}</h3><ul>" + string.Concat(a.Unlikely.Select(u => $"<li><b>{E(u.Title)}</b> — {E(string.Join(" ", u.Reasons))}</li>")) + "</ul>");
        if (a.NotEvaluated.Count > 0)
            o.Append($"<h3>{E(T("rep.h.noteval"))}</h3><ul>" + string.Concat(a.NotEvaluated.Select(u => $"<li><b>{E(u.Title)}</b> — {E(u.Reason)}</li>")) + "</ul>");
        o.Append($"<h2>{E(T("rep.h.actions"))}</h2>");
        o.Append(a.Actions.Count > 0 ? "<ol>" + string.Concat(a.Actions.Select(x => $"<li>{E(x)}</li>")) + "</ol>" : $"<p>{E(T("rep.noactions"))}</p>");

        o.Append($"<h2>{E(T("rep.h.timeline"))}</h2>");
        if (a.Timeline.Count > 0)
        {
            o.Append($"<table><tr><th>{E(T("rep.th.time"))}</th><th>{E(T("rep.th.type"))}</th><th>{E(T("rep.th.zone"))}</th><th>{E(T("rep.th.details"))}</th></tr>");
            var timeFmt = Ts(d.Started, "yyyy-MM-dd") != Ts(d.EndOrLast, "yyyy-MM-dd") ? "yyyy-MM-dd HH:mm:ss" : "HH:mm:ss";  // a session over midnight needs the date
            foreach (var i in a.Timeline)
            {
                var det = string.Concat(i.Details.Select(x => $"<div>{E(x)}</div>")) + (i.Note.Length > 0 ? $"<div class='muted'>{E(i.Note)}</div>" : "");
                var zone = E(i.Zone != null ? T("rep.zone." + i.Zone) : "") + (i.ZoneText.Length > 0 ? $"<div class='muted'>{E(i.ZoneText)}</div>" : "");
                o.Append($"<tr><td>{Ts(i.T, timeFmt)}</td><td>{E(T("rep.type." + i.Type))}</td><td>{zone}</td><td>{det}</td></tr>");
            }
            o.Append("</table>");
        }
        else o.Append($"<p>{E(T("rep.noincident"))}</p>");

        o.Append($"<h2>{E(T("rep.h.curves"))}</h2>");
        var ls = LatencySeries(d);
        var vals = ls.SelectMany(s => s.Pts.Select(p => p.V)).OrderBy(x => x).ToList();
        double? cap = vals.Count > 0 ? Math.Max(100.0, Stats.Percentile(vals, 99.5)!.Value * 1.5) : null;
        var marks = d.Marks.Where(m => m.Kind == "lag").ToList();
        o.Append($"<h3>{E(T("rep.latency"))}</h3>").Append(SvgChart(ls, d.Started, end, marks, d.Phases, clip: cap, title: T("rep.latency"))).Append(Legend(ls));
        var tr = new List<ChartSeries>
        {
            new() { Name = T("rep.s.down"), Color = "#0969da", Pts = d.S("net:down_bps").Where(s => s.V.HasValue).Select(s => (s.T, s.V!.Value / 1e6)).ToList() },
            new() { Name = T("rep.s.up"), Color = "#bc4c00", Pts = d.S("net:up_bps").Where(s => s.V.HasValue).Select(s => (s.T, s.V!.Value / 1e6)).ToList() },
        };
        if (tr.Any(s => s.Pts.Count > 0)) o.Append($"<h3>{E(T("rep.traffic"))}</h3>").Append(SvgChart(tr, d.Started, end, marks, d.Phases, unit: "Mbps", title: T("rep.traffic"))).Append(Legend(tr));
        var dns = new List<ChartSeries> { new() { Name = T("rep.s.dns"), Color = "#1a7f37", Pts = d.S("dns:sys_hit").Where(s => s.Ok && s.V.HasValue).Select(s => (s.T, s.V!.Value)).ToList(), Lost = d.S("dns:sys_hit").Where(s => !s.Ok).Select(s => s.T).ToList() } };
        if (dns[0].Pts.Count > 0 || dns[0].Lost.Count > 0) o.Append($"<h3>{E(T("rep.dns"))}</h3>").Append(SvgChart(dns, d.Started, end, marks, d.Phases, height: 120, title: T("rep.dns"))).Append(Legend(dns));
        var wf = new List<ChartSeries> { new() { Name = T("rep.s.wifi"), Color = "#8250df", Pts = d.S("wifi:signal").Where(s => s.V.HasValue).Select(s => (s.T, s.V!.Value)).ToList() } };
        if (wf[0].Pts.Count > 0) o.Append($"<h3>{E(T("rep.s.wifi"))}</h3>").Append(SvgChart(wf, d.Started, end, marks, d.Phases, height: 110, unit: "%", title: T("rep.s.wifi"))).Append(Legend(wf));

        o.Append($"<h2>{E(T("rep.h.measures"))}</h2><table><tr><th>{E(T("rep.th.target"))}</th><th>{E(T("rep.th.state"))}</th><th>{E(T("rep.th.requests"))}</th><th>{E(T("rep.th.loss"))}</th><th>{E(T("rep.th.median"))}</th><th>p95 ms</th><th>{E(T("rep.th.max"))}</th><th>{E(T("rep.th.jitter"))}</th><th>Min ms</th></tr>");
        foreach (var t in a.Stats.Targets) o.Append(StatRow($"{t.Label} — {t.Host}", t.StateText, t.Stats));
        foreach (var (s, key) in new[] { (a.Stats.Dns.SysHit, "rep.dns.hit"), (a.Stats.Dns.SysMiss, "rep.dns.miss"), (a.Stats.Dns.RefMiss, "rep.dns.ref") })
            if (s != null) o.Append(StatRow(T(key), T("rep.measured"), s));
        o.Append($"</table><p class='muted'>{E(T("rep.measures_note"))} " + E(string.Join(" ", Stats.Definitions().Values)) + "</p>");
        var t2 = a.Stats.Traffic;
        if (t2.Down != null && t2.Up != null)
            o.Append("<p>" + E(T("rep.traffic_line", Num(t2.Down.Median, 2), Num(t2.Down.P95, 2), Num(t2.Down.Max, 1), Num(t2.Up.Median, 2), Num(t2.Up.P95, 2), Num(t2.Up.Max, 1))) + $" <span class='muted'>{E(T("rep.traffic_note"))}</span></p>");
        var w = a.Stats.Wifi;
        if (w != null)
            o.Append("<p>" + E(T("rep.wifi_line", Num(w.SignalMed), Num(w.SignalMin), w.Channel?.ToString() ?? "?", w.Band ?? "?", Num(w.TxMed), Num(w.TxMin), Num(w.RxMed))) + "</p>");

        var bb = a.Bufferbloat;
        if (bb != null)
        {
            o.Append($"<h2>{E(T("rep.h.sat"))}</h2><table><tr><th>{E(T("rep.th.dir"))}</th><th>{E(T("rep.th.sustained"))}</th><th>{E(T("rep.th.lat_idle_load"))}</th><th>{E(T("rep.th.increase"))}</th><th>{E(T("rep.th.p95load"))}</th><th>{E(T("rep.th.maxloss"))}</th><th>{E(T("rep.th.gwincrease"))}</th><th>{E(T("rep.th.grade"))}</th></tr>");
            foreach (var k in new[] { "down", "up" })
            {
                if (!bb.Directions.TryGetValue(k, out var r)) continue;
                var gw = Signed(r.GwDelta, 0, " ms");
                o.Append($"<tr><td>{E(Cap(T("d.dir." + k)))}</td><td class='n'>{Num(r.Mbps, 0, " Mbps")}</td><td class='n'>{Num(r.IdleMed)} → {Num(r.LoadMed)} ms</td><td class='n'>{Signed(r.Delta, 0, " ms")}</td>" +
                         $"<td class='n'>{Num(r.LoadP95)} ms</td><td class='n'>{Num(r.LossPct, 1, " %")}</td><td class='n'>{gw}</td><td>{E(r.Grade ?? "—")}{(r.Valid ? "" : E(T("rep.inconclusive")))}</td></tr>");
            }
            var lt = d.Meta.Loadtest;
            o.Append("</table><p class='muted'>" + E(T("rep.sat_method", lt?.Streams.ToString() ?? "?", lt?.Server ?? "?", lt?.CapDownMb.ToString() ?? "?", lt?.CapUpMb.ToString() ?? "?")) + " " + E(T("loadtest.link_note")) + " " + E(T("rep.grades")) + "</p>");
        }

        var rcfg = cfg.Router ?? d.Meta.CfgSnapshot?.Router;
        var shotList = shots.ToList();
        if (rcfg != null || shotList.Count > 0)
        {
            (double? Down, double? Up) meas = (bb?.Directions.GetValueOrDefault("down")?.Mbps, bb?.Directions.GetValueOrDefault("up")?.Mbps);
            double worst = (bb?.Directions.Values.Where(r => r.Valid && r.Delta.HasValue).Select(r => r.Delta!.Value) ?? Enumerable.Empty<double>()).DefaultIfEmpty(0).Max();
            var ra = RouterQos.Analysis(rcfg, meas, cfg, worst);
            o.Append($"<h2>{E(T("rep.h.router"))}</h2>");
            var qos = T(rcfg?.QosEnabled switch { true => "rep.qos.on", false => "rep.qos.off", _ => "rep.qos.unknown" });
            o.Append("<p>" + T("rep.router_line", E(string.IsNullOrEmpty(rcfg?.Model) ? T("rep.notentered") : rcfg!.Model), E(string.IsNullOrEmpty(rcfg?.HwVersion) ? "?" : rcfg!.HwVersion), E(string.IsNullOrEmpty(rcfg?.Firmware) ? "?" : rcfg!.Firmware), qos, E(T("rep.qostype." + (rcfg?.QosType is "priority" or "bandwidth_limit" or "sqm" ? rcfg.QosType : "unknown")))) + "</p>");
            o.Append("<ul>" + string.Concat(ra.Findings.Select(f => $"<li><b>{E(T("rep.sev." + f.Severity))}</b> — {E(f.Text)}</li>")) + "</ul>");
            if (ra.Proposals.Count > 0)
            {
                o.Append($"<h3>{E(T("rep.h.proposed"))}</h3>");
                foreach (var p in ra.Proposals)
                    o.Append($"<div class='card'><b>{E(p.Change)}</b><br>{E(T("rep.why"))} {E(p.Justification)}<br>{E(T("rep.rollback"))} {E(p.Rollback)}</div>");
            }
            o.Append($"<h3>{E(T("rep.h.protocol"))}</h3><ol>" + string.Concat(ra.Protocol.Select(x => $"<li>{E(x)}</li>")) + "</ol>");
            foreach (var (name, uri) in shotList) o.Append($"<p class='muted'>{E(name)}</p><img class='shot' src='{E(uri)}' alt='{E(name)}'>");
        }

        if (d.Traces.Count > 0)
        {
            o.Append($"<h2>{E(T("rep.h.traces"))}</h2>");
            foreach (var t in d.Traces)
            {
                o.Append($"<h3>{Ts(t.T)} — {E(T("rep.trace_to", t.Target))}</h3>");
                if (t.Data.Error != null) { o.Append($"<p>{E(T("trace.error." + t.Data.Error))}</p>"); continue; }
                o.Append($"<table><tr><th>{E(T("rep.th.hop"))}</th><th>{E(T("rep.th.address"))}</th><th>{E(T("rep.th.times"))}</th><th>{E(T("rep.th.noreply"))}</th></tr>" + string.Concat((t.Data.Hops ?? new()).Select(h =>
                    $"<tr><td>{h.Hop}</td><td>{E(h.Ip ?? "*")}</td><td>{E(string.Join(" / ", h.Rtts.Select(x => x.ToString("0", Inv))))}</td><td>{h.Lost}/{h.Sent}</td></tr>")) + "</table>");
                o.Append(Ul(Probes.TraceNotes(t.Data.Analysis, (t.Data.Hops?.Count ?? 0) > 0)));
            }
        }

        o.Append($"<h2>{E(T("rep.h.env"))}</h2>");
        var vpn = env.Vpn;
        o.Append("<p>" + E(T("rep.env_line", act?.Name ?? "?", act?.Kind ?? "?", act?.Gw4 ?? act?.Gw6 ?? "?", act is { Dns.Count: > 0 } ? string.Join(", ", act.Dns) : "?",
                  vpn.Active ? T("rep.vpn_yes", string.Join(", ", vpn.Adapters)) : T("rep.vpn_no"), env.Ipv6Global ? T("rep.yes") : T("rep.no"))) + "</p>");
        o.Append(Ul(SysInfo.Notes(env))).Append(Ul(a.GeneralLimits));
        o.Append($"<p class='muted'>{E(T("rep.footer"))}</p></body></html>");
        return o.ToString();
    }
}

public static partial class Loc
{
    static void RegisterReport()
    {
        Add("rep.title", "Network diagnosis report", "Rapport de diagnostic réseau");
        Add("rep.session", "Session #{0} \"{1}\" — {2} → {3} ({4} min) — link: {5}", "Session n°{0} « {1} » — {2} → {3} ({4} min) — liaison : {5}");
        Add("rep.nolabel", "no label", "sans libellé");
        Add("rep.version", " — Network Analyzer {0}", " — Analyseur réseau {0}");
        Add("rep.disclaimer", "This report presents <b>hypotheses</b> deduced from measurements (pings, DNS, network counters), not confirmed causes. Each hypothesis states its evidence, its limits and the next useful test.", "Ce rapport présente des <b>hypothèses</b> déduites de mesures (pings, DNS, compteurs réseau), pas des causes confirmées. Chaque hypothèse indique ses preuves, ses limites et le prochain test utile.");
        Add("rep.h.summary", "Summary", "Résumé");
        Add("rep.h.hypotheses", "Ranked hypotheses", "Hypothèses classées");
        Add("rep.nohyp", "No hypothesis reaches the evidence threshold.", "Aucune hypothèse n'atteint le seuil d'indices suffisant.");
        Add("rep.confidence", "confidence {0}", "confiance {0}");
        Add("rep.evidence", "Evidence", "Preuves");
        Add("rep.counter", "Counter-evidence / nuances", "Éléments contre / nuances");
        Add("rep.limits", "Limits", "Limites");
        Add("rep.next", "Next useful test:", "Prochain test utile :");
        Add("rep.h.unlikely", "Leads that look unlikely given the measurements", "Pistes peu probables d'après les mesures");
        Add("rep.h.noteval", "Not evaluated", "Non évalué");
        Add("rep.h.actions", "Recommended actions (most relevant first)", "Actions recommandées (par ordre de pertinence)");
        Add("rep.noactions", "No targeted action: run a new monitoring session during a lag episode.", "Aucune action ciblée : relancez une surveillance pendant un épisode de lag.");
        Add("rep.h.timeline", "Incident timeline", "Chronologie des incidents");
        Add("rep.th.time", "Time", "Heure");
        Add("rep.th.type", "Type", "Type");
        Add("rep.th.zone", "Probable location", "Localisation probable");
        Add("rep.th.details", "Details", "Détails");
        Add("rep.noincident", "No incident reported and no episode detected.", "Aucun incident signalé ni épisode détecté.");
        Add("rep.type.lag", "\"I'm lagging now\"", "« Je lag maintenant »");
        Add("rep.type.episode", "Detected episode", "Épisode détecté");
        Add("rep.type.gap", "System pause", "Pause du système");
        Add("rep.type.roam", "Access point change", "Changement de point d'accès");
        Add("rep.zone.local", "Local network", "Réseau local");
        Add("rep.zone.upstream", "Upstream (ISP/Internet)", "En amont (FAI/Internet)");
        Add("rep.zone.path", "Single path", "Un seul trajet");
        Add("rep.zone.custom_path", "Custom destination", "Destination personnalisée");
        Add("rep.zone.dns", "DNS", "DNS");
        Add("rep.zone.none", "Nothing measured", "Rien de mesuré");
        Add("rep.zone.undetermined", "Undetermined", "Indéterminé");
        Add("rep.h.curves", "Charts", "Courbes");
        Add("rep.latency", "Latency (ms) — maximum per interval; red vertical lines: \"I'm lagging now\"; bottom ticks: losses", "Latence (ms) — maximum par intervalle ; traits verticaux rouges : « Je lag maintenant » ; ticks du bas : pertes");
        Add("rep.traffic", "Traffic of this computer (Mbps)", "Trafic de cet ordinateur (Mbps)");
        Add("rep.dns", "DNS resolution (ms)", "Résolution DNS (ms)");
        Add("rep.s.down", "PC download", "Descendant du PC");
        Add("rep.s.up", "PC upload", "Montant du PC");
        Add("rep.s.dns", "DNS (common name)", "DNS (nom courant)");
        Add("rep.s.wifi", "Wi-Fi signal (%)", "Signal Wi-Fi (%)");
        Add("rep.nochart", "No data for this chart.", "Aucune donnée pour ce graphique.");
        Add("rep.chart_desc", "{0}: median {1} {2}, maximum {3} {2}, failed probes: {4}.", "{0} : médiane {1} {2}, maximum {3} {2}, sondes échouées : {4}.");
        Add("rep.chart_desc_loss", "{0}: no successful measurement, failed probes: {1}.", "{0} : aucune mesure réussie, sondes échouées : {1}.");
        Add("rep.h.measures", "Measurements", "Mesures");
        Add("rep.th.target", "Target", "Cible");
        Add("rep.th.state", "State", "État");
        Add("rep.th.requests", "Requests", "Requêtes");
        Add("rep.th.loss", "Loss %", "Perte %");
        Add("rep.th.median", "Median ms", "Médiane ms");
        Add("rep.th.max", "Max ms", "Max ms");
        Add("rep.th.jitter", "Jitter ms", "Gigue ms");
        Add("rep.dns.hit", "System DNS — common names", "DNS système — noms courants");
        Add("rep.dns.miss", "System DNS — cold", "DNS système — à froid");
        Add("rep.dns.ref", "DNS 1.1.1.1 — cold (reference)", "DNS 1.1.1.1 — à froid (référence)");
        Add("rep.measured", "measured", "mesuré");
        Add("rep.measures_note", "Outside the load phases of the saturation test (except idle).", "Hors phases de charge du test de saturation (sauf le repos).");
        Add("rep.traffic_line", "PC traffic (outside the test) — download: median {0} Mbps, p95 {1}, max {2}; upload: median {3} Mbps, p95 {4}, max {5}.", "Trafic du PC (hors test) — descendant : médiane {0} Mbps, p95 {1}, max {2} ; montant : médiane {3} Mbps, p95 {4}, max {5}.");
        Add("rep.traffic_note", "The computer's totals only; no per-process attribution (not reliable without administrator rights).", "Totaux de l'ordinateur uniquement ; pas d'attribution par processus (non fiable sans droits administrateur).");
        Add("rep.wifi_line", "Wi-Fi: median signal {0}% (min {1}%), channel {2}, band {3}, LINK rate (≠ Internet speed) transmit median {4} Mbps (min {5}), receive {6} Mbps.", "Wi-Fi : signal médian {0} % (min {1} %), canal {2}, bande {3}, débit de LIAISON (≠ débit Internet) émission médian {4} Mbps (min {5}), réception {6} Mbps.");
        Add("rep.h.sat", "Saturation test (indicative)", "Test de saturation (indicatif)");
        Add("rep.th.dir", "Direction", "Sens");
        Add("rep.th.sustained", "Sustained throughput", "Débit soutenu");
        Add("rep.th.lat_idle_load", "Internet latency idle → load", "Latence Internet repos → charge");
        Add("rep.th.increase", "Increase", "Hausse");
        Add("rep.th.p95load", "p95 under load", "p95 sous charge");
        Add("rep.th.maxloss", "Max loss", "Perte max");
        Add("rep.th.gwincrease", "Gateway increase", "Hausse passerelle");
        Add("rep.th.grade", "Grade", "Note");
        Add("rep.inconclusive", " (inconclusive)", " (non concluant)");
        Add("rep.sat_method", "Method: {0} parallel HTTP(S) streams to {1}, phases idle → download → recovery → upload → recovery, caps {2} MB (download) / {3} MB (upload) per phase; sustained throughput = median after 3 s of ramp-up.", "Méthode : {0} flux HTTP(S) parallèles vers {1}, phases repos → téléchargement → récupération → envoi → récupération, plafonds {2} Mo (descendant) / {3} Mo (montant) par phase ; débit soutenu = médiane après 3 s de montée en charge.");
        Add("rep.grades", "Grades: A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, F beyond (median latency increase).", "Notes : A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, F au-delà (hausse de latence médiane).");
        Add("rep.h.router", "Router and QoS (manual entry)", "Routeur et QoS (saisie manuelle)");
        Add("rep.qos.on", "enabled", "activée");
        Add("rep.qos.off", "disabled", "désactivée");
        Add("rep.qos.unknown", "unknown", "inconnue");
        Add("rep.qostype.unknown", "Unknown", "Inconnu");
        Add("rep.qostype.priority", "Prioritisation", "Priorisation");
        Add("rep.qostype.bandwidth_limit", "Rate limit", "Limite de débit");
        Add("rep.qostype.sqm", "SQM (Smart Queue)", "SQM (Smart Queue)");
        Add("rep.notentered", "not entered", "non renseigné");
        Add("rep.router_line", "Model: <b>{0}</b>, hardware version {1}, firmware {2}. QoS: {3} (type: {4}).", "Modèle : <b>{0}</b>, version matérielle {1}, firmware {2}. QoS : {3} (type : {4}).");
        Add("rep.sev.ok", "ok", "ok");
        Add("rep.sev.info", "info", "info");
        Add("rep.sev.warning", "warning", "attention");
        Add("rep.sev.problem", "problem", "problème");
        Add("rep.h.proposed", "PROPOSED changes (not applied by the application)", "Changements PROPOSÉS (non appliqués par l'application)");
        Add("rep.why", "Why:", "Pourquoi :");
        Add("rep.rollback", "Rollback:", "Retour arrière :");
        Add("rep.h.protocol", "Before/after protocol", "Protocole avant/après");
        Add("rep.h.traces", "One-off path diagnostics", "Diagnostics de trajet ponctuels");
        Add("rep.trace_to", "to {0}", "vers {0}");
        Add("rep.th.hop", "Hop", "Saut");
        Add("rep.th.address", "Address", "Adresse");
        Add("rep.th.times", "Times (ms)", "Temps (ms)");
        Add("rep.th.noreply", "No reply", "Sans réponse");
        Add("rep.h.env", "Environment and limits", "Environnement et limites");
        Add("rep.env_line", "Measured interface: {0} ({1}), gateway {2}, DNS {3}. VPN: {4}. Global IPv6: {5}.", "Interface mesurée : {0} ({1}), passerelle {2}, DNS {3}. VPN : {4}. IPv6 global : {5}.");
        Add("rep.vpn_yes", "detected ({0})", "détecté ({0})");
        Add("rep.vpn_no", "not detected", "non détecté");
        Add("rep.yes", "yes", "oui");
        Add("rep.no", "no", "non");
        Add("rep.footer", "Pings: Windows ICMP API through .NET (≈ 1 ms resolution; \"<1 ms\" = 0.5 ms), 1 request/s per target. Data kept locally; no communication content is captured; no router credential is requested or stored.", "Pings : API ICMP de Windows via .NET (≈ 1 ms de résolution ; « <1 ms » = 0,5 ms), 1 requête/s par cible. Données conservées localement ; aucun contenu de communication n'est capturé ; aucun identifiant de routeur n'est demandé ni enregistré.");
    }
}
