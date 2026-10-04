using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetworkAnalyzer;
using Xunit;

namespace NetworkAnalyzer.Tests;

public class TcpProbeTests
{
    [Fact]
    public async Task TheTimedConnectDoesNotIncludeNameResolution()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // a resolver that takes half a second, like a slow DNS answer
        async Task<IPAddress?> Slow(string host, CancellationToken ct) { await Task.Delay(500, ct); return IPAddress.Loopback; }
        var r = await Probes.TcpAsync("game.example.net", port, 3000, Slow);
        Assert.True(r.Ok, r.Info);
        Assert.True(r.Ms < 250, $"the connect took {r.Ms} ms: the name resolution was timed with it");
    }

    [Fact]
    public async Task AnAddressIsNotResolvedAndAnUnresolvableNameIsUnreachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True((await Probes.TcpAsync("127.0.0.1", port, 2000, (_, _) => throw new InvalidOperationException("must not resolve an address"))).Ok);
        var r = await Probes.TcpAsync("nowhere.example.net", port, 2000, (_, _) => Task.FromResult<IPAddress?>(null));
        Assert.False(r.Ok);
        Assert.Equal("unreachable", r.Info);
    }
}

public class TargetLabelTests
{
    [Fact]
    public void EveryBuiltTargetHasADisplayLabelDerivedFromItsIdentity()
    {
        var env = new EnvInfo { Ipv6Global = true, Active = new AdapterInfo { Gw4 = "192.168.0.1" } };
        var targets = Recorder.BuildTargets(env, "game.example.net:27015");
        Assert.Equal(6, targets.Count);
        Assert.All(targets, t => Assert.NotEqual(t.Id, t.Label));
        Assert.Equal("Cloudflare (1.1.1.1)", targets.Single(t => t.Id == "cloudflare").Label);
        Assert.Contains("game.example.net", targets.Single(t => t.Id == "custom").Label);
        Assert.False(string.IsNullOrWhiteSpace(targets.Single(t => t.Id == "gateway").Label));
    }
}

public class StatsTests
{
    static List<Sample> S(params double?[] vals) => vals.Select((v, i) => new Sample(i, v, v.HasValue, "")).ToList();
    static List<Sample> Range(int a, int b) => S(Enumerable.Range(a, b - a + 1).Select(x => (double?)x).ToArray());

    [Fact]
    public void MedianP95Max()
    {
        var r = Stats.Rtt(Range(1, 100))!;
        Assert.Equal(50.5, r.Median);
        Assert.Equal(95, r.P95);  // nearest rank: ceil(0.95·100) = 95th value
        Assert.Equal(100, r.Max);
        Assert.Equal(0, r.LossPct);
    }

    [Fact]
    public void P95IsNearestRankNotFloor()
    {
        Assert.Equal(10, Stats.Rtt(Range(1, 10))!.P95);  // ceil(9.5) = 10th value
        Assert.Equal(19, Stats.Rtt(Range(1, 20))!.P95);  // ceil(19) = 19th value
        Assert.Equal(7, Stats.Rtt(S(7))!.P95);
    }

    [Fact]
    public void Loss()
    {
        var r = Stats.Rtt(S(10, null, 10, null, 10, 10, 10, 10, 10, 10))!;
        Assert.Equal((10, 2, 20.0), (r.N, r.Lost, r.LossPct));
    }

    [Fact]
    public void JitterDefinition()
    {
        Assert.Equal(7.5, Stats.Jitter(S(10, 20, 15)));            // (|20-10| + |15-20|) / 2
        Assert.Null(Stats.Jitter(S(10, null, 50)));                // a loss breaks the chain
        Assert.Equal(3.0, Stats.Jitter(S(10, 12, null, 50, 54)));  // (2 + 4) / 2
    }

    [Fact]
    public void EdgeCases()
    {
        Assert.Null(Stats.Rtt(new List<Sample>()));
        var r = Stats.Rtt(S(null, null, null))!;
        Assert.Equal(100.0, r.LossPct);
        Assert.Null(r.Median);
        Assert.Null(Stats.Percentile(new List<double>(), 95));
    }

