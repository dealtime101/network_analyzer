# NetworkAnalyzer — analyseur réseau local (Windows)

Diagnostic de lag : mesures simultanées (passerelle, 3 destinations Internet indépendantes, destination personnalisée, DNS, Wi‑Fi, trafic du PC), test de saturation / bufferbloat, analyse de la configuration QoS du routeur (saisie manuelle), rapport HTML exportable. Interface en français, servie **uniquement sur `127.0.0.1`**. Ne demande aucun droit administrateur.

Application .NET 10 (Kestrel + une page web embarquée, aucune dépendance NuGet à l'exécution).

## Lancer

Prérequis : runtime **.NET 10** (ASP.NET Core). Puis :

```
NetworkAnalyzer.exe            # ouvre http://127.0.0.1:8765 dans le navigateur
NetworkAnalyzer.exe --no-browser --port 8765
```

Depuis les sources : `dotnet run --project src/NetworkAnalyzer`. Fermer la fenêtre (ou Ctrl+C) arrête proprement la surveillance en cours.

Données : `%LOCALAPPDATA%\NetworkAnalyzer` (une session = `sessions\<n>.meta.json` + `<n>.jsonl`, plus `config.json`). Variable `NA_DATA` pour changer le dossier. Rien n'est envoyé ailleurs.

## Protocole pour capturer un épisode de lag

1. Onglet **Surveillance** : lancer 30 min, avec le serveur de jeu en destination personnalisée (`hôte` ou `hôte:port`).
2. Au premier ressenti de lag, cliquer **« Je lag maintenant »** (un traceroute part en arrière‑plan).
3. À la fin : onglet **Diagnostic** (hypothèses, preuves, limites, prochain test) puis **Rapport HTML** / CSV / JSON.
4. Hors lag : **Test de saturation** en Wi‑Fi puis en Ethernet (2–3 répétitions, libellés différents), puis **Historique › Comparer**.

## Ce qui est mesuré, et comment

| Mesure | Méthode | Droits |
|---|---|---|
| Latence | `System.Net.NetworkInformation.Ping` (API ICMP de Windows, ≈ 1 ms ; « <1 ms » = 0,5 ms), 1 req/s/cible ; repli TCP si l'ICMP est muet | non |
| DNS | requête UDP/53 directe au résolveur configuré (cache de Windows contourné) + test « à froid » + référence 1.1.1.1 | non |
| Interfaces, passerelle, DNS, VPN | `NetworkInterface` ; interface réellement utilisée = `GetBestInterface` vers 1.1.1.1 | non |
| Wi‑Fi | `netsh wlan show interfaces` (signal %, canal, bande, débits de **liaison**) + voisins sur le même canal | non (la localisation Windows peut être exigée) |
| Trafic du PC | compteurs de l'interface — **pas d'attribution par processus** (non fiable sans admin/capture) | non |
| Trajet | pings à TTL croissant à la demande ; une perte intermédiaire sans perte à destination n'est pas une perte réelle | non |
| Saturation | 4 flux HTTPS vers speed.cloudflare.com, repos/↓/récup/↑/récup, plafonds de durée **et** de volume, confirmation, annulable | non |

Définitions : médiane ; p95 = rang le plus proche ; perte = sans réponse ÷ envoyées (une veille n'est pas une perte) ; **gigue = moyenne de |RTT(i) − RTT(i−1)|** entre réponses consécutives (une perte interrompt la chaîne).

Les conclusions sont toujours des **hypothèses** (preuves, éléments contre, limites, niveau de confiance, prochain test), jamais des causes confirmées.

## Routeur TP‑Link / QoS

L'application **ne se connecte pas au routeur** (pas d'API TP‑Link commune documentée ; cela demanderait un mot de passe, jamais demandé ni stocké) et **ne modifie aucun réglage**. Onglet *Routeur & QoS* : modèle exact, version matérielle, firmware, état/type de QoS, limites et unité, priorités (durées), règles de bande passante, captures d'écran. Elle compare ces valeurs aux débits mesurés, distingue **priorisation ≠ SQM**, ne suppose jamais que SQM existe, propose des changements avec justification et retour arrière, et fournit un protocole avant/après.

## Développement

```
dotnet test        # 100+ tests : statistiques, analyseurs, DNS, 11 scénarios d'incident simulés, test de charge (faux serveur), rapport, serveur
```

Chaque livraison monte `<Version>` dans `src/NetworkAnalyzer/NetworkAnalyzer.csproj` (affichée dans l'interface et le rapport).

## Limites assumées

Trafic = ce PC seulement. Interférences radio non mesurées. Test de saturation indicatif (serveur, Wi‑Fi, PC peuvent borner le débit). ICMP parfois traité en basse priorité par les routeurs.
