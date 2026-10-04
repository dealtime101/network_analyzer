using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace NetworkAnalyzer;

public sealed class LoadPhase
{
    public string Name { get; init; } = "";
    public int DurationS { get; init; }
    /// <summary>"down", "up" or null (idle phase).</summary>
    public string? Direction { get; init; }
}

public sealed class LoadConfig
{
    public string BaseUrl { get; set; } = "https://speed.cloudflare.com";
    public int Streams { get; set; } = 4;
    public List<LoadPhase> Phases { get; set; } = DefaultPhases(15);
    /// <summary>Maximum volume per download phase.</summary>
    public int CapDownMb { get; set; } = 600;
    /// <summary>Maximum volume per upload phase.</summary>
    public int CapUpMb { get; set; } = 200;

    public static List<LoadPhase> DefaultPhases(int loadSeconds)
    {
        int idle = Math.Max(5, Math.Min(10, loadSeconds * 2 / 3));  // 15 s of load → 10 s of idle / recovery
        return new List<LoadPhase>
        {
            new() { Name = "idle", DurationS = idle }, new() { Name = "download", DurationS = loadSeconds, Direction = "down" },
            new() { Name = "recovery1", DurationS = idle }, new() { Name = "upload", DurationS = loadSeconds, Direction = "up" },
            new() { Name = "recovery2", DurationS = idle },
        };
    }
}

public sealed class LoadEstimate
{
    public int DurationS { get; set; }
    public int MaxDownMb { get; set; }
    public int MaxUpMb { get; set; }
    public int MaxTotalMb { get; set; }
    public double? TypicalDownMb { get; set; }
    public double? TypicalUpMb { get; set; }
    public double? TypicalTotalMb { get; set; }
    public string Server { get; set; } = "";
    public int Streams { get; set; }
}

public sealed class LoadStatus
{
    /// <summary>idle | running | done | cancelled | error</summary>
    public string State { get; set; } = "idle";
    public string? Phase { get; set; }
    public string? PhaseLabel { get; set; }
    public double? ElapsedS { get; set; }
    public int TotalS { get; set; }
    public double CurrentMbps { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<PhaseMeta> Results { get; set; } = new();
}

/// <summary>
/// Saturation / bufferbloat test — documented method.
/// Service: Cloudflare's public speed test (https://speed.cloudflare.com/__down?bytes=N and __up), the one behind
/// speed.cloudflare.com. The address can be changed (BaseUrl) to target another compatible server.
/// Method: chained phases idle → download → recovery → upload → recovery. During ALL phases the pings (gateway + Internet)
/// and DNS keep running (they belong to the running monitoring session). Load: N parallel HTTP/1.1 streams, each on its own
/// TCP connection (a single TCP flow rarely saturates a fast line). Hard limits: duration per phase AND volume per phase;
/// cancellable at any time. Throughput: bytes received (download) or handed to the system (upload) per second; the sustained
/// throughput is the median of the seconds after 3 s of ramp-up. Uploaded bytes include a socket buffer, so very short phases
/// slightly overestimate. Limits: throughput can be capped by the server, the Wi-Fi or the PC: results are INDICATIVE.
/// </summary>
public sealed class LoadTest
{
    public const int WarmupS = 3;
    const int Chunk = 65536;

    readonly Recorder rec;
    readonly LoadConfig cfg;
    readonly CancellationTokenSource cancel = new();
    readonly object gate = new();
    readonly List<string> errors = new();
    readonly List<PhaseMeta> results = new();
    readonly HttpClient http;
    long bytes;
    string state = "idle";
    string? phase;
    double current;
    readonly Stopwatch clock = new();

    public Task? Task { get; private set; }

    public LoadTest(Recorder rec, LoadConfig? cfg = null, HttpMessageHandler? handler = null)
    {
        this.rec = rec;
        this.cfg = cfg ?? new LoadConfig();
        http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1), AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetworkAnalyzer", "1.0"));
    }

