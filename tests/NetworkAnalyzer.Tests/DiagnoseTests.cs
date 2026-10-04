using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

public class ScenarioTests
{
    internal static readonly JsonSerializerOptions ReadableJson = new(Json.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static Analysis Run(string scn, AppConfig? cfg = null, Action<SimParams>? tweak = null)
    {
        cfg ??= scn == "saturation" ? new AppConfig { PlanDownMbps = 100, PlanUpMbps = 20 } : new AppConfig();
        return Diagnose.Analyze(Simulator.Make(scn, 7, tweak), cfg);
    }

    internal static List<string> Ids(Analysis a) => a.Hypotheses.Select(h => h.Id).ToList();
    internal static string Text(Analysis a) => JsonSerializer.Serialize(a, ReadableJson);

    [Fact]
    public void HealthySessionHasNoHypothesisAndFlagsNonNetworkLag()
    {
        var a = Run("healthy");
        Assert.Empty(a.Hypotheses);
        Assert.Equal(new[] { "none" }, a.Timeline.Where(i => i.Type == "lag").Select(i => i.Zone).ToArray());
        Assert.Contains(a.Summary, r => r.Contains("outside the network"));
        Assert.Contains(a.Unlikely, u => u.Id == "lan" && u.Reasons.Count > 0);
    }

    [Fact]
    public void SessionWithDownloadTrafficOnlyStillAnalyses()
    {
        var d = Simulator.Make("background", 7);
        d.Series.Remove("net:up_bps");
        var a = Diagnose.Analyze(d, new AppConfig());
        var text = Text(a);
        Assert.Contains("PC traffic: ↓", text);
        Assert.Contains("↑ — Mbps", text);
    }

    [Fact]
    public void WifiInstability()
    {
        var a = Run("wifi_unstable");
        Assert.Equal("lan", Ids(a)[0]);
        var top = a.Hypotheses[0];
        Assert.Equal("high", top.Level);
        Assert.Contains(top.Evidence, p => p.Contains("Weak Wi-Fi signal"));
        Assert.Contains(top.Evidence, p => p.Contains("access point"));
        Assert.DoesNotContain("isp", Ids(a));
        Assert.Contains("Ethernet", top.NextTest);
    }

    [Fact]
    public void WiredGatewayProblemPointsToRouterNotWifi()
    {
        var a = Run("wired_router");
        Assert.Equal("lan", Ids(a)[0]);
        Assert.Contains("router_qos", Ids(a));
        var lan = a.Hypotheses[0];
        Assert.Contains(lan.Counter, c => c.Contains("Wired connection"));
        Assert.DoesNotContain(lan.Evidence, p => p.Contains("Weak Wi-Fi"));
    }

    [Fact]
    public void BufferbloatDirectionAndLocation()
    {
        var a = Run("bufferbloat");
        Assert.Equal(new[] { "bufferbloat" }, Ids(a));
        var h = a.Hypotheses[0];
        Assert.True(h.Score >= 7);
        var up = h.Evidence.First(p => p.StartsWith("Upload"));
        Assert.Contains("+2", up);
        Assert.Contains(h.Evidence, p => p.Contains("gateway stays stable"));
        var bb = a.Bufferbloat!.Directions;
        Assert.True(bb["down"].Delta < 50);
        Assert.True(bb["up"].Delta > 200);
        Assert.Equal("D", bb["up"].Grade);    // +200 to +400 ms
        Assert.Equal("A", bb["down"].Grade);  // +5 to +30 ms
        Assert.InRange(bb["down"].Mbps!.Value, 280, 320);
    }

    [Fact]
    public void LoadTestThatNeverLoadedTheLineIsNotAVerdict()
    {
        var a = Run("bufferbloat", null, p => p.Load = new LoadSim { MbpsDown = 0.2, MbpsUp = 0.2 });
        Assert.DoesNotContain("bufferbloat", Ids(a));
        Assert.Contains(a.NotEvaluated, n => n.Id == "bufferbloat" && n.Reason.Contains("not evaluated"));
    }

    [Fact]
    public void PhaseWithTooFewLatencySamplesIsNotAVerdict()
    {
        var d = Simulator.Make("bufferbloat");
        double a0 = Simulator.T0 + 65, b0 = Simulator.T0 + 75;  // download phase: keep only 2 samples after the ramp-up
        d.Series = d.Series.ToDictionary(kv => kv.Key, kv => kv.Key.StartsWith("ping:") ? kv.Value.Where(s => !(a0 <= s.T && s.T <= b0)).ToList() : kv.Value);
        var bb = Diagnose.Analyze(d, new AppConfig()).Bufferbloat!.Directions;
        Assert.False(bb["down"].Valid);
        Assert.True(bb["up"].Valid);
    }

    [Fact]
    public void BufferbloatOnWifiIsDowngradedAndQualified()
    {
        double eth = Run("bufferbloat").Hypotheses[0].Score;
        var wifi = Run("bufferbloat", null, p => p.Link = "wifi").Hypotheses[0];
        Assert.True(wifi.Score < eth);
        Assert.Contains(wifi.Limits, l => l.Contains("Wi-Fi"));
    }

    [Fact]
    public void LocalLinkSaturationIsNotBlamedOnTheLine()
    {
        var a = Run("bufferbloat", null, p => p.Load = new LoadSim { DownDelta = 150, UpDelta = 150, GwDown = 140, GwUp = 140 });
        var bloat = a.Hypotheses.Where(h => h.Id == "bufferbloat").ToList();
        Assert.True(bloat.Count == 0 || bloat[0].Score < 4);
        var lan = a.Hypotheses.Where(h => h.Id == "lan").ToList();
        Assert.True(lan.Count > 0 && lan[0].Evidence.Any(p => p.Contains("also rises") || p.Contains("saturates")));
    }

    [Fact]
    public void IspUpstreamEpisodes()
    {
        var a = Run("isp");
        Assert.Equal("isp", Ids(a)[0]);
        Assert.Equal("medium", a.Hypotheses[0].Level);
        Assert.DoesNotContain("lan", Ids(a));
        Assert.Equal(new[] { "upstream" }, a.Timeline.Where(i => i.Type is "lag" or "episode").Select(i => i.Zone!).Distinct().ToArray());
    }

    [Fact]
    public void SingleDestinationIsAPathNotTheIsp()
    {
        var h = Run("isp_single_path").Hypotheses[0];
        Assert.Equal(("isp", "low"), (h.Id, h.Level));
        Assert.Contains(h.Evidence, p => p.Contains("ONE Internet destination"));
    }

    [Fact]
    public void CustomTargetOnly()
    {
        var a = Run("custom_only");
        Assert.Contains(a.Hypotheses[0].Evidence, p => p.Contains("custom destination"));
        Assert.Equal("low", a.Hypotheses[0].Level);
    }

    [Fact]
    public void Dns()
    {
        var a = Run("dns");
        Assert.Equal("dns", Ids(a)[0]);
        Assert.Contains(a.Hypotheses[0].Evidence, p => p.Contains("1.1.1.1"));
        Assert.DoesNotContain("isp", Ids(a));
        Assert.Equal(new[] { "dns" }, a.Timeline.Where(i => i.Type == "lag").Select(i => i.Zone).ToArray());
    }

    [Fact]
    public void BackgroundTrafficDoesNotBecomeAnIspAccusation()
    {
        var a = Run("background");
        Assert.Equal(new[] { "background" }, Ids(a));
        var lag = a.Timeline.First(i => i.Type == "lag");
        Assert.Contains("WARNING", lag.ZoneText);
        var h = a.Hypotheses[0];
        Assert.Contains(h.Limits, l => l.Contains("per-process"));
        Assert.Contains("Task Manager", h.NextTest);
    }

    [Theory]
    [InlineData(6.96, 7.0, "high")]
    [InlineData(3.96, 4.0, "medium")]
    [InlineData(3.94, 3.9, "low")]
    [InlineData(12.0, 10.0, "high")]
    public void LevelFollowsTheDisplayedScore(double raw, double shown, string level)
    {
        var h = new Hypothesis { Score = raw };
        Diagnose.Finalise(h);
        Assert.Equal(shown, h.Score);
        Assert.Equal(level, h.Level);
    }

    [Fact]
    public void SnapshotConfigDoesNotMixInLiveTargetSettings()
    {
        var d = Simulator.Make("healthy", 7);
        d.Meta.CfgSnapshot = new ConfigSnapshot { PlanDownMbps = 100, PlanUpMbps = 20 };
        var live = new AppConfig { CustomTarget = "changed.example.net", GatewayOverride = "10.9.9.9", PlanDownMbps = 500 };
        var cfg = Diagnose.ConfigFor(d, live);
        Assert.Equal(100, cfg.PlanDownMbps);
        Assert.Equal(new AppConfig().CustomTarget, cfg.CustomTarget);
        Assert.Equal(new AppConfig().GatewayOverride, cfg.GatewayOverride);
    }

    [Fact]
    public void ComparisonMetricsIgnoreAGatewayThatDoesNotAnswerIcmp()
    {
        var a = Run("healthy", null, p => p.IcmpBlocked.Add("gateway"));
        Assert.Null(a.Metrics.GwLoss);
        Assert.Null(a.Metrics.GwP95);
        Assert.NotNull(a.Metrics.InetP95);
    }

    [Fact]
    public void EpisodeListIsCappedWithAWarning()
    {
        foreach (var lang in new[] { "en", "fr" })
            using (Loc.Scope(lang))
            {
                var a = Run("healthy", null, p => { p.Minutes = 15; for (int i = 0; i < 50; i++) p.Events.Add(new("inet_all", 10 + 16 * i, 15 + 16 * i)); });
                Assert.Equal(Th.MaxEpisodes, a.Timeline.Count(i => i.Type == "episode"));
                Assert.True(a.GeneralLimits.Any(l => l.Contains("first 40") || l.Contains("40 premiers")), string.Join(" / ", a.GeneralLimits));
                Assert.DoesNotContain("‹", string.Join(" ", a.GeneralLimits));
            }
    }

    [Fact]
    public void IspCounterEvidenceIsNotClaimedWhenUpstreamEpisodesWereSetAsideAsPcBusy()
    {
        var a = Run("background");
        Assert.Contains(a.Timeline, i => i.Zone is "upstream" or "path" && i.ZoneText.Contains("WARNING"));
        var isp = a.Unlikely.FirstOrDefault(u => u.Id == "isp");
        Assert.True(isp is null || !isp.Reasons.Any(r => r.Contains("No episode degrades the Internet")));
    }

    [Fact]
    public void IdleTrafficIsTheSumOfBothDirectionsPerSecond()
    {
        // Bursty background download in the idle phase (3 Mbps in 60 % of the seconds, nothing up):
        // the median of the mixed down+up values is 0, the median of the per-second sum is 3.
        var d = Simulator.Make("bufferbloat", 7);
        double t0 = Simulator.T0 + 30, t1 = Simulator.T0 + 40;
        foreach (var name in new[] { "net:down_bps", "net:up_bps" })
            d.Series[name] = d.Series[name].Select(s =>
                s.T < t0 || s.T > t1 ? s : s with { V = name == "net:down_bps" && (int)(s.T - t0) % 5 < 3 ? 3e6 : 0 }).ToList();
        var a = Diagnose.Analyze(d, new AppConfig());
        Assert.Contains("already exchanges 3.0 Mbps", Text(a));
    }

    [Fact]
    public void SaturationNeedsKnownCapacity()
    {
        var a = Run("saturation");
        Assert.Equal("saturation", Ids(a)[0]);
        Assert.Contains(a.Hypotheses[0].Evidence, p => p.Contains('%'));
        var unknown = Run("saturation", new AppConfig());
        Assert.NotEqual("saturation", Ids(unknown).FirstOrDefault());
    }

    [Fact]
    public void IcmpBlockedTargetIsExplicitAndExcluded()
    {
        var a = Run("icmp_blocked");
        var row = a.Stats.Targets.First(t => t.Id == "custom");
        Assert.Equal("no_response", row.State);
        Assert.Contains("does not answer ICMP", row.StateText);
        Assert.Contains(a.Summary, r => r.Contains("Custom (game.example.net)") && r.Contains("does not answer ICMP") && r.Contains(":443"));
        Assert.Empty(a.Hypotheses);
    }

    [Fact]
    public void InsufficientData()
    {
        var d = Simulator.Make("healthy");
        d.Series = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => s.T < Simulator.T0 + 30).ToList());  // 30 s only
        var a = Diagnose.Analyze(d, new AppConfig());
        Assert.Contains(a.Summary, r => r.Contains("Not enough data"));
        Assert.DoesNotContain(a.Summary, r => r.Contains("No clear degradation"));
    }

    [Fact]
    public void TotalOutageOrNoIcmpAtAll()
    {
        var a = Diagnose.Analyze(Simulator.Make("healthy", 7, p => p.IcmpBlocked.AddRange(new[] { "gateway", "cloudflare", "google", "quad9", "custom" })), new AppConfig());
        Assert.Contains(a.Summary, r => r.Contains("No target answered"));
        Assert.Empty(a.Hypotheses);
    }

    [Fact]
    public void EmptySessionDoesNotCrash()
    {
        var a = Diagnose.Analyze(new SessionData { Id = 1, Started = 1000 }, new AppConfig());
        Assert.Equal(new[] { "No measurement recorded." }, a.Summary.ToArray());
    }

    [Fact]
    public void TracerouteEvidenceAndIcmpRateLimitCaveat()
    {
        var d = Simulator.Make("isp");
        d.Traces = new()
        {
            new TraceRec { T = Simulator.T0 + 320, Target = "1.1.1.1", Data = new TraceResult { Analysis = new TraceAnalysis { Step = new TraceStep { Hop = 4, Ip = "80.2.2.2", FromMs = 15, ToMs = 85 } } } },
            new TraceRec { T = Simulator.T0 + 330, Target = "8.8.8.8", Data = new TraceResult { Analysis = new TraceAnalysis { IntermediateLoss = new() { 3 } } } },
        };
        var h = Diagnose.Analyze(d, new AppConfig()).Hypotheses.First(x => x.Id == "isp");
        Assert.Contains(h.Evidence, p => p.Contains("hop 4"));
        Assert.Contains(h.Counter, c => c.Contains("NOT found again") && c.Contains("rate-limiting"));
    }

    [Fact]
    public void GapMarksAreNotLosses()
    {
        var d = Simulator.Make("healthy");
        int baseN = Diagnose.Analyze(d, new AppConfig()).Stats.Targets[0].Stats!.N;
        double a = Simulator.T0 + 300, b = Simulator.T0 + 600;  // a 5-minute sleep: samples removed, as the recorder would
        d.Series = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => !(a <= s.T && s.T <= b)).ToList());
        d.Marks.Add(new Mark { T = a, Kind = "gap" });
        var r = Diagnose.Analyze(d, new AppConfig());
        var st = r.Stats.Targets[0].Stats!;
        Assert.Equal(baseN - 301, st.N);  // these 301 seconds count neither as replies nor as losses
        Assert.True(st.LossPct < 2);
        Assert.Contains(r.Timeline, i => i.Type == "gap" && i.ZoneText.Contains("System pause"));
    }
}

