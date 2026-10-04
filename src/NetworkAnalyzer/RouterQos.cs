namespace NetworkAnalyzer;

public sealed class RouterFinding
{
    /// <summary>ok | info | warning | problem</summary>
    public string Severity { get; set; } = "info";
    public string Text { get; set; } = "";
}

public sealed class Proposal
{
    public string Change { get; set; } = "";
    public string Justification { get; set; } = "";
    public string Rollback { get; set; } = "";
}

public sealed class RouterAnalysis
{
    public List<RouterFinding> Findings { get; set; } = new();
    public List<Proposal> Proposals { get; set; } = new();
    public List<string> Protocol { get; set; } = new();
    public string DocUrl { get; set; } = "";
    public string Reminder { get; set; } = "";
}

/// <summary>
/// Analysis of the router's QoS configuration from a MANUAL entry (or attached screenshots).
/// Why no automatic access: TP-Link does not document an API common to all models; the admin page varies with model, hardware
/// version and firmware. Logging in would also require the router password. This application therefore never asks for it,
/// never connects to the router and changes NO setting: it shows PROPOSED changes with justification and rollback.
/// Important distinction: "priority" QoS (prioritised devices/applications) does not manage the line's queue; queue management
/// (SQM: fq_codel/CAKE…) or a rate limit just under the real speed does. Never assume your model has SQM: check the official
/// documentation of the model.
/// </summary>
public static class RouterQos
{
    /// <summary>qos_type values stored in the settings.</summary>
    public static readonly string[] QosTypes = { "unknown", "priority", "bandwidth_limit", "sqm" };
    public const string DocUrl = "https://www.tp-link.com/en/support/";
    const int ProtocolSteps = 6;

    static string G(double x) => x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    static string F0(double x) => x.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>English templates open with the direction ("Download limit"); the French ones put it after "Limite", in lower case.</summary>
    static string Cap(string s) => s.Length == 0 || Loc.Lang != "en" ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string Dir(string d) => Loc.T("dir." + d);

    /// <summary>A limit at or above this share of the measured throughput limits nothing: Check reports it and Propose offers a lower one.</summary>
    const double NoEffectRatio = 1.1;

    public static double? ToMbps(double? value, string? unit)
    {
        if (value is null) return null;
        return (unit ?? "Mbps").ToLowerInvariant() switch
        {
            "kbps" => value * 0.001,
            "mbps" => value,
            "gbps" => value * 1000.0,
            _ => null,
        };
    }

    public static (double? Down, double? Up) Limits(RouterConfig r) => (ToMbps(r.LimitDown, r.Unit), ToMbps(r.LimitUp, r.Unit));

    static readonly string[] AlwaysWords = { "always", "toujours", "unlimited", "illimitee", "illimitée", "∞" };

