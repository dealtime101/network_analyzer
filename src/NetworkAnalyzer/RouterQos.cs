namespace NetworkAnalyzer;

public sealed class RouterFinding
{
    /// <summary>ok | info | attention | probleme</summary>
    public string Severite { get; set; } = "info";
    public string Texte { get; set; } = "";
}

public sealed class Proposal
{
    public string Changement { get; set; } = "";
    public string Justification { get; set; } = "";
    public string RetourArriere { get; set; } = "";
}

public sealed class RouterAnalysis
{
    public List<RouterFinding> Findings { get; set; } = new();
    public List<Proposal> Propositions { get; set; } = new();
    public List<string> Protocole { get; set; } = new();
    public string DocUrl { get; set; } = "";
    public string Rappel { get; set; } = "";
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
    public static readonly string[] QosTypes = { "inconnu", "priorite", "limite_bande_passante", "sqm" };
    public const string DocUrl = "https://www.tp-link.com/fr/support/";

    public static readonly string[] ProtocoleAvantApres =
    {
        "Même condition à chaque mesure : même ordinateur, de préférence en Ethernet, mêmes applications fermées, même plage horaire (le soir et la matinée ne sont pas comparables).",
        "Faire 3 tests de saturation AVANT (libellé « avant … ») sans rien changer entre les trois.",
        "Changer UN SEUL réglage du routeur (noter l'ancienne valeur et l'unité, capture d'écran avant/après). Redémarrer seulement si le constructeur le demande.",
        "Faire 3 tests identiques APRÈS (libellé « après … »), à la même heure que les premiers.",
        "Historique › cocher les 3 « avant » dans le groupe A et les 3 « après » dans le groupe B › Comparer. Un changement n'est retenu que s'il dépasse la variabilité entre les répétitions.",
        "Si aucune amélioration nette : revenir à l'ancienne valeur (retour arrière) avant d'essayer autre chose.",
    };

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

    static string G(double x) => x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    static string F0(double x) => x.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Configuration ↔ measurements comparison.</summary>
    public static List<RouterFinding> Check(RouterConfig? r, (double? Down, double? Up) meas, AppConfig cfg, double worstDelta)
    {
        var o = new List<RouterFinding>();
        if (r is null) return o;
        void Add(string sev, string txt) => o.Add(new RouterFinding { Severite = sev, Texte = txt });
        var qos = r.QosEnabled;
        var typ = r.QosType;
        var (ld, lu) = Limits(r);
        if (qos is null) Add("info", "État de la QoS inconnu : à relever dans l'interface du routeur.");
        else if (qos == false)
        {
            if (worstDelta >= 30) Add("attention", $"QoS désactivée alors que la latence monte de {F0(worstDelta)} ms sous charge : aucune limite ni gestion de file ne protège la latence.");
        }
        else
        {
            if (typ == "priorite" && worstDelta >= 30)
                Add("attention", "La QoS activée est de type priorisation : elle ne supprime pas la file d'attente de la ligne, ce qui est compatible avec la hausse de latence mesurée sous charge.");
            if (typ == "inconnu") Add("info", "Type de QoS inconnu (priorisation, limite de débit ou SQM ?) : précisez-le, la conclusion en dépend.");
        }
        foreach (var (lim, m, name, plan) in new[] { (ld, meas.Down, "descendante", cfg.PlanDownMbps), (lu, meas.Up, "montante", cfg.PlanUpMbps) })
        {
            if (lim is null) continue;
            double? refv = m is > 0 ? m : plan;
            if (refv is null or 0) continue;
            double l = lim.Value, rf = refv.Value, r2 = l / rf;
            var src = m is > 0 ? "débit mesuré" : "débit annoncé";
            if ((r2 >= 800 && r2 <= 1200) || (r2 >= 0.0008 && r2 <= 0.0012))
                Add("probleme", $"Limite {name} ({G(l)} Mbps) ≈ ×{G(r2)} du {src} ({F0(rf)} Mbps) : confusion d'unité Kbps/Mbps probable dans la saisie ou dans le routeur.");
            else if (m is > 0 && qos == true && m > 1.15 * l)
                Add("probleme", $"Débit {name} mesuré ({F0(m.Value)} Mbps) DÉPASSE la limite configurée ({G(l)} Mbps) : la limite ne s'applique pas à ce flux (QoS inactive, limite par appareil, mauvaise unité…).");
            else if (r2 >= 1.1)
                Add(worstDelta >= 30 ? "attention" : "info", $"Limite {name} ({G(l)} Mbps) supérieure au {src} ({F0(rf)} Mbps) : elle ne limite rien, la file d'attente reste dans le modem/la ligne.");
            else if (r2 < 0.5)
                Add("attention", $"Limite {name} ({G(l)} Mbps) très inférieure au {src} ({F0(rf)} Mbps) : bride inutilement la connexion.");
            else if (r2 < 0.85)
                Add("info", $"Limite {name} ({G(l)} Mbps) à {F0(r2 * 100)} % du {src} : acceptable si l'objectif est de garder la file vide, au prix de débit.");
            else
                Add("ok", $"Limite {name} ({G(l)} Mbps) cohérente avec le {src} ({F0(rf)} Mbps) : {F0(r2 * 100)} %.");
        }
        foreach (var rule in r.BandwidthRules)
        {
            foreach (var (val, m, name) in new[] { (rule.Down, meas.Down, "descendante"), (rule.Up, meas.Up, "montante") })
            {
                var lim = ToMbps(val, rule.Unit ?? r.Unit);
                if (lim is not null && m is > 0 && lim < 0.5 * m)
                    Add("attention", $"Règle de bande passante « {(string.IsNullOrEmpty(rule.Name) ? "?" : rule.Name)} » : limite {name} {G(lim.Value)} Mbps, très inférieure au débit mesuré ({F0(m.Value)} Mbps). " +
                                     "Si cette règle s'applique à votre PC (ou à sa plage d'adresses), elle le bride ; vérifiez à quels appareils elle s'applique.");
            }
        }
        foreach (var d in r.PriorityDevices)
        {
            var dur = (d.Duration ?? "").Trim().ToLowerInvariant();
            if (dur.Length > 0 && dur is not ("toujours" or "always" or "illimitee" or "illimitée" or "∞"))
                Add("info", $"Priorité de « {(string.IsNullOrEmpty(d.Name) ? "?" : d.Name)} » limitée à {d.Duration} : si l'épisode de lag a eu lieu après expiration, l'appareil n'était plus prioritaire.");
        }
        if (string.IsNullOrEmpty(r.SqmAvailable) || r.SqmAvailable == "inconnu")
            if (typ != "sqm")
                Add("info", "Présence de SQM sur votre modèle : inconnue. À vérifier dans la documentation officielle (modèle + version matérielle + firmware) avant de la supposer.");
        return o;
    }

