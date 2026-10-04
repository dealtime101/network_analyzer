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
    /// <summary>unknown | priority | bandwidth_limit | sqm</summary>
    public string QosType { get; set; } = "unknown";
    public double? LimitDown { get; set; }
    public double? LimitUp { get; set; }
    public string Unit { get; set; } = "Mbps";
    /// <summary>unknown | yes | no</summary>
    public string? SqmAvailable { get; set; }
    public List<PriorityDevice> PriorityDevices { get; set; } = new();
    public List<BandwidthRule> BandwidthRules { get; set; } = new();
    public string? Notes { get; set; }
    public List<string> Screenshots { get; set; } = new();

    /// <summary>The values the analysis understands for the two free-text choices.</summary>
    public static readonly string[] SqmValues = { "unknown", "yes", "no" };

    /// <summary>A value outside the documented ones (a typo in a hand-edited file) becomes "unknown": the analysis
    /// then never meets a type or an SQM answer it was not written for. Empty/null stay as they are.</summary>
    public void Normalize()
    {
        if (!RouterQos.QosTypes.Contains(QosType)) QosType = "unknown";
        if (!string.IsNullOrEmpty(SqmAvailable) && !SqmValues.Contains(SqmAvailable)) SqmAvailable = "unknown";
    }
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

/// <summary>Write-then-rename that survives a power cut: the data is forced to disk BEFORE the rename makes it the real file,
/// so the file is either the old content or the new one, never an empty or truncated one.</summary>
public static class DurableFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";   // its own name: two writers never share a temporary file
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, true);
        }
        catch { try { File.Delete(tmp); } catch (IOException) { } throw; }
    }
}

public sealed class ConfigStore
{
    // one lock per file, shared by every store on it: two instances on the same folder serialise their read-change-write
    // (another PROCESS on the same folder is not covered: the rename keeps the file whole, but a change can still be lost)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new();
    readonly string path;
    readonly object gate;

    public ConfigStore(string dataDir)
    {
        path = Path.Combine(dataDir, "config.json");
        gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());
    }

    /// <summary>Why the last Load fell back to defaults: "invalid" (not valid JSON; a copy was kept) or "unreadable" (the file exists but cannot be read).
    /// Null when the settings were loaded, or when there is no file yet (a first run is not a problem).</summary>
    public string? LoadProblem { get; private set; }

    public AppConfig Load()
    {
        lock (gate)
        {
            string text;
            LoadProblem = null;
            try { text = File.ReadAllText(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new AppConfig(); }  // no file yet: the normal first run
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)  // the file is there but cannot be read
            {
                LoadProblem = "unreadable";
                Console.Error.WriteLine($"[config] cannot read {path} ({e.Message}); defaults are used and nothing will be saved over it");
                return new AppConfig();
            }
            try
            {
                var cfg = Json.From<AppConfig>(text) ?? new AppConfig();
                cfg.Router?.Normalize();
                return cfg;
            }
            catch (JsonException e)
            {
                LoadProblem = "invalid";
                // unreadable (truncated, bad hand edit): the next save would overwrite it with an empty one, so keep a copy first
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try
                {
                    // once per distinct content: reading the same bad file again must not pile up copies
                    bool kept = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*").Any(f => File.ReadAllText(f) == text);
                    if (!kept) File.WriteAllText(backup, text, new UTF8Encoding(false));
                }
                catch (IOException) { }
                Console.Error.WriteLine($"[config] {path} is not valid JSON ({e.Message}); a copy was kept as {backup}, defaults are used");
                return new AppConfig();
            }
        }
    }

    public void Save(AppConfig c)
    {
        lock (gate) { DurableFile.WriteAllText(path, Json.To(c)); LoadProblem = null; }
    }

    public AppConfig Update(Action<AppConfig> change)
    {
        lock (gate)
        {
            var c = Load();
            // a file that exists but cannot be read must not be replaced by defaults plus one change: that would destroy it
            if (LoadProblem == "unreadable") throw new ApiException(Loc.T("err.config_unreadable"), 409);
            change(c);
            Save(c);
            return c;
        }
    }
}