    [Fact]
    public void WindowAndOutside()
    {
        var s = Range(1, 5);
        Assert.Equal(3, Stats.Window(s, 1, 3).Count);  // t = 1, 2, 3 (indices)
        Assert.Equal(new double?[] { 1, 5 }, Stats.Outside(s, new List<(double, double)> { (1, 3) }).Select(x => x.V).ToArray());
    }
}

public class ProbeTests
{
    [Fact]
    public void HostValidationBlocksOptionInjection()
    {
        foreach (var bad in new[] { "-n 1000", "", "a b", "x;rm", "$(id)", "-f" })
            Assert.Throws<ArgumentException>(() => Probes.ValidateHost(bad));
        foreach (var good in new[] { "1.1.1.1", "jeu.example.net", "fe80::1%12", "2606:4700:4700::1111" })
            Assert.Equal(good, Probes.ValidateHost(good));
    }

    [Fact]
    public void PingStatusMapping()
    {
        Assert.Equal("timeout", Probes.MapPingStatus(IPStatus.TimedOut));
        Assert.Equal("unreachable", Probes.MapPingStatus(IPStatus.DestinationHostUnreachable));
        Assert.Equal("unreachable", Probes.MapPingStatus(IPStatus.DestinationNetworkUnreachable));
    }

    [Fact]
    public async Task PingLoopback()
    {
        var r = await Probes.PingAsync(IPAddress.Loopback, 1000);
        if (!r.Ok) return;  // ICMP can be unavailable in a sandbox: the status mapping is tested above
        Assert.True(r.Ms is >= 0 and < 500);
    }

