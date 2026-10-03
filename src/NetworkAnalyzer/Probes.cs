using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetworkAnalyzer;

public readonly record struct ProbeResult(bool Ok, double? Ms, string Info);

/// <summary>
/// Network probes. None needs administrator rights:
/// ping = .NET <see cref="Ping"/> (system ICMP API, 1 ms resolution; "&lt;1 ms" is recorded as 0.5 ms);
/// TCP = connection time (≈ 1 RTT), used when a target does not answer ICMP;
/// DNS = hand-built UDP/53 query sent to ONE resolver, so Windows' DNS cache is bypassed;
/// traceroute = pings with increasing TTL (no text parsing, independent of the Windows language).
/// </summary>
public static class Probes
{
    static readonly Regex HostOk = new(@"^[A-Za-z0-9._:%\-]+$", RegexOptions.Compiled);

    /// <summary>Refuses anything that could be taken for an option or an injection.</summary>
    public static string ValidateHost(string? host)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0 || host.StartsWith('-') || host.Length > 253 || !HostOk.IsMatch(host))
            throw new ArgumentException($"Adresse invalide : '{host}'");
        return host;
    }

    // ------------------------------------------------------------------ resolution (cached by the caller)
    public static async Task<IPAddress?> ResolveAsync(string host, int family, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host, ct);
        var want = family == 6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return all.FirstOrDefault(a => a.AddressFamily == want) ?? all.FirstOrDefault();
    }

    // ------------------------------------------------------------------ ping
    public static string MapPingStatus(IPStatus s) => s switch
    {
        IPStatus.TimedOut => "timeout",
        IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable or IPStatus.DestinationUnreachable
            or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProhibited
            or IPStatus.DestinationScopeMismatch => "injoignable",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static ProbeResult FromReply(PingReply r)
    {
        if (r.Status == IPStatus.Success)
            return new ProbeResult(true, r.RoundtripTime == 0 ? 0.5 : r.RoundtripTime, "");
        return new ProbeResult(false, null, MapPingStatus(r.Status));
    }

    public static async Task<ProbeResult> PingAsync(IPAddress address, int timeoutMs = 1000)
    {
        try
        {
            using var ping = new Ping();
            return FromReply(await ping.SendPingAsync(address, timeoutMs));
        }
        catch (PlatformNotSupportedException) { return new ProbeResult(false, null, "erreur: ping indisponible"); }
        catch (Exception e) when (e is PingException or SocketException or InvalidOperationException)
        {
            return new ProbeResult(false, null, "erreur: " + (e.InnerException ?? e).GetType().Name);
        }
    }

    // ------------------------------------------------------------------ TCP
    public static async Task<ProbeResult> TcpAsync(string host, int port, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(host, port, cts.Token);
            return new ProbeResult(true, sw.Elapsed.TotalMilliseconds, "tcp");
        }
        catch (OperationCanceledException) { return new ProbeResult(false, null, "timeout"); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return new ProbeResult(false, null, "port fermé"); }
        catch (SocketException) { return new ProbeResult(false, null, "injoignable"); }
    }

    // ------------------------------------------------------------------ DNS
    static readonly Dictionary<int, string> RCodes = new() { [0] = "OK", [1] = "FORMERR", [2] = "SERVFAIL", [3] = "NXDOMAIN", [4] = "NOTIMP", [5] = "REFUSED" };

    public static byte[] BuildDnsQuery(string name, ushort qid, ushort qtype = 1)
    {
        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr[0..], qid);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], 1);
        ms.Write(hdr);
        foreach (var label in name.Trim('.').Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[0..], qtype);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        ms.Write(tail);
        return ms.ToArray();
    }

    /// <summary>(rcode, answer count), or null when this is not the expected reply.</summary>
    public static (int RCode, int Answers)? ParseDnsReply(ReadOnlySpan<byte> data, ushort qid)
    {
        if (data.Length < 12) return null;
        var id = BinaryPrimitives.ReadUInt16BigEndian(data);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (id != qid || (flags & 0x8000) == 0) return null;
        return (flags & 0x0F, BinaryPrimitives.ReadUInt16BigEndian(data[6..]));
    }

    /// <summary>Queries <paramref name="server"/> over UDP. <paramref name="acceptNxdomain"/>: NXDOMAIN counts as a valid reply.</summary>
    public static async Task<ProbeResult> DnsQueryAsync(string server, string name, int timeoutMs = 2000, bool acceptNxdomain = false, int port = 53)
    {
        if (!IPAddress.TryParse(server, out var ip)) return new ProbeResult(false, null, "erreur: serveur DNS invalide");
        var qid = (ushort)Random.Shared.Next(65536);
        var pkt = BuildDnsQuery(name, qid);
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var sock = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var sw = Stopwatch.StartNew();
            await sock.SendToAsync(pkt, SocketFlags.None, new IPEndPoint(ip, port), cts.Token);
            while (true)
            {
                int n = await sock.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                var r = ParseDnsReply(buf.AsSpan(0, n), qid);
                if (r is null) continue;
                double ms = sw.Elapsed.TotalMilliseconds;
                var (rcode, an) = r.Value;
                bool good = (rcode == 0 && an > 0) || (acceptNxdomain && (rcode == 0 || rcode == 3));
                return new ProbeResult(good, ms, RCodes.TryGetValue(rcode, out var s) ? s : $"rcode{rcode}");
            }
        }
        catch (OperationCanceledException) { return new ProbeResult(false, null, "timeout"); }
        catch (SocketException e) { return new ProbeResult(false, null, "erreur: " + e.SocketErrorCode); }
    }

    // ------------------------------------------------------------------ traceroute
    /// <summary>
    /// Cautious reading of a traceroute. A loss on an INTERMEDIATE hop is not a real loss when the following hops
    /// (and the destination) answer: routers often rate-limit their own ICMP replies.
    /// </summary>
    public static TraceAnalysis AnalyzeTrace(IReadOnlyList<TraceHop> hops, string? destIp = null)
    {
        var res = new TraceAnalysis();
        if (hops.Count == 0) { res.Notes.Add("Aucun saut lisible."); return res; }
        var last = hops[^1];
        res.Reached = last.Rtts.Count > 0 && (destIp is null || last.Ip == destIp);
        for (int i = 0; i < hops.Count - 1; i++)
            if (hops[i].Lost > 0 && hops.Skip(i + 1).Any(x => x.Rtts.Count > 0)) res.IntermediateLoss.Add(hops[i].Hop);
        if (res.IntermediateLoss.Count > 0)
            res.Notes.Add($"Perte(s) au(x) saut(s) {string.Join(", ", res.IntermediateLoss)} mais les sauts suivants répondent : très probablement une limitation ICMP du routeur, PAS une perte réelle.");
        var meds = hops.Where(h => h.Rtts.Count > 0).Select(h => (h.Hop, Ms: h.Rtts.Average(), h.Ip)).ToList();
        for (int i = 1; i < meds.Count; i++)
        {
            double b = meds[i - 1].Ms;
            if (meds[i].Ms - b >= 40 && meds.Skip(i).All(m => m.Ms >= b + 30))
            {
                res.Step = new TraceStep { Hop = meds[i].Hop, Ip = meds[i].Ip, FromMs = b, ToMs = meds[i].Ms };
                res.Notes.Add($"La latence passe de {b:0} à {meds[i].Ms:0} ms au saut {meds[i].Hop} et ne redescend pas jusqu'à la destination : l'augmentation commence à ce niveau du trajet.");
                break;
            }
        }
        res.DestLossPct = last.Sent > 0 ? 100.0 * last.Lost / last.Sent : null;
        if (!res.Reached) res.Notes.Add("La destination n'a pas répondu au traceroute (peut être un filtrage ICMP, pas forcément une panne).");
        else if (res.DestLossPct is > 0) res.Notes.Add($"Perte de {res.DestLossPct:0} % à la destination (sur {last.Sent} sondes : trop peu pour conclure seul).");
        return res;
    }

    public static async Task<TraceResult> TracerouteAsync(string host, int maxHops = 20, CancellationToken ct = default)
    {
        host = ValidateHost(host);
        IPAddress? dest;
        try { dest = await ResolveAsync(host, 4, ct); }
        catch (SocketException) { return new TraceResult { Error = "Nom introuvable." }; }
        if (dest is null) return new TraceResult { Error = "Nom introuvable." };
        var hops = new List<TraceHop>();
        try
        {
            for (int ttl = 1; ttl <= maxHops && !ct.IsCancellationRequested; ttl++)
            {
                // Windows reports RoundtripTime = 0 for "TTL expired" replies: the time is measured here instead, for every hop alike.
                var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
                {
                    using var p = new Ping();
                    var sw = Stopwatch.StartNew();
                    var rep = await p.SendPingAsync(dest, 800, new byte[32], new PingOptions(ttl, true));
                    return (Reply: rep, Ms: sw.Elapsed.TotalMilliseconds);
                }));
                var hop = new TraceHop { Hop = ttl, Sent = 3 };
                bool reached = false;
                foreach (var (r, ms) in replies)
                {
                    if (r.Status == IPStatus.TimedOut) { hop.Lost++; continue; }
                    hop.Rtts.Add(Math.Round(ms, 1));
                    hop.Ip ??= r.Address?.ToString();
                    if (r.Status == IPStatus.Success) reached = true;
                }
                hops.Add(hop);
                if (reached) break;
            }
        }
        catch (PlatformNotSupportedException) { return new TraceResult { Error = "Traceroute indisponible sur ce système (privilèges requis)." }; }
        catch (Exception e) when (e is PingException or SocketException) { return new TraceResult { Error = "Traceroute impossible : " + (e.InnerException ?? e).GetType().Name }; }
        return new TraceResult { Hops = hops, Analysis = AnalyzeTrace(hops, dest.ToString()) };
    }
}