    /// <summary>PROPOSED changes (never applied) with justification and rollback.</summary>
    public static List<Proposal> Propose(RouterConfig? r, (double? Down, double? Up) meas, double worstDelta)
    {
        var props = new List<Proposal>();
        if (r is null) return props;
        var (ld, lu) = Limits(r);
        var unit = r.Unit;
        foreach (var (lim, m, name, cur) in new[] { (ld, meas.Down, "descendante", r.LimitDown), (lu, meas.Up, "montante", r.LimitUp) })
        {
            if (m is null or 0 || worstDelta < 30) continue;
            var target = Math.Round(m.Value * 0.92, MidpointRounding.ToEven);
            if (lim is null || lim > 1.05 * m)
            {
                var now = cur.HasValue ? $"{G(cur.Value)} {unit}" : "aucune limite saisie";
                props.Add(new Proposal
                {
                    Changement = $"Régler la limite {name} à environ {F0(target)} Mbps (≈ 92 % du débit soutenu mesuré, {F0(m.Value)} Mbps).",
                    Justification = $"La latence monte de {F0(worstDelta)} ms sous charge ; une limite juste sous le débit réel déplace la file d'attente du modem vers le routeur, qui peut la gérer. Valeur actuelle : {now}.",
                    RetourArriere = $"Remettre la valeur actuelle ({now}) ou désactiver la limite, enregistrer, puis refaire le test.",
                });
            }
        }
        if (worstDelta >= 30 && r.QosType != "sqm")
            props.Add(new Proposal
            {
                Changement = "Vérifier dans la documentation officielle de votre modèle si une gestion de file (SQM / Smart Queue) existe ; ne l'activer que si elle est documentée.",
                Justification = "La priorisation seule ne corrige pas la file d'attente de la ligne.",
                RetourArriere = "Désactiver l'option et restaurer les réglages notés avant le changement.",
            });
        return props;
    }

    public static RouterAnalysis Analysis(RouterConfig? r, (double? Down, double? Up) meas, AppConfig cfg, double worstDelta) => new()
    {
        Findings = Check(r, meas, cfg, worstDelta), Propositions = Propose(r, meas, worstDelta), Protocole = ProtocoleAvantApres.ToList(),
        DocUrl = DocUrl, Rappel = "Aucun réglage n'est modifié par l'application. Appliquez vous‑même un changement à la fois.",
    };
}
