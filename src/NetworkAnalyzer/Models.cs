using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkAnalyzer;

/// <summary>One measurement. V is null when Ok is false.</summary>
public readonly record struct Sample(double T, double? V, bool Ok, string Info);

public sealed class Target
{
    public string Id { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>gateway | internet | internet6 | custom</summary>
    public string Role { get; set; } = "";
    public int Family { get; set; }
    public int? TcpPort { get; set; }

    /// <summary>Display name, derived from id/role/host in the current language (never stored: the setter does not exist).</summary>
    public string Label => Id switch
    {
        "gateway" => Loc.T(Family == 6 ? "target.gateway6" : "target.gateway"),
        "custom" => Loc.T("target.custom", Host),
        "cloudflare" => $"Cloudflare ({Host})",
        "google" => $"Google ({Host})",
        "quad9" => $"Quad9 ({Host})",
        "cloudflare6" => "Cloudflare IPv6",
        _ => Id,
    };
}

/// <summary>A user or system event. Kind: lag | gap | roam. For gap and roam the note is empty (the text is derived from the kind).</summary>
public sealed class Mark
{
    public double T { get; set; }
    public string Kind { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>Result of one phase of the saturation test (also stored with the phase).</summary>
public sealed class PhaseMeta
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Label { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Direction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? DurationS { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? Bytes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? AvgMbps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? SustainedMbps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PeakMbps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? VolumeCapReached { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Streams { get; set; }
    /// <summary>True when the phase ended before the ramp-up period was over (the sustained rate then includes the ramp-up).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RampOnly { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Errors { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Cancelled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Interrupted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
}

/// <summary>Phase names: idle | download | recovery1 | upload | recovery2.</summary>
public sealed class Phase
{
    public string Name { get; set; } = "";
    public double T0 { get; set; }
    public double T1 { get; set; }
    public PhaseMeta Meta { get; set; } = new();
}

public sealed class TraceHop
{
    public int Hop { get; set; }
    public string? Ip { get; set; }
    public List<double> Rtts { get; set; } = new();
    public int Lost { get; set; }
    public int Sent { get; set; }
}

public sealed class TraceStep
{
    public int Hop { get; set; }
    public string? Ip { get; set; }
    public double FromMs { get; set; }
    public double ToMs { get; set; }
}

/// <summary>Structured reading of a traceroute (the sentences are built at display time, see <see cref="Probes.TraceNotes"/>).</summary>
public sealed class TraceAnalysis
{
    public bool Reached { get; set; }
    public List<int> IntermediateLoss { get; set; } = new();
    public TraceStep? Step { get; set; }
    public double? DestLossPct { get; set; }
    public int DestSent { get; set; }
}

public sealed class TraceResult
{
    /// <summary>Error code: unsupported | name_not_found | failed (translated at display time).</summary>
    public string? Error { get; set; }
    public List<TraceHop>? Hops { get; set; }
    public TraceAnalysis? Analysis { get; set; }
}

public sealed class TraceRec
{
    public double T { get; set; }
    public string Target { get; set; } = "";
    public TraceResult Data { get; set; } = new();
}

public sealed class AdapterInfo
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string LinkSpeed { get; set; } = "";
    /// <summary>wifi | ethernet | vpn | virtual | other</summary>
    public string Kind { get; set; } = "other";
    public List<string> Ipv4 { get; set; } = new();
    public List<string> Ipv6 { get; set; } = new();
    public string? Gw4 { get; set; }
    public string? Gw6 { get; set; }
    public List<string> Dns { get; set; } = new();
    public int Metric { get; set; }
    /// <summary>When this is a virtual interface bridging a physical one (Hyper-V external switch): the physical adapter's name.</summary>
    public string? BridgedPhysical { get; set; }
}

public sealed class VpnInfo
{
    public bool Active { get; set; }
    public List<string> Adapters { get; set; } = new();
}

public sealed class EnvInfo
{
    public List<AdapterInfo> Interfaces { get; set; } = new();
    public AdapterInfo? Active { get; set; }
    public VpnInfo Vpn { get; set; } = new();
    public bool Ipv6Global { get; set; }
    public string Platform { get; set; } = "";
}

public sealed class WifiInfo
{
    public bool Connected { get; set; }
    public string? Ssid { get; set; }
    public string? Bssid { get; set; }
    public string? Radio { get; set; }
    public int? Channel { get; set; }
    public string? Band { get; set; }
    public int? Signal { get; set; }
    public double? Rssi { get; set; }
    public double? RxRate { get; set; }
    public double? TxRate { get; set; }
}

public sealed class WifiNeighbors
{
    public int Total { get; set; }
    public int SameChannel { get; set; }
    public int SameChannelStrong { get; set; }
    public Dictionary<string, int> Channels { get; set; } = new();
}

public sealed class ConfigSnapshot
{
    public RouterConfig? Router { get; set; }
    public double? PlanDownMbps { get; set; }
    public double? PlanUpMbps { get; set; }
}

public sealed class LoadMeta
{
    public string Server { get; set; } = "";
    public int Streams { get; set; }
    public int CapDownMb { get; set; }
    public int CapUpMb { get; set; }
    public List<object?[]> Phases { get; set; } = new();
}

public sealed class SessionMeta
{
    public EnvInfo Env { get; set; } = new();
    public List<Target> Targets { get; set; } = new();
    public double IntervalS { get; set; } = 1.0;
    public ConfigSnapshot? CfgSnapshot { get; set; }
    public WifiInfo? Wifi { get; set; }
    public WifiNeighbors? WifiNeighbors { get; set; }
    public LoadMeta? Loadtest { get; set; }
    public Metrics? Metrics { get; set; }
}

public sealed class SessionData
{
    public int Id { get; set; }
    public double Started { get; set; }
    public double? Ended { get; set; }
    public string Label { get; set; } = "";
    public string Link { get; set; } = "auto";
    public int PlannedS { get; set; }
    public SessionMeta Meta { get; set; } = new();
    public Dictionary<string, List<Sample>> Series { get; set; } = new();
    public List<Mark> Marks { get; set; } = new();
    public List<Phase> Phases { get; set; } = new();
    public List<TraceRec> Traces { get; set; } = new();

    public IReadOnlyList<Sample> S(string name) => Series.TryGetValue(name, out var l) ? l : Array.Empty<Sample>();
    public List<Target> Targets => Meta.Targets;

    /// <summary>End of the session, or the last measurement when it is still running.</summary>
    public double EndOrLast => Ended ?? Math.Max(Started, Series.Values.Where(v => v.Count > 0).Select(v => v[^1].T).DefaultIfEmpty(Started).Max());
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    /// <summary>For files on disk: same as <see cref="Options"/> but the derived, language-dependent Target.label is never written.</summary>
    public static readonly JsonSerializerOptions Storage = new(Options)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    if (ti.Type != typeof(Target)) return;
                    for (int i = ti.Properties.Count - 1; i >= 0; i--)
                        if (ti.Properties[i].Name == "label") ti.Properties.RemoveAt(i);
                },
            },
        },
    };

    public static string To(object? o) => JsonSerializer.Serialize(o, Options);
    public static T? From<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
}
