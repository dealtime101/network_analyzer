using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

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
