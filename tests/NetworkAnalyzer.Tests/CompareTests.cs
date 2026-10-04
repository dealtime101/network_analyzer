using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

public class CompareTests
{
    static Metrics M(double v) => new() { InetP95 = v, BloatUp = v * 2 };

    [Fact]
    public void ImprovementBeyondVariability()
    {
        var rows = Diagnose.Compare(new[] { M(100), M(105), M(95) }, new[] { M(40), M(45), M(42) });
        Assert.All(rows, r => Assert.Equal("improvement", r.Verdict));
    }

    [Fact]
    public void DifferenceWithinVariabilityIsIndistinct()
        => Assert.All(Diagnose.Compare(new[] { M(100), M(140) }, new[] { M(110), M(125) }), r => Assert.Equal("indistinct", r.Verdict));

    [Fact]
    public void SingleMeasureIsOnlyIndicative()
        => Assert.All(Diagnose.Compare(new[] { M(100) }, new[] { M(40) }), r => Assert.Equal("indicative", r.Verdict));

    [Fact]
    public void DegradationAndThroughputDirection()
    {
        var rows = Diagnose.Compare(new[] { new Metrics { DownMbps = 300 }, new Metrics { DownMbps = 305 } }, new[] { new Metrics { DownMbps = 100 }, new Metrics { DownMbps = 102 } });
        Assert.Equal("degradation", rows[0].Verdict);
    }
}
