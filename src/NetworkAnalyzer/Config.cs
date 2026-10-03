using System.Text;
using System.Text.Json;

namespace NetworkAnalyzer;

public sealed class PriorityDevice
{
    public string Name { get; set; } = "";
    public string? Priority { get; set; }
    public string? Duration { get; set; }
}

public sealed class BandwidthRule
{
    public string Name { get; set; } = "";
    public double? Down { get; set; }
    public double? Up { get; set; }
    public string? Unit { get; set; }
}

/// <summary>QoS settings typed in by hand (see <see cref="RouterQos"/>). Never holds a credential.</summary>
public sealed class RouterConfig
{
    public string? Model { get; set; }
    public string? HwVersion { get; set; }
    public string? Firmware { get; set; }
    public bool? QosEnabled { get; set; }
    public string QosType { get; set; } = "inconnu";
    public double? LimitDown { get; set; }
    public double? LimitUp { get; set; }
    public string Unit { get; set; } = "Mbps";
    public string? SqmAvailable { get; set; }
    public List<PriorityDevice> PriorityDevices { get; set; } = new();
    public List<BandwidthRule> BandwidthRules { get; set; } = new();
    public string? Notes { get; set; }
    public List<string> Screenshots { get; set; } = new();
}

/// <summary>Local settings (config.json). The application does not connect to the router: no router credential exists here.</summary>
public sealed class AppConfig
{
    public string CustomTarget { get; set; } = "";
    public string GatewayOverride { get; set; } = "";
    public double? PlanDownMbps { get; set; }
    public double? PlanUpMbps { get; set; }
    public RouterConfig? Router { get; set; }
}

public sealed class ConfigStore
{
    readonly string path;
    readonly object gate = new();

    public ConfigStore(string dataDir) => path = Path.Combine(dataDir, "config.json");

    public AppConfig Load()
    {
        lock (gate)
        {
            try { return Json.From<AppConfig>(File.ReadAllText(path)) ?? new AppConfig(); }
            catch (Exception e) when (e is IOException or JsonException) { return new AppConfig(); }
        }
    }

    public void Save(AppConfig c)
    {
        lock (gate)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.To(c), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
    }

    public AppConfig Update(Action<AppConfig> change)
    {
        lock (gate)
        {
            var c = Load();
            change(c);
            Save(c);
            return c;
        }
    }
}
