using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NetworkAnalyzer;

/// <summary>Exports (CSV, JSON) and self-contained HTML report (no external resource, charts in SVG).</summary>
public static class Report
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    static readonly Dictionary<string, string> Colors = new()
    {
        ["gateway"] = "#1a7f37", ["cloudflare"] = "#0969da", ["google"] = "#bc4c00", ["quad9"] = "#8250df", ["custom"] = "#cf222e", ["cloudflare6"] = "#0a7ea4",
    };
    static readonly Dictionary<string, string> ZoneLabel = new()
    {
        ["local"] = "Réseau local", ["amont"] = "En amont (FAI/Internet)", ["trajet"] = "Un seul trajet", ["trajet_custom"] = "Destination personnalisée",
        ["dns"] = "DNS", ["aucune"] = "Rien de mesuré", ["indetermine"] = "Indéterminé",
    };
    static readonly Dictionary<string, string> TypeLabel = new()
    {
        ["lag"] = "« Je lag maintenant »", ["episode"] = "Épisode détecté", ["gap"] = "Pause du système", ["roam"] = "Changement de point d'accès",
    };

    public static string Ts(double t, string fmt = "HH:mm:ss") => DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).ToLocalTime().ToString(fmt, Inv);
    static string Num(double? x, int d = 0, string unit = "") => x is null ? "—" : x.Value.ToString("F" + d, Inv) + unit;

    // ------------------------------------------------------------------ exports
    static string Csv(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;  // spreadsheet formula protection
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string ExportCsv(SessionData d)
    {
        var sb = new StringBuilder("session,horodatage_local,epoch_s,serie,valeur,ok,info\n");
        foreach (var (name, s) in d.Series.SelectMany(kv => kv.Value.Select(s => (kv.Key, s))).OrderBy(x => x.s.T))
            sb.Append(d.Id).Append(',').Append(Ts(s.T, "yyyy-MM-dd HH:mm:ss")).Append(',').Append(s.T.ToString("F3", Inv)).Append(',').Append(Csv(name)).Append(',')
              .Append(s.V.HasValue ? s.V.Value.ToString("F3", Inv) : "").Append(',').Append(s.Ok ? 1 : 0).Append(',').Append(Csv(s.Info)).Append('\n');
        foreach (var m in d.Marks)
            sb.Append(d.Id).Append(',').Append(Ts(m.T, "yyyy-MM-dd HH:mm:ss")).Append(',').Append(m.T.ToString("F3", Inv)).Append(",marque:").Append(Csv(m.Kind)).Append(",,,").Append(Csv(m.Note)).Append('\n');
        return sb.ToString();
    }

    public static string ExportJson(SessionData d, Analysis a) => JsonSerializer.Serialize(new
    {
        session = new { id = d.Id, debut = d.Started, fin = d.Ended, libelle = d.Label, liaison = d.Link, meta = d.Meta },
        definitions = Stats.Definitions.ToDictionary(kv => kv.Key == "mediane" ? "mediane" : kv.Key, kv => kv.Value),
        mesures = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => new object?[] { s.T, s.V, s.Ok ? 1 : 0, s.Info }).ToList()),
        marques = d.Marks, phases = d.Phases, traceroutes = d.Traces, analyse = a,
    }, Json.Indented);

    // ------------------------------------------------------------------ SVG charts
    public sealed class ChartSeries
    {
        public string Name { get; init; } = "";
        public string Color { get; init; } = "#555";
        public List<(double T, double V)> Pts { get; init; } = new();
        public List<double> Lost { get; init; } = new();
    }

    /// <summary>Curve = maximum per column (spikes stay visible).</summary>
    public static string SvgChart(List<ChartSeries> series, double t0, double t1, IEnumerable<Mark>? marks = null, IEnumerable<Phase>? phases = null, int height = 170, string unit = "ms", double? clip = null)
    {
        const int W = 860, L = 46, B = 18, cols = 430;
        int H = height;
        double span = Math.Max(1.0, t1 - t0);
        var allv = series.SelectMany(s => s.Pts.Select(p => p.V)).ToList();
        if (allv.Count == 0) return "<p class=\"muted\">Aucune donnée pour ce graphique.</p>";
        double ymax = Math.Max(clip ?? allv.Max(), 1e-9) * 1.08;
        double X(double t) => L + (t - t0) / span * (W - L - 6);
        double Y(double v) => 4 + (1 - Math.Min(v, ymax) / ymax) * (H - B - 4);
        string N1(double x) => x.ToString("0.0", Inv);
        var o = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" class=\"chart\">");
        foreach (var p in phases ?? Enumerable.Empty<Phase>())
            o.Append($"<rect x=\"{N1(X(p.T0))}\" y=\"4\" width=\"{N1(Math.Max(1, X(p.T1) - X(p.T0)))}\" height=\"{H - B - 4}\" fill=\"#8884\" /><text x=\"{N1(X(p.T0) + 2)}\" y=\"14\" font-size=\"9\" fill=\"#666\">{E(p.Name)}</text>");
        for (int i = 0; i < 5; i++)
        {
            double y = 4 + i * (H - B - 4) / 4.0;
            o.Append($"<line x1=\"{L}\" x2=\"{W - 6}\" y1=\"{N1(y)}\" y2=\"{N1(y)}\" stroke=\"#ddd\"/><text x=\"{L - 4}\" y=\"{N1(y + 3)}\" font-size=\"9\" text-anchor=\"end\" fill=\"#666\">{(ymax * (1 - i / 4.0)).ToString("0", Inv)}</text>");
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
            var pts = string.Join(" ", buckets.Select(kv => $"{N1(X(t0 + kv.Key / (double)cols * span))},{N1(Y(kv.Value))}"));
            o.Append($"<polyline fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"1.2\" points=\"{pts}\"/>");
            foreach (var t in s.Lost.Take(400))
                o.Append($"<line x1=\"{N1(X(t))}\" x2=\"{N1(X(t))}\" y1=\"{H - B - 6}\" y2=\"{H - B}\" stroke=\"{s.Color}\" stroke-width=\"1.5\"/>");
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
        foreach (var tg in d.Targets)
        {
            var raw = d.S($"ping:{tg.Id}").ToList();
            if (raw.Any(s => s.Info == "tcp")) raw = raw.Where(s => s.Info != "icmp_sans_reponse").ToList();
            bool answered = raw.Any(s => s.Ok);
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
.badge{display:inline-block;border-radius:10px;padding:1px 9px;font-size:12px;color:#fff}.b-faible{background:#9a6700}.b-moyenne{background:#bc4c00}.b-élevée{background:#cf222e}
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
        return $"<tr><td>{E(label)}</td><td>{E(state)}</td><td class='n'>{s.N}</td><td class='n'>{s.LossPct.ToString("0.0", Inv)}</td><td class='n'>{Num(s.Median, 1)}</td>" +
               $"<td class='n'>{Num(s.P95, 1)}</td><td class='n'>{Num(s.Max, 1)}</td><td class='n'>{Num(s.Jitter, 1)}</td><td class='n'>{Num(s.Min, 1)}</td></tr>";
    }

    public static string Html(SessionData d, Analysis a, AppConfig? cfg = null, IEnumerable<(string Name, string Uri)>? shots = null, string version = "")
    {
        cfg ??= new AppConfig();
        shots ??= Enumerable.Empty<(string, string)>();
        var env = d.Meta.Env;
        var act = env.Active;
        double end = d.EndOrLast;
        var o = new StringBuilder();
        o.Append($"<!doctype html><html lang='fr'><head><meta charset='utf-8'><title>Rapport réseau — session {d.Id}</title><style>{Css}</style></head><body>");
        o.Append($"<h1>Rapport de diagnostic réseau</h1><p class='muted'>Session n°{d.Id} « {E(string.IsNullOrEmpty(d.Label) ? "sans libellé" : d.Label)} » — {Ts(d.Started, "dd/MM/yyyy HH:mm")} → {Ts(end, "HH:mm")} " +
                 $"({((end - d.Started) / 60).ToString("0", Inv)} min) — liaison : {E(d.Link)}" + (version.Length > 0 ? $" — Analyseur réseau {E(version)}" : "") + "</p>");
        o.Append("<p class='warn'>Ce rapport présente des <b>hypothèses</b> déduites de mesures (pings, DNS, compteurs réseau), pas des causes confirmées. Chaque hypothèse indique ses preuves, ses limites et le prochain test utile.</p>");
        o.Append("<h2>Résumé</h2>").Append(string.Concat(a.Resume.Select(r => $"<p>{E(r)}</p>")));
        o.Append("<h2>Hypothèses classées</h2>");
        if (a.Hypotheses.Count == 0) o.Append("<p>Aucune hypothèse n'atteint le seuil d'indices suffisant.</p>");
        foreach (var h in a.Hypotheses)
            o.Append($"<div class='card'><h3>{E(h.Titre)} <span class='badge b-{h.Niveau}'>confiance {h.Niveau}</span></h3><b>Preuves</b>{Ul(h.Preuves)}" +
                     (h.Contre.Count > 0 ? $"<b>Éléments contre / nuances</b>{Ul(h.Contre)}" : "") + $"<b>Limites</b>{Ul(h.Limites)}<p><b>Prochain test utile :</b> {E(h.ProchainTest)}</p></div>");
        if (a.PeuProbables.Count > 0)
            o.Append("<h3>Pistes peu probables d'après les mesures</h3><ul>" + string.Concat(a.PeuProbables.Select(u => $"<li><b>{E(u.Titre)}</b> — {E(string.Join(" ", u.Raisons))}</li>")) + "</ul>");
        if (a.NonEvalue.Count > 0)
            o.Append("<h3>Non évalué</h3><ul>" + string.Concat(a.NonEvalue.Select(u => $"<li><b>{E(u.Titre)}</b> — {E(u.Raison)}</li>")) + "</ul>");
        o.Append("<h2>Actions recommandées (par ordre de pertinence)</h2>");
        o.Append(a.Actions.Count > 0 ? "<ol>" + string.Concat(a.Actions.Select(x => $"<li>{E(x)}</li>")) + "</ol>" : "<p>Aucune action ciblée : relancez une surveillance pendant un épisode de lag.</p>");

        o.Append("<h2>Chronologie des incidents</h2>");
        if (a.Timeline.Count > 0)
        {
            o.Append("<table><tr><th>Heure</th><th>Type</th><th>Localisation probable</th><th>Détails</th></tr>");
            foreach (var i in a.Timeline)
            {
                var det = string.Concat(i.Details.Select(x => $"<div>{E(x)}</div>")) + (i.Note.Length > 0 ? $"<div class='muted'>{E(i.Note)}</div>" : "");
                var zone = E(i.Zone != null ? ZoneLabel.GetValueOrDefault(i.Zone, "") : "") + (i.ZoneTexte.Length > 0 ? $"<div class='muted'>{E(i.ZoneTexte)}</div>" : "");
                o.Append($"<tr><td>{Ts(i.T)}</td><td>{E(TypeLabel.GetValueOrDefault(i.Type, i.Type))}</td><td>{zone}</td><td>{det}</td></tr>");
            }
            o.Append("</table>");
        }
        else o.Append("<p>Aucun incident signalé ni épisode détecté.</p>");

        o.Append("<h2>Courbes</h2>");
        var ls = LatencySeries(d);
        var vals = ls.SelectMany(s => s.Pts.Select(p => p.V)).OrderBy(x => x).ToList();
        double? cap = vals.Count > 0 ? Math.Max(100.0, Stats.Percentile(vals, 99.5)!.Value * 1.5) : null;
        var marks = d.Marks.Where(m => m.Kind == "lag").ToList();
        o.Append("<h3>Latence (ms) — maximum par intervalle ; traits verticaux rouges : « Je lag maintenant » ; ticks du bas : pertes</h3>")
         .Append(SvgChart(ls, d.Started, end, marks, d.Phases, clip: cap)).Append(Legend(ls));
        var tr = new List<ChartSeries>
        {
            new() { Name = "Descendant du PC", Color = "#0969da", Pts = d.S("net:down_bps").Where(s => s.V.HasValue).Select(s => (s.T, s.V!.Value / 1e6)).ToList() },
            new() { Name = "Montant du PC", Color = "#bc4c00", Pts = d.S("net:up_bps").Where(s => s.V.HasValue).Select(s => (s.T, s.V!.Value / 1e6)).ToList() },
        };
        if (tr[0].Pts.Count > 0) o.Append("<h3>Trafic de cet ordinateur (Mbps)</h3>").Append(SvgChart(tr, d.Started, end, marks, d.Phases, unit: "Mbps")).Append(Legend(tr));
        var dns = new List<ChartSeries> { new() { Name = "DNS (nom courant)", Color = "#1a7f37", Pts = d.S("dns:sys_hit").Where(s => s.Ok && s.V.HasValue).Select(s => (s.T, s.V!.Value)).ToList(), Lost = d.S("dns:sys_hit").Where(s => !s.Ok).Select(s => s.T).ToList() } };
        if (dns[0].Pts.Count > 0) o.Append("<h3>Résolution DNS (ms)</h3>").Append(SvgChart(dns, d.Started, end, marks, d.Phases, height: 120)).Append(Legend(dns));
        var wf = new List<ChartSeries> { new() { Name = "Signal Wi‑Fi (%)", Color = "#8250df", Pts = d.S("wifi:signal").Where(s => s.V.HasValue).Select(s => (s.T, s.V!.Value)).ToList() } };
        if (wf[0].Pts.Count > 0) o.Append("<h3>Signal Wi‑Fi (%)</h3>").Append(SvgChart(wf, d.Started, end, marks, d.Phases, height: 110, unit: "%")).Append(Legend(wf));

        o.Append("<h2>Mesures</h2><table><tr><th>Cible</th><th>État</th><th>Requêtes</th><th>Perte %</th><th>Médiane ms</th><th>p95 ms</th><th>Max ms</th><th>Gigue ms</th><th>Min ms</th></tr>");
        foreach (var t in a.Stats.Targets) o.Append(StatRow($"{t.Label} — {t.Host}", t.Etat, t.Stats));
        foreach (var (s, lab) in new[] { (a.Stats.Dns.SysHit, "DNS système — noms courants"), (a.Stats.Dns.SysMiss, "DNS système — à froid"), (a.Stats.Dns.RefMiss, "DNS 1.1.1.1 — à froid (référence)") })
            if (s != null) o.Append(StatRow(lab, "mesuré", s));
        o.Append("</table><p class='muted'>Hors phases de charge du test de saturation (sauf le repos). " + E(string.Join(" ", Stats.Definitions.Values)) + "</p>");
        var t2 = a.Stats.Traffic;
        if (t2.Down != null && t2.Up != null)
            o.Append($"<p>Trafic du PC (hors test) — descendant : médiane {Num(t2.Down.Median, 2)} Mbps, p95 {Num(t2.Down.P95, 2)}, max {Num(t2.Down.Max, 1)} ; " +
                     $"montant : médiane {Num(t2.Up.Median, 2)} Mbps, p95 {Num(t2.Up.P95, 2)}, max {Num(t2.Up.Max, 1)}. <span class='muted'>Totaux de l'ordinateur uniquement ; pas d'attribution par processus (non fiable sans droits administrateur).</span></p>");
        var w = a.Stats.Wifi;
        if (w != null)
            o.Append($"<p>Wi‑Fi : signal médian {Num(w.SignalMed)} % (min {Num(w.SignalMin)} %), canal {E(w.Channel?.ToString() ?? "?")}, bande {E(w.Band ?? "?")}, " +
                     $"débit de LIAISON (≠ débit Internet) émission médian {Num(w.TxMed)} Mbit/s (min {Num(w.TxMin)}), réception {Num(w.RxMed)} Mbit/s.</p>");

        var bb = a.Bufferbloat;
        if (bb != null)
        {
            o.Append("<h2>Test de saturation (indicatif)</h2><table><tr><th>Sens</th><th>Débit soutenu</th><th>Latence Internet repos → charge</th><th>Hausse</th><th>p95 sous charge</th><th>Perte max</th><th>Hausse passerelle</th><th>Note</th></tr>");
            foreach (var (k, lab) in new[] { ("down", "Téléchargement"), ("up", "Envoi") })
            {
                if (!bb.Directions.TryGetValue(k, out var r)) continue;
                var gw = r.GwDelta.HasValue ? "+" + Num(r.GwDelta) + " ms" : "—";
                o.Append($"<tr><td>{lab}</td><td class='n'>{Num(r.Mbps, 0, " Mbps")}</td><td class='n'>{Num(r.IdleMed)} → {Num(r.LoadMed)} ms</td><td class='n'>+{Num(r.Delta)} ms</td>" +
                         $"<td class='n'>{Num(r.LoadP95)} ms</td><td class='n'>{Num(r.LossPct, 1, " %")}</td><td class='n'>{gw}</td><td>{E(r.Grade ?? "—")}{(r.Valid ? "" : " (non concluant)")}</td></tr>");
            }
            var lt = d.Meta.Loadtest;
            o.Append($"</table><p class='muted'>Méthode : {lt?.Streams.ToString() ?? "?"} flux HTTP(S) parallèles vers {E(lt?.Server ?? "?")}, phases repos → téléchargement → récupération → envoi → récupération, " +
                     $"plafonds {lt?.CapDownMb.ToString() ?? "?"} Mo (descendant) / {lt?.CapUpMb.ToString() ?? "?"} Mo (montant) par phase ; débit soutenu = médiane après 3 s de montée en charge. " +
                     "Résultats indicatifs : le serveur, le Wi‑Fi ou le PC peuvent borner le débit. Notes : A+ &lt; 5 ms, A &lt; 30, B &lt; 60, C &lt; 200, D &lt; 400, F au‑delà (hausse de latence médiane).</p>");
        }

        var rcfg = cfg.Router ?? d.Meta.CfgSnapshot?.Router;
        var shotList = shots.ToList();
        if (rcfg != null || shotList.Count > 0)
        {
            (double? Down, double? Up) meas = (bb?.Directions.GetValueOrDefault("down")?.Mbps, bb?.Directions.GetValueOrDefault("up")?.Mbps);
            double worst = (bb?.Directions.Values.Where(r => r.Valid && r.Delta.HasValue).Select(r => r.Delta!.Value) ?? Enumerable.Empty<double>()).DefaultIfEmpty(0).Max();
            var ra = RouterQos.Analysis(rcfg, meas, cfg, worst);
            o.Append("<h2>Routeur et QoS (saisie manuelle)</h2>");
            var qos = rcfg?.QosEnabled switch { true => "activée", false => "désactivée", _ => "inconnue" };
            o.Append($"<p>Modèle : <b>{E(string.IsNullOrEmpty(rcfg?.Model) ? "non renseigné" : rcfg!.Model)}</b>, version matérielle {E(string.IsNullOrEmpty(rcfg?.HwVersion) ? "?" : rcfg!.HwVersion)}, firmware {E(string.IsNullOrEmpty(rcfg?.Firmware) ? "?" : rcfg!.Firmware)}. QoS : {qos} (type : {E(rcfg?.QosType ?? "inconnu")}).</p>");
            o.Append("<ul>" + string.Concat(ra.Findings.Select(f => $"<li><b>{E(f.Severite)}</b> — {E(f.Texte)}</li>")) + "</ul>");
            if (ra.Propositions.Count > 0)
            {
                o.Append("<h3>Changements PROPOSÉS (non appliqués par l'application)</h3>");
                foreach (var p in ra.Propositions)
                    o.Append($"<div class='card'><b>{E(p.Changement)}</b><br>Pourquoi : {E(p.Justification)}<br>Retour arrière : {E(p.RetourArriere)}</div>");
            }
            o.Append("<h3>Protocole avant/après</h3><ol>" + string.Concat(ra.Protocole.Select(x => $"<li>{E(x)}</li>")) + "</ol>");
            foreach (var (name, uri) in shotList) o.Append($"<p class='muted'>{E(name)}</p><img class='shot' src='{E(uri)}' alt='{E(name)}'>");
        }

        if (d.Traces.Count > 0)
        {
            o.Append("<h2>Diagnostics de trajet ponctuels</h2>");
            foreach (var t in d.Traces)
            {
                o.Append($"<h3>{Ts(t.T)} — vers {E(t.Target)}</h3>");
                if (t.Data.Error != null) { o.Append($"<p>{E(t.Data.Error)}</p>"); continue; }
                o.Append("<table><tr><th>Saut</th><th>Adresse</th><th>Temps (ms)</th><th>Sans réponse</th></tr>" + string.Concat((t.Data.Hops ?? new()).Select(h =>
                    $"<tr><td>{h.Hop}</td><td>{E(h.Ip ?? "*")}</td><td>{E(string.Join(" / ", h.Rtts.Select(x => x.ToString("0", Inv))))}</td><td>{h.Lost}/{h.Sent}</td></tr>")) + "</table>");
                o.Append(Ul(t.Data.Analysis?.Notes ?? new()));
            }
        }

        o.Append("<h2>Environnement et limites</h2>");
        o.Append($"<p>Interface mesurée : {E(act?.Name ?? "?")} ({E(act?.Kind ?? "?")}), passerelle {E(act?.Gw4 ?? act?.Gw6 ?? "?")}, DNS {E(act is { Dns.Count: > 0 } ? string.Join(", ", act.Dns) : "?")}. " +
                 $"VPN : {(env.Vpn.Active ? "détecté (" + E(string.Join(", ", env.Vpn.Adapters)) + ")" : "non détecté")}. IPv6 global : {(env.Ipv6Global ? "oui" : "non")}.</p>");
        o.Append(Ul(env.Notes)).Append(Ul(a.LimitesGenerales));
        o.Append("<p class='muted'>Pings : API ICMP de Windows via .NET (≈ 1 ms de résolution ; « &lt;1 ms » = 0,5 ms), 1 requête/s par cible. " +
                 "Données conservées localement ; aucun contenu de communication n'est capturé ; aucun identifiant de routeur n'est demandé ni enregistré.</p></body></html>");
        return o.ToString();
    }
}
