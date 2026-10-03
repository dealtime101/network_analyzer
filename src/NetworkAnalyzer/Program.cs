using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetworkAnalyzer;

// Analyseur réseau local — interface web sur 127.0.0.1 uniquement.
int port = 8765;
bool openBrowser = true;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p)) port = p;
    else if (args[i] == "--no-browser") openBrowser = false;
    else if (args[i] is "--help" or "-h" or "/?")
    {
        Console.WriteLine("NetworkAnalyzer [--port 8765] [--no-browser]\nAnalyseur réseau local : interface sur http://127.0.0.1:<port> (accessible uniquement depuis cet ordinateur).");
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
    var json = await http.GetStringAsync($"http://127.0.0.1:{port}/api/identite");
    if (JsonDocument.Parse(json).RootElement.TryGetProperty("app", out var a) && a.GetString() == "NetworkAnalyzer")
    {
        Console.WriteLine($"Analyseur réseau déjà lancé : http://127.0.0.1:{port}");
        if (openBrowser) Open($"http://127.0.0.1:{port}");
        return 0;
    }
}
catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }

var app = new App();
var (web, actual) = await Api.StartAsync(app, port);
var url = $"http://127.0.0.1:{actual}";
Console.WriteLine($"Analyseur réseau {AppVersion.Display} — interface : {url}  (accessible uniquement depuis cet ordinateur)\nDonnées : {app.Store.DataDir}\nCtrl+C ou fermeture de cette fenêtre pour quitter.");
if (openBrowser) Open(url);

// The host drives the shutdown (Ctrl+C, SIGTERM, closing the console window): wait for it, then stop the session cleanly.
await web.WaitForShutdownAsync();
await app.StopSessionAsync();
await web.StopAsync();
return 0;
