# NetworkAnalyzer — analyseur réseau local pour Windows

Diagnostic de lag : mesures simultanées (passerelle, 3 destinations Internet indépendantes, destination personnalisée, DNS, Wi-Fi, trafic du PC), test de saturation / bufferbloat, analyse de la configuration QoS du routeur (saisie manuelle), rapport HTML exportable. L'interface web est servie **uniquement sur `127.0.0.1`**, ne demande aucun droit administrateur et existe en **anglais (par défaut) et en français** (sélecteur dans l'en-tête, choix mémorisé).

English: see [README.md](README.md) (full documentation).

## Lancer

Prérequis : runtime **.NET 10** (ASP.NET Core).

```
NetworkAnalyzer.exe            # ouvre http://127.0.0.1:8765 dans le navigateur
NetworkAnalyzer.exe --no-browser --port 8765
```

Fermer la fenêtre (ou Ctrl+C) arrête proprement la surveillance en cours. Données : `%LOCALAPPDATA%\NetworkAnalyzer` (variable `NA_DATA` pour changer le dossier). Rien n'est envoyé ailleurs.

## Protocole pour capturer un épisode de lag

1. Onglet **Surveillance** : lancer 30 min, avec le serveur de jeu en destination personnalisée (`hôte` ou `hôte:port`).
2. Au premier ressenti de lag, cliquer **« Je lag maintenant »** (un traceroute part en arrière-plan).
3. À la fin : onglet **Diagnostic** (hypothèses, preuves, limites, prochain test) puis **Rapport HTML** / CSV / JSON.
4. Hors lag : **Test de saturation** en Wi-Fi puis en Ethernet (2–3 répétitions, libellés différents), puis **Historique › Comparer**.

## À savoir

- Les conclusions sont des **hypothèses** (preuves, éléments contre, limites, confiance, prochain test), jamais des causes confirmées.
- Le trafic est celui de **ce** PC seulement, sans attribution par programme ; les interférences radio ne sont pas mesurées ; le test de saturation est indicatif.
- L'application **ne se connecte pas au routeur** et **ne modifie aucun réglage** : la configuration QoS est saisie à la main, les changements sont seulement proposés (avec retour arrière).
- Le code, les clés JSON et les valeurs stockées sont en anglais ; les textes affichés sont traduits (`en`/`fr`), et un fichier de session enregistré dans une langue se lit dans l'autre.
