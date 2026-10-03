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
        var a = Run("sain");
        Assert.Empty(a.Hypotheses);
        Assert.Equal(new[] { "aucune" }, a.Timeline.Where(i => i.Type == "lag").Select(i => i.Zone).ToArray());
        Assert.Contains(a.Resume, r => r.Contains("hors réseau"));
        Assert.Contains(a.PeuProbables, u => u.Id == "lan" && u.Raisons.Count > 0);
    }

    [Fact]
    public void WifiInstability()
    {
        var a = Run("wifi_instable");
        Assert.Equal("lan", Ids(a)[0]);
        var top = a.Hypotheses[0];
        Assert.Equal("élevée", top.Niveau);
        Assert.Contains(top.Preuves, p => p.Contains("Signal Wi‑Fi faible"));
        Assert.Contains(top.Preuves, p => p.Contains("point d'accès"));
        Assert.DoesNotContain("fai", Ids(a));
        Assert.Contains("Ethernet", top.ProchainTest);
    }

    [Fact]
    public void WiredGatewayProblemPointsToRouterNotWifi()
    {
        var a = Run("filaire_routeur");
        Assert.Equal("lan", Ids(a)[0]);
        Assert.Contains("routeur_qos", Ids(a));
        var lan = a.Hypotheses[0];
        Assert.Contains(lan.Contre, c => c.Contains("filaire"));
        Assert.DoesNotContain(lan.Preuves, p => p.Contains("Wi‑Fi faible"));
    }

    [Fact]
    public void BufferbloatDirectionAndLocation()
    {
        var a = Run("bufferbloat");
        Assert.Equal(new[] { "bufferbloat" }, Ids(a));
        var h = a.Hypotheses[0];
        Assert.True(h.Score >= 7);
        var up = h.Preuves.First(p => p.StartsWith("Envoi"));
        Assert.Contains("+2", up);
        Assert.Contains(h.Preuves, p => p.Contains("passerelle reste stable"));
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
        Assert.Contains(a.NonEvalue, n => n.Id == "bufferbloat" && n.Raison.Contains("non évalué"));
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
        Assert.Contains(wifi.Limites, l => l.Contains("Wi‑Fi"));
    }

    [Fact]
    public void LocalLinkSaturationIsNotBlamedOnTheLine()
    {
        var a = Run("bufferbloat", null, p => p.Load = new LoadSim { DownDelta = 150, UpDelta = 150, GwDown = 140, GwUp = 140 });
        var bloat = a.Hypotheses.Where(h => h.Id == "bufferbloat").ToList();
        Assert.True(bloat.Count == 0 || bloat[0].Score < 4);
        var lan = a.Hypotheses.Where(h => h.Id == "lan").ToList();
        Assert.True(lan.Count > 0 && lan[0].Preuves.Any(p => p.Contains("passerelle monte aussi") || p.Contains("sature")));
    }

    [Fact]
    public void IspUpstreamEpisodes()
    {
        var a = Run("fai");
        Assert.Equal("fai", Ids(a)[0]);
        Assert.Equal("moyenne", a.Hypotheses[0].Niveau);
        Assert.DoesNotContain("lan", Ids(a));
        Assert.Equal(new[] { "amont" }, a.Timeline.Where(i => i.Type is "lag" or "episode").Select(i => i.Zone!).Distinct().ToArray());
    }

    [Fact]
    public void SingleDestinationIsAPathNotTheIsp()
    {
        var h = Run("fai_un_seul").Hypotheses[0];
        Assert.Equal(("fai", "faible"), (h.Id, h.Niveau));
        Assert.Contains(h.Preuves, p => p.Contains("UNE destination"));
    }

    [Fact]
    public void CustomTargetOnly()
    {
        var a = Run("custom_seul");
        Assert.Contains(a.Hypotheses[0].Preuves, p => p.Contains("personnalisée"));
        Assert.Equal("faible", a.Hypotheses[0].Niveau);
    }

    [Fact]
    public void Dns()
    {
        var a = Run("dns");
        Assert.Equal("dns", Ids(a)[0]);
        Assert.Contains(a.Hypotheses[0].Preuves, p => p.Contains("1.1.1.1"));
        Assert.DoesNotContain("fai", Ids(a));
        Assert.Equal(new[] { "dns" }, a.Timeline.Where(i => i.Type == "lag").Select(i => i.Zone).ToArray());
    }

    [Fact]
    public void BackgroundTrafficDoesNotBecomAnIspAccusation()
    {
        var a = Run("fond");
        Assert.Equal(new[] { "fond" }, Ids(a));
        var lag = a.Timeline.First(i => i.Type == "lag");
        Assert.Contains("ATTENTION", lag.ZoneTexte);
        var h = a.Hypotheses[0];
        Assert.Contains(h.Limites, l => l.Contains("processus") || l.Contains("attribution"));
        Assert.Contains("Gestionnaire des tâches", h.ProchainTest);
    }

    [Fact]
    public void SaturationNeedsKnownCapacity()
    {
        var a = Run("saturation");
        Assert.Equal("saturation", Ids(a)[0]);
        Assert.Contains(a.Hypotheses[0].Preuves, p => p.Contains("92") || p.Contains('%'));
        var unknown = Run("saturation", new AppConfig());
        Assert.NotEqual("saturation", Ids(unknown).FirstOrDefault());
    }

    [Fact]
    public void IcmpBlockedTargetIsExplicitAndExcluded()
    {
        var a = Run("icmp_bloque");
        var row = a.Stats.Targets.First(t => t.Id == "custom");
        Assert.Equal("no_response", row.State);
        Assert.Contains("ne répond pas à l'ICMP", row.Etat);
        Assert.Contains(a.Resume, r => r.Contains("Personnalisée") && r.Contains("ne répond pas à l'ICMP") && r.Contains(":443"));
        Assert.Empty(a.Hypotheses);
    }

    [Fact]
    public void InsufficientData()
    {
        var d = Simulator.Make("sain");
        d.Series = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => s.T < Simulator.T0 + 30).ToList());  // 30 s only
        var a = Diagnose.Analyze(d, new AppConfig());
        Assert.Contains(a.Resume, r => r.Contains("Données insuffisantes"));
        Assert.DoesNotContain(a.Resume, r => r.Contains("Aucune dégradation nette"));
    }

    [Fact]
    public void TotalOutageOrNoIcmpAtAll()
    {
        var a = Diagnose.Analyze(Simulator.Make("sain", 7, p => p.IcmpBlocked.AddRange(new[] { "gateway", "cloudflare", "google", "quad9", "custom" })), new AppConfig());
        Assert.Contains(a.Resume, r => r.Contains("Aucune cible n'a répondu"));
        Assert.Empty(a.Hypotheses);
    }

    [Fact]
    public void EmptySessionDoesNotCrash()
    {
        var a = Diagnose.Analyze(new SessionData { Id = 1, Started = 1000 }, new AppConfig());
        Assert.Equal(new[] { "Aucune mesure enregistrée." }, a.Resume.ToArray());
    }

    [Fact]
    public void TracerouteEvidenceAndIcmpRateLimitCaveat()
    {
        var d = Simulator.Make("fai");
        d.Traces = new()
        {
            new TraceRec { T = Simulator.T0 + 320, Target = "1.1.1.1", Data = new TraceResult { Analysis = new TraceAnalysis { Step = new TraceStep { Hop = 4, Ip = "80.2.2.2", FromMs = 15, ToMs = 85 } } } },
            new TraceRec { T = Simulator.T0 + 330, Target = "8.8.8.8", Data = new TraceResult { Analysis = new TraceAnalysis { IntermediateLoss = new() { 3 } } } },
        };
        var h = Diagnose.Analyze(d, new AppConfig()).Hypotheses.First(x => x.Id == "fai");
        Assert.Contains(h.Preuves, p => p.Contains("saut 4"));
        Assert.Contains(h.Contre, c => c.Contains("NON retrouvée") && c.Contains("limitation ICMP"));
    }

    [Fact]
    public void GapMarksAreNotLosses()
    {
        var d = Simulator.Make("sain");
        int baseN = Diagnose.Analyze(d, new AppConfig()).Stats.Targets[0].Stats!.N;
        double a = Simulator.T0 + 300, b = Simulator.T0 + 600;  // a 5-minute sleep: samples removed, as the recorder would
        d.Series = d.Series.ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => !(a <= s.T && s.T <= b)).ToList());
        d.Marks.Add(new Mark { T = a, Kind = "gap", Note = "veille" });
        var r = Diagnose.Analyze(d, new AppConfig());
        var st = r.Stats.Targets[0].Stats!;
        Assert.Equal(baseN - 301, st.N);  // these 301 seconds count neither as replies nor as losses
        Assert.True(st.LossPct < 2);
        Assert.Contains(r.Timeline, i => i.Type == "gap");
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
                Assert.True(h.Preuves.Count > 0, $"{scn}/{h.Id} preuves");
                Assert.Contains(h.Niveau, new[] { "faible", "moyenne", "élevée" });
                Assert.True(h.Limites.Count > 0, $"{scn}/{h.Id} limites");
                Assert.False(string.IsNullOrEmpty(h.ProchainTest), $"{scn}/{h.Id} prochain test");
                Assert.True(h.Actions.Count > 0, $"{scn}/{h.Id} actions");
            }
    }

    [Fact]
    public void NeverPresentsHypothesisAsConfirmedCause()
    {
        var bad = new Regex(@"cause (est )?(certaine|confirmée)|est confirmé|prouvé que|c'est sûr");
        foreach (var scn in Simulator.Scenarios)
        {
            var a = ScenarioTests.Run(scn);
            Assert.False(bad.IsMatch(ScenarioTests.Text(a)), scn);
            if (a.Hypotheses.Count > 0) Assert.Contains(a.Resume, r => r.Contains("pas des causes confirmées"));
        }
    }

    [Fact]
    public void GatewayStableExcludesLanHypothesis()
    {
        foreach (var scn in new[] { "fai", "dns", "bufferbloat" }) Assert.DoesNotContain("lan", ScenarioTests.Ids(ScenarioTests.Run(scn)));
    }

    [Fact]
    public void Deterministic() => Assert.Equal(ScenarioTests.Text(ScenarioTests.Run("wifi_instable")), ScenarioTests.Text(ScenarioTests.Run("wifi_instable")));

    [Fact]
    public void JsonNamesMatchWhatTheInterfaceReads()
    {
        // The web page reads these exact snake_case keys.
        var json = JsonSerializer.Serialize(ScenarioTests.Run("bufferbloat"), Json.Options) + JsonSerializer.Serialize(ScenarioTests.Run("fai"), Json.Options);
        foreach (var key in new[] { "\"peu_probables\"", "\"non_evalue\"", "\"limites_generales\"", "\"prochain_test\"", "\"zone_texte\"", "\"loss_pct\"", "\"gw_delta\"", "\"idle_med\"",
                                    "\"load_p95\"", "\"p95\"", "\"t0\"", "\"inet_p95\"", "\"bloat_up\"", "\"down_mbps\"" })
            Assert.Contains(key, json);
    }
}

