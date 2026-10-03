using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NetworkAnalyzer;

/// <summary>
/// Network environment detection. Interfaces come from <see cref="NetworkInterface"/> (no PowerShell, no admin);
/// the interface really used for Internet is the one Windows picks for a route to 1.1.1.1 (GetBestInterface:
/// honours route metrics and VPNs). Wi-Fi details come from `netsh wlan` (localized output: French and English labels
/// are recognised; Windows may require the location permission to expose them).
/// </summary>
public static partial class SysInfo
{
    static readonly Regex VpnRx = new(@"vpn|tap-windows|wintun|wireguard|openvpn|nordlynx|tailscale|zerotier|fortinet|forticlient|anyconnect|globalprotect|pulse secure|protonvpn|mullvad|warp|softether|l2tp|sstp|\bppp", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex VirtualRx = new(@"hyper-v|vmware|virtualbox|vethernet|wsl|docker|loopback|bluetooth|miniport|isatap|teredo|veth|br-|virbr", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WifiRx = new(@"wi-?fi|wireless|802\.11|wlan|sans fil", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [DllImport("iphlpapi.dll")]
    static extern int GetBestInterface(uint destAddr, out uint ifIndex);

    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    // Windows' own plumbing: never a VPN (a live Teredo tunnel would otherwise be reported as one).
    static readonly Regex WindowsBuiltinRx = new(@"^WAN Miniport|Teredo|6to4|IP-HTTPS|ISATAP|Kernel Debug", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Per-adapter filter bindings (WFP, QoS Packet Scheduler, Hyper-V switch extension) show up as extra "interfaces" named "<adapter>-<filter>-0000".
    static readonly Regex FilterInstanceRx = new(@"-(WFP .*|QoS Packet Scheduler|.*Virtual Switch Extension Filter)-\d{4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsFilterInstance(string name) => FilterInstanceRx.IsMatch(name);

    public static string Kind(string name, string description, NetworkInterfaceType type)
    {
        var text = $"{name} {description}";
        if (WindowsBuiltinRx.IsMatch(description)) return "virtuel";
        if (VpnRx.IsMatch(text) || type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return "vpn";
        if (VirtualRx.IsMatch(text)) return "virtuel";
        if (type == NetworkInterfaceType.Wireless80211 || WifiRx.IsMatch(text)) return "wifi";
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit) return "ethernet";
        return "autre";
    }

    static string Speed(long bps) => bps <= 0 ? "" : bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0.#} Mbps";

    public static EnvInfo Collect()
    {
        var ifaces = new List<AdapterInfo>();
        int best = -1;
        if (OperatingSystem.IsWindows())
        {
            try { if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0), out var idx) == 0) best = (int)idx; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsFilterInstance(ni.Name)) continue;
            var ip = ni.GetIPProperties();
            int index = 0;
            try { index = ip.GetIPv4Properties().Index; } catch (NetworkInformationException) { try { index = ip.GetIPv6Properties().Index; } catch (NetworkInformationException) { } }
            string Gw(AddressFamily f) => ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == f && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))?.ToString() ?? "";
            var gw4 = Gw(AddressFamily.InterNetwork);
            var gw6 = Gw(AddressFamily.InterNetworkV6);
            ifaces.Add(new AdapterInfo
            {
                Name = ni.Name, Index = index, Description = ni.Description, Status = ni.OperationalStatus.ToString(), LinkSpeed = Speed(ni.Speed),
                Kind = Kind(ni.Name, ni.Description, ni.NetworkInterfaceType),
                Ipv4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(),
                Ipv6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.Address.ToString()).ToList(),
                Gw4 = gw4.Length > 0 ? gw4 : null, Gw6 = gw6.Length > 0 ? gw6 : null,
                Dns = ip.DnsAddresses.Select(a => a.ToString()).ToList(),
                Metric = index == best ? 0 : 1,
            });
        }
        var env = Summarize(ifaces);
        env.Platform = OperatingSystem.IsWindows() ? "win32" : "linux";
        return env;
    }

    public static EnvInfo Summarize(List<AdapterInfo> ifaces)
    {
        bool IsUp(AdapterInfo i) => i.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) || i.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        var vpnUp = ifaces.Where(i => i.Kind == "vpn" && IsUp(i) && (i.Ipv4.Count > 0 || i.Ipv6.Count > 0)).ToList();
        // A virtual interface qualifies only if it carries the default gateway (e.g. a Hyper-V "External Network Switch" bridging the NIC).
        var cands = ifaces.Where(i => (i.Gw4 != null || i.Gw6 != null) && i.Kind != "vpn" && IsUp(i)).OrderBy(i => i.Metric).ToList();
        var active = cands.FirstOrDefault();
        var env = new EnvInfo { Interfaces = ifaces, Active = active, Vpn = new VpnInfo { Active = vpnUp.Count > 0, Adapters = vpnUp.Select(i => i.Name).ToList() } };
        if (cands.Count > 1)
            env.Notes.Add($"Plusieurs interfaces avec passerelle ({string.Join(", ", cands.Select(i => i.Name))}) : la plus prioritaire est mesurée ({active!.Name}).");
        if (active is { Kind: "virtuel" })
        {
            var phys = ifaces.FirstOrDefault(i => i.Kind is "wifi" or "ethernet" && IsUp(i) && i.Gw4 == null && i.Gw6 == null);
            if (phys != null)
            {
                env.Notes.Add($"« {active.Name} » est une interface virtuelle (pont Hyper-V / machine virtuelle) : la liaison physique serait « {phys.Name} » ({(phys.Kind == "wifi" ? "Wi-Fi" : "Ethernet")}), le type de liaison en est déduit.");
                active.Kind = phys.Kind;
                if (active.LinkSpeed.Length == 0) active.LinkSpeed = phys.LinkSpeed;
            }
        }
        if (vpnUp.Count > 0)
            env.Notes.Add($"VPN détecté ({string.Join(", ", vpnUp.Select(i => i.Name))}) : les mesures Internet passent peut-être par le tunnel ; la passerelle mesurée est celle de l'interface physique.");
        if (active is null) env.Notes.Add("Aucune connexion active avec passerelle détectée.");
        env.Ipv6Global = active != null && active.Gw6 != null && active.Ipv6.Any(a => !a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("::1"));
        return env;
    }

    // ------------------------------------------------------------------ Wi-Fi (netsh wlan)
    // Explicit accent table: string.Normalize() does nothing useful when InvariantGlobalization is on.
    const string Accented = "àâäãåáéèêëíìîïóòôöõúùûüçñ";
    const string Plain = "aaaaaaeeeeiiiiooooouuuucn";

    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = char.ToLowerInvariant(ch == ' ' || ch == ' ' ? ' ' : ch);
            int i = Accented.IndexOf(c);
            sb.Append(i >= 0 ? Plain[i] : c);
        }
        return sb.ToString().Trim();
    }

    static double? Num(string v)
    {
        var m = NumRx().Match(v);
        return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex NumRx();

    public static string? BandFromChannel(int? ch) => ch switch
    {
        null => null,
        >= 1 and <= 14 => "2,4 GHz",
        >= 36 and <= 177 => "5 GHz (ou 6 GHz : le numéro de canal seul ne permet pas de trancher)",
        _ => null,
    };

    /// <summary>`netsh wlan show interfaces` (fr/en) → first connected adapter, or null.</summary>
    public static WifiInfo? ParseNetshInterfaces(string text)
    {
        var blocks = new List<WifiInfo>();
        WifiInfo? cur = null;
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(':');
            if (i < 0) continue;
            var k = Norm(line[..i]);
            var v = line[(i + 1)..].Trim();
            if (k is "nom" or "name") { cur = new WifiInfo(); blocks.Add(cur); }
            if (cur is null) continue;
            if (k is "etat" or "state") cur.Connected = Norm(v).StartsWith("connect");
            else if (k == "ssid") cur.Ssid = v;
            else if (k is "bssid" or "ap bssid") cur.Bssid = v;
            else if (k.StartsWith("type de radio") || k.StartsWith("radio type")) cur.Radio = v;
            else if (k is "canal" or "channel") cur.Channel = Num(v) is { } c ? (int)c : null;
            else if (k.StartsWith("bande") || k == "band") cur.Band = v;
            else if (k == "signal") cur.Signal = Num(v) is { } s ? (int)s : null;
            else if (k == "rssi") cur.Rssi = Num(v);
            else if (k.StartsWith("debit de reception") || k.StartsWith("receive rate")) cur.RxRate = Num(v);
            else if (k.StartsWith("debit de transmission") || k.StartsWith("transmit rate")) cur.TxRate = Num(v);
        }
        var b = blocks.FirstOrDefault(x => x.Connected && x.Signal.HasValue);
        if (b != null) b.Band ??= BandFromChannel(b.Channel);
        return b;
    }

    public sealed record Ap(string? Bssid, int? Signal, int? Channel);

    /// <summary>`netsh wlan show networks mode=bssid` → access points (neighbours' SSIDs are deliberately ignored).</summary>
    public static List<Ap> ParseNetshNetworks(string text)
    {
        var aps = new List<(int? Signal, int? Channel)>();
        int? sig = null, ch = null;
        bool open = false;
        void Flush() { if (open) aps.Add((sig, ch)); sig = ch = null; }
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(':');
            if (i < 0) continue;
            var k = Norm(line[..i]);
            var v = line[(i + 1)..];
            if (k.StartsWith("bssid")) { Flush(); open = true; }
            else if (open && k == "signal") sig = Num(v) is { } s ? (int)s : null;
            else if (open && k is "canal" or "channel") ch = Num(v) is { } c ? (int)c : null;
        }
        Flush();
        return aps.Select(a => new Ap(null, a.Signal, a.Channel)).ToList();
    }

    public static WifiNeighbors NeighborsSummary(IReadOnlyList<Ap> aps, int? myChannel)
    {
        var same = aps.Where(a => a.Channel != null && a.Channel == myChannel).ToList();
        return new WifiNeighbors
        {
            Total = aps.Count, SameChannel = same.Count, SameChannelStrong = same.Count(a => (a.Signal ?? 0) >= 50),
            Channels = aps.Where(a => a.Channel != null).GroupBy(a => a.Channel!.Value).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
        };
    }

    static async Task<string> RunAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var enc = OperatingSystem.IsWindows() ? CodePagesEncodingProvider.Instance.GetEncoding((int)GetOEMCP()) : null;
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = enc ?? Encoding.UTF8 };
            using var p = Process.Start(psi);
            if (p is null) return "";
            using var cts = new CancellationTokenSource(timeoutMs);
            var outp = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return outp;
        }
        catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    /// <summary>Current Wi-Fi details, or null when Windows does not expose them (no Wi-Fi, location permission refused…).</summary>
    public static async Task<WifiInfo?> ReadWifiAsync()
        => OperatingSystem.IsWindows() ? ParseNetshInterfaces(await RunAsync("netsh", "wlan show interfaces", 10000)) : null;

    public static async Task<WifiNeighbors?> ReadNeighborsAsync(int? myChannel)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var aps = ParseNetshNetworks(await RunAsync("netsh", "wlan show networks mode=bssid", 15000));
        return aps.Count > 0 ? NeighborsSummary(aps, myChannel) : null;
    }
}
