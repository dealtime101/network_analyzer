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

    /// <summary>Every property path of a JSON document, arrays written as [] (dictionary keys are not followed: they are data).</summary>
    static HashSet<string> JsonPaths(string json, params string[] dictionaryProperties)
    {
        var o = new HashSet<string>();
        void Walk(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    o.Add(path + "." + p.Name);
                    if (!(dictionaryProperties.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.Object)) Walk(p.Value, path + "." + p.Name);
                }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Walk(x, path + "[]");
        }
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement, "");
        return o;
    }

    [Fact]
    public void JsonNamesMatchWhatTheInterfaceReads()
    {
        // The web page reads these exact snake_case keys, at these places. Each result is read on its own: a key found only in the
        // other result, or at another level, does not count.
        string[] always =
        {
            ".summary", ".hypotheses", ".unlikely", ".not_evaluated", ".general_limits", ".actions", ".timeline", ".thresholds", ".session",
            ".hypotheses[].title", ".hypotheses[].level", ".hypotheses[].evidence", ".hypotheses[].counter", ".hypotheses[].limits", ".hypotheses[].next_test", ".hypotheses[].actions",
            ".metrics.inet_p95", ".metrics.gw_p95", ".metrics.bloat_up", ".metrics.bloat_down", ".metrics.down_mbps", ".metrics.up_mbps",
            ".stats.targets[].state_text", ".stats.targets[].stats.loss_pct", ".stats.targets[].stats.p95", ".stats.targets[].stats.median", ".stats.targets[].label",
            ".stats.traffic.down.p95",
        };
        var results = new[] { "bufferbloat", "isp" }.ToDictionary(n => n, n => JsonPaths(JsonSerializer.Serialize(ScenarioTests.Run(n), Json.Options), "targets", "dns", "windows"));
        foreach (var (name, paths) in results)
            foreach (var key in always) Assert.True(paths.Contains(key), $"{name}: {key} is missing");
        // what only a result that has the item can show (a bloat verdict, timeline entries, phases) is checked on the result that has it
        string[] bloat =
        {
            ".bufferbloat.directions.down.gw_delta", ".bufferbloat.directions.down.idle_med", ".bufferbloat.directions.down.load_p95", ".bufferbloat.directions.down.loss_pct",
            ".bufferbloat.directions",
            ".bufferbloat.directions.down.mbps", ".bufferbloat.directions.up.gw_delta", ".bufferbloat.directions.up.load_p95",
        };
        foreach (var key in bloat) Assert.True(results["bufferbloat"].Contains(key), $"bufferbloat: {key} is missing");
        string[] shape = { ".timeline[].zone_text", ".timeline[].t0", ".timeline[].type", ".stats.phases[].t0", ".stats.phases[].name" };
        foreach (var key in shape) Assert.True(results.Values.Any(p => p.Contains(key)), $"{key} is in none of the results");
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