public class RouterTests
{
    static readonly (double? Down, double? Up) Meas = (300.0, 30.0);

    static List<(string Sev, string Txt)> Kinds(RouterConfig r, double worst = 200) => RouterQos.Check(r, Meas, new AppConfig(), worst).Select(f => (f.Severite, f.Texte)).ToList();

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
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, QosType = "limite_bande_passante", LimitDown = 500, LimitUp = 50 }), k => k.Txt.Contains("ne limite rien"));

    [Fact]
    public void MeasuredAboveLimitMeansLimitNotApplied()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, QosType = "limite_bande_passante", LimitDown = 100 }), k => k.Sev == "probleme" && k.Txt.Contains("DÉPASSE"));

    [Fact]
    public void UnitConfusionDetected()
    {
        Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, LimitDown = 300000 }), k => k.Sev == "probleme" && k.Txt.Contains("unité"));
        Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, LimitDown = 0.3 }), k => k.Txt.Contains("unité"));
    }

    [Fact]
    public void CoherentLimitIsReportedOk() => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, LimitDown = 280 }), k => k.Sev == "ok");

    [Fact]
    public void PriorityIsNotQueueManagement()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, QosType = "priorite" }), k => k.Txt.Contains("priorisation") && k.Txt.Contains("file d'attente"));

    [Fact]
    public void SqmIsNeverAssumed() => Assert.Contains(Kinds(new RouterConfig { QosEnabled = false }), k => k.Txt.Contains("SQM") && k.Txt.Contains("inconnue"));

    [Fact]
    public void TemporaryPriorityNoted()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = "2 heures" } } }), k => k.Txt.Contains("PC") && k.Txt.Contains("2 heures"));

    [Fact]
    public void BandwidthRuleBelowMeasuredSpeedIsFlagged()
        => Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, BandwidthRules = { new BandwidthRule { Name = "Salon", Down = 50, Up = 5 } } }), k => k.Sev == "attention" && k.Txt.Contains("Salon"));

    [Fact]
    public void ProposalsHaveJustificationAndRollbackAndNothingAutomatic()
    {
        var rc = new RouterConfig { QosEnabled = true, QosType = "priorite", LimitDown = 500, LimitUp = 50 };
        var props = RouterQos.Propose(rc, Meas, 200);
        Assert.NotEmpty(props);
        Assert.Contains("276", props[0].Changement);  // 92 % of 300
        foreach (var p in props) { Assert.NotEmpty(p.Justification); Assert.NotEmpty(p.RetourArriere); }
        Assert.Contains("Aucun réglage n'est modifié", RouterQos.Analysis(rc, Meas, new AppConfig(), 200).Rappel);
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
        var cfg = new AppConfig { Router = new RouterConfig { QosEnabled = true, QosType = "priorite", LimitDown = 900, LimitUp = 900 } };
        var a = ScenarioTests.Run("bufferbloat", cfg);
        var r = a.Hypotheses.Where(h => h.Id == "routeur_qos").ToList();
        Assert.True(r.Count > 0 && r[0].Findings!.Count > 0);
        Assert.Contains(r[0].Preuves, p => p.Contains("ne limite rien") || p.Contains("priorisation"));
    }
}

public class CompareTests
{
    static Metrics M(double v) => new() { InetP95 = v, BloatUp = v * 2 };

    [Fact]
    public void ImprovementBeyondVariability()
    {
        var rows = Diagnose.Compare(new[] { M(100), M(105), M(95) }, new[] { M(40), M(45), M(42) });
        Assert.All(rows, r => Assert.Equal("amélioration", r.Verdict));
    }

    [Fact]
    public void DifferenceWithinVariabilityIsIndistinct()
        => Assert.All(Diagnose.Compare(new[] { M(100), M(140) }, new[] { M(110), M(125) }), r => Assert.Contains("indistinct", r.Verdict));

    [Fact]
    public void SingleMeasureIsOnlyIndicative()
        => Assert.All(Diagnose.Compare(new[] { M(100) }, new[] { M(40) }), r => Assert.Contains("indicatif", r.Verdict));

    [Fact]
    public void DegradationAndThroughputDirection()
    {
        var rows = Diagnose.Compare(new[] { new Metrics { DownMbps = 300 }, new Metrics { DownMbps = 305 } }, new[] { new Metrics { DownMbps = 100 }, new Metrics { DownMbps = 102 } });
        Assert.Equal("dégradation", rows[0].Verdict);
    }
}