public class InvariantTests
{
    [Fact]
    public void EveryHypothesisHasEvidenceConfidenceLimitsNextTest()
    {
        foreach (var scn in Simulator.Scenarios)
            foreach (var h in ScenarioTests.Run(scn).Hypotheses)
            {
                Assert.True(h.Evidence.Count > 0, $"{scn}/{h.Id} evidence");
                Assert.Contains(h.Level, new[] { "low", "medium", "high" });
                Assert.True(h.Limits.Count > 0, $"{scn}/{h.Id} limits");
                Assert.False(string.IsNullOrEmpty(h.NextTest), $"{scn}/{h.Id} next test");
                Assert.True(h.Actions.Count > 0, $"{scn}/{h.Id} actions");
            }
    }

    [Fact]
    public void NeverPresentsHypothesisAsConfirmedCause()
    {
        var bad = new Regex(@"cause is (certain|confirmed)|\bis confirmed\b|proven that|it is certain|certain cause", RegexOptions.IgnoreCase);
        foreach (var scn in Simulator.Scenarios)
        {
            var a = ScenarioTests.Run(scn);
            Assert.False(bad.IsMatch(ScenarioTests.Text(a)), scn);
            if (a.Hypotheses.Count > 0) Assert.Contains(a.Summary, r => r.Contains("not confirmed causes"));
        }
    }