    public static LoadEstimate Estimate(LoadConfig c, double? planDown = null, double? planUp = null)
    {
        int Dur(string d) => c.Phases.Where(p => p.Direction == d).Sum(p => p.DurationS);
        int Count(string d) => c.Phases.Count(p => p.Direction == d);
        int capDown = c.CapDownMb * Count("down"), capUp = c.CapUpMb * Count("up");
        double? typDown = planDown is > 0 ? Math.Min(capDown, planDown.Value / 8 * Dur("down")) : null;
        double? typUp = planUp is > 0 ? Math.Min(capUp, planUp.Value / 8 * Dur("up")) : null;
        return new LoadEstimate
        {
            DurationS = c.Phases.Sum(p => p.DurationS), MaxDownMb = capDown, MaxUpMb = capUp, MaxTotalMb = capDown + capUp,
            TypicalDownMb = typDown, TypicalUpMb = typUp, TypicalTotalMb = typDown.HasValue && typUp.HasValue ? typDown + typUp : null,
            Server = c.BaseUrl, Streams = c.Streams,
        };
    }

    public void Start()
    {
        state = "running";
        clock.Restart();
        rec.SetMeta(m => m.Loadtest = new LoadMeta
        {
            Server = cfg.BaseUrl, Streams = cfg.Streams, CapDownMb = cfg.CapDownMb, CapUpMb = cfg.CapUpMb,
            Phases = cfg.Phases.Select(p => new object?[] { p.Name, p.DurationS, p.Direction }).ToList(),
        });
        Task = Task.Run(RunAsync);
    }

    public void Cancel() => cancel.Cancel();
    public string State => state;

    public LoadStatus Status()
    {
        lock (gate)
            return new LoadStatus
            {
                State = state, Phase = phase, PhaseLabel = phase != null ? Loc.T("phase." + phase) : null,
                ElapsedS = state == "running" ? clock.Elapsed.TotalSeconds : null, TotalS = cfg.Phases.Sum(p => p.DurationS),
                CurrentMbps = current / 1e6, Errors = errors.Take(3).ToList(),
                Results = results.Select(r => { var c = (PhaseMeta)Clone(r); c.Label = Loc.T("phase." + r.Name); return c; }).ToList(),
            };
    }

    static object Clone(PhaseMeta m) => Json.From<PhaseMeta>(Json.To(m))!;

    // -------------------------------------------------------------- execution
    async Task RunAsync()
    {
        try
        {
            foreach (var p in cfg.Phases)
            {
                if (cancel.IsCancellationRequested) break;
                phase = p.Name;
                rec.BeginPhase(p.Name, new PhaseMeta { Direction = p.Direction });
                var extra = p.Direction != null ? await Saturate(p.Direction, p.DurationS) : await Idle(p.DurationS);
                if (cancel.IsCancellationRequested) extra.Cancelled = true;
                current = 0;  // the load has stopped: the next phase must not show its last rate
                rec.EndPhase(extra);
                extra.Name = p.Name;
                lock (gate) results.Add(extra);
            }
            state = cancel.IsCancellationRequested ? "cancelled" : "done";
        }
        catch (Exception e)  // the test must never bring the application down
        {
            lock (gate) errors.Add($"{e.GetType().Name}: {e.Message}");
            state = "error";
            rec.EndPhase(new PhaseMeta { Error = e.Message });
        }
        phase = null;
        current = 0;
    }

