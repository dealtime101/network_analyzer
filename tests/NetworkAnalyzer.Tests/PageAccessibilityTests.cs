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