    [Fact]
    public void GatewayStableExcludesLanHypothesis()
    {
        foreach (var scn in new[] { "isp", "dns", "bufferbloat" }) Assert.DoesNotContain("lan", ScenarioTests.Ids(ScenarioTests.Run(scn)));
    }

    [Fact]
    public void Deterministic() => Assert.Equal(ScenarioTests.Text(ScenarioTests.Run("wifi_unstable")), ScenarioTests.Text(ScenarioTests.Run("wifi_unstable")));

    [Fact]
    public void JsonNamesMatchWhatTheInterfaceReads()
    {
        // The web page reads these exact snake_case keys.
        var json = JsonSerializer.Serialize(ScenarioTests.Run("bufferbloat"), Json.Options) + JsonSerializer.Serialize(ScenarioTests.Run("isp"), Json.Options);
        foreach (var key in new[] { "\"summary\"", "\"unlikely\"", "\"not_evaluated\"", "\"general_limits\"", "\"next_test\"", "\"zone_text\"", "\"evidence\"", "\"counter\"", "\"level\"", "\"title\"",
                                    "\"state_text\"", "\"loss_pct\"", "\"gw_delta\"", "\"idle_med\"", "\"load_p95\"", "\"p95\"", "\"t0\"", "\"inet_p95\"", "\"bloat_up\"", "\"down_mbps\"" })
            Assert.Contains(key, json);
    }