    /// <summary>Configuration ↔ measurements comparison.</summary>
    public static List<RouterFinding> Check(RouterConfig? r, (double? Down, double? Up) meas, AppConfig cfg, double worstDelta)
    {
        var o = new List<RouterFinding>();
        if (r is null) return o;
        void Add(string sev, string txt) => o.Add(new RouterFinding { Severity = sev, Text = txt });
        var qos = r.QosEnabled;
        var typ = r.QosType;
        var (ld, lu) = Limits(r);
        if (qos is null) Add("info", Loc.T("router.qos_unknown"));
        else if (qos == false)
        {
            if (worstDelta >= 30) Add("warning", Loc.T("router.qos_off_bloat", F0(worstDelta)));
        }
        else
        {
            if (typ == "priority" && worstDelta >= 30) Add("warning", Loc.T("router.priority_bloat"));
            if (typ == "unknown") Add("info", Loc.T("router.type_unknown"));
        }
        foreach (var (lim, m, dir, plan) in new[] { (ld, meas.Down, "down", cfg.PlanDownMbps), (lu, meas.Up, "up", cfg.PlanUpMbps) })
        {
            if (lim is null) continue;
            double? refv = m is > 0 ? m : plan;
            if (refv is null or 0) continue;
            double l = lim.Value, rf = refv.Value, ratio = l / rf;
            var src = Loc.T(m is > 0 ? "router.src.measured" : "router.src.plan");
            var d = Dir(dir);
            if ((ratio >= 800 && ratio <= 1200) || (ratio >= 0.0008 && ratio <= 0.0012))
                Add("problem", Loc.T("router.limit_unit", Cap(d), G(l), G(ratio), src, F0(rf)));
            else if (m is > 0 && m > 1.15 * l)  // measured above the limit: it does not apply, whether QoS is on, off or unknown
                Add("problem", Loc.T("router.limit_exceeded", Loc.T("dir." + dir + ".m"), F0(m.Value), G(l)));
            else if (ratio >= NoEffectRatio)
                Add(worstDelta >= 30 ? "warning" : "info", Loc.T("router.limit_noeffect", Cap(d), G(l), src, F0(rf)));
            else if (ratio < 0.5)
                Add("warning", Loc.T("router.limit_toolow", Cap(d), G(l), src, F0(rf)));
            else if (ratio < 0.85)
                Add("info", Loc.T("router.limit_below", Cap(d), G(l), F0(ratio * 100), src));
            else
                Add("ok", Loc.T("router.limit_ok", Cap(d), G(l), src, F0(rf), F0(ratio * 100)));
        }
        foreach (var rule in r.BandwidthRules)
        {
            foreach (var (val, m, dir) in new[] { (rule.Down, meas.Down, "down"), (rule.Up, meas.Up, "up") })
            {
                var lim = ToMbps(val, rule.Unit ?? r.Unit);
                if (lim is not null && m is > 0 && lim < 0.5 * m)
                    Add("warning", Loc.T("router.rule_low", string.IsNullOrEmpty(rule.Name) ? "?" : rule.Name, Dir(dir), G(lim.Value), F0(m.Value)));
            }
        }
        foreach (var d in r.PriorityDevices)
        {
            var dur = (d.Duration ?? "").Trim().ToLowerInvariant();
            if (dur.Length > 0 && !AlwaysWords.Contains(dur))
                Add("info", Loc.T("router.priority_temp", string.IsNullOrEmpty(d.Name) ? "?" : d.Name, d.Duration));
        }
        if (string.IsNullOrEmpty(r.SqmAvailable) || r.SqmAvailable == "unknown")
            if (typ != "sqm") Add("info", Loc.T("router.sqm_unknown"));
        return o;
    }

    /// <summary>PROPOSED changes (never applied) with justification and rollback.</summary>
    public static List<Proposal> Propose(RouterConfig? r, (double? Down, double? Up) meas, double worstDelta)
    {
        var props = new List<Proposal>();
        if (r is null) return props;
        var (ld, lu) = Limits(r);
        foreach (var (lim, m, dir, cur) in new[] { (ld, meas.Down, "down", r.LimitDown), (lu, meas.Up, "up", r.LimitUp) })
        {
            if (m is null or 0 || worstDelta < 30) continue;
            // two decimals below 10 Mbps (0.5 Mbps upstream gives 0.46, not 0), whole numbers above
            double Fit(double v) => Math.Round(v, v < 10 ? 2 : 0, MidpointRounding.ToEven);
            var target = Fit(m.Value * 0.92);
            if (lim is null || lim >= NoEffectRatio * m)
            {
                var now = cur.HasValue ? $"{G(cur.Value)} {r.Unit}" : Loc.T("router.prop.none_set");
                props.Add(new Proposal
                {
                    Change = Loc.T("router.prop.limit", Dir(dir), G(target), G(Fit(m.Value))),
                    Justification = Loc.T("router.prop.limit_why", F0(worstDelta), now),
                    Rollback = Loc.T("router.prop.limit_back", now),
                });
            }
        }
        if (worstDelta >= 30 && r.QosType != "sqm" && r.SqmAvailable != "no")  // the model has none: do not advise enabling it
        {
            // "prioritisation alone" only makes sense when prioritisation is what is configured
            var why = r.QosEnabled == true && r.QosType == "priority" ? Loc.T("router.prop.sqm_why") : Loc.T("router.prop.sqm_why_generic", F0(worstDelta));
            props.Add(new Proposal { Change = Loc.T("router.prop.sqm"), Justification = why, Rollback = Loc.T("router.prop.sqm_back") });
        }
        return props;
    }