    // ---- DNS
    static (Socket Sock, int Port) DnsStub(int rcode, int answers, bool silent = false)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(() =>
        {
            try
            {
                var buf = new byte[512];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = sock.ReceiveFrom(buf, ref from);
                if (silent) return;
                var reply = new byte[n];
                Array.Copy(buf, reply, n);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)(0x8180 | rcode));
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), (ushort)answers);
                sock.SendTo(reply, from);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        });
        return (sock, ((IPEndPoint)sock.LocalEndPoint!).Port);
    }

    static async Task<ProbeResult> Query(int rcode, int answers, bool acceptNx = false, bool silent = false, int timeout = 2000)
    {
        var (s, port) = DnsStub(rcode, answers, silent);
        try { return await Probes.DnsQueryAsync("127.0.0.1", "x.example.com", timeout, acceptNx, port); }
        finally { s.Dispose(); }
    }

    [Fact]
    public void DnsPacket()
    {
        var pkt = Probes.BuildDnsQuery("www.example.com", 0x1234);
        Assert.Equal(new byte[] { 0x12, 0x34 }, pkt[..2]);
        var label = new byte[] { 3, (byte)'w', (byte)'w', (byte)'w', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0 };
        Assert.True(pkt.AsSpan(12).IndexOf(label) >= 0);
        Assert.Null(Probes.ParseDnsReply(new byte[5], 1));
    }

    [Fact]
    public async Task AReplyFromAnotherSenderIsIgnored()
    {
        // the real server answers NXDOMAIN; a third party sends a "good" reply with the right id from another port just before
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.LocalEndPoint!).Port;
        _ = Task.Run(() =>
        {
            try
            {
                var buf = new byte[512];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = server.ReceiveFrom(buf, ref from);
                byte[] Reply(int rcode, int answers)
                {
                    var r = buf[..n];
                    BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)(0x8180 | rcode));
                    BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), (ushort)answers);
                    return r;
                }
                using var rogue = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                rogue.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                rogue.SendTo(Reply(0, 1), from);      // forged: right id, wrong sender
                Thread.Sleep(150);
                server.SendTo(Reply(3, 0), from);     // the real answer
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        });
        var res = await Probes.DnsQueryAsync("127.0.0.1", "x.example.com", 2000, false, port);
        Assert.False(res.Ok);
        Assert.Equal("NXDOMAIN", res.Info);
    }

    [Fact]
    public async Task DnsNamesAreValidatedAndInternationalNamesEncoded()
    {
        Assert.Throws<ArgumentException>(() => Probes.BuildDnsQuery("a..b.example", 1));                        // empty label
        Assert.Throws<ArgumentException>(() => Probes.BuildDnsQuery(new string('a', 64) + ".example", 1));    // label over 63 bytes
        Assert.Throws<ArgumentException>(() => Probes.BuildDnsQuery(string.Join(".", Enumerable.Repeat(new string('a', 60), 5)), 1));   // name over 253
        Assert.Equal(1, Probes.BuildDnsQuery("trailing.dot.example.", 1)[^5] == 0 ? 1 : 0);                     // a final dot is fine
        var idn = Probes.BuildDnsQuery("bücher.example", 1);
        Assert.True(idn.AsSpan(12).IndexOf(System.Text.Encoding.ASCII.GetBytes("xn--bcher-kva")) >= 0, "the name must be sent as punycode, not with '?'");
        Assert.DoesNotContain((byte)'?', idn);
        var r = await Probes.DnsQueryAsync("127.0.0.1", "a..b.example", 500);
        Assert.False(r.Ok);
        Assert.Equal("error:invalid_name", r.Info);
    }

    [Fact]
    public async Task DnsOk()
    {
        var r = await Query(0, 1);
        Assert.True(r.Ok);
        Assert.Equal("OK", r.Info);
        Assert.True(r.Ms >= 0);
    }

    [Fact]
    public async Task DnsNxdomainOnlyValidWhenAccepted()
    {
        var a = await Query(3, 0);
        Assert.False(a.Ok);
        Assert.Equal("NXDOMAIN", a.Info);
        var b = await Query(3, 0, acceptNx: true);
        Assert.True(b.Ok);
        Assert.Equal("NXDOMAIN", b.Info);
    }

    [Fact]
    public async Task DnsServfailIsFailureEvenWhenNxdomainAccepted()
    {
        var r = await Query(2, 0, acceptNx: true);
        Assert.False(r.Ok);
        Assert.Equal("SERVFAIL", r.Info);
    }

    [Fact]
    public async Task DnsTimeout()
    {
        var r = await Query(0, 0, silent: true, timeout: 300);
        Assert.Equal(new ProbeResult(false, null, "timeout"), r);
    }

    // ---- traceroute analysis
    static TraceHop Hop(int n, string? ip, double[] rtts, int lost) => new() { Hop = n, Ip = ip, Rtts = rtts.ToList(), Lost = lost, Sent = rtts.Length + lost };

    [Fact]
    public void IntermediateLossIsNotRealLoss()
    {
        var hops = new[] { Hop(1, "192.168.0.1", new[] { .5, .5, .5 }, 0), Hop(2, "10.20.0.1", new[] { 8.0, 9, 8 }, 0), Hop(3, null, Array.Empty<double>(), 3),
                           Hop(4, "80.10.1.1", new[] { 12.0, 11, 13 }, 0), Hop(5, "1.1.1.1", new[] { 13.0, 12, 12 }, 0) };
        var a = Probes.AnalyzeTrace(hops, "1.1.1.1");
        Assert.True(a.Reached);
        Assert.Equal(new[] { 3 }, a.IntermediateLoss);
        Assert.True(!a.DestLossPct.HasValue || a.DestLossPct == 0);
        Assert.Contains(Probes.TraceNotes(a), n => n.Contains("rate-limiting") && n.Contains("NOT a real loss"));
    }

    [Fact]
    public void LatencyStepDetected()
    {
        var hops = new[] { Hop(1, "192.168.0.1", new[] { .5, .5, .5 }, 0), Hop(2, "10.0.0.1", new[] { 10.0, 11, 10 }, 0), Hop(3, "80.1.1.1", new[] { 14.0, 15, 14 }, 0),
                           Hop(4, "80.2.2.2", new[] { 82.0, 85, 80 }, 0), Hop(5, "80.3.3.3", new[] { 84.0, 83, 85 }, 0), Hop(6, "8.8.8.8", new[] { 85.0, 86, 84 }, 0) };
        var a = Probes.AnalyzeTrace(hops, "8.8.8.8");
        Assert.Equal(4, a.Step!.Hop);
        Assert.Equal("80.2.2.2", a.Step.Ip);
    }

    [Fact]
    public void DestinationNotReached()
    {
        var hops = new[] { Hop(1, "192.168.0.1", new[] { .5, .5, .5 }, 0), Hop(2, "10.0.0.1", new[] { 10.0, 10 }, 1), Hop(3, null, Array.Empty<double>(), 3) };
        var a = Probes.AnalyzeTrace(hops, "1.1.1.1");
        Assert.False(a.Reached);
        Assert.Equal(100.0, a.DestLossPct);
        Assert.False(Probes.AnalyzeTrace(new List<TraceHop>()).Reached);
    }
}

