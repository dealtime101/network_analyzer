using System.Text.RegularExpressions;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

/// <summary>Accessibility checks on the web page as it is served (the embedded index.html). The page is small enough to read with
/// regular expressions; each rule below is one thing a screen reader or a keyboard user depends on.</summary>
public class PageAccessibilityTests
{
    static readonly string Page = System.Text.Encoding.UTF8.GetString(Api.IndexBytes);

    /// <summary>The static markup only: the script and the i18n dictionaries are not part of what is parsed here.</summary>
    static string Markup => Regex.Replace(Page, @"<script\b[\s\S]*?</script>", "", RegexOptions.IgnoreCase);

    static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    [Fact]
    public void TheParserSeesTheFormControls()
    {
        Assert.True(Regex.Matches(Markup, @"<(input|select|textarea)\b", RegexOptions.IgnoreCase).Count > 25, "the checks below would prove nothing");
        Assert.True(Regex.Matches(Markup, @"<label\b", RegexOptions.IgnoreCase).Count > 20);
    }

    /// <summary>Every form control with the translation key that names it: its label (for=, or wrapping it) or its aria-label key.</summary>
    static List<(string Id, string? NameKey)> ControlNames()
    {
        var labelsFor = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Markup, @"<label\b([^>]*)>", RegexOptions.IgnoreCase))
        {
            var target = Attr(m.Groups[1].Value, "for");
            var key = Attr(m.Groups[1].Value, "data-i18n") ?? Attr(m.Groups[1].Value, "data-i18n-html");
            if (target != null && key != null) labelsFor[target] = key;
        }
        var wrapped = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Markup, @"<label\b[^>]*>([\s\S]*?)</label>", RegexOptions.IgnoreCase))
        {
            var inner = m.Groups[1].Value;
            var control = Regex.Match(inner, @"<(?:input|select|textarea)\b[^>]*>", RegexOptions.IgnoreCase);
            var text = Regex.Match(inner, @"data-i18n(?:-html)?\s*=\s*""([^""]+)""");
            if (control.Success && text.Success && Attr(control.Value, "id") is { } id) wrapped[id] = text.Groups[1].Value;
        }
        var res = new List<(string, string?)>();
        foreach (Match m in Regex.Matches(Markup, @"<(?:input|select|textarea)\b[^>]*>", RegexOptions.IgnoreCase))
        {
            if (Attr(m.Value, "type") == "hidden") continue;
            var id = Attr(m.Value, "id") ?? "(no id)";
            var aria = Attr(m.Value, "data-i18n-aria");
            res.Add((id, aria ?? (labelsFor.TryGetValue(id, out var l) ? l : wrapped.TryGetValue(id, out var w) ? w : null)));
        }
        return res;
    }

    [Fact]
    public void EveryControlHasAnAccessibleNameAndNoTwoShareOne()
    {
        var names = ControlNames();
        Assert.True(names.Count > 25);
        Assert.Empty(names.Where(n => n.NameKey is null).Select(n => $"{n.Id}: no accessible name"));
        // within one tab (only one is on screen at a time): e.g. two numeric fields under one label would read the same to a screen reader
        var tabOf = new Dictionary<string, string>();
        foreach (Match s in Regex.Matches(Markup, @"<section\b[^>]*\bid=""(tab-[a-z]+)""[^>]*>([\s\S]*?)</section>", RegexOptions.IgnoreCase))
            foreach (Match c in Regex.Matches(s.Groups[2].Value, @"<(?:input|select|textarea)\b[^>]*\bid=""([^""]+)""", RegexOptions.IgnoreCase))
                tabOf[c.Groups[1].Value] = s.Groups[1].Value;
        var clash = names.GroupBy(n => (Tab: tabOf.GetValueOrDefault(n.Id, "(outside any tab)"), n.NameKey)).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Tab}/{g.Key.NameKey}: {string.Join(", ", g.Select(x => x.Id))}").ToList();
        Assert.Empty(clash);
        Assert.True(tabOf.Count > 20, "the tab sections were not found, so the clash check proved nothing");
    }

    [Fact]
    public void EveryTranslationKeyUsedInTheMarkupExistsInBothDictionaries()
    {
        var used = Regex.Matches(Markup, @"data-i18n(?:-html|-ph|-aria)?\s*=\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(used.Count > 100);
        var missing = new List<string>();
        foreach (var key in used)
            if (Regex.Matches(Page, $@"'{Regex.Escape(key)}'\s*:").Count < 2) missing.Add(key);   // the English and the French dictionary
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryLabelIsTiedToAControl()
    {
        var ids = Regex.Matches(Markup, @"<(?:input|select|textarea)\b[^>]*>", RegexOptions.IgnoreCase).Select(m => Attr(m.Value, "id")).Where(i => i != null).ToHashSet();
        var loose = new List<string>();
        foreach (Match m in Regex.Matches(Markup, @"<label\b([^>]*)>([\s\S]*?)</label>", RegexOptions.IgnoreCase))
        {
            var target = Attr(m.Groups[1].Value, "for");
            bool wraps = Regex.IsMatch(m.Groups[2].Value, @"<(input|select|textarea)\b", RegexOptions.IgnoreCase);
            if (wraps) continue;
            if (target is null) loose.Add($"label without for: {Attr(m.Groups[1].Value, "data-i18n") ?? Attr(m.Groups[1].Value, "data-i18n-html")}");
            else if (!ids.Contains(target)) loose.Add($"label for='{target}': no such control");
        }
        Assert.Empty(loose);
    }
}
