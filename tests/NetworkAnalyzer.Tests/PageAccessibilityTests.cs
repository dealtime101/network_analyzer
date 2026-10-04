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
    public void TabsAndPanelsAreLinkedBothWaysAndOperableFromTheKeyboard()
    {
        var tabs = Regex.Matches(Markup, @"<button\b[^>]*\brole=""tab""[^>]*>", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        var panels = Regex.Matches(Markup, @"<section\b[^>]*\bclass=""tab[^""]*""[^>]*>", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        Assert.Equal(6, tabs.Count);
        Assert.Equal(tabs.Count, panels.Count);
        var tabIds = tabs.Select(t => Attr(t, "id")).ToList();
        var panelIds = panels.Select(p => Attr(p, "id")).ToList();
        Assert.DoesNotContain(null, tabIds);
        Assert.Equal(tabIds.Count, tabIds.Distinct().Count());
        foreach (var t in tabs) Assert.Contains(Attr(t, "aria-controls"), panelIds);              // tab -> its panel
        foreach (var p in panels)
        {
            Assert.Equal("tabpanel", Attr(p, "role"));
            Assert.Contains(Attr(p, "aria-labelledby"), tabIds);                                   // panel -> its tab
        }
        Assert.Equal(panelIds.OrderBy(x => x), tabs.Select(t => Attr(t, "aria-controls")).OrderBy(x => x));   // one panel per tab, none shared
        // keyboard: only the selected tab is in the tab order; arrows, Home and End move between tabs
        Assert.Contains("tabindex", Regex.Match(Page, @"function showTab\([^)]*\)\s*\{[\s\S]*?\n\}").Value, StringComparison.OrdinalIgnoreCase);   // b.tabIndex in the DOM API
        foreach (var key in new[] { "ArrowRight", "ArrowLeft", "Home", "End" }) Assert.Contains($"'{key}'", Page);
    }

    // ---- behaviour of the page's script, checked on its text (there is no browser in the test run)
    [Fact]
    public void OpeningASessionFromHistoryDoesNotRaceWithTheDiagnosisList()
    {
        // no more "switch tab, wait 300 ms, hope the list has loaded"
        Assert.DoesNotContain("setTimeout(async () => { $('#dsess').value", Page);
        var openHandler = Regex.Match(Page, @"\[data-open\]'\)\.forEach\([^\n]*").Value;
        Assert.NotEmpty(openHandler);
        Assert.DoesNotContain("setTimeout", openHandler);
        Assert.Contains("wantDiag", openHandler);                                   // the clicked id is handed over to the loader
        var loader = Regex.Match(Page, @"async function loadDiagList\(\)\s*\{[\s\S]*?\n\}").Value;
        Assert.Contains("wantDiag", loader);                                       // and the loader prefers it to the old selection
        // and whatever the order of the answers, only the latest request may draw
        var show = Regex.Match(Page, @"async function showDiag\(id\)\s*\{[\s\S]*?\n\}").Value;
        Assert.Contains("diagSeq", show);
        Assert.Matches(@"if \(\w+ !== diagSeq\) return;", show);
    }

    [Fact]
    public void ChartsAreRedrawnOnlyWhileTheyCanBeSeen()
    {
        var render = Regex.Match(Page, @"function renderLive\(d\)\s*\{[\s\S]*?\n\}").Value;
        Assert.NotEmpty(render);
        // drawing (the costly part: four canvases, sorting every series) only on the visible live tab of a visible page...
        Assert.Matches(@"if \(currentTab === 'live' && !document\.hidden\) drawAll\(", render);
        // ...and it catches up the moment the tab or the page is shown again
        Assert.Matches(@"if \(n === 'live' && lastLive\) renderLive\(lastLive\)", Regex.Match(Page, @"function showTab\([^)]*\)\s*\{[\s\S]*?\n\}").Value);
        Assert.Matches(@"addEventListener\('visibilitychange', \(\) => \{ if \(!document\.hidden && lastLive\) renderLive\(lastLive\)", Page);
        // the data keeps being collected, but a hidden page polls less often
        Assert.Matches(@"setInterval\(\(\) => \{ if \(document\.hidden && \+\+hiddenTicks % 5\) return; poll\(\); \}, 1000\)", Page);
    }

    [Fact]
    public void WifiSignalPercentAndLinkRateMbpsAreOnSeparateCharts()
    {
        // two canvases in the live tab and two in the diagnosis tab, each with its own legend and translated name
        foreach (var id in new[] { "c_wifi", "c_wifi_rate", "d_wifi", "d_wifi_rate" })
            Assert.Matches($@"<canvas id=""{id}""[^>]*(role=""img"")", Page);
        foreach (var id in new[] { "l_wifi", "l_wifi_rate", "dl_wifi", "dl_wifi_rate" })
            Assert.Contains($"id=\"{id}\"", Page);
        var draw = Regex.Match(Page, @"function drawAll\([^\n]*\n(?:[^\n]*\n)*?\}").Value;
        Assert.NotEmpty(draw);
        // the signal series is alone on its chart, the two rates share theirs (same unit)
        Assert.Matches(@"ids\.wifi\)[^\n]*series: sig", draw);
        Assert.Matches(@"ids\.wifir\)[^\n]*series: rates", draw);
        Assert.DoesNotMatch(@"simple\(map, 'wifi:signal'[^\n]*simple\(map, 'wifi:tx'", draw);   // no longer one list
        foreach (var key in new[] { "chart.wifi_sig", "chart.wifi_rate" })
            Assert.True(Regex.Matches(Page, $@"'{Regex.Escape(key)}'\s*:").Count >= 2, $"{key} must exist in both dictionaries");
    }

    [Fact]
    public void EveryHistoryCheckboxSaysWhichSessionAndWhichGroup()
    {
        var boxes = Regex.Matches(Page, @"<input type=""checkbox"" data-g=""[ab]""[^>]*>").Select(m => m.Value).ToList();
        Assert.Equal(2, boxes.Count);   // the A box and the B box of each history row
        foreach (var b in boxes)
            Assert.Matches(@"aria-label=""\$\{esc\(t\('hist\.in_group', s\.id, '[AB]'\)\)\}""", b);
        Assert.Contains("'A'", boxes[0]); Assert.Contains("'B'", boxes[1]);
        Assert.True(Regex.Matches(Page, @"'hist\.in_group'\s*:").Count >= 2, "the key must exist in both dictionaries");
    }

    [Fact]
    public void RenamingASessionIsDoneWithAFocusableButton()
    {
        Assert.DoesNotContain("<span data-ren=", Page);                                  // a span cannot be reached with the keyboard
        var cell = Regex.Match(Page, @"<button\b[^>]*\bdata-ren=[^>]*>").Value;
        Assert.NotEmpty(cell);
        Assert.Equal("button", Attr(cell, "type"));
        Assert.NotNull(Attr(cell, "title"));
        // styled like the text it replaces, with a visible keyboard focus
        Assert.Matches(@"\.linklike\{[^}]*background:none", Page);
        Assert.Matches(@"\.linklike:focus-visible\{[^}]*outline", Page);
        Assert.Contains("class=\"linklike\"", cell);
    }

    [Fact]
    public void ScreenshotThumbnailsAreNamedAndDeletionNeedsConfirmation()
    {
        var thumbs = Regex.Match(Page, @"\$\('#r_shots'\)\.innerHTML = [^\n]*").Value;
        Assert.NotEmpty(thumbs);
        Assert.Matches(@"<img class=""thumb""[^>]*\balt=""\$\{esc\(t\('r\.shot_alt'", thumbs);        // a described picture, not an unnamed link
        Assert.Matches(@"<button[^>]*\baria-label=""\$\{esc\(t\('r\.shot_del'", thumbs);               // the cross has a name
        Assert.Contains("title=", thumbs);
        // the deletion asks first
        var del = Regex.Match(Page, @"\$\$\('#r_shots \[data-del\]'\)[^\n]*").Value;
        Assert.NotEmpty(del);
        Assert.Matches(@"if \(!confirm\(t\('r\.shot_confirm'\)\)\) return;", del);
        foreach (var key in new[] { "r.shot_alt", "r.shot_del", "r.shot_confirm" })
            Assert.True(Regex.Matches(Page, $@"'{Regex.Escape(key)}'\s*:").Count >= 2, $"{key} must exist in both dictionaries");
    }

    [Fact]
    public void ANumberInABandwidthRuleIsReadWithADecimalCommaOrReportedNeverSentAsNull()
    {
        // the helper itself, extracted from the page and run as a function (no browser needed for a pure function)
        var fn = Regex.Match(Page, @"function ruleNum\(text, line\) \{[\s\S]*?\n\}").Value;
        Assert.NotEmpty(fn);
        Assert.Contains("replace(',', '.')", fn);
        Assert.Contains("Number.isFinite", fn);
        Assert.Contains("throw new Error", fn);
        // and the rules line uses it for both columns instead of a bare unary plus
        var save = Regex.Match(Page, @"\$\('#r_save'\)\.onclick = guard\(async \(\) => \{[\s\S]*?\n\}\);").Value;
        Assert.Contains("ruleNum(p[1]", save);
        Assert.Contains("ruleNum(p[2]", save);
        Assert.DoesNotMatch(@"\+p\[[12]\]", save);
        foreach (var key in new[] { "r.bad_number" })
            Assert.True(Regex.Matches(Page, $@"'{Regex.Escape(key)}'\s*:").Count >= 2, $"{key} must exist in both dictionaries");
    }

    [Fact]
    public void TabLoadersReportAFailureInsteadOfFailingSilently()
    {
        var show = Regex.Match(Page, @"function showTab\([^)]*\)\s*\{[\s\S]*?\n\}").Value;
        Assert.NotEmpty(show);
        foreach (var loader in new[] { "loadDiagList", "loadHist", "loadRouter", "loadEstimate" })
        {
            Assert.Contains($"guard({loader})()", show);                                   // errors become a toast
            Assert.DoesNotMatch($@"(?<!guard\()\b{loader}\(\)", show);                      // and none is called bare
        }
        Assert.Matches(@"const guard = fn => async \(\.\.\.a\) => \{ try \{ return await fn\(\.\.\.a\); \} catch \(e\) \{ toast\(", Page);   // what guard does
        // and inside the guarded handlers a reload is awaited, so its failure reaches the guard too
        Assert.DoesNotMatch(@"(?<!await )(?<!function )(?<!guard\()\bloadRouter\(\);", Page);
    }

    [Fact]
    public void SwitchingLanguageDoesNotEraseWhatWasTypedInTheRouterForm()
    {
        var load = Regex.Match(Page, @"async function loadRouter\(\)\s*\{[\s\S]*?\n\}").Value;
        Assert.NotEmpty(load);
        // the form is refilled from the saved values only while the user has not typed anything...
        Assert.Matches(@"if \(!routerDirty\) \{[\s\S]*r_model[\s\S]*r_notes[\s\S]*\}", load);
        // ...the analysis texts are re-rendered either way (they are what changes with the language)...
        Assert.Contains("r_analysis", load);
        Assert.True(load.IndexOf("r_analysis", StringComparison.Ordinal) > load.LastIndexOf("r_notes", StringComparison.Ordinal));
        // ...typing marks the form as modified, and saving clears the mark
        Assert.Matches(@"getElementById\('tab-router'\)\.addEventListener\('input'|\$\('#tab-router'\)\.addEventListener\('input'", Page);
        var save = Regex.Match(Page, @"\$\('#r_save'\)\.onclick = guard\(async \(\) => \{[\s\S]*?\n\}\);").Value;
        Assert.NotEmpty(save);
        Assert.Contains("routerDirty = false", save);
    }

    [Fact]
    public void TheLanguageSwitchExposesItsStateAndItsNameIsTranslated()
    {
        var group = Regex.Match(Markup, @"<div\b[^>]*\bid=""langs""[^>]*>").Value;
        Assert.Equal("group", Attr(group, "role"));
        Assert.Null(Attr(group, "aria-label"));                                  // not a fixed English word...
        Assert.Equal("lang.label", Attr(group, "data-i18n-aria"));                 // ...but a translated name
        var buttons = Regex.Matches(Markup, @"<button\b[^>]*\bdata-lang=""[a-z]{2}""[^>]*>", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Equal(new[] { "English", "Français" }, buttons.Select(b => Attr(b, "title")).ToArray());        // each language named in itself
        Assert.Equal(new[] { "en", "fr" }, buttons.Select(b => Attr(b, "lang")).ToArray());                    // so it is read with the right voice
        Assert.All(buttons, b => Assert.NotNull(Attr(b, "aria-pressed")));                                      // state is not only a CSS class
        Assert.Contains("aria-pressed", Regex.Match(Page, @"function applyI18n\(\)\s*\{[\s\S]*?\n\}").Value);   // and it follows the choice
        var pill = Regex.Match(Markup, @"<span\b[^>]*\bid=""pill""[^>]*>").Value;
        Assert.Equal("polite", Attr(pill, "aria-live"));                                                         // the monitoring status changes are announced
        Assert.Equal("status", Attr(pill, "role"));
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