public class SysInfoTests
{
    const string NetshFr = @"
Il y a 1 interface sur le système :

    Nom                    : Wi-Fi
    Description            : Intel(R) Wi-Fi 6 AX201 160MHz
    GUID                   : 11111111-2222-3333-4444-555555555555
    Adresse physique       : aa:bb:cc:dd:ee:ff
    État                   : connecté
    SSID                   : MaBox
    BSSID                  : 11:22:33:44:55:66
    Type de réseau         : Infrastructure
    Type de radio          : 802.11ax
    Authentification       : WPA2-Personnel
    Canal                  : 36
    Débit de réception (Mbit/s)  : 866,7
    Débit de transmission (Mbit/s) : 780
    Signal                 : 92%
    Profil                 : MaBox
";
    static readonly string NetshEn = NetshFr.Replace("Nom ", "Name").Replace("État", "State").Replace("connecté", "connected").Replace("Canal", "Channel")
        .Replace("Débit de réception (Mbit/s)", "Receive rate (Mbps)").Replace("Débit de transmission (Mbit/s)", "Transmit rate (Mbps)").Replace("Type de radio", "Radio type");
    static readonly string NetshDown = NetshFr.Replace("connecté", "déconnecté");
    const string NetworksFr = @"
SSID 1 : MaBox
    Type de réseau          : Infrastructure
    BSSID 1                 : 11:22:33:44:55:66
         Signal             : 92%
         Type de radio      : 802.11ax
         Canal              : 36
SSID 2 : Voisin
    BSSID 1                 : aa:aa:aa:aa:aa:01
         Signal             : 70%
         Canal              : 36
    BSSID 2                 : aa:aa:aa:aa:aa:02
         Signal             : 20%
         Canal              : 36
SSID 3 : Autre
    BSSID 1                 : bb:bb:bb:bb:bb:01
         Signal             : 60%
         Canal              : 6
";

    [Fact]
    public void NetshFrench()
    {
        var w = SysInfo.ParseNetshInterfaces(NetshFr)!;
        Assert.Equal((92, 36, 866.7, 780.0), (w.Signal, w.Channel, w.RxRate, w.TxRate));
        Assert.Equal(("11:22:33:44:55:66", "802.11ax"), (w.Bssid, w.Radio));
        Assert.StartsWith("5 GHz", w.Band);
    }

    [Fact]
    public void PlatformNameFollowsTheOperatingSystem()
    {
        var expected = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        Assert.Equal(expected, SysInfo.PlatformName());
        Assert.NotEqual("linux", SysInfo.PlatformName(windows: false, macOS: true));   // macOS is not Linux
        Assert.Equal("darwin", SysInfo.PlatformName(windows: false, macOS: true));
        Assert.Equal("win32", SysInfo.PlatformName(windows: true, macOS: false));
    }

    [Theory]
    [InlineData("2001:db8::1", true)] [InlineData("2a01:cb00:1::5%12", true)]
    [InlineData("fd12:3456:789a::1", false)] [InlineData("fc00::1", false)]   // unique local: private, not Internet
    [InlineData("fe80::1%12", false)] [InlineData("::1", false)] [InlineData("ff02::1", false)] [InlineData("::", false)]
    [InlineData("192.168.0.2", false)] [InlineData("not an address", false)]
    public void OnlyARoutableIpv6AddressCountsAsGlobal(string address, bool expected) => Assert.Equal(expected, SysInfo.IsGlobalIpv6(address));

