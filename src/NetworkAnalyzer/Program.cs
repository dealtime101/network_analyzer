using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetworkAnalyzer;

// Network Analyzer — local web interface on 127.0.0.1 only (English by default, French on demand in the page).
int port = 8765;
bool openBrowser = true;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p)) port = p;
    else if (args[i] == "--no-browser") openBrowser = false;
    else if (args[i] is "--help" or "-h" or "/?")
    {
        Console.WriteLine("NetworkAnalyzer [--port 8765] [--no-browser]\nLocal network analyzer: interface on http://127.0.0.1:<port> (reachable from this computer only).");
        return 0;
    }
}
try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

static void Open(string url)
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo("xdg-open", url) { RedirectStandardError = true, RedirectStandardOutput = true });
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
}

// Already running? (same program answering on the port) → just open the browser on it.
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
    var json = await http.GetStringAsync($"http://127.0.0.1:{port}/api/identity");
    if (JsonDocument.Parse(json).RootElement.TryGetProperty("app", out var a) && a.GetString() == "NetworkAnalyzer")
    {
        Console.WriteLine($"Network Analyzer is already running: http://127.0.0.1:{port}");
        if (openBrowser) Open($"http://127.0.0.1:{port}");
        return 0;
    }
}
catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }

var app = new App();
var (web, actual) = await Api.StartAsync(app, port);
var url = $"http://127.0.0.1:{actual}";
Console.WriteLine($"Network Analyzer {AppVersion.Display} — interface: {url}  (reachable from this computer only)\nData: {app.Store.DataDir}\nPress Ctrl+C or close this window to quit.");
if (openBrowser) Open(url);

// The host drives the shutdown (Ctrl+C, SIGTERM, closing the console window): wait for it, then stop the session cleanly.
await web.WaitForShutdownAsync();
await app.StopSessionAsync();
await web.StopAsync();
return 0;
