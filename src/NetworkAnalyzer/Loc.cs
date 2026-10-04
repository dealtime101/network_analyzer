using System.Globalization;

namespace NetworkAnalyzer;

/// <summary>
/// Server-side localization (English by default, French on request). Every sentence the server builds (diagnosis, report,
/// errors, notes) goes through <see cref="T"/> with a key registered in both languages; nothing language-specific is ever
/// stored in session files (they hold codes, numbers and user input only). The language is per request (AsyncLocal), set from
/// the <c>lang</c> query parameter or the <c>X-Lang</c> header.
/// </summary>
public static partial class Loc
{
    public const string Default = "en";
    public static readonly string[] Languages = { "en", "fr" };

    static readonly AsyncLocal<string?> current = new();
    static readonly Dictionary<string, (string En, string Fr)> table = new();

    static Loc()
    {
        RegisterCore();
        RegisterDiagnosis();
        RegisterRouter();
        RegisterReport();
    }

    static void Add(string key, string en, string fr) => table[key] = (en, fr);

    public static string Normalize(string? lang) => lang != null && lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    public static string Lang
    {
        get => current.Value ?? Default;
        set => current.Value = Normalize(value);
    }

    /// <summary>Runs a block in a given language, then restores the previous one.</summary>
    public static IDisposable Scope(string lang) => new LangScope(lang);

    sealed class LangScope : IDisposable
    {
        readonly string? previous = current.Value;
        public LangScope(string lang) => current.Value = Normalize(lang);
        public void Dispose() => current.Value = previous;
    }

    /// <summary>Translated text with {0}, {1}… placeholders filled (invariant culture). A missing key shows as ‹key›.</summary>
    public static string T(string key, params object?[] args)
    {
        if (!table.TryGetValue(key, out var v)) return $"‹{key}›";
        var tpl = Lang == "fr" ? v.Fr : v.En;
        return args.Length == 0 ? tpl : string.Format(CultureInfo.InvariantCulture, tpl, args);
    }

    public static string In(string lang, string key, params object?[] args)
    {
        using (Scope(lang)) return T(key, args);
    }

    public static IEnumerable<string> Keys => table.Keys;
    public static (string En, string Fr) Raw(string key) => table[key];

    /// <summary>Every key under a prefix, in registration order of the numeric suffix (for lists such as the protocol steps).</summary>
    public static List<string> List(string prefix, int count) => Enumerable.Range(1, count).Select(i => T($"{prefix}.{i}")).ToList();

