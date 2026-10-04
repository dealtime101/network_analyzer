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

    // ---- colour contrast (WCAG 2: 4.5:1 for normal text)
    static Dictionary<string, string> Theme(bool dark)
    {
        var vars = new Dictionary<string, string>();
        void Read(string block) { foreach (Match m in Regex.Matches(block, @"--([a-z-]+):\s*(#[0-9a-fA-F]{3,6})")) vars[m.Groups[1].Value] = m.Groups[2].Value; }
        Read(Regex.Match(Page, @"(?<!\{):root\{([^}]*)\}").Groups[1].Value);   // the light theme (the first :root, outside the media query)
        if (dark) Read(Regex.Match(Page, @"prefers-color-scheme:dark\)\{:root\{([^}]*)\}").Groups[1].Value);
        return vars;
    }

    static double Luminance(string hex)
    {
        hex = hex.TrimStart('#'); if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        double Ch(int i) { var v = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
    }

    static double Contrast(string a, string b) { var (x, y) = (Luminance(a), Luminance(b)); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    static string Resolve(string value, Dictionary<string, string> theme)
    {
        value = value.Trim();
        var v = Regex.Match(value, @"^var\(--([a-z-]+)\)$");
        return v.Success ? theme.GetValueOrDefault(v.Groups[1].Value, "#000000") : value;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TextOnColouredBackgroundsReachesTheWcagContrast(bool dark)
    {
        var theme = Theme(dark);
        Assert.True(theme.Count >= 8, "the theme variables were not read");
        var low = new List<string>();
        int checkedPairs = 0;
        // every rule that sets a background and a text colour together (components), plus the page text on the page and card backgrounds
        foreach (Match rule in Regex.Matches(Page, @"([^{}@]+)\{([^{}]*\bbackground:[^{};]*;[^{}]*\bcolor:[^{};]*)[^{}]*\}"))
        {
            var body = rule.Groups[2].Value;
            var bg = Regex.Match(body, @"\bbackground:\s*(var\(--[a-z-]+\)|#[0-9a-fA-F]{3,6})");
            var fg = Regex.Match(body, @"(?<![-a-z])color:\s*(var\(--[a-z-]+\)|#[0-9a-fA-F]{3,6})");
            if (!bg.Success || !fg.Success) continue;
            var (b, f) = (Resolve(bg.Groups[1].Value, theme), Resolve(fg.Groups[1].Value, theme));
            if (!b.StartsWith('#') || !f.StartsWith('#')) continue;
            checkedPairs++;
            if (Contrast(b, f) < 4.5) low.Add($"{rule.Groups[1].Value.Trim()}: {f} on {b} = {Contrast(b, f):0.0}:1");
        }
        foreach (var (fgVar, bgVar) in new[] { ("fg", "bg"), ("fg", "card"), ("muted", "bg"), ("muted", "card") })
        {
            checkedPairs++;
            if (Contrast(theme[fgVar], theme[bgVar]) < 4.5) low.Add($"--{fgVar} on --{bgVar} = {Contrast(theme[fgVar], theme[bgVar]):0.0}:1");
        }
        Assert.True(checkedPairs >= 12, $"only {checkedPairs} colour pairs were found: the rule pattern no longer sees the page");
        Assert.Empty(low);
    }

    [Fact]
    public void TheContrastMathIsRight()
    {
        Assert.Equal(21.0, Contrast("#000", "#fff"), 1);
        Assert.Equal(1.0, Contrast("#777", "#777"), 3);
        Assert.InRange(Contrast("#fff", "#58a6ff"), 2.3, 2.8);   // the reported case: white on the dark-theme accent
    }

    [Fact]
    public void ToastMessagesAreAnnouncedToScreenReaders()
    {
        var toast = Regex.Match(Markup, @"<div\b[^>]*\bid=""toast""[^>]*>").Value;
        Assert.NotEmpty(toast);
        Assert.Equal("status", Attr(toast, "role"));
        Assert.Equal("polite", Attr(toast, "aria-live"));
        // a live region must stay in the accessibility tree: hiding it with display:none (or visibility:hidden) would make it silent
        var css = Regex.Match(Page, @"#toast\{([^}]*)\}").Groups[1].Value;
        Assert.NotEmpty(css);
        Assert.DoesNotContain("display:none", css);
        Assert.DoesNotContain("visibility:hidden", css);
        var script = Regex.Match(Page, @"function toast\([^)]*\)\s*\{[^\n]*").Value;
        Assert.NotEmpty(script);
        Assert.DoesNotContain("display = 'none'", script);
        Assert.DoesNotContain("display = 'block'", script);
    }

    [Fact]
    public void EveryChartCanvasIsAnImageWithAName()
    {
        // the static markup AND the markup the script builds (the diagnosis tab's charts)
        var canvases = Regex.Matches(Page, @"<canvas\b[^>]*>", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        Assert.True(canvases.Count >= 8, $"only {canvases.Count} canvases found");
        var bad = canvases.Where(c => Attr(c, "role") != "img" || (Attr(c, "aria-label") is null && Attr(c, "data-i18n-aria") is null)).ToList();
        Assert.Empty(bad);
        // a data-i18n-aria name must be a real, translated key
        foreach (var c in canvases.Select(c => Attr(c, "data-i18n-aria")).Where(k => k != null))
            Assert.True(Regex.Matches(Page, $@"'{Regex.Escape(c!)}'\s*:").Count >= 2, $"{c} is not in both dictionaries");
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