    [Fact]
    public void StoredFilesNeverContainDisplayText()
    {
        // Session files hold codes, numbers and user input only: no sentence, no translated label.
        var dir = Tmp.Dir();
        var store = new SessionStore(dir);
        foreach (var lang in Loc.Languages)
            using (Loc.Scope(lang))
            {
                int id = store.SaveComplete(Simulator.Make("wifi_unstable", 7, p => p.Minutes = 1));
                var meta = File.ReadAllText(Path.Combine(dir, "sessions", $"{id}.meta.json"));
                Assert.DoesNotContain("Gateway", meta);
                Assert.DoesNotContain("Passerelle", meta);
                Assert.DoesNotContain("\"label\":\"Custom", meta);
                Assert.Contains("\"host\":\"192.168.0.1\"", meta);
            }
        // …yet what the API returns carries the label of the current language
        Assert.Contains("\"label\":\"Gateway (router)\"", Json.To(Simulator.Targets));
    }
}

public class LocalizationTests
{
    static readonly Regex Placeholder = new(@"\{(\d+)\}");

    [Fact]
    public void EveryKeyExistsInBothLanguagesWithTheSamePlaceholders()
    {
        var keys = Loc.Keys.ToList();
        Assert.True(keys.Count > 300);
        foreach (var k in keys)
        {
            var (en, fr) = Loc.Raw(k);
            Assert.False(string.IsNullOrWhiteSpace(en), $"{k}: English missing");
            Assert.False(string.IsNullOrWhiteSpace(fr), $"{k}: French missing");
            Assert.Equal(Placeholder.Matches(en).Select(m => m.Value).OrderBy(x => x), Placeholder.Matches(fr).Select(m => m.Value).OrderBy(x => x));
        }
    }

    [Fact]
    public void TheP95DefinitionMatchesTheNearestRankCalculation()
    {
        // nearest rank: the value at rank ceil(0.95 n); at least 95 % of the replies are <= it (not strictly "faster")
        var v = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        var p95 = Stats.Percentile(v, 95)!.Value;
        Assert.Equal(19.0, p95);
        Assert.True(v.Count(x => x <= p95) >= 0.95 * v.Count);
        Assert.Contains("at least 95% of the replies are at most this value", Loc.Raw("def.p95").En);
        Assert.Contains("au moins 95 % des réponses sont au plus égales à cette valeur", Loc.Raw("def.p95").Fr);
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NetworkAnalyzer", "wwwroot", "index.html"));
        Assert.DoesNotContain("are faster", html);
        Assert.DoesNotContain("sont plus rapides", html);
    }