    static void RegisterCore()
    {
        // ---- targets (labels are derived from id/role/host at display time, never stored)
        Add("target.gateway", "Gateway (router)", "Passerelle (routeur)");
        Add("target.gateway6", "Gateway (IPv6)", "Passerelle IPv6");
        Add("target.custom", "Custom ({0})", "Personnalisée ({0})");

        // ---- API errors
        Add("err.running", "A monitoring session is already running.", "Une surveillance est déjà en cours.");
        Add("err.invalid_duration", "Invalid duration.", "Durée invalide.");
        Add("err.invalid_link", "Invalid link type.", "Type de liaison invalide.");
        Add("err.invalid_number", "Invalid number.", "Nombre invalide.");
        Add("err.invalid_host", "Invalid address: '{0}'", "Adresse invalide : '{0}'");
        Add("err.invalid_port", "Invalid port: '{0}'", "Port invalide : '{0}'");
        Add("err.invalid_loadtest", "Invalid test parameter.", "Paramètre de test invalide.");
        Add("err.invalid_server_url", "Invalid test server address.", "Adresse de serveur de test invalide.");
        Add("err.confirm_required", "Confirmation required: this test deliberately saturates the connection.", "Confirmation requise : ce test sature volontairement la connexion.");
        Add("err.test_running", "A test is already running.", "Un test est déjà en cours.");
        Add("err.session_too_short", "The running monitoring ends before the test is over ({0} s): restart it with more minutes.", "La surveillance en cours se termine avant la fin du test ({0} s) : relancez-la avec plus de minutes.");
        Add("err.session_not_found", "Session not found.", "Session introuvable.");
        Add("err.not_found", "Not found.", "Introuvable.");
        Add("err.forbidden_host", "Host refused.", "Hôte refusé.");
        Add("err.content_type", "Content-Type: application/json required.", "Content-Type: application/json requis.");
        Add("err.too_large", "Request too large.", "Requête trop grande.");
        Add("err.invalid_json", "Invalid JSON.", "JSON invalide.");
        Add("err.object_expected", "JSON object expected.", "Objet JSON attendu.");
        Add("err.method", "Method not allowed.", "Méthode non autorisée.");
        Add("err.stop_first", "Stop the running monitoring first.", "Arrêtez d'abord la surveillance en cours.");
        Add("err.invalid_image", "Invalid image (PNG, JPEG or WebP).", "Image invalide (PNG, JPEG ou WebP).");
        Add("err.image_too_large", "Image too large (5 MB max).", "Image trop grande (5 Mo max).");
        Add("err.invalid_name", "Invalid name.", "Nom invalide.");
        Add("err.invalid_router", "Invalid router configuration.", "Configuration de routeur invalide.");
        Add("err.invalid_qos_value", "Invalid QoS type or unit.", "Type de QoS ou unité invalide.");

        // ---- default labels
        Add("label.auto_mark", "Auto (\"I'm lagging now\")", "Auto (« Je lag maintenant »)");
        Add("label.saturation_test", "Saturation test", "Test de saturation");

        // ---- recorder notes (stored as codes, translated when shown)
        Add("note.no_dns", "System DNS not detected: DNS measurements unavailable.", "DNS système non détecté : mesures DNS indisponibles.");
        Add("note.not_wifi", "Connection is not Wi-Fi (or unknown): no Wi-Fi measurements.", "Connexion non Wi-Fi (ou inconnue) : pas de mesures Wi-Fi.");
        Add("note.wifi_unavailable", "Wi-Fi details unavailable (Windows may require the location permission: Settings › Privacy › Location).", "Infos Wi-Fi indisponibles (Windows peut exiger l'autorisation de localisation : Paramètres › Confidentialité › Localisation).");
        Add("note.no_iface", "No readable physical network interface: computer traffic unavailable.", "Aucune interface réseau physique lisible : trafic de l'ordinateur indisponible.");
        Add("note.counters_unreadable", "Network interface counters unreadable: computer traffic unavailable.", "Compteurs de l'interface réseau illisibles : trafic de l'ordinateur indisponible.");

        // ---- marks stored as codes
        Add("mark.gap", "System pause (sleep?) or freeze: no measurement over this interval (not counted as loss).", "Pause du système (veille ?) ou blocage : aucune mesure sur cet intervalle (pas compté comme perte).");
        Add("mark.roam", "Wi-Fi access point change (roaming): brief drop possible.", "Changement de point d'accès Wi-Fi (itinérance) : micro-coupure possible.");

        // ---- environment notes (derived from the stored adapters)
        Add("env.multiple", "Several interfaces have a gateway ({0}): the highest-priority one is measured ({1}).", "Plusieurs interfaces avec passerelle ({0}) : la plus prioritaire est mesurée ({1}).");
        Add("env.vpn", "VPN detected ({0}): Internet measurements may go through the tunnel; the measured gateway is the physical interface's.", "VPN détecté ({0}) : les mesures Internet passent peut-être par le tunnel ; la passerelle mesurée est celle de l'interface physique.");
        Add("env.noactive", "No active connection with a gateway detected.", "Aucune connexion active avec passerelle détectée.");
        Add("env.bridged", "\"{0}\" is a virtual interface (Hyper-V bridge / virtual machine): the physical link would be \"{1}\" ({2}), the link type is deduced from it.", "« {0} » est une interface virtuelle (pont Hyper-V / machine virtuelle) : la liaison physique serait « {1} » ({2}), le type de liaison en est déduit.");
        Add("kind.wifi", "Wi-Fi", "Wi-Fi");
        Add("kind.ethernet", "Ethernet", "Ethernet");

        // ---- traceroute
        Add("trace.error.unsupported", "Traceroute unavailable on this system (privileges required).", "Traceroute indisponible sur ce système (privilèges requis).");
        Add("trace.error.name_not_found", "Name not found.", "Nom introuvable.");
        Add("trace.error.cancelled", "Traceroute interrupted before it finished.", "Traceroute interrompu avant la fin.");
        Add("trace.error.failed", "Traceroute failed.", "Traceroute impossible.");
        Add("trace.nohops", "No readable hop.", "Aucun saut lisible.");
        Add("trace.intermediate", "Loss at hop(s) {0} but the following hops answer: most likely ICMP rate-limiting by the router, NOT a real loss.", "Perte(s) au(x) saut(s) {0} mais les sauts suivants répondent : très probablement une limitation ICMP du routeur, PAS une perte réelle.");
        Add("trace.step", "Latency goes from {0} to {1} ms at hop {2} and does not come back down to the destination: the increase starts at this point of the path.", "La latence passe de {0} à {1} ms au saut {2} et ne redescend pas jusqu'à la destination : l'augmentation commence à ce niveau du trajet.");
        Add("trace.unreached", "The destination did not answer the traceroute (may be ICMP filtering, not necessarily an outage).", "La destination n'a pas répondu au traceroute (peut être un filtrage ICMP, pas forcément une panne).");
        Add("trace.destloss", "{0} % loss at the destination (over {1} probes: too few to conclude alone).", "Perte de {0} % à la destination (sur {1} sondes : trop peu pour conclure seul).");

        // ---- load test phases
        Add("phase.idle", "Idle", "Repos");
        Add("phase.download", "Download", "Téléchargement");
        Add("phase.recovery1", "Recovery (after download)", "Récupération (après téléchargement)");
        Add("phase.upload", "Upload", "Envoi");
        Add("phase.recovery2", "Recovery (after upload)", "Récupération (après envoi)");
        Add("loadtest.link_note", "Indicative throughput: may be capped by the server, the Wi-Fi or the PC.", "Débit indicatif : peut être borné par le serveur, le Wi-Fi ou le PC.");

        // ---- definitions (shown in the UI, report and JSON export)
        Add("def.median", "Median: central value of the replies received.", "Médiane : valeur centrale des temps de réponse reçus.");
        Add("def.p95", "p95: 95 % of the replies are faster than this value (nearest rank).", "p95 : 95 % des réponses sont plus rapides que cette valeur (rang le plus proche).");
        Add("def.loss", "Loss: requests without reply ÷ requests sent. System pauses (sleep) do not count.", "Perte : requêtes sans réponse ÷ requêtes envoyées. Les pauses système (veille) ne comptent pas.");
        Add("def.jitter", "Jitter: mean of |RTT(i) − RTT(i−1)| between two consecutive replies (a loss breaks the chain).", "Gigue : moyenne de |RTT(i) − RTT(i−1)| entre deux réponses consécutives (une perte interrompt la chaîne).");
    }
}
