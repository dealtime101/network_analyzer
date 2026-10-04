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
    public void SlowOrFailingUncachedDnsQueriesAreLocatedAsDnsEvenWhenCachedOnesAreFast()
    {
        TargetFacts T(string role) => new() { Role = role, Label = role, Bad = false, Stats = new RttStats { N = 20 } };
        RttStats Dns(int n, double median, double loss = 0) => new() { N = n, Median = median, LossPct = loss };
        WindowFacts W(RttStats? hit, RttStats? miss) => new() { Targets = { ["gateway"] = T("gateway"), ["cloudflare"] = T("internet") }, DnsHit = hit, DnsMiss = miss };
        Assert.Equal("dns", Diagnose.Localize(W(Dns(10, 15), Dns(4, 600))));        // cached answers fast, uncached ones slow: the DNS RuleDns calls out
        Assert.Equal("dns", Diagnose.Localize(W(Dns(10, 15), Dns(4, 80, loss: 50)))); // uncached queries failing
        Assert.Equal("none", Diagnose.Localize(W(Dns(10, 15), Dns(4, 70))));        // a normal uncached lookup (tens of ms) is not an incident
        Assert.Equal("none", Diagnose.Localize(W(Dns(10, 15), Dns(1, 900))));       // one slow sample is too little to conclude
        Assert.Equal("none", Diagnose.Localize(W(Dns(10, 15), null)));              // no uncached query in the window
        Assert.Equal("dns", Diagnose.Localize(W(Dns(10, 250), Dns(4, 70))));        // and the cached-query rule is unchanged
    }

    [Fact]
    public void AnyDegradedCustomTargetMakesTheWindowACustomPathNotOnlyTheFirst()
    {
        TargetFacts T(string role, bool bad) => new() { Role = role, Label = role, Bad = bad, Stats = new RttStats { N = 20 } };
        var healthyFirst = new WindowFacts { Targets = { ["gateway"] = T("gateway", false), ["cloudflare"] = T("internet", false), ["c1"] = T("custom", false), ["c2"] = T("custom", true) } };
        Assert.Equal("custom_path", Diagnose.Localize(healthyFirst));   // the second custom target is the degraded one
        var allHealthy = new WindowFacts { Targets = { ["gateway"] = T("gateway", false), ["cloudflare"] = T("internet", false), ["c1"] = T("custom", false), ["c2"] = T("custom", false) } };
        Assert.Equal("none", Diagnose.Localize(allHealthy));
        // and nothing changed for the usual single custom target
        var single = new WindowFacts { Targets = { ["gateway"] = T("gateway", false), ["cloudflare"] = T("internet", false), ["custom"] = T("custom", true) } };
        Assert.Equal("custom_path", Diagnose.Localize(single));
    }

    [Fact]
    public void SessionWithUploadTrafficOnlyStillAnalysesToo()
    {
        var d = Simulator.Make("background", 7);
        d.Series.Remove("net:down_bps");
        var a = Diagnose.Analyze(d, new AppConfig());   // the mirror image of the download-only case: no exception, the missing side shows a dash
        var text = Text(a);
        Assert.Contains("PC traffic: ↓ — Mbps (max —)", text);
        Assert.DoesNotContain("PC traffic: ↓ — Mbps (max —), ↑ — Mbps", text);   // the upload figures are shown, not dashed too
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
    public void ALoadDirectionRecordedInSeveralPhasesIsAnalysedAsAWhole()
    {
        var whole = Simulator.Make("bufferbloat");
        var wholeDown = Diagnose.Analyze(whole, new AppConfig()).Bufferbloat!.Directions["down"];
        Assert.True(wholeDown.Valid);

        // the same data, but the download recorded as two phases: a first one too short to measure (4 s, 3 s of warm-up: 2 samples),
        // then the rest. The old analysis looked at the first window only and called the whole direction unusable.
        var split = Simulator.Make("bufferbloat");
        var dl = split.Phases.Single(p => p.Name == "download");
        split.Phases.Remove(dl);
        split.Phases.Add(new Phase { Name = "download", T0 = dl.T0, T1 = dl.T0 + 4 });
        split.Phases.Add(new Phase { Name = "download", T0 = dl.T0 + 4, T1 = dl.T1 });
        split.Phases = split.Phases.OrderBy(p => p.T0).ToList();
        var splitDown = Diagnose.Analyze(split, new AppConfig()).Bufferbloat!.Directions["down"];
        Assert.True(splitDown.Valid, "the later phase has plenty of data: the direction must not be dismissed because of the first one");
        Assert.InRange(splitDown.Delta!.Value, wholeDown.Delta!.Value * 0.5, wholeDown.Delta.Value * 1.5);   // and it says the same thing
        Assert.InRange(splitDown.Mbps!.Value, 250, 350);
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
    public void IdleTrafficIsSummedWhicheverDirectionCarriesIt()
    {
        // the mirror of the download case: a bursty UPLOAD (3 Mbps in 60 % of the seconds) with nothing down, then both directions busy together
        string Idle(Action<List<Sample>, List<Sample>, double> shape)
        {
            var d = Simulator.Make("bufferbloat", 7);
            double t0 = Simulator.T0 + 30, t1 = Simulator.T0 + 40;
            var down = d.Series["net:down_bps"].Select(s => s.T < t0 || s.T > t1 ? s : s with { V = 0 }).ToList();
            var up = d.Series["net:up_bps"].Select(s => s.T < t0 || s.T > t1 ? s : s with { V = 0 }).ToList();
            shape(down, up, t0);
            d.Series["net:down_bps"] = down; d.Series["net:up_bps"] = up;
            return Text(Diagnose.Analyze(d, new AppConfig()));
        }
        void Set(List<Sample> l, double t0, Func<int, double> mbps)
        {
            for (int i = 0; i < l.Count; i++) if (l[i].T >= t0 && l[i].T <= t0 + 10) l[i] = l[i] with { V = mbps((int)(l[i].T - t0)) * 1e6 };
        }
        Assert.Contains("already exchanges 3.0 Mbps", Idle((dn, up, t0) => Set(up, t0, k => k % 5 < 3 ? 3 : 0)));
        // 1 Mbps down + 0.6 up in EVERY second = 1.6 Mbps in total: above the idle threshold only as a sum (each direction alone is below it)
        Assert.Contains("already exchanges 1.6 Mbps", Idle((dn, up, t0) => { Set(dn, t0, _ => 1.0); Set(up, t0, _ => 0.6); }));
    }

    [Fact]
    public void SaturationNeedsKnownCapacity()
    {
        var a = Run("saturation");
        Assert.Equal("saturation", Ids(a)[0]);
        Assert.Contains(a.Hypotheses[0].Evidence, p => p.Contains('%'));
        var unknown = Run("saturation", new AppConfig());
        Assert.DoesNotContain("saturation", Ids(unknown));   // anywhere in the list, not only first
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