    async Task<PhaseMeta> Idle(int dur)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(dur), cancel.Token); } catch (OperationCanceledException) { }
        return new PhaseMeta { DurationS = dur };
    }

    async Task<PhaseMeta> Saturate(string direction, int dur)
    {
        var series = $"load:{direction}_bps";
        long cap = (direction == "down" ? cfg.CapDownMb : cfg.CapUpMb) * 1_000_000L;
        Interlocked.Exchange(ref bytes, 0);
        int errs0;
        lock (gate) errs0 = errors.Count;
        var sw = Stopwatch.StartNew();
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        phaseCts.CancelAfter(TimeSpan.FromSeconds(dur));
        var ct = phaseCts.Token;
        var workers = Enumerable.Range(0, cfg.Streams).Select(_ => Task.Run(() => direction == "down" ? DownWorker(cap, ct) : UpWorker(cap, ct))).ToList();
        var rates = new List<double>();
        var all = new List<double>();
        long lastN = 0;
        double lastT = 0;
        while (!ct.IsCancellationRequested && Interlocked.Read(ref bytes) < cap)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            double now = sw.Elapsed.TotalSeconds;
            long n = Interlocked.Read(ref bytes);
            double bps = (n - lastN) * 8 / Math.Max(1e-6, now - lastT);
            lastN = n; lastT = now; current = bps;
            rec.Emit(series, bps, true);
            all.Add(bps);
            if (now >= WarmupS) rates.Add(bps);
        }
        double elapsed = sw.Elapsed.TotalSeconds;  // read before waiting for the workers: their shutdown is not part of the phase
        long total = Interlocked.Read(ref bytes);
        phaseCts.Cancel();  // streams stop by themselves: deadline, volume cap or cancellation
        try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(6)); } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        var src = rates.Count > 0 ? rates : all;
        var res = new PhaseMeta
        {
            Direction = direction, DurationS = Math.Round(elapsed, 1), Bytes = total, AvgMbps = elapsed > 0 ? total * 8 / elapsed / 1e6 : 0,
            SustainedMbps = (Stats.Median(src) ?? 0) / 1e6, PeakMbps = (all.Count > 0 ? all.Max() : 0) / 1e6,
            VolumeCapReached = total >= cap, Streams = cfg.Streams, RampOnly = rates.Count == 0 && all.Count > 0,
        };
        lock (gate) if (errors.Count > errs0) res.Errors = errors.Skip(errs0).Take(3).ToList();
        return res;
    }

    bool Stopped(long cap, CancellationToken ct) => ct.IsCancellationRequested || Interlocked.Read(ref bytes) >= cap;

    void AddError(Exception e) { lock (gate) errors.Add($"{e.GetType().Name}: {e.Message}"); }

    async Task DownWorker(long cap, CancellationToken ct)
    {
        int fails = 0;
        var buf = new byte[Chunk];
        while (!Stopped(cap, ct) && fails < 5)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, cfg.BaseUrl + "/__down?bytes=25000000") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                while (!Stopped(cap, ct))
                {
                    int n = await s.ReadAsync(buf, ct);
                    if (n == 0) break;
                    Interlocked.Add(ref bytes, n);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                fails++;
                AddError(e);
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    sealed class BodyContent : HttpContent
    {
        readonly long size;
        readonly Func<bool> stopped;
        readonly Action<int> counted;
        readonly byte[] block;

        public BodyContent(long size, byte[] block, Func<bool> stopped, Action<int> counted)
        {
            this.size = size; this.block = block; this.stopped = stopped; this.counted = counted;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            long sent = 0;
            while (sent < size && !stopped())
            {
                int len = (int)Math.Min(block.Length, size - sent);  // the last block is cut to the announced length
                await stream.WriteAsync(block.AsMemory(0, len), ct);
                sent += len;
                counted(len);
            }
            if (sent < size) throw new OperationCanceledException();  // never end a half body as if it were complete
        }

        protected override bool TryComputeLength(out long length) { length = size; return true; }
    }

    async Task UpWorker(long cap, CancellationToken ct)
    {
        int fails = 0;
        var block = new byte[Chunk];
        Random.Shared.NextBytes(block);  // incompressible
        while (!Stopped(cap, ct) && fails < 5)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + "/__up") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                req.Content = new BodyContent(10_000_000, block, () => Stopped(cap, ct), n => Interlocked.Add(ref bytes, n));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");  // a refused upload is a failure, as for the download
                await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                fails++;
                AddError(e);
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
