namespace NetworkAnalyzer;

/// <summary>English and French texts of the diagnosis engine (see Diagnose.cs for where each key is used).</summary>
public static partial class Loc
{
    static void RegisterDiagnosis()
    {
        // ---- zones, window descriptions, levels, states
        Add("d.zone.local", "local network / Wi-Fi / router: the gateway is affected", "réseau local / Wi-Fi / routeur : la passerelle est affectée");
        Add("d.zone.upstream", "upstream of the router (Internet access, provider or path): the gateway is healthy but several Internet destinations are affected", "en amont du routeur (accès Internet, fournisseur ou trajet) : la passerelle est saine mais plusieurs destinations Internet sont affectées");
        Add("d.zone.path", "path to a single Internet destination: the other destinations and the gateway are healthy", "trajet vers une seule destination Internet : les autres destinations et la passerelle sont sains");
        Add("d.zone.custom_path", "path to your custom destination: the rest of the Internet and the gateway are healthy", "trajet vers votre destination personnalisée : le reste d'Internet et la passerelle sont sains");
        Add("d.zone.dns", "DNS resolution: pings are healthy but DNS is slow or failing", "résolution DNS : les pings sont sains mais le DNS est lent ou en échec");
        Add("d.zone.none", "no network anomaly measured over this period (the lag would come from elsewhere: PC, game, server — not measured here)", "aucune anomalie réseau mesurée sur ce créneau (le lag viendrait d'ailleurs : PC, jeu, serveur — non mesuré ici)");
        Add("d.zone.undetermined", "period without enough measurements", "créneau sans mesure suffisante");
        Add("d.zone.busy", " — WARNING: this PC exchanges ≥ {0} Mbps at the same time, the latency increase may come from this traffic itself", " — ATTENTION : ce PC échange ≥ {0} Mbps au même moment, la hausse de latence peut venir de ce trafic lui‑même");
        Add("d.win.target", "{0}: median {1}, max {2}, loss {3} %", "{0} : médiane {1}, max {2}, perte {3} %");
        Add("d.win.dns", "DNS: median {0}, failures {1} %", "DNS : médiane {0}, échecs {1} %");
        Add("d.win.traffic", "PC traffic: ↓ {0} Mbps (max {1}), ↑ {2} Mbps (max {3})", "Trafic du PC : ↓ {0} Mbps (max {1}), ↑ {2} Mbps (max {3})");
        Add("d.win.wifi", "Wi‑Fi signal min {0} %", "Signal Wi‑Fi min {0} %");
        Add("d.episode.note", "Episode detected automatically: {0} degraded measurements ({1}).", "Épisode détecté automatiquement : {0} mesures dégradées ({1}).");
        Add("d.dir.down", "download", "téléchargement");
        Add("d.dir.up", "upload", "envoi");
        Add("d.adj.down", "download", "descendant");
        Add("d.adj.up", "upload", "montant");
        Add("d.level.low", "low", "faible");
        Add("d.level.medium", "medium", "moyenne");
        Add("d.level.high", "high", "élevée");
        Add("d.state.ok", "measured (ICMP)", "mesuré (ICMP)");
        Add("d.state.tcp", "measured over TCP (ICMP does not answer)", "mesuré en TCP (l'ICMP ne répond pas)");
        Add("d.state.no_response", "does not answer ICMP (target excluded from the diagnosis)", "ne répond pas à l'ICMP (cible exclue du diagnostic)");
        Add("d.general.1", "ICMP pings may be handled at low priority by a router or filtered: an ICMP loss is not always a real traffic loss.", "Les pings ICMP peuvent être traités en basse priorité par un routeur ou filtrés : une perte ICMP n'est pas toujours une perte réelle de trafic.");
        Add("d.general.2", "Measurements come from THIS computer: they do not represent the traffic of the other devices in the home.", "Les mesures viennent de CET ordinateur : elles ne représentent pas le trafic des autres appareils de la maison.");
        Add("d.general.3", "No communication content is captured: only metadata (timings, counters) is used.", "Aucune capture du contenu des communications n'est faite : seules des métadonnées (temps, compteurs) sont utilisées.");
        Add("d.general.episodes_capped", "The session has {0} episodes: only the first {1} are listed and scored, the others are not.", "La session compte {0} épisodes : seuls les {1} premiers sont listés et évalués, les autres ne le sont pas.");

        // ---- Wi-Fi / local network
        Add("lan.title", "Wi‑Fi or local network instability", "Instabilité du Wi‑Fi ou du réseau local");
        Add("lan.limit1", "The router may answer pings slowly while forwarding traffic fine (ICMP at low priority): jitter to the gateway alone does not prove a network problem.", "Le routeur peut répondre lentement aux pings tout en acheminant bien le trafic (ICMP en basse priorité) : une gigue vers la passerelle seule ne prouve pas un problème de réseau.");
        Add("lan.limit2", "Windows' \"signal %\" is a coarse scale; the application measures neither channel occupancy nor interference.", "Le « signal % » de Windows est une échelle grossière ; l'application ne mesure ni l'occupation du canal ni les interférences.");
        Add("lan.next_wifi", "Repeat exactly the same monitoring over Ethernet (cable). If the episodes disappear, Wi‑Fi is involved; if they persist, look at the cable, the port or the router.", "Refaire exactement la même surveillance en Ethernet (câble). Si les épisodes disparaissent, le Wi‑Fi est en cause ; s'ils persistent, regarder le câble, le port ou le routeur.");
        Add("lan.next_wired", "Plug another device into another router port over Ethernet and compare, then try another cable.", "Brancher un autre appareil en Ethernet sur un autre port du routeur et comparer, puis tester un autre câble.");
        Add("lan.limit_gw_noicmp", "The gateway does not answer ICMP pings: the local network could not be assessed by this method.", "La passerelle ne répond pas aux pings ICMP : le réseau local n'a pas pu être évalué par cette méthode.");
        Add("lan.ev.gw_loss", "Loss to the gateway: {0} % ({1} of {2} requests).", "Perte vers la passerelle : {0} % ({1} sur {2} requêtes).");
        Add("lan.ev.gw_p95", "Latency to the gateway p95 = {0} (max {1}); a healthy local network usually stays under a few ms (Ethernet) to ~20 ms (Wi‑Fi).", "Latence vers la passerelle p95 = {0} (max {1}) ; un réseau local sain reste généralement sous quelques ms (Ethernet) à ~20 ms (Wi‑Fi).");
        Add("lan.ev.gw_peaks", "Occasional spikes to the gateway up to {0}.", "Pics ponctuels vers la passerelle jusqu'à {0}.");
        Add("lan.ev.gw_jitter", "High jitter to the gateway: {0} ms.", "Gigue vers la passerelle élevée : {0} ms.");
        Add("lan.counter.gw_stable", "Stable gateway: loss {0} %, p95 {1}, max {2}.", "Passerelle stable : perte {0} %, p95 {1}, max {2}.");
        Add("lan.ev.zones_local", "{0} episode(s)/incident(s) out of {1} hit the gateway first (so below the Internet level).", "{0} épisode(s)/incident(s) sur {1} touchent d'abord la passerelle (donc en deçà d'Internet).");
        Add("lan.counter.zones_none", "None of the {0} episode(s)/incident(s) degrades the gateway.", "Aucun des {0} épisode(s)/incident(s) ne dégrade la passerelle.");
        Add("lan.ev.wifi_weak", "Weak Wi‑Fi signal: median {0} % (min {1} %).", "Signal Wi‑Fi faible : médiane {0} % (min {1} %).");
        Add("lan.ev.wifi_drops", "Wi‑Fi signal drops down to {0} % (median {1} %).", "Chutes du signal Wi‑Fi jusqu'à {0} % (médiane {1} %).");
        Add("lan.counter.wifi_ok", "Good Wi‑Fi signal: median {0} %, min {1} %.", "Signal Wi‑Fi correct : médiane {0} %, min {1} %.");
        Add("lan.ev.wifi_link", "The Wi‑Fi LINK rate (≠ Internet speed) drops from {0} to {1} Mbit/s.", "Le débit de LIAISON Wi‑Fi (≠ débit Internet) chute de {0} à {1} Mbit/s.");
        Add("lan.ev.roams", "{0} Wi‑Fi access point change(s) during the session.", "{0} changement(s) de point d'accès Wi‑Fi pendant la session.");
        Add("lan.ev.neighbors", "{0} neighbouring networks with a notable signal on the same channel (indicative; non-Wi‑Fi interference — microwave ovens, Bluetooth — cannot be measured here).", "{0} réseaux voisins avec un signal notable sur le même canal (indicatif ; les interférences non Wi‑Fi — micro‑ondes, Bluetooth — ne sont pas mesurables ici).");
        Add("lan.limit_no_wifi", "No Wi‑Fi measurement available (Windows did not expose them).", "Aucune mesure Wi‑Fi disponible (Windows ne les a pas exposées).");
        Add("lan.counter.wired", "Wired connection: Wi‑Fi is ruled out for this session.", "Connexion filaire : le Wi‑Fi est hors de cause pour cette session.");
        Add("lan.ev.load_gw", "Under load, latency to the gateway rises too (+{0} ms): the local link itself saturates (Wi‑Fi or router queue on the LAN side).", "Sous charge, la latence vers la passerelle monte aussi (+{0} ms) : le lien local lui‑même sature (Wi‑Fi ou file du routeur côté LAN).");
        Add("lan.action.wifi1", "Move the computer closer to the router or use an Ethernet cable to compare.", "Rapprocher l'ordinateur du routeur ou utiliser un câble Ethernet pour comparer.");
        Add("lan.action.wifi2", "If Wi‑Fi: change channel/band (5 GHz) and move the router away from interference sources.", "Si Wi‑Fi : changer de canal/bande (5 GHz) et éloigner le routeur des sources d'interférences.");
        Add("lan.action.wired", "Check the cable and the router port; restart the router, then run another session to compare.", "Vérifier câble et port du routeur ; redémarrer le routeur puis refaire une session pour comparer.");

        // ---- bufferbloat
        Add("bloat.title", "Bufferbloat (latency that explodes when the connection is loaded)", "Bufferbloat (latence qui explose quand la connexion est chargée)");
        Add("bloat.limit1", "Throughput may be capped by the test server, the Wi‑Fi or the PC: INDICATIVE result.", "Le débit obtenu peut être borné par le serveur de test, le Wi‑Fi ou le PC : résultat INDICATIF.");
        Add("bloat.limit2", "ICMP ping is not necessarily handled like your game traffic (a QoS queue may separate them).", "Le ping ICMP n'est pas forcément traité comme votre trafic de jeu (une file QoS peut les séparer).");
        Add("bloat.next", "Repeat the saturation test over Ethernet, then after a single setting change (QoS/SQM limit), with the before/after protocol.", "Refaire le test de saturation en Ethernet, puis après un seul changement de réglage (limite QoS/SQM), avec le protocole avant/après.");
        Add("bloat.not_eval.none", "No saturation test in this session: run the \"Saturation test\" tab.", "Aucun test de saturation dans cette session : lancez l'onglet « Test de saturation ».");
        Add("bloat.not_eval.unusable", "The saturation test did not load the line in a usable way (see the limits): bufferbloat not evaluated.", "Le test de saturation n'a pas chargé la ligne de façon exploitable (voir les limites) : bufferbloat non évalué.");
        Add("bloat.none", "none", "nul");
        Add("bloat.limit_invalid", "{0} phase inconclusive (throughput {1} or silent targets): the line was not really loaded.", "Phase {0} non concluante (débit {1} ou cibles muettes) : la ligne n'a pas été réellement chargée.");
        Add("bloat.cap_early", " The volume cap was reached in {0} s: rerun with a higher cap.", " Le plafond de volume a été atteint en {0} s : relancez avec un plafond plus élevé.");
        Add("bloat.ev", "{0}: median Internet latency {1} → {2} ms (+{3} ms, indicative grade {4}), p95 under load {5}, max loss {6} %, sustained throughput {7} Mbps.", "{0} : latence Internet médiane {1} → {2} ms (+{3} ms, note indicative {4}), p95 sous charge {5}, perte max {6} %, débit soutenu {7} Mbps.");
        Add("bloat.ev_gw_stable", "{0}: the gateway stays stable (+{1} ms) while the Internet side goes up: the queue forms beyond the local network (router exit, modem or line) — consistent with bufferbloat.", "{0} : la passerelle reste stable (+{1} ms) alors qu'Internet monte : la file d'attente se forme au‑delà du réseau local (sortie du routeur, modem ou ligne) — compatible avec du bufferbloat.");
        Add("bloat.counter_gw", "{0}: latency to the gateway also rises (+{1} ms): the local link saturates, the delay cannot be (entirely) attributed to the Internet line.", "{0} : la latence vers la passerelle monte aussi (+{1} ms) : le lien local sature, on ne peut pas attribuer (tout) le retard à la ligne Internet.");
        Add("bloat.counter_stable", "{0}: latency stable under load (+{1} ms).", "{0} : latence stable sous charge (+{1} ms).");
        Add("bloat.limit_wifi", "Test run over Wi‑Fi: Wi‑Fi can add latency under load; repeat over Ethernet to isolate the line.", "Test réalisé en Wi‑Fi : le Wi‑Fi peut ajouter de la latence sous charge ; refaire en Ethernet pour isoler la ligne.");
        Add("bloat.action", "Enable queue management (SQM/Smart Queue) if your router offers it, otherwise manually limit upload/download rates to ~90–95 % of the measured throughput.", "Activer une gestion de file (SQM/Smart Queue) si votre routeur la propose, sinon limiter manuellement débit montant/descendant à ~90–95 % du débit mesuré.");

        // ---- saturation
        Add("sat.title", "Download or upload saturation", "Saturation du téléchargement ou de l'envoi");
        Add("sat.limit1", "Only THIS computer's traffic is visible; the other devices in the home can saturate the line without showing up here.", "Seul le trafic de CET ordinateur est visible ; les autres appareils de la maison peuvent saturer la ligne sans apparaître ici.");
        Add("sat.limit2", "Without an advertised speed or a saturation test, the line's capacity is unknown.", "Sans débit annoncé ni test de saturation, la capacité de la ligne est inconnue.");
        Add("sat.next", "During a lag, note what the other devices are doing (streaming, updates); enter the advertised speed or run the test to learn the capacity.", "Pendant un lag, noter ce que font les autres appareils (streaming, mises à jour) ; renseigner le débit annoncé ou lancer le test pour connaître la capacité.");
        Add("sat.ev.pc_over", "During {0} of the {1} episode(s)/incident(s), this PC's traffic exceeds {2} % of the known capacity (↓ {3} / ↑ {4} Mbps).", "Pendant {0} des {1} épisode(s)/incident(s), le trafic de ce PC dépasse {2} % de la capacité connue (↓ {3} / ↑ {4} Mbps).");
        Add("sat.ev.p95", "The PC's {0} traffic reaches ≥ {1} % of the capacity (p95 {2} Mbps).", "Le trafic {0} du PC atteint ≥ {1} % de la capacité (p95 {2} Mbps).");
        Add("sat.ev.household", "{0} episode(s) with high Internet latency while this PC exchanges little data: another device in the home could be saturating the line (weak clue, cannot be verified from this PC).", "{0} épisode(s) avec latence Internet élevée alors que ce PC échange peu de données : un autre appareil du foyer pourrait saturer la ligne (indice faible, non vérifiable depuis ce PC).");
        Add("sat.counter.far", "This PC's traffic stays far from the capacity during the episodes.", "Le trafic de ce PC reste loin de la capacité pendant les épisodes.");
        Add("sat.action1", "Pause downloads/updates/streaming while gaming, then run another session to compare.", "Mettre en pause les téléchargements/mises à jour/streaming pendant le jeu, puis refaire une session pour comparer.");
        Add("sat.action2", "Check the router's status page (connected devices / traffic) during a lag, if your model has one.", "Consulter la page d'état du routeur (appareils connectés / trafic) pendant un lag, si elle existe sur votre modèle.");

        // ---- router / QoS
        Add("router.title", "Router or QoS configuration problem", "Problème de routeur ou de configuration QoS");
        Add("rq.limit1", "The application does not read the router automatically: it only knows what you entered in the Router & QoS tab.", "L'application ne lit pas le routeur automatiquement : elle ne connaît que ce que vous avez saisi dans l'onglet Routeur & QoS.");
        Add("rq.limit2", "QoS prioritisation ≠ queue management (SQM): do not assume your model offers SQM (check the official documentation for your model and firmware).", "Priorisation QoS ≠ gestion de file (SQM) : ne pas supposer que votre modèle propose SQM (à vérifier dans la documentation officielle de votre modèle et firmware).");
        Add("rq.next", "Enter the QoS configuration, apply ONE proposed change, then repeat the saturation test following the before/after protocol.", "Renseigner la configuration QoS, appliquer UN seul changement proposé, puis refaire le test de saturation selon le protocole avant/après.");
        Add("rq.ev.wired", "Wired connection but the gateway is unstable (loss {0} %, p95 {1}): the router (load, firmware), the cable or the port should be examined.", "Connexion filaire mais la passerelle est instable (perte {0} %, p95 {1}) : le routeur (charge, firmware), le câble ou le port sont à examiner.");
        Add("rq.limit_noconfig", "No router configuration entered: the QoS analysis could not compare the limits with the measured throughput.", "Aucune configuration de routeur saisie : l'analyse QoS n'a pas pu comparer les limites aux débits mesurés.");
        Add("rq.action.default", "Restart the router, check the manufacturer's site for a newer firmware for your model/hardware version, try another cable or port.", "Redémarrer le routeur, vérifier sur le site du constructeur si un firmware plus récent existe pour votre modèle/version matérielle, essayer un autre câble ou port.");

        // ---- ISP / path
        Add("isp.title", "Internet provider or Internet path problem", "Problème chez le fournisseur Internet ou sur un trajet Internet");
        Add("isp.limit1", "The default targets are public DNS resolvers (anycast): they do not necessarily follow the same path as your game or site.", "Les cibles par défaut sont des résolveurs DNS publics (anycast) : elles ne passent pas forcément par le même chemin que votre jeu ou votre site.");
        Add("isp.limit2", "A one-off traceroute shows a single moment and three probes per hop.", "Un traceroute ponctuel ne montre qu'un instant et trois sondes par saut.");
        Add("isp.next", "Run a session with your game server as the custom destination and click \"I'm lagging now\" during the lag (automatic traceroute); compare at another time of day.", "Refaire une session avec votre serveur de jeu comme destination personnalisée et cliquer « Je lag maintenant » pendant le lag (traceroute automatique) ; comparer à une autre heure.");
        Add("isp.limit_busy", "{0} episode(s) coincide with sustained traffic (≥ {1} Mbps) from this PC: the latency increase may come from that traffic itself, they are not held against the provider.", "{0} épisode(s) coïncident avec un trafic soutenu (≥ {1} Mbps) de ce PC : la hausse de latence peut venir de ce trafic lui‑même, ils ne sont pas retenus contre le fournisseur.");
        Add("isp.ev.upstream", "{0} episode(s)/incident(s) out of {1}: the gateway is healthy but several independent Internet destinations degrade at the same time, without notable PC traffic.", "{0} épisode(s)/incident(s) sur {1} : la passerelle est saine mais plusieurs destinations Internet indépendantes sont dégradées en même temps, sans trafic notable du PC.");
        Add("isp.ev.path", "{0} episode(s) affect only ONE Internet destination (the others and the gateway stay healthy): more likely the path to that destination than the whole connection.", "{0} épisode(s) n'affectent qu'UNE destination Internet (les autres et la passerelle restent sains) : plutôt le trajet vers cette destination que la connexion entière.");
        Add("isp.ev.custom", "{0} episode(s) affect only your custom destination: possible problem on the path or at that server.", "{0} épisode(s) n'affectent que votre destination personnalisée : problème possible sur le trajet ou chez ce serveur.");
        Add("isp.counter.none", "No episode degrades the Internet while sparing the gateway.", "Aucun épisode ne dégrade Internet tout en épargnant la passerelle.");
        Add("isp.limit_partial_local", "Part of the episodes also hit the gateway: the local network may explain part of the degradation toward the Internet.", "Une partie des épisodes touche aussi la passerelle : le réseau local peut expliquer une partie de la dégradation vers Internet.");
        Add("isp.unknown_ip", "unknown address", "adresse inconnue");
        Add("isp.ev.trace_step", "Traceroute to {0}: latency goes from {1} to {2} ms at hop {3} ({4}) and persists to the destination.", "Traceroute vers {0} : la latence monte de {1} à {2} ms au saut {3} ({4}) et persiste jusqu'à la destination.");
        Add("isp.counter.trace_icmp", "Traceroute to {0}: loss at intermediate hops ({1}) NOT found again at the destination → routers' ICMP rate-limiting, not a real loss.", "Traceroute vers {0} : perte(s) sur des sauts intermédiaires ({1}) NON retrouvée(s) à destination → limitation ICMP des routeurs, pas une perte réelle.");
        Add("isp.ev.trace_destloss", "Traceroute to {0}: loss at the destination ({1} %).", "Traceroute vers {0} : perte à la destination ({1} %).");
        Add("isp.action1", "Note the time and frequency of the episodes and contact the provider with the exported report if the \"upstream\" episodes repeat.", "Noter heure et fréquence des épisodes et contacter le fournisseur avec le rapport exporté si les épisodes « en amont » se répètent.");
        Add("isp.action2", "Test with another computer over Ethernet plugged straight into the modem to rule out the router.", "Tester avec un autre appareil/ordinateur en Ethernet branché directement au modem pour exclure le routeur.");

        // ---- DNS
        Add("dnsr.title", "DNS problem", "Problème DNS");
        Add("dnsr.limit1", "The measurement queries your resolver directly over UDP/53; a browser using DNS-over-HTTPS bypasses that resolver.", "La mesure interroge directement votre résolveur en UDP/53 ; un navigateur qui utilise DNS‑sur‑HTTPS contourne ce résolveur.");
        Add("dnsr.limit2", "The \"cold\" test uses random names under example.com (reserved for this purpose).", "Le test « à froid » utilise des noms aléatoires sous example.com (réservé à cet usage).");
        Add("dnsr.next", "Change the PC's DNS (e.g. 1.1.1.1 or 9.9.9.9), run another session and compare resolution times.", "Changer le DNS du PC (ex. 1.1.1.1 ou 9.9.9.9), refaire une session et comparer les temps de résolution.");
        Add("dnsr.limit_none", "No DNS measurement available.", "Aucune mesure DNS disponible.");
        Add("dnsr.ev.fail", "Resolution failures: {0} % ({1} of {2}) on the configured DNS.", "Échecs de résolution : {0} % ({1} sur {2}) sur le DNS configuré.");
        Add("dnsr.ev.slow", "Slow resolution (common names): median {0}.", "Résolution lente (noms courants) : médiane {0}.");
        Add("dnsr.ev.peaks", "Resolution spikes: p95 {0}, max {1}.", "Pics de résolution : p95 {0}, max {1}.");
        Add("dnsr.ev.cold", "Slow cold resolution: median {0}.", "Résolution à froid lente : médiane {0}.");
        Add("dnsr.ev.ref", "The public resolver 1.1.1.1 resolves cold names much faster ({0}) than your DNS ({1}).", "Le résolveur public 1.1.1.1 résout à froid bien plus vite ({0}) que votre DNS ({1}).");
        Add("dnsr.counter.fast", "Fast, reliable DNS: median {0}, failures {1} %.", "DNS rapide et fiable : médiane {0}, échecs {1} %.");
        Add("dnsr.action", "Try another resolver (1.1.1.1, 9.9.9.9 or 8.8.8.8) in Windows' network settings.", "Essayer un autre résolveur (1.1.1.1, 9.9.9.9 ou 8.8.8.8) dans les réglages réseau de Windows.");

        // ---- background traffic
        Add("bg.title", "Background traffic on my computer", "Trafic de fond sur mon ordinateur");
        Add("bg.limit1", "Method: network interface counters (the PC's totals). No per-process attribution is made because no reliable method exists without administrator rights or a capture: the application therefore does NOT point at a guilty program.", "Méthode : compteurs de l'interface réseau (totaux du PC). Aucune attribution par processus n'est faite car il n'existe pas de méthode fiable sans droits administrateur ni capture : l'application ne vous désigne donc PAS un programme coupable.");
        Add("bg.limit2", "The traffic of the other devices in the home is not visible.", "Le trafic des autres appareils de la maison n'est pas visible.");
        Add("bg.next", "Open Task Manager (Performance/Processes tab, Network column) or Resource Monitor (resmon › Network) during a lag, then run another session with background applications closed.", "Ouvrir le Gestionnaire des tâches (onglet Performances/Processus, colonne Réseau) ou le Moniteur de ressources (resmon › Réseau) pendant un lag, puis refaire une session avec les applications de fond fermées.");
        Add("bg.limit_unavailable", "PC traffic unavailable (network interface not read).", "Trafic du PC indisponible (interface réseau non lue).");
        Add("bg.ev.notable", "Notable background traffic outside the test: median ↓ {0} Mbps, ↑ {1} Mbps.", "Trafic de fond notable hors test : médiane ↓ {0} Mbps, ↑ {1} Mbps.");
        Add("bg.ev.idle", "During the test's idle phase (nothing was supposed to flow), the PC already exchanges {0} Mbps (↓+↑ combined).", "Pendant la phase de repos du test (rien ne devait circuler), le PC échange déjà {0} Mbps (↓+↑ cumulés).");
        Add("bg.ev.corr", "At the degraded moments, the PC's traffic is {0}× higher than the rest of the time ({1} versus {2} Mbps): a correlation consistent with background activity (a correlation is not proof).", "Aux instants dégradés, le trafic du PC est {0}× plus élevé que le reste du temps ({1} contre {2} Mbps) : corrélation compatible avec une activité de fond (une corrélation n'est pas une preuve).");
        Add("bg.ev.hot", "{0} episode(s)/incident(s) coincide with PC traffic ≥ 5 Mbps.", "{0} épisode(s)/incident(s) coïncident avec un trafic du PC ≥ 5 Mbps.");
        Add("bg.counter.low", "Low PC traffic outside the test: median ↓ {0} Mbps, ↑ {1} Mbps.", "Trafic du PC faible hors test : médiane ↓ {0} Mbps, ↑ {1} Mbps.");
        Add("bg.action", "Close cloud backups, updates, game launchers and streaming tabs while gaming, then run another session to compare.", "Fermer sauvegardes cloud, mises à jour, lanceurs de jeux, onglets de streaming pendant le jeu, puis refaire une session pour comparer.");

        // ---- summary
        Add("sum.nodata", "No measurement recorded.", "Aucune mesure enregistrée.");
        Add("sum.dead", "No target answered: connection down for the whole session, or ICMP blocked on this machine/network. Nothing can be concluded about the lag.", "Aucune cible n'a répondu : connexion coupée pendant toute la session, ou ICMP bloqué sur cette machine/ce réseau. Rien ne peut être conclu sur le lag.");
        Add("sum.sparse", "Not enough data ({0} calm measurements; at least {1} are needed). Let it run for 10 to 30 minutes.", "Données insuffisantes ({0} mesures calmes ; il en faut au moins {1}). Laissez tourner 10 à 30 minutes.");
        Add("sum.top", "Hypothesis most consistent with the measurements: \"{0}\" (confidence {1}).", "Hypothèse la plus compatible avec les mesures : « {0} » (confiance {1}).");
        Add("sum.others", "Other leads: {0}.", "Autres pistes : {0}.");
        Add("sum.disclaimer", "These are hypotheses deduced from measurements, not confirmed causes: see the evidence, the limits and the next test for each.", "Ce sont des hypothèses déduites de mesures, pas des causes confirmées : voir pour chacune les preuves, les limites et le test suivant.");
        Add("sum.none", "No clear degradation measured during this session: the lag was probably not captured. Restart the monitoring and click \"I'm lagging now\" during an episode.", "Aucune dégradation nette mesurée pendant cette session : le lag n'a probablement pas été capturé. Relancez la surveillance et cliquez « Je lag maintenant » pendant un épisode.");
        Add("sum.noicmp", "\"{0}\" does not answer ICMP (ping): target excluded from the diagnosis. Specify a TCP port (e.g. {1}:443) to measure it another way.", "« {0} » ne répond pas à l'ICMP (ping) : cible exclue du diagnostic. Précisez un port TCP (ex. {1}:443) pour la mesurer autrement.");
        Add("sum.quiet", "For {0} report(s) out of {1}, no network anomaly is measured around that moment: look outside the network (computer, game, server).", "Pour {0} signalement(s) sur {1}, aucune anomalie réseau n'est mesurée autour de l'instant : piste hors réseau (ordinateur, jeu, serveur).");

        // ---- session comparison
        Add("cmp.inet_p95", "Internet latency p95 (ms)", "Latence Internet p95 (ms)");
        Add("cmp.inet_loss", "Internet loss (%)", "Perte Internet (%)");
        Add("cmp.gw_p95", "Gateway latency p95 (ms)", "Latence passerelle p95 (ms)");
        Add("cmp.jitter", "Internet jitter (ms)", "Gigue Internet (ms)");
        Add("cmp.dns", "DNS median (ms)", "DNS médiane (ms)");
        Add("cmp.bloat_down", "Latency increase during download (ms)", "Hausse de latence en téléchargement (ms)");
        Add("cmp.bloat_up", "Latency increase during upload (ms)", "Hausse de latence en envoi (ms)");
        Add("cmp.down_mbps", "Sustained download throughput (Mbps)", "Débit descendant soutenu (Mbps)");
        Add("cmp.up_mbps", "Sustained upload throughput (Mbps)", "Débit montant soutenu (Mbps)");
        Add("cmp.verdict.improvement", "improvement", "amélioration");
        Add("cmp.verdict.degradation", "degradation", "dégradation");
        Add("cmp.verdict.indistinct", "indistinct (within the variability between repetitions)", "indistinct (dans la variabilité entre répétitions)");
        Add("cmp.verdict.indicative", "indicative (a single measurement per group: repeat to judge)", "indicatif (une seule mesure par groupe : répéter pour juger)");
    }
}
