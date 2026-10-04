using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

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
    public void ALimitIsNeverProposedAsZero()
    {
        var r = new RouterConfig { QosType = "bandwidth_limit", Unit = "Mbps" };
        using var _ = Loc.Scope("en");
        // 0.004 Mbps rounds to 0.00 at two decimals: a "0 Mbps" limit may block the line or switch the limit off
        var tiny = RouterQos.Propose(r, (null, 0.004), 200).Select(p => p.Change);
        Assert.DoesNotContain(tiny, c => c.Contains("about 0 Mbps") || c.Contains("about 0.00"));
        Assert.Contains(RouterQos.Propose(r, (null, 0.5), 200), p => p.Change.Contains("about 0.46 Mbps"));
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
        // one criterion for both directions: the "limited to" note about a priority that expires
        bool Temporary((string Sev, string Txt) k) => k.Txt.Contains("PC") && k.Txt.Contains("limited to");
        Assert.Contains(Kinds(new RouterConfig { QosEnabled = true, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = "2 hours" } } }), k => Temporary(k) && k.Txt.Contains("2 hours"));
        foreach (var forever in new[] { "always", "toujours", "Unlimited", "∞" })
            Assert.DoesNotContain(Kinds(new RouterConfig { QosEnabled = true, PriorityDevices = { new PriorityDevice { Name = "PC", Duration = forever } } }), Temporary);
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