    public static RouterAnalysis Analysis(RouterConfig? r, (double? Down, double? Up) meas, AppConfig cfg, double worstDelta) => new()
    {
        Findings = Check(r, meas, cfg, worstDelta), Proposals = Propose(r, meas, worstDelta), Protocol = Loc.List("router.protocol", ProtocolSteps),
        DocUrl = Loc.T("router.doc_url"), Reminder = Loc.T("router.reminder"),
    };
}

public static partial class Loc
{
    static void RegisterRouter()
    {
        Add("dir.down", "download", "descendante");   // feminine: "limite descendante"
        Add("dir.up", "upload", "montante");
        Add("dir.down.m", "download", "descendant");  // masculine: "débit descendant"
        Add("dir.up.m", "upload", "montant");
        Add("router.src.measured", "measured throughput", "débit mesuré");
        Add("router.src.plan", "advertised speed", "débit annoncé");
        Add("router.doc_url", "https://www.tp-link.com/en/support/", "https://www.tp-link.com/fr/support/");
        Add("router.reminder", "No setting is changed by the application. Apply one change at a time yourself.", "Aucun réglage n'est modifié par l'application. Appliquez vous-même un changement à la fois.");
        Add("router.qos_unknown", "QoS state unknown: read it in the router interface.", "État de la QoS inconnu : à relever dans l'interface du routeur.");
        Add("router.qos_off_bloat", "QoS disabled while latency rises by {0} ms under load: no limit or queue management protects latency.", "QoS désactivée alors que la latence monte de {0} ms sous charge : aucune limite ni gestion de file ne protège la latence.");
        Add("router.priority_bloat", "The enabled QoS is priority-based: it does not remove the line's queue, which is consistent with the latency increase measured under load.", "La QoS activée est de type priorisation : elle ne supprime pas la file d'attente de la ligne, ce qui est compatible avec la hausse de latence mesurée sous charge.");
        Add("router.type_unknown", "QoS type unknown (priority, rate limit or SQM?): specify it, the conclusion depends on it.", "Type de QoS inconnu (priorisation, limite de débit ou SQM ?) : précisez-le, la conclusion en dépend.");
        Add("router.limit_unit", "{0} limit ({1} Mbps) ≈ ×{2} of the {3} ({4} Mbps): Kbps/Mbps unit mix-up likely in the entry or in the router.", "Limite {0} ({1} Mbps) ≈ ×{2} du {3} ({4} Mbps) : confusion d'unité Kbps/Mbps probable dans la saisie ou dans le routeur.");
        Add("router.limit_exceeded", "Measured {0} throughput ({1} Mbps) EXCEEDS the configured limit ({2} Mbps): the limit does not apply to this flow (QoS inactive, per-device limit, wrong unit…).", "Débit {0} mesuré ({1} Mbps) DÉPASSE la limite configurée ({2} Mbps) : la limite ne s'applique pas à ce flux (QoS inactive, limite par appareil, mauvaise unité…).");
        Add("router.limit_noeffect", "{0} limit ({1} Mbps) above the {2} ({3} Mbps): it limits nothing, the queue stays in the modem/line.", "Limite {0} ({1} Mbps) supérieure au {2} ({3} Mbps) : elle ne limite rien, la file d'attente reste dans le modem/la ligne.");
        Add("router.limit_toolow", "{0} limit ({1} Mbps) far below the {2} ({3} Mbps): needlessly throttles the connection.", "Limite {0} ({1} Mbps) très inférieure au {2} ({3} Mbps) : bride inutilement la connexion.");
        Add("router.limit_below", "{0} limit ({1} Mbps) at {2} % of the {3}: acceptable if the goal is to keep the queue empty, at the cost of throughput.", "Limite {0} ({1} Mbps) à {2} % du {3} : acceptable si l'objectif est de garder la file vide, au prix de débit.");
        Add("router.limit_ok", "{0} limit ({1} Mbps) consistent with the {2} ({3} Mbps): {4} %.", "Limite {0} ({1} Mbps) cohérente avec le {2} ({3} Mbps) : {4} %.");
        Add("router.rule_low", "Bandwidth rule \"{0}\": {1} limit {2} Mbps, far below the measured throughput ({3} Mbps). If this rule applies to your PC (or its address range), it throttles it; check which devices it applies to.", "Règle de bande passante « {0} » : limite {1} {2} Mbps, très inférieure au débit mesuré ({3} Mbps). Si cette règle s'applique à votre PC (ou à sa plage d'adresses), elle le bride ; vérifiez à quels appareils elle s'applique.");
        Add("router.priority_temp", "Priority of \"{0}\" limited to {1}: if the lag episode happened after it expired, the device was no longer prioritised.", "Priorité de « {0} » limitée à {1} : si l'épisode de lag a eu lieu après expiration, l'appareil n'était plus prioritaire.");
        Add("router.sqm_unknown", "SQM on your model: unknown. Check the official documentation (model + hardware version + firmware) before assuming it.", "Présence de SQM sur votre modèle : inconnue. À vérifier dans la documentation officielle (modèle + version matérielle + firmware) avant de la supposer.");
        Add("router.prop.none_set", "no limit entered", "aucune limite saisie");
        Add("router.prop.limit", "Set the {0} limit to about {1} Mbps (≈ 92 % of the measured sustained throughput, {2} Mbps).", "Régler la limite {0} à environ {1} Mbps (≈ 92 % du débit soutenu mesuré, {2} Mbps).");
        Add("router.prop.limit_why", "Latency rises by {0} ms under load; a limit just under the real throughput moves the queue from the modem to the router, which can manage it. Current value: {1}.", "La latence monte de {0} ms sous charge ; une limite juste sous le débit réel déplace la file d'attente du modem vers le routeur, qui peut la gérer. Valeur actuelle : {1}.");
        Add("router.prop.limit_back", "Restore the current value ({0}) or disable the limit, save, then rerun the test.", "Remettre la valeur actuelle ({0}) ou désactiver la limite, enregistrer, puis refaire le test.");
        Add("router.prop.sqm", "Check in your model's official documentation whether queue management (SQM / Smart Queue) exists; enable it only if documented.", "Vérifier dans la documentation officielle de votre modèle si une gestion de file (SQM / Smart Queue) existe ; ne l'activer que si elle est documentée.");
        Add("router.prop.sqm_why", "Prioritisation alone does not fix the line's queue.", "La priorisation seule ne corrige pas la file d'attente de la ligne.");
        Add("router.prop.sqm_why_generic", "Latency rises by {0} ms under load: the line's queue is not controlled where it forms, which queue management (SQM) addresses directly.", "La latence monte de {0} ms en charge : la file d'attente de la ligne n'est pas maîtrisée là où elle se forme, ce que la gestion de file (SQM) traite directement.");
        Add("router.prop.sqm_back", "Disable the option and restore the settings noted before the change.", "Désactiver l'option et restaurer les réglages notés avant le changement.");
        Add("router.protocol.1", "Same conditions for every measurement: same computer, preferably on Ethernet, same applications closed, same time of day (evening and morning are not comparable).", "Même condition à chaque mesure : même ordinateur, de préférence en Ethernet, mêmes applications fermées, même plage horaire (le soir et la matinée ne sont pas comparables).");
        Add("router.protocol.2", "Run 3 saturation tests BEFORE (label \"before …\") without changing anything between them.", "Faire 3 tests de saturation AVANT (libellé « avant … ») sans rien changer entre les trois.");
        Add("router.protocol.3", "Change ONE router setting only (note the old value and unit, screenshot before/after). Reboot only if the manufacturer says so.", "Changer UN SEUL réglage du routeur (noter l'ancienne valeur et l'unité, capture d'écran avant/après). Redémarrer seulement si le constructeur le demande.");
        Add("router.protocol.4", "Run 3 identical tests AFTER (label \"after …\"), at the same time of day as the first ones.", "Faire 3 tests identiques APRÈS (libellé « après … »), à la même heure que les premiers.");
        Add("router.protocol.5", "History › tick the 3 \"before\" in group A and the 3 \"after\" in group B › Compare. A change is kept only if it exceeds the variability between repetitions.", "Historique › cocher les 3 « avant » dans le groupe A et les 3 « après » dans le groupe B › Comparer. Un changement n'est retenu que s'il dépasse la variabilité entre les répétitions.");
        Add("router.protocol.6", "If there is no clear improvement: go back to the old value (rollback) before trying anything else.", "Si aucune amélioration nette : revenir à l'ancienne valeur (retour arrière) avant d'essayer autre chose.");
    }
}
