using System.Diagnostics;

namespace NetworkAnalyzer;

/// <summary>The start-up logic of Program.cs, kept here so that it can be tested.</summary>
public static class Launcher
{
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
