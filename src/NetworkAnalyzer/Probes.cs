using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetworkAnalyzer;

/// <summary>Outcome of one probe. Info holds a short code: "" (ok), timeout, unreachable, error, port_closed, tcp, truncated, or a DNS rcode name.</summary>
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
            throw new ArgumentException(Loc.T("err.invalid_host", host));
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
            or IPStatus.DestinationScopeMismatch => "unreachable",
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
        catch (PlatformNotSupportedException) { return new ProbeResult(false, null, "ping_unavailable"); }
        catch (Exception e) when (e is PingException or SocketException or InvalidOperationException)
        {
            return new ProbeResult(false, null, "error:" + (e.InnerException ?? e).GetType().Name);
        }
    }

    // ------------------------------------------------------------------ TCP
    /// <summary>Time of the TCP handshake only (≈ 1 RTT). A host name is resolved first, outside the timed part;
    /// `resolve` replaces the system resolver (tests).</summary>
    public static async Task<ProbeResult> TcpAsync(string host, int port, int timeoutMs = 2000, Func<string, CancellationToken, Task<IPAddress?>>? resolve = null)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var ip = IPAddress.TryParse(host, out var parsed) ? parsed : await (resolve ?? ((h, t) => ResolveAsync(h, 0, t)))(host, cts.Token);
            if (ip is null) return new ProbeResult(false, null, "unreachable");
            var sw = Stopwatch.StartNew();
            using var c = new TcpClient(ip.AddressFamily);
            await c.ConnectAsync(ip, port, cts.Token);
            return new ProbeResult(true, sw.Elapsed.TotalMilliseconds, "tcp");
        }
        catch (OperationCanceledException) { return new ProbeResult(false, null, "timeout"); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return new ProbeResult(false, null, "port_closed"); }
        catch (SocketException) { return new ProbeResult(false, null, "unreachable"); }
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
        string ascii;
        try { ascii = new System.Globalization.IdnMapping().GetAscii(name.Trim().TrimEnd('.')); }  // international names become punycode instead of '?'
        catch (ArgumentException) { throw new ArgumentException(Loc.T("err.invalid_host", name)); }
        if (ascii.Length == 0 || ascii.Length > 253) throw new ArgumentException(Loc.T("err.invalid_host", name));
        foreach (var label in ascii.Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            if (b.Length is 0 or > 63) throw new ArgumentException(Loc.T("err.invalid_host", name));  // empty or too long: the packet would be malformed
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

    /// <summary>(rcode, answer count, truncated), or null when this is not a complete reply to the question that was asked:
    /// wrong id, not a response, not a standard query, another question than <paramref name="query"/>'s, or announced records the packet does not carry.
    /// A truncated reply (TC bit) is returned as such: its sections are partial, so they are not walked.</summary>
    public static (int RCode, int Answers, bool Truncated)? ParseDnsReply(ReadOnlySpan<byte> data, ushort qid, ReadOnlySpan<byte> query = default)
    {
        if (data.Length < 12) return null;
        var id = BinaryPrimitives.ReadUInt16BigEndian(data);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (id != qid || (flags & 0x8000) == 0 || ((flags >> 11) & 0xF) != 0) return null;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        int pos = 12;
        if (query.Length > 12)
        {
            var asked = query[12..];                       // name + type + class, as built by BuildDnsQuery
            if (qd != 1 || data.Length < 12 + asked.Length || !System.Text.Ascii.EqualsIgnoreCase(data.Slice(12, asked.Length), asked)) return null;
            pos += asked.Length;
        }
        else
            for (int i = 0; i < qd; i++) { if (!SkipName(data, ref pos) || (pos += 4) > data.Length) return null; }
        int rcode = flags & 0x0F;
        if ((flags & 0x0200) != 0) return (rcode, an, true);
        for (int i = 0; i < an; i++)
        {
            if (!SkipName(data, ref pos) || pos + 10 > data.Length) return null;
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
            pos += 10 + rdlen;
            if (pos > data.Length) return null;
        }
        return (rcode, an, false);
    }

    /// <summary>Moves past a (possibly compressed) domain name; false when it runs off the packet.</summary>
    static bool SkipName(ReadOnlySpan<byte> d, ref int pos)
    {
        while (pos < d.Length)
        {
            int len = d[pos];
            if (len == 0) { pos++; return true; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return pos <= d.Length; }   // a pointer ends the name
            if ((len & 0xC0) != 0) return false;                               // reserved label types
            pos += 1 + len;
        }
        return false;
    }

    /// <summary>Queries <paramref name="server"/> over UDP. <paramref name="acceptNxdomain"/>: NXDOMAIN counts as a valid reply.</summary>
    public static async Task<ProbeResult> DnsQueryAsync(string server, string name, int timeoutMs = 2000, bool acceptNxdomain = false, int port = 53)
    {
        if (!IPAddress.TryParse(server, out var ip)) return new ProbeResult(false, null, "error:invalid_dns_server");
        var qid = (ushort)Random.Shared.Next(65536);
        byte[] pkt;
        try { pkt = BuildDnsQuery(name, qid); }
        catch (ArgumentException) { return new ProbeResult(false, null, "error:invalid_name"); }
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var sock = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            // a connected UDP socket only receives datagrams from the server we asked: another sender cannot answer for it
            await sock.ConnectAsync(new IPEndPoint(ip, port), cts.Token);
            var sw = Stopwatch.StartNew();
            await sock.SendAsync(pkt, SocketFlags.None, cts.Token);
            while (true)
            {
                int n = await sock.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                var r = ParseDnsReply(buf.AsSpan(0, n), qid, pkt);
                if (r is null) continue;
                double ms = sw.Elapsed.TotalMilliseconds;
                var (rcode, an, truncated) = r.Value;
                if (truncated) return new ProbeResult(false, ms, "truncated");   // the resolver answered but the reply is incomplete: not a resolution
                bool good = (rcode == 0 && an > 0) || (acceptNxdomain && (rcode == 0 || rcode == 3));
                return new ProbeResult(good, ms, RCodes.TryGetValue(rcode, out var s) ? s : $"rcode{rcode}");
            }
        }
        catch (OperationCanceledException) { return new ProbeResult(false, null, "timeout"); }
        catch (SocketException e) { return new ProbeResult(false, null, "error:" + e.SocketErrorCode); }
    }

    // ------------------------------------------------------------------ traceroute
    /// <summary>
    /// Cautious reading of a traceroute. A loss on an INTERMEDIATE hop is not a real loss when the following hops
    /// (and the destination) answer: routers often rate-limit their own ICMP replies.
    /// </summary>
    public static TraceAnalysis AnalyzeTrace(IReadOnlyList<TraceHop> hops, string? destIp = null)
    {
        var res = new TraceAnalysis();
        if (hops.Count == 0) return res;
        var last = hops[^1];
        res.Reached = last.Rtts.Count > last.Unreachable && (destIp is null || last.Ip == destIp);   // an error from the destination's address is not an echo reply
        for (int i = 0; i < hops.Count - 1; i++)
            if (hops[i].Lost > 0 && hops.Skip(i + 1).Any(x => x.Rtts.Count > 0)) res.IntermediateLoss.Add(hops[i].Hop);
        var meds = hops.Where(h => h.Rtts.Count > 0).Select(h => (h.Hop, Ms: h.Rtts.Average(), h.Ip)).ToList();
        for (int i = 1; i < meds.Count; i++)
        {
            double b = meds[i - 1].Ms;
            if (meds[i].Ms - b >= 40 && meds.Skip(i).All(m => m.Ms >= b + 30))
            {
                res.Step = new TraceStep { Hop = meds[i].Hop, Ip = meds[i].Ip, FromMs = b, ToMs = meds[i].Ms };
                break;
            }
        }
        res.DestSent = last.Sent;
        res.DestLossPct = last.Sent > 0 ? 100.0 * (last.Lost + last.Unreachable) / last.Sent : null;   // the probes an error answered did not measure the destination
        return res;
    }

    /// <summary>The sentences explaining a traceroute analysis, in the current language.</summary>
    public static List<string> TraceNotes(TraceAnalysis? a, bool hasHops = true)
    {
        var notes = new List<string>();
        if (a is null) return notes;
        if (!hasHops) { notes.Add(Loc.T("trace.nohops")); return notes; }
        if (a.IntermediateLoss.Count > 0) notes.Add(Loc.T("trace.intermediate", string.Join(", ", a.IntermediateLoss)));
        if (a.Step != null) notes.Add(Loc.T("trace.step", a.Step.FromMs.ToString("0"), a.Step.ToMs.ToString("0"), a.Step.Hop));
        if (!a.Reached) notes.Add(Loc.T("trace.unreached"));
        else if (a.DestLossPct is > 0) notes.Add(Loc.T("trace.destloss", a.DestLossPct.Value.ToString("0"), a.DestSent));
        return notes;
    }

    /// <summary>True when the status is an ICMP answer from the hop (echo reply, TTL expired, destination unreachable…);
    /// timeouts and local errors (bad route, packet too big, hardware…) are not.</summary>
    public static bool IsHopReply(IPStatus s) => s is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded
        or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable or IPStatus.DestinationProtocolUnreachable
        or IPStatus.DestinationPortUnreachable or IPStatus.DestinationUnreachable or IPStatus.DestinationProhibited or IPStatus.DestinationScopeMismatch;

    static bool IsTtlReply(IPStatus s) => s is IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded;

    public static async Task<TraceResult> TracerouteAsync(string host, int maxHops = 20, CancellationToken ct = default)
    {
        host = ValidateHost(host);
        IPAddress? dest;
        try { dest = await ResolveAsync(host, 4, ct); }
        catch (SocketException) { return new TraceResult { Error = "name_not_found" }; }
        if (dest is null) return new TraceResult { Error = "name_not_found" };
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
                    if (!IsHopReply(r.Status)) { hop.Lost++; continue; }  // a local error is not an answer from the hop
                    hop.Rtts.Add(Math.Round(ms, 1));
                    hop.Ip ??= r.Address?.ToString();
                    if (r.Status == IPStatus.Success) reached = true;
                    else if (!IsTtlReply(r.Status)) hop.Unreachable++;   // kept apart: the hop answered, but with an error
                }
                hops.Add(hop);
                if (reached) break;
            }
        }
        catch (PlatformNotSupportedException) { return new TraceResult { Error = "unsupported" }; }
        catch (Exception e) when (e is PingException or SocketException) { return new TraceResult { Error = "failed" }; }
        // interrupted by the caller: what was measured is partial, so no verdict (a missing destination would read as "unreachable")
        if (ct.IsCancellationRequested) return new TraceResult { Hops = hops, Error = "cancelled" };
        return new TraceResult { Hops = hops, Analysis = AnalyzeTrace(hops, dest.ToString()) };
    }
}