    [Fact]
    public void EnglishWritesPercentWithoutASpace()
    {
        // French puts a space before %, English does not: "5% loss"
        var spaced = new Regex(@"(\{\d+\}|\d) %");
        var bad = Loc.Keys.Where(k => spaced.IsMatch(Loc.Raw(k).En)).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void AMissingKeyIsRecordedOncePerKeyAndStillVisible()
    {
        var key = "test.missing." + Guid.NewGuid().ToString("N");
        Assert.Equal($"‹{key}›", Loc.T(key));
        Assert.Equal($"‹{key}›", Loc.T(key));
        Assert.Single(Loc.MissingKeys, k => k == key);
    }

    [Fact]
    public void EveryTranslationKeyWrittenInTheSourceIsRegistered()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "NetworkAnalyzer.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var literal = new Regex("""\b(?:Loc\.)?(?:T|In)\((?:"[a-z]{2}",\s*)?"([a-z][a-z0-9_]*(?:\.[a-z0-9_]+)+)"[,)]""");
        var registered = new HashSet<string>(Loc.Keys);
        var unknown = new List<string>();
        int seen = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir!, "src"), "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            foreach (Match m in literal.Matches(File.ReadAllText(f)))
            {
                seen++;
                if (!registered.Contains(m.Groups[1].Value)) unknown.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
            }
        Assert.True(seen > 150, $"only {seen} call sites matched: the pattern no longer sees the code");
        Assert.Empty(unknown);
    }

    [Fact]
    public void FrenchTextUsesTheDecimalCommaAndEnglishTheDot()
    {
        // a raw double given to a template
        Assert.Contains("≥ 5,5 Mbps", Loc.In("fr", "d.zone.busy", 5.5));
        Assert.Contains("≥ 5.5 Mbps", Loc.In("en", "d.zone.busy", 5.5));
        // numbers formatted by the engine before they reach the template
        var r = new RouterConfig { QosType = "bandwidth_limit", Unit = "Mbps" };
        using (Loc.Scope("fr")) Assert.Contains("environ 0,46 Mbps", RouterQos.Propose(r, (null, 0.5), 200).First().Change);
        using (Loc.Scope("en")) Assert.Contains("about 0.46 Mbps", RouterQos.Propose(r, (null, 0.5), 200).First().Change);
        // and the report's chart axis
        string Axis(string lang) { using (Loc.Scope(lang)) return Report.SvgChart(new() { new Report.ChartSeries { Name = "x", Pts = new() { (1, 0.4), (2, 0.2) } } }, 0, 10); }
        Assert.Contains(">0,43<", Axis("fr"));
        Assert.Contains(">0.43<", Axis("en"));
    }

    [Fact]
    public async Task ScopesOnDifferentTasksNeverSeeEachOther()
    {
        // 400 tasks at once, alternating French scopes and the default: each one must always read its own language,
        // which is what lets xUnit run test classes in parallel without a shared-state collection
        var work = Enumerable.Range(0, 400).Select(i => Task.Run(async () =>
        {
            bool fr = i % 2 == 0;
            IDisposable? scope = fr ? Loc.Scope("fr") : null;
            try
            {
                for (int k = 0; k < 20; k++)
                {
                    await Task.Yield();
                    if (Loc.Lang != (fr ? "fr" : "en") || Loc.T("phase.download") != (fr ? "Téléchargement" : "Download")) return false;
                }
                return true;
            }
            finally { scope?.Dispose(); }
        })).ToList();
        Assert.All(await Task.WhenAll(work), ok => Assert.True(ok));
        Assert.Equal("en", Loc.Lang);   // and this test's own context was left untouched
    }

    [Fact]
    public void BothLanguagesNameTheSameInvalidFields()
    {
        // SaveConfig refuses a bad QoS TYPE, a bad unit or a bad SQM answer: both messages must say so
        var (en, fr) = Loc.Raw("err.invalid_qos_value");
        Assert.Equal("Invalid QoS type, unit or SQM answer.", en);
        Assert.Equal("Type de QoS, unité ou réponse SQM invalide.", fr);
    }

    [Fact]
    public void NoActionMessageReadsAsEnglish()
    {
        Assert.Equal("No targeted action: run a new monitoring session during a lag episode.", Loc.Raw("rep.noactions").En);
    }

    [Fact]
    public void TextUsesTheOrdinaryHyphenSoSearchFindsWiFi()
    {
        foreach (var k in Loc.Keys)
        {
            var (en, fr) = Loc.Raw(k);
            Assert.False((en + fr).Contains('‑'), $"{k}: non-breaking hyphen");
        }
    }

    [Fact]
    public void ExampleDotComIsDescribedAsReservedForDocumentation()
    {
        var (en, fr) = Loc.Raw("dnsr.limit2");
        Assert.DoesNotContain("reserved for this purpose", en);
        Assert.Contains("documentation", en);
        Assert.Contains("documentation", fr);
    }

    [Fact]
    public void NeighbourCountIsWrittenForOneNetworkToo()
    {
        var (en, fr) = Loc.Raw("lan.ev.neighbors");
        Assert.Contains("network(s)", en);
        Assert.Contains("réseau(x) voisin(s)", fr);
    }

    [Fact]
    public void BusyThresholdInTextComesFromTheEngine()
    {
        var (en, fr) = Loc.Raw("bg.ev.hot");
        Assert.Contains("{1}", en);
        Assert.Contains("{1}", fr);
        Assert.DoesNotContain("5 Mbps", en + fr);
    }

    [Fact]
    public void OneNotationForTheBitRateUnit()
    {
        foreach (var k in Loc.Keys)
        {
            var (en, fr) = Loc.Raw(k);
            Assert.False(en.Contains("Mbit/s") || fr.Contains("Mbit/s"), $"{k}: use Mbps");
        }
    }

    [Fact]
    public void FrenchTextHasNoKnownAgreementMistakes()
    {
        // wording mistakes found in review: add the faulty phrase here when one is fixed
        var faulty = new[] { "destinations et la passerelle sont sains", "les autres et la passerelle restent sains" };
        foreach (var k in Loc.Keys)
        {
            var fr = Loc.Raw(k).Fr;
            foreach (var f in faulty) Assert.False(fr.Contains(f), $"{k}: '{f}'");
        }
    }

    [Fact]
    public void EnglishIsTheDefaultAndScopeRestoresTheLanguage()
    {
        Assert.Equal("en", Loc.Lang);
        Assert.Equal("Download", Loc.In("en", "phase.download"));
        Assert.Equal("Téléchargement", Loc.In("fr", "phase.download"));
        using (Loc.Scope("fr")) Assert.Equal("fr", Loc.Lang);
        Assert.Equal("en", Loc.Lang);
        Assert.Equal("fr", Loc.Normalize("fr-CA"));
        Assert.Equal("en", Loc.Normalize("de"));
        Assert.Equal("en", Loc.Normalize(null));
    }

    [Fact]
    public void AMissingKeyIsVisibleNotSilent() => Assert.Equal("‹no.such.key›", Loc.T("no.such.key"));

    [Fact]
    public void NoScenarioOutputContainsAMissingKeyInEitherLanguage()
    {
        foreach (var lang in Loc.Languages)
            using (Loc.Scope(lang))
                foreach (var scn in Simulator.Scenarios)
                {
                    var cfg = new AppConfig { PlanDownMbps = 100, PlanUpMbps = 20, Router = new RouterConfig { QosEnabled = true, QosType = "priority", LimitDown = 900, LimitUp = 900, BandwidthRules = { new BandwidthRule { Name = "Lounge", Down = 5 } }, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = "2 hours" } } } };
                    var d = Simulator.Make(scn);
                    var a = Diagnose.Analyze(d, cfg);
                    Assert.DoesNotContain("‹", ScenarioTests.Text(a));
                    Assert.DoesNotContain("‹", Report.Html(d, a, cfg));
                }
    }

    [Fact]
    public void FrenchOutputIsReallyFrench()
    {
        using (Loc.Scope("fr"))
        {
            var a = ScenarioTests.Run("wifi_unstable");
            Assert.Contains(a.Summary, r => r.Contains("Hypothèse la plus compatible"));
            Assert.Equal("Instabilité du Wi-Fi ou du réseau local", a.Hypotheses[0].Title);
            Assert.Equal("high", a.Hypotheses[0].Level);  // codes never change with the language
            Assert.Equal("local", a.Timeline.First(i => i.Type == "episode").Zone);
            Assert.Contains("Passerelle", a.Stats.Targets[0].Label);
            var html = Report.Html(Simulator.Make("wifi_unstable"), a);
            Assert.Contains("Rapport de diagnostic réseau", System.Net.WebUtility.HtmlDecode(html));
            Assert.Contains("<html lang='fr'>", html);
        }
    }

    [Fact]
    public void EnglishOutputIsReallyEnglish()
    {
        var a = ScenarioTests.Run("wifi_unstable");
        Assert.Equal("Wi-Fi or local network instability", a.Hypotheses[0].Title);
        Assert.Contains("Gateway", a.Stats.Targets[0].Label);
        var html = Report.Html(Simulator.Make("wifi_unstable"), a);
        Assert.Contains("Network diagnosis report", html);
        Assert.Contains("<html lang='en'>", html);
        Assert.DoesNotContain("Rapport", html);
    }

    [Fact]
    public void CsvAndJsonKeysAreEnglishWhateverTheLanguage()
    {
        var d = Simulator.Make("wifi_unstable");
        foreach (var lang in Loc.Languages)
            using (Loc.Scope(lang))
            {
                Assert.StartsWith("session,local_time,epoch_s,series,value,ok,info", Report.ExportCsv(d));
                var json = Report.ExportJson(d, Diagnose.Analyze(d, new AppConfig()));
                foreach (var k in new[] { "\"measurements\"", "\"marks\"", "\"traceroutes\"", "\"analysis\"", "\"definitions\"" }) Assert.Contains(k, json);
            }
    }

    [Fact]
    public void ComparisonVerdictsAreCodesWithATranslatedText()
    {
        using (Loc.Scope("fr"))
        {
            var rows = Diagnose.Compare(new[] { new Metrics { InetP95 = 100 }, new Metrics { InetP95 = 105 } }, new[] { new Metrics { InetP95 = 40 }, new Metrics { InetP95 = 42 } });
            Assert.Equal("improvement", rows[0].Verdict);
            Assert.Equal("amélioration", rows[0].VerdictText);
            Assert.Equal("Latence Internet p95 (ms)", rows[0].Metric);
        }
    }

    [Fact]
    public void EnvironmentNotesAreDerivedNotStored()
    {
        var env = SysInfo.Summarize(new List<AdapterInfo>
        {
            new() { Name = "vEthernet (External Network Switch)", Status = "Up", Kind = "virtual", Gw4 = "192.168.0.1", Ipv4 = { "192.168.0.50" } },
            new() { Name = "Ethernet", Status = "Up", Kind = "ethernet" },
        });
        Assert.Equal("Ethernet", env.Active!.BridgedPhysical);
        Assert.Contains(SysInfo.Notes(env), n => n.Contains("virtual interface") && n.Contains("Ethernet"));
        using (Loc.Scope("fr")) Assert.Contains(SysInfo.Notes(env), n => n.Contains("interface virtuelle"));
        Assert.DoesNotContain("virtual interface", Json.To(env));
    }

    [Fact]
    public void TraceNotesAreTranslatedAtDisplayTime()
    {
        var a = new TraceAnalysis { Reached = true, IntermediateLoss = { 5 }, Step = new TraceStep { Hop = 4, FromMs = 15, ToMs = 85 } };
        Assert.Contains(Probes.TraceNotes(a), n => n.Contains("hop 4"));
        using (Loc.Scope("fr")) Assert.Contains(Probes.TraceNotes(a), n => n.Contains("au saut 4"));
        Assert.Contains(Probes.TraceNotes(new TraceAnalysis(), hasHops: false), n => n.Contains("No readable hop"));
    }
}

public class RouterTests
{
    static readonly (double? Down, double? Up) Meas = (300.0, 30.0);

    static List<(string Sev, string Txt)> Kinds(RouterConfig r, double worst = 200) => RouterQos.Check(r, Meas, new AppConfig(), worst).Select(f => (f.Severity, f.Text)).ToList();

    [Theory]
    [InlineData("Kbit/s", 0.005)] [InlineData("Mb/s", 5.0)] [InlineData(" mbps ", 5.0)] [InlineData("Gbit/s", 5000.0)] [InlineData("Mbit/s", 5.0)] [InlineData("Mbps", 5.0)]
    public void CommonSpellingsOfTheUnitAreUnderstood(string unit, double expected) => Assert.Equal(expected, RouterQos.ToMbps(5, unit));

    [Fact]
    public void AnUnrecognisedUnitIsReportedNotSilentlyIgnored()
    {
        using var _ = Loc.Scope("en");
        var r = new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", LimitDown = 50, Unit = "furlongs" };
        var f = RouterQos.Check(r, (300.0, 30.0), new AppConfig(), 200);
        Assert.Contains(f, x => x.Severity == "info" && x.Text.Contains("furlongs"));
        var withRule = new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", BandwidthRules = { new BandwidthRule { Name = "Kids", Down = 5, Unit = "parsecs" } } };
        Assert.Contains(RouterQos.Check(withRule, (300.0, 30.0), new AppConfig(), 200), x => x.Severity == "info" && x.Text.Contains("parsecs"));
        Assert.DoesNotContain(RouterQos.Check(new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", LimitDown = 50, Unit = "Mbps" }, (300.0, 30.0), new AppConfig(), 200), x => x.Text.Contains("unit '"));
    }

    [Fact]
    public void CurrentValueShownInTheProposalAlwaysHasItsUnit()
    {
        using var _ = Loc.Scope("en");
        var r = new RouterConfig { QosType = "bandwidth_limit", LimitDown = 900, Unit = null! };   // a saved file may hold a null unit; ToMbps reads it as Mbps
        var p = RouterQos.Propose(r, (300.0, null), 120).First();
        Assert.Contains("900 Mbps", p.Justification);
        Assert.Contains("900 Mbps", p.Rollback);
    }

    [Fact]
    public void SqmProposalMatchesTheQosTypeAndTheModel()
    {
        using var _ = Loc.Scope("en");
        List<Proposal> Props(RouterConfig r) => RouterQos.Propose(r, (null, null), 120);
        bool IsSqm(Proposal p) => p.Change.Contains("SQM");
        var prio = Props(new RouterConfig { QosEnabled = true, QosType = "priority" }).Single(IsSqm);
        Assert.Contains("Prioritisation alone", prio.Justification);
        foreach (var other in new[] { new RouterConfig { QosEnabled = false }, new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit" }, new RouterConfig() })
        {
            var why = Props(other).Single(IsSqm).Justification;
            Assert.DoesNotContain("Prioritisation", why);
            Assert.Contains("120", why);   // the measured increase it is based on
        }
        Assert.DoesNotContain(Props(new RouterConfig { QosEnabled = true, QosType = "priority", SqmAvailable = "no" }), IsSqm);   // the model has none: no advice to enable it
    }

    [Theory]
    [InlineData(324.0, false)]   // 108 % of the measured 300: Check says consistent, so nothing is proposed
    [InlineData(345.0, true)]    // 115 %: Check says it limits nothing, so a lower limit is proposed
    public void CheckAndProposeAgreeAroundTheNoEffectThreshold(double limit, bool proposes)
    {
        var r = new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", LimitDown = limit, Unit = "Mbps" };
        var meas = ((double?)300.0, (double?)null);
        var down = RouterQos.Check(r, meas, new AppConfig(), 200).Where(f => f.Text.Contains("Mbps")).ToList();
        var noEffect = down.Any(f => f.Severity != "ok");
        var proposed = RouterQos.Propose(r, meas, 200).Any(p => p.Change.Contains("Download") || p.Change.Contains("download"));
        Assert.Equal(proposes, noEffect);
        Assert.Equal(noEffect, proposed);
    }

    [Fact]
    public void ProposedLimitKeepsItsPrecisionOnSlowLines()
    {
        var r = new RouterConfig { QosType = "bandwidth_limit", Unit = "Mbps" };
        using var _ = Loc.Scope("en");
        var slowUp = RouterQos.Propose(r, (null, 0.5), 200).Select(p => p.Change).First();
        Assert.Contains("about 0.46 Mbps", slowUp);      // 92 % of 0.5, not "0"
        Assert.Contains("0.5 Mbps)", slowUp);            // the measured value is not shown as "0" either
        Assert.Contains("about 0.92 Mbps", RouterQos.Propose(r, (null, 1.0), 200).Select(p => p.Change).First());
        Assert.Contains("about 276 Mbps", RouterQos.Propose(r, (300.0, null), 200).Select(p => p.Change).First());
    }

    [Fact]
    public void FrenchThroughputTakesTheMasculineDirection()
    {
        var r = new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", LimitDown = 50, Unit = "Mbps" };   // measured 300 Mbps > limit
        using (Loc.Scope("en")) Assert.Contains(Kinds(r), k => k.Txt.StartsWith("Measured download throughput"));
        using (Loc.Scope("fr"))
        {
            var fr = Kinds(r).Select(k => k.Txt).ToList();
            Assert.Contains(fr, t => t.StartsWith("Débit descendant mesuré"));
            Assert.DoesNotContain(fr, t => t.Contains("Débit descendante") || t.Contains("Débit montante"));
        }
    }

    [Theory]
    [InlineData(null)] [InlineData(false)] [InlineData(true)]
    public void AMeasuredRateAboveTheLimitIsReportedAsALimitThatDoesNotApplyWhateverTheQosState(bool? qos)
    {
        var r = new RouterConfig { QosEnabled = qos, QosType = "bandwidth_limit", LimitDown = 50, Unit = "Mbps" };   // 300 Mbps measured through a 50 Mbps limit
        using var _ = Loc.Scope("en");
        var texts = Kinds(r).Select(k => k.Txt).ToList();
        Assert.Contains(texts, t => t.Contains("EXCEEDS the configured limit"));
        Assert.DoesNotContain(texts, t => t.Contains("needlessly throttles"));
    }

    [Fact]
    public void DirectionIsCapitalisedOnlyWhereItOpensTheSentence()
    {
        // a limit far below the PLAN (nothing measured yet): the 'throttles needlessly' message
        var r = new RouterConfig { QosType = "bandwidth_limit", LimitDown = 50, Unit = "Mbps" };
        List<(string Sev, string Txt)> Kinds2() => RouterQos.Check(r, (null, null), new AppConfig { PlanDownMbps = 300 }, 200).Select(f => (f.Severity, f.Text)).ToList();
        using (Loc.Scope("en")) Assert.Contains(Kinds2(), k => k.Txt.StartsWith("Download limit"));
        using (Loc.Scope("fr"))
        {
            var fr = Kinds2().Select(k => k.Txt).ToList();
            Assert.Contains(fr, t => t.StartsWith("Limite descendante"));
            Assert.DoesNotContain(fr, t => t.Contains("Descendante"));
        }
    }

    [Fact]
    public void UnitConversion()
    {
        Assert.Equal(0.5, RouterQos.ToMbps(500, "Kbps"));
        Assert.Equal(1000.0, RouterQos.ToMbps(1, "Gbps"));
        Assert.Null(RouterQos.ToMbps(null, "Mbps"));
        Assert.Null(RouterQos.ToMbps(5, "Tbps"));
    }

    [Fact]
    public void LimitAboveRealSpeedDoesNothing()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", LimitDown = 500, LimitUp = 50 }), k => k.Txt.Contains("limits nothing"));

    [Fact]
    public void MeasuredAboveLimitMeansLimitNotApplied()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, QosType = "bandwidth_limit", LimitDown = 100 }), k => k.Sev == "problem" && k.Txt.Contains("EXCEEDS"));

    [Fact]
    public void UnitConfusionDetected()
    {
        Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, LimitDown = 300000 }), k => k.Sev == "problem" && k.Txt.Contains("unit mix-up"));
        Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, LimitDown = 0.3 }), k => k.Txt.Contains("unit mix-up"));
    }

    [Fact]
    public void CoherentLimitIsReportedOk() => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, LimitDown = 280 }), k => k.Sev == "ok");

    [Fact]
    public void PriorityIsNotQueueManagement()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, QosType = "priority" }), k => k.Txt.Contains("priority-based") && k.Txt.Contains("queue"));

    [Fact]
    public void SqmIsNeverAssumed() => Assert.Contains(Kinds(new RouterConfig { QosEnabled = false }), k => k.Txt.Contains("SQM") && k.Txt.Contains("unknown"));

    [Fact]
    public void TemporaryPriorityNoted()
    {
        Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = "2 hours" } } }), k => k.Txt.Contains("PC") && k.Txt.Contains("2 hours"));
        Assert.DoesNotContain(Kinds(new RouterConfig { QosEnabled = true, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = "always" } } }), k => k.Txt.Contains("\"PC\""));
        Assert.DoesNotContain(Kinds(new RouterConfig { QosEnabled = true, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = "toujours" } } }), k => k.Txt.Contains("\"PC\""));
    }

    [Fact]
    public void BandwidthRuleBelowMeasuredSpeedIsFlagged()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, BandwidthRules = { new BandwidthRule { Name = "Lounge", Down = 50, Up = 5 } } }), k => k.Sev == "warning" && k.Txt.Contains("Lounge"));

    [Fact]
    public void ProposalsHaveJustificationAndRollbackAndNothingAutomatic()
    {
        var rc = new RouterConfig { QosEnabled = true, QosType = "priority", LimitDown = 500, LimitUp = 50 };
        var props = RouterQos.Propose(rc, Meas, 200);
        Assert.NotEmpty(props);
        Assert.Contains("276", props[0].Change);  // 92 % of 300
        foreach (var p in props) { Assert.NotEmpty(p.Justification); Assert.NotEmpty(p.Rollback); }
        Assert.Contains("No setting is changed", RouterQos.Analysis(rc, Meas, new AppConfig(), 200).Reminder);
        using (Loc.Scope("fr")) Assert.Contains("Aucun réglage n'est modifié", RouterQos.Analysis(rc, Meas, new AppConfig(), 200).Reminder);
    }

    [Fact]
    public void NoProposalWithoutBloatOrConfig()
    {
        Assert.Empty(RouterQos.Propose(null, Meas, 200));
        Assert.Empty(RouterQos.Propose(new RouterConfig { QosEnabled = true, LimitDown = 280 }, Meas, 5));
    }

    [Fact]
    public void RouterRuleIntegration()
    {
        var cfg = new AppConfig { Router = new RouterConfig { QosEnabled = true, QosType = "priority", LimitDown = 900, LimitUp = 900 } };
        var a = ScenarioTests.Run("bufferbloat", cfg);
        var r = a.Hypotheses.Where(h => h.Id == "router_qos").ToList();
        Assert.True(r.Count > 0 && r[0].Findings!.Count > 0);
        Assert.Contains(r[0].Evidence, p => p.Contains("limits nothing") || p.Contains("priority-based"));
    }

    [Fact]
    public void ProtocolHasSixSteps() => Assert.Equal(6, RouterQos.Analysis(null, Meas, new AppConfig(), 0).Protocol.Count);
}

public class CompareTests
{
    static Metrics M(double v) => new() { InetP95 = v, BloatUp = v * 2 };

    [Fact]
    public void ImprovementBeyondVariability()
    {
        var rows = Diagnose.Compare(new[] { M(100), M(105), M(95) }, new[] { M(40), M(45), M(42) });
        Assert.All(rows, r => Assert.Equal("improvement", r.Verdict));
    }

    [Fact]
    public void DifferenceWithinVariabilityIsIndistinct()
        => Assert.All(Diagnose.Compare(new[] { M(100), M(140) }, new[] { M(110), M(125) }), r => Assert.Equal("indistinct", r.Verdict));

    [Fact]
    public void SingleMeasureIsOnlyIndicative()
        => Assert.All(Diagnose.Compare(new[] { M(100) }, new[] { M(40) }), r => Assert.Equal("indicative", r.Verdict));

    [Fact]
    public void DegradationAndThroughputDirection()
    {
        var rows = Diagnose.Compare(new[] { new Metrics { DownMbps = 300 }, new Metrics { DownMbps = 305 } }, new[] { new Metrics { DownMbps = 100 }, new Metrics { DownMbps = 102 } });
        Assert.Equal("degradation", rows[0].Verdict);
    }
}
