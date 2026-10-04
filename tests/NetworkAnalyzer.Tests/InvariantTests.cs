using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

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
        // wording that would present a hypothesis as a fact, in each language (the cautious "not confirmed causes" sentence is plural, so it is not matched)
        var bad = new Dictionary<string, Regex>
        {
            ["en"] = new(@"cause is (certain|confirmed)|\bis confirmed\b|proven that|it is certain|certain cause", RegexOptions.IgnoreCase),
            ["fr"] = new(@"cause (est )?(certaine|confirmée)|\best confirmée?\b|il est (prouvé|certain)|prouvé que|cause (certaine|confirmée)|c'est certain", RegexOptions.IgnoreCase),
        };
        var caution = new Dictionary<string, string> { ["en"] = "not confirmed causes", ["fr"] = "pas des causes confirmées" };
        foreach (var lang in bad.Keys)
            using (Loc.Scope(lang))
                foreach (var scn in Simulator.Scenarios)
                {
                    var a = ScenarioTests.Run(scn);
                    Assert.False(bad[lang].IsMatch(ScenarioTests.Text(a)), $"{lang}/{scn}");
                    if (a.Hypotheses.Count > 0) Assert.Contains(a.Summary, r => r.Contains(caution[lang]));
                }
        // the patterns themselves must be able to match, or the loop above proves nothing
        Assert.Matches(bad["fr"], "la cause est confirmée");
        Assert.Matches(bad["fr"], "Il est certain que votre FAI");
        Assert.Matches(bad["en"], "the cause is confirmed");
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
