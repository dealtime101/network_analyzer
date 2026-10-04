using System.Diagnostics;

namespace NetworkAnalyzer;

/// <summary>The start-up logic of Program.cs, kept here so that it can be tested.</summary>
public static class Launcher
{
    public const string Usage = "NetworkAnalyzer [--port 8765] [--no-browser]\nLocal network analyzer: interface on http://127.0.0.1:<port> (reachable from this computer only).";

    /// <summary>Is this the answer of a Network Analyzer's /api/identity? Whatever else is listening on the port
    /// (any JSON, not JSON at all) just gives false: it must never stop the start-up.</summary>
    public static bool IsNetworkAnalyzer(string answer)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(answer);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("app", out var a)
                && a.ValueKind == System.Text.Json.JsonValueKind.String
                && a.GetString() == "NetworkAnalyzer";
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public sealed record Options(int Port, bool OpenBrowser, bool Help, string? Error);

    /// <summary>Command line → options. Anything wrong is reported in Error (never silently ignored).</summary>
    public static Options ParseArgs(string[] args)
    {
        int port = 8765;
        bool open = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (i + 1 >= args.Length) return new Options(port, open, false, "--port needs a value (1-65535).");
                    var text = args[++i];
                    if (!int.TryParse(text, out var p) || p is < 1 or > 65535) return new Options(port, open, false, $"--port must be a number between 1 and 65535 (got '{text}').");
                    port = p;
                    break;
                case "--no-browser": open = false; break;
                case "--help" or "-h" or "/?": return new Options(port, open, true, null);
                default: return new Options(port, open, false, $"Unknown option '{args[i]}'.");
            }
        }
        return new Options(port, open, false, null);
    }

    /// <summary>How to open `url` in the default browser on this platform, or null when nothing is known for it
    /// (the caller then tells the user to open the address by hand). The flags are parameters only for tests.</summary>
    public static ProcessStartInfo? BrowserCommand(string url, bool? windows = null, bool? macOS = null, bool? linux = null)
    {
        if (windows ?? OperatingSystem.IsWindows()) return new ProcessStartInfo(url) { UseShellExecute = true };
        if (macOS ?? OperatingSystem.IsMacOS()) return new ProcessStartInfo("open", url) { RedirectStandardError = true, RedirectStandardOutput = true };
        if (linux ?? OperatingSystem.IsLinux()) return new ProcessStartInfo("xdg-open", url) { RedirectStandardError = true, RedirectStandardOutput = true };
        return null;
    }
}