    [Fact]
    public async Task ACommandThatTimesOutIsKilled()
    {
        if (!OperatingSystem.IsLinux()) return;   // uses /proc to see whether the child is still alive
        var pidFile = Path.Combine(Path.GetTempPath(), "na-pid-" + Guid.NewGuid().ToString("N"));
        var started = DateTime.UtcNow;
        var output = await SysInfo.RunAsync("/bin/sh", $"-c \"echo $$ > {pidFile}; exec sleep 30\"", 700);
        Assert.Equal("", output);
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 10);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        File.Delete(pidFile);
        await Task.Delay(300);
        Assert.False(Directory.Exists($"/proc/{pid}"), "the timed-out process is still running");
    }

    [Fact]
    public void RssiKeepsItsNegativeSign()
    {
        var w = SysInfo.ParseNetshInterfaces(NetshFr.Replace("    Signal ", "    RSSI                   : -57\n    Signal "))!;
        Assert.Equal(-57.0, w.Rssi);
        Assert.Equal(92, w.Signal);
        Assert.Equal(866.7, w.RxRate);   // other numbers are untouched
    }

    [Theory]
    [InlineData("État", "etat")]
    [InlineData("Débit de réception (Mbit/s)", "debit de reception (mbit/s)")]
    [InlineData("Type de réseau", "type de reseau")]
    [InlineData("  CANAL ", "canal")]
    [InlineData("Débit de transmission", "debit de transmission")]
    public void NormStripsAccentsWithoutGlobalizationData(string input, string expected) => Assert.Equal(expected, SysInfo.Norm(input));

    [Fact]
    public void NetshEnglish()
    {
        var w = SysInfo.ParseNetshInterfaces(NetshEn)!;
        Assert.Equal((92, 36, 866.7, 780.0), (w.Signal, w.Channel, w.RxRate, w.TxRate));
    }

    [Fact]
    public void NetshDisconnectedOrGarbage()
    {
        Assert.Null(SysInfo.ParseNetshInterfaces(NetshDown));
        Assert.Null(SysInfo.ParseNetshInterfaces(""));
        Assert.Null(SysInfo.ParseNetshInterfaces("L'accès à l'emplacement est requis."));
    }

    [Fact]
    public void BandFromChannel()
    {
        Assert.Equal("2.4 GHz", SysInfo.BandFromChannel(6));
        Assert.Null(SysInfo.BandFromChannel(null));
        // 6 GHz channels start at 1 too: only a radio that can use 6 GHz (802.11ax / be) makes the low channels ambiguous
        Assert.Equal("2.4 GHz", SysInfo.BandFromChannel(1, "802.11n"));
        Assert.Equal("2.4 GHz", SysInfo.BandFromChannel(11, "802.11ac"));
        Assert.Equal("2.4 GHz / 6 GHz", SysInfo.BandFromChannel(1, "802.11ax"));
        Assert.Equal("2.4 GHz / 6 GHz", SysInfo.BandFromChannel(9, "802.11be"));
        Assert.Equal("6 GHz", SysInfo.BandFromChannel(193));
        Assert.Equal("6 GHz", SysInfo.BandFromChannel(233));
        Assert.Null(SysInfo.BandFromChannel(234));
    }

    [Fact]
    public void ParsedBandUsesTheRadioTypeToResolveLowChannels()
    {
        var w = SysInfo.ParseNetshInterfaces(NetshFr.Replace("Canal                  : 36", "Canal                  : 5"))!;
        Assert.Equal("2.4 GHz / 6 GHz", w.Band);   // an 802.11ax radio on channel 5: 2.4 GHz or Wi-Fi 6E
    }

    [Fact]
    public void NeighborsIgnoreSsidAndCountSameChannel()
    {
        var aps = SysInfo.ParseNetshNetworks(NetworksFr);
        Assert.Equal(4, aps.Count);
        var n = SysInfo.NeighborsSummary(aps, 36);
        Assert.Equal((4, 3, 2), (n.Total, n.SameChannel, n.SameChannelStrong));
        Assert.DoesNotContain("Voisin", Json.To(n));
    }

    [Fact]
    public void TheUsersOwnAccessPointIsNotItsOwnNeighbour()
    {
        var aps = SysInfo.ParseNetshNetworks(NetworksFr);
        Assert.Equal("11:22:33:44:55:66", aps[0].Bssid);
        var n = SysInfo.NeighborsSummary(aps, 36, "11:22:33:44:55:66");
        Assert.Equal((3, 2, 1), (n.Total, n.SameChannel, n.SameChannelStrong));   // 4 / 3 / 2 with our own AP counted
        Assert.Equal((3, 2, 1), (SysInfo.NeighborsSummary(aps, 36, "11:22:33:44:55:66".ToUpperInvariant()).Total, n.SameChannel, n.SameChannelStrong));   // case does not matter
        Assert.DoesNotContain("aa:aa", Json.To(n));   // the neighbours' addresses are compared, never stored
    }

    static AdapterInfo Ad(string name, string desc, string kind, string status, string? gw4 = null, string? gw6 = null, string[]? v4 = null, string[]? v6 = null, int metric = 1) => new()
    {
        Name = name, Description = desc, Kind = kind, Status = status, Gw4 = gw4, Gw6 = gw6, Ipv4 = (v4 ?? Array.Empty<string>()).ToList(), Ipv6 = (v6 ?? Array.Empty<string>()).ToList(), Dns = new() { "192.168.0.1" }, Metric = metric,
    };

    [Fact]
    public void ActiveInterfaceSkipsVpnAndVirtualAndReportsVpnAndIpv6()
    {
        var env = SysInfo.Summarize(new List<AdapterInfo>
        {
            Ad("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", "wifi", "Up", "192.168.0.1", "fe80::ff", new[] { "192.168.0.20" }, new[] { "2a01:cb00::5", "fe80::1" }, metric: 0),
            Ad("Ethernet", "Realtek PCIe GbE", "ethernet", "Down"),
            Ad("NordLynx", "NordLynx Tunnel", "vpn", "Up", "10.5.0.1", null, new[] { "10.5.0.2" }),
            Ad("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "virtual", "Up", null, null, new[] { "172.20.0.1" }),
        });
        Assert.Equal(("Wi-Fi", "wifi", "192.168.0.1"), (env.Active!.Name, env.Active.Kind, env.Active.Gw4));
        Assert.True(env.Vpn.Active);
        Assert.Equal(new[] { "NordLynx" }, env.Vpn.Adapters);
        Assert.True(env.Ipv6Global);
        Assert.Contains(SysInfo.Notes(env), n => n.Contains("VPN"));
    }

    [Fact]
    public void BestRouteMetricWinsAmongSeveralCandidates()
    {
        var env = SysInfo.Summarize(new List<AdapterInfo>
        {
            Ad("Ethernet", "Realtek PCIe GbE", "ethernet", "Up", "192.168.1.1", null, new[] { "192.168.1.5" }, metric: 1),
            Ad("Wi-Fi", "Intel Wi-Fi", "wifi", "Up", "192.168.0.1", null, new[] { "192.168.0.20" }, metric: 0),
        });
        Assert.Equal("Wi-Fi", env.Active!.Name);
        Assert.Contains(SysInfo.Notes(env), n => n.Contains("Several interfaces"));
    }

    [Fact]
    public void NoActiveConnection()
    {
        var env = SysInfo.Summarize(new List<AdapterInfo>());
        Assert.Null(env.Active);
        Assert.NotEmpty(SysInfo.Notes(env));
    }

    [Theory]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, "wifi")]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, "ethernet")]
    [InlineData("NordLynx", "NordLynx Tunnel", NetworkInterfaceType.Unknown, "vpn")]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, "virtual")]
    [InlineData("Connexion au réseau local", "Adaptateur", NetworkInterfaceType.Tunnel, "vpn")]
    public void AdapterKind(string name, string desc, NetworkInterfaceType type, string expected) => Assert.Equal(expected, SysInfo.Kind(name, desc, type));

    [Theory]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("vEthernet (External Network Switch)-QoS Packet Scheduler-0000", true)]
    [InlineData("vSwitch (Default Switch)-Hyper-V Virtual Switch Extension Filter-0000", true)]
    [InlineData("Ethernet-WFP 802.3 MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet", false)]
    [InlineData("vEthernet (External Network Switch)", false)]
    [InlineData("Wi-Fi 2", false)]
    public void FilterBindingsAreNotInterfaces(string name, bool expected) => Assert.Equal(expected, SysInfo.IsFilterInstance(name));

    // Names read on a real Windows 11 machine (Hyper-V "External Network Switch" bridging an Intel NIC).
    [Theory]
    [InlineData("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", NetworkInterfaceType.Tunnel, "virtual")]
    [InlineData("6to4 Adapter", "Microsoft 6to4 Adapter", NetworkInterfaceType.Tunnel, "virtual")]
    [InlineData("Local Area Connection* 4", "WAN Miniport (PPTP)", NetworkInterfaceType.Ppp, "virtual")]
    [InlineData("Local Area Connection* 3", "WAN Miniport (L2TP)", NetworkInterfaceType.Ppp, "virtual")]
    [InlineData("Ethernet (Kernel Debugger)", "Microsoft Kernel Debug Network Adapter", NetworkInterfaceType.Ethernet, "virtual")]
    [InlineData("Ethernet", "Intel(R) I211 Gigabit Network Connection", NetworkInterfaceType.Ethernet, "ethernet")]
    [InlineData("vEthernet (External Network Switch)", "Hyper-V Virtual Ethernet Adapter #2", NetworkInterfaceType.Ethernet, "virtual")]
    [InlineData("Cisco AnyConnect", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", NetworkInterfaceType.Ethernet, "vpn")]
    [InlineData("OpenVPN", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, "vpn")]
    public void WindowsBuiltinsAreNotVpn(string name, string desc, NetworkInterfaceType type, string expected) => Assert.Equal(expected, SysInfo.Kind(name, desc, type));

    [Fact]
    public void HyperVBridgedMachineKeepsItsRealConnection()
    {
        // Real case: the IP and the gateway sit on the virtual external switch, the physical NIC has neither.
        var env = SysInfo.Summarize(new List<AdapterInfo>
        {
            Ad("vEthernet (External Network Switch)", "Hyper-V Virtual Ethernet Adapter #2", "virtual", "Up", "192.168.0.1", null, new[] { "192.168.0.50" }, metric: 0),
            Ad("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "virtual", "Up", null, null, new[] { "172.24.0.1" }),
            Ad("Ethernet", "Intel(R) I211 Gigabit Network Connection", "ethernet", "Up"),
            Ad("Local Area Connection* 5", "WAN Miniport (PPPOE)", "virtual", "Down"),
        });
        Assert.Equal("vEthernet (External Network Switch)", env.Active!.Name);
        Assert.Equal("ethernet", env.Active.Kind);
        Assert.Equal("192.168.0.1", env.Active.Gw4);
        Assert.Contains(SysInfo.Notes(env), n => n.Contains("virtual interface") && n.Contains("Ethernet"));
        Assert.False(env.Vpn.Active);
    }

    [Fact]
    public void LiveTeredoTunnelIsNotReportedAsVpn()
    {
        var teredo = Ad("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", SysInfo.Kind("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", NetworkInterfaceType.Tunnel), "Up", v6: new[] { "2001:0:1::2" });
        var env = SysInfo.Summarize(new List<AdapterInfo> { Ad("Ethernet", "Realtek", "ethernet", "Up", "192.168.0.1", null, new[] { "192.168.0.5" }, metric: 0), teredo });
        Assert.False(env.Vpn.Active);
        Assert.Equal("Ethernet", env.Active!.Name);
    }

    [Fact]
    public void RealEnvironmentCollectDoesNotThrow()
    {
        var env = SysInfo.Collect();
        Assert.NotNull(env.Interfaces);
    }
}
