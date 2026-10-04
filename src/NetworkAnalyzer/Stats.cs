namespace NetworkAnalyzer;

/// <summary>Statistics of a series of latency samples.</summary>
public sealed class RttStats
{
    public int N { get; set; }
    public int Lost { get; set; }
    public double LossPct { get; set; }
    public double? Median { get; set; }
    public double? P95 { get; set; }
    public double? Max { get; set; }
    public double? Min { get; set; }
    public double? Mean { get; set; }
    public double? Jitter { get; set; }
}

/// <summary>
/// Definitions (shown as-is in the UI and the report):
/// median = central value of the replies received; p95 = nearest rank, the value at index ceil(0.95 · n) of the sorted list;
/// loss = requests without reply / requests sent (a system pause is NOT a loss: nothing is counted during the pause);
/// jitter = mean of |RTT(i) − RTT(i−1)| between two consecutive replies (a loss breaks the chain).
/// </summary>
public static class Stats
{
    /// <summary>The four definitions, translated into the current language.</summary>
    public static Dictionary<string, string> Definitions() => new()
    {
        ["median"] = Loc.T("def.median"), ["p95"] = Loc.T("def.p95"), ["loss"] = Loc.T("def.loss"), ["jitter"] = Loc.T("def.jitter"),
    };

    public static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        int k = Math.Max(1, (int)Math.Ceiling(p / 100 * sorted.Count));
        return sorted[Math.Min(k, sorted.Count) - 1];
    }

    public static double? Median(IEnumerable<double> vals)
    {
        var s = vals.OrderBy(x => x).ToList();
        int n = s.Count;
        if (n == 0) return null;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }

    public static double? Jitter(IEnumerable<Sample> samples)
    {
        double? prev = null;
        double sum = 0;
        int count = 0;
        foreach (var s in samples)
        {
            if (s.Ok && s.V.HasValue)
            {
                if (prev.HasValue) { sum += Math.Abs(s.V.Value - prev.Value); count++; }
                prev = s.V;
            }
            else prev = null;
        }
        return count > 0 ? sum / count : null;
    }

    public static RttStats? Rtt(IEnumerable<Sample> samples)
    {
        var list = samples as IList<Sample> ?? samples.ToList();
        int n = list.Count;
        if (n == 0) return null;
        var vals = list.Where(s => s.Ok && s.V.HasValue).Select(s => s.V!.Value).OrderBy(x => x).ToList();
        int lost = n - vals.Count;
        return new RttStats
        {
            N = n, Lost = lost, LossPct = 100.0 * lost / n,
            Median = Median(vals), P95 = Percentile(vals, 95),
            Max = vals.Count > 0 ? vals[^1] : null, Min = vals.Count > 0 ? vals[0] : null,
            Mean = vals.Count > 0 ? vals.Average() : null, Jitter = Jitter(list),
        };
    }

    public static List<Sample> Window(IEnumerable<Sample> s, double t0, double t1) => s.Where(x => x.T >= t0 && x.T <= t1).ToList();

    public static List<double> Values(IEnumerable<Sample> s) => s.Where(x => x.Ok && x.V.HasValue).Select(x => x.V!.Value).ToList();

    /// <summary>Samples outside the excluded intervals [(t0, t1), ...].</summary>
    public static List<Sample> Outside(IEnumerable<Sample> s, IReadOnlyList<(double A, double B)> excluded)
    {
        if (excluded.Count == 0) return s.ToList();
        return s.Where(x => !excluded.Any(e => e.A <= x.T && x.T <= e.B)).ToList();
    }
}
