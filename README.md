# NetworkAnalyzer — local network analyzer for Windows

Lag diagnosis tool: simultaneous measurements (gateway, 3 independent Internet destinations, a custom destination, DNS, Wi‑Fi, the PC's traffic), saturation / bufferbloat test, analysis of the router's QoS configuration (manual entry), exportable HTML report. The web interface is served **on `127.0.0.1` only**, needs no administrator rights, and is available in **English (default) and French** (switch in the header; the choice is remembered). The code, the data formats and the API are in English.

.NET 10 application (Kestrel + one embedded web page, no NuGet dependency at run time). Version history: see `<Version>` in `src/NetworkAnalyzer/NetworkAnalyzer.csproj`.

Français : voir [README.fr.md](README.fr.md).

## Run

Requirement: the **.NET 10** runtime (ASP.NET Core).

```
NetworkAnalyzer.exe            # opens http://127.0.0.1:8765 in the browser
NetworkAnalyzer.exe --no-browser --port 8765
```

From the sources: `dotnet run --project src/NetworkAnalyzer`. Closing the window (or Ctrl+C) stops the running monitoring cleanly.

Data: `%LOCALAPPDATA%\NetworkAnalyzer` (one session = `sessions\<n>.meta.json` + `<n>.jsonl`, plus `config.json`). Set `NA_DATA` to change the folder. Nothing is sent anywhere else.

## Protocol to capture a lag episode

1. **Monitoring** tab: start 30 minutes, with the game server as the custom destination (`host` or `host:port`).
2. At the first sign of lag, click **"I'm lagging now"** (a traceroute starts in the background).
3. Afterwards: **Diagnosis** tab (hypotheses, evidence, limits, next test), then the **HTML report** / CSV / JSON.
4. Outside lag periods: **Saturation test** over Wi‑Fi then over Ethernet (2–3 repetitions, different labels), then **History › Compare**.

## What is measured, and how

| Measurement | Method | Rights |
|---|---|---|
| Latency | `System.Net.NetworkInformation.Ping` (Windows ICMP API, ≈ 1 ms; "<1 ms" = 0.5 ms), 1 request/s/target; TCP fallback if ICMP is silent | no |
| DNS | direct UDP/53 query to the configured resolver (Windows' cache bypassed) + "cold" test + 1.1.1.1 reference | no |
| Interfaces, gateway, DNS, VPN | `NetworkInterface`; the interface really used = `GetBestInterface` towards 1.1.1.1 | no |
| Wi‑Fi | `netsh wlan show interfaces` (signal %, channel, band, **link** rates, French and English labels) + neighbours on the same channel | no (Windows may require the location permission) |
| PC traffic | counters of ONE interface — **no per-process attribution** (not reliable without admin/capture) | no |
| Path | pings with increasing TTL on demand; a loss on an intermediate hop with no loss at the destination is not a real loss | no |
| Saturation | 4 HTTPS streams to speed.cloudflare.com, idle/↓/recovery/↑/recovery, duration **and** volume caps, confirmation, cancellable | no |

Definitions: median; p95 = nearest rank; loss = unanswered ÷ sent (a system sleep is not a loss); **jitter = mean of |RTT(i) − RTT(i−1)|** between consecutive replies (a loss breaks the chain).

Conclusions are always **hypotheses** (evidence, counter-evidence, limits, confidence level, next test), never confirmed causes.

## TP‑Link router / QoS

The application **does not connect to the router** (no common documented TP‑Link API; it would need a password, which is never requested nor stored) and **changes no setting**. *Router & QoS* tab: exact model, hardware version, firmware, QoS state/type, limits and unit, priorities (durations), bandwidth rules, screenshots. It compares them with the measured throughput, tells **prioritisation from SQM**, never assumes SQM exists, proposes changes with justification and rollback, and provides a before/after protocol.

## Languages and code conventions

- Everything in the code base is English: identifiers, comments, JSON keys, stored values (`wifi`, `upstream`, `router_qos`, `high`…), commit messages.
- Text shown to people is localized (`en` default, `fr`): the server builds every sentence through `Loc.T(key, …)` (`src/NetworkAnalyzer/Loc.cs`, tables next to the code that uses them) and the page through its own dictionary. The language comes from `?lang=` or the `X-Lang` header.
- Session files hold codes, numbers and user input only — never a translated sentence — so a session recorded in one language reads in the other.
- Adding a language = adding a column to those tables; a test checks that every key exists in every language with the same placeholders.

## Development

```
dotnet test        # 135 tests: statistics, parsers, DNS, 11 simulated incident scenarios, localization, load test (fake server), report, API
```

Each delivery bumps `<Version>` in `src/NetworkAnalyzer/NetworkAnalyzer.csproj` (shown in the interface and the report).

## Known limits

Traffic = this PC only. Radio interference is not measured. The saturation test is indicative (server, Wi‑Fi, PC may cap the throughput). Routers sometimes handle ICMP at low priority.
