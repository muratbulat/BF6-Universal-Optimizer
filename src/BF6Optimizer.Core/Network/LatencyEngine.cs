using System.Net;
using System.Text.Json;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Network;

public sealed class NetworkLatencyService(INetworkProber prober, IClock clock)
{
    public const int MaxCandidates = 4;
    public const int MaxConcurrentRegions = 3;
    public static readonly TimeSpan TargetFreshness = TimeSpan.FromDays(7);

    public async Task<LatencyStats> MeasureAsync(ProbeTarget target, MeasureOptions o, Action<double?>? onSample, CancellationToken ct)
    {
        var rtts = new List<double>(o.Samples);
        for (int i = 0; i < o.Samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rtt = await prober.ProbeAsync(target, o.Protocol, o.Timeout, ct).ConfigureAwait(false);
            if (rtt is { } v) rtts.Add(v);
            onSample?.Invoke(rtt);
            if (i < o.Samples - 1) await Task.Delay(o.Interval, ct).ConfigureAwait(false);
        }
        return LatencyStats.From(o.Samples, rtts);
    }

    private static ProbeTarget ToProbe(RegionTarget t) => new(t.Ip, 80, t.HttpUrl);

    /// <summary>Recently validated targets first, so a stale pick is re-checked rather than trusted.</summary>
    public IEnumerable<RegionTarget> Order(IEnumerable<RegionTarget> targets) => targets
        .OrderByDescending(t => t.ValidatedAt is { } v && clock.Now - v < TargetFreshness)
        .ThenByDescending(t => t.ValidatedAt ?? DateTimeOffset.MinValue);

    /// <summary>
    /// Finds a target that answers on this protocol and measures it. If ICMP gets no answer the region is
    /// retried over TCP 80, which AWS's own reachability page relies on; the result says so.
    /// </summary>
    public async Task<RegionResult> TestRegionAsync(string region, IReadOnlyList<RegionTarget> targets, MeasureOptions o, CancellationToken ct)
    {
        if (targets.Count == 0)
            return new RegionResult { Region = region, Protocol = o.Protocol, EndpointUnavailable = true, TestedAt = clock.Now };

        var result = await TryProtocolAsync(region, targets, o, ct).ConfigureAwait(false);
        if (result is null && o.Protocol == ProbeProtocol.Icmp)
        {
            result = await TryProtocolAsync(region, targets, o with { Protocol = ProbeProtocol.Tcp }, ct).ConfigureAwait(false);
            if (result is not null) result = result with { FellBackFromIcmp = true };
        }
        return result ?? new RegionResult { Region = region, Protocol = o.Protocol, EndpointUnavailable = true, TestedAt = clock.Now };
    }

    private async Task<RegionResult?> TryProtocolAsync(string region, IReadOnlyList<RegionTarget> targets, MeasureOptions o, CancellationToken ct)
    {
        foreach (var t in Order(targets).Take(MaxCandidates))
        {
            ct.ThrowIfCancellationRequested();
            var probe = ToProbe(t);
            if (await prober.ProbeAsync(probe, o.Protocol, o.Timeout, ct).ConfigureAwait(false) is null) continue;

            var stats = await MeasureAsync(probe, o, null, ct).ConfigureAwait(false);
            if (stats.Received == 0) continue;
            return new RegionResult
            {
                Region = region,
                TargetIp = t.Ip,
                Protocol = o.Protocol,
                Stats = stats,
                TestedAt = clock.Now,
                UsedTarget = t with { ValidatedAt = clock.Now },
            };
        }
        return null;
    }

    public async Task TestRegionsAsync(
        IReadOnlyList<string> regions, Func<string, IReadOnlyList<RegionTarget>> targets, MeasureOptions o,
        Action<RegionResult> onResult, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentRegions);
        var tasks = regions.Select(async region =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { onResult(await TestRegionAsync(region, targets(region), o, ct).ConfigureAwait(false)); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public static class RegionRanking
{
    /// <summary>Only results measured with the requested protocol compete; fallback rows never do.</summary>
    public static IReadOnlyList<RegionResult> Rank(IEnumerable<RegionResult> results, ProbeProtocol protocol) => results
        .Where(r => r.Protocol == protocol && !r.FellBackFromIcmp && r.Stats?.Average is not null)
        .OrderBy(r => r.Stats!.Average).ThenBy(r => r.Stats!.Jitter ?? double.MaxValue)
        .ToList();
}

public sealed record AwsParseResult(IReadOnlyList<RegionTarget> Targets, int Rejected);

public sealed record AwsRange(uint Network, uint Mask, string Region);

public static class AwsTargetParser
{
    public static (uint Network, uint Mask)? ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits is < 0 or > 32) return null;
        if (ToUInt(parts[0]) is not { } ip) return null;
        uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
        return (ip & mask, mask);
    }

    public static uint? ToUInt(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        if (ip.Count(c => c == '.') != 3) return null;
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    /// <summary>
    /// Reads the [{"region": {"prefix": "ip"}}] document behind ec2-reachability.amazonaws.com. Only addresses
    /// AWS itself lists are kept, each inside its stated prefix; nothing is derived from a CIDR range.
    /// </summary>
    public static AwsParseResult Parse(string json, IEnumerable<string> regions, string source, DateTimeOffset now)
    {
        var wanted = regions.ToHashSet(StringComparer.Ordinal);
        var targets = new List<RegionTarget>();
        int rejected = 0;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unexpected reachability format.");

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { rejected++; continue; }
            foreach (var region in item.EnumerateObject())
            {
                if (!wanted.Contains(region.Name)) continue;
                if (region.Value.ValueKind != JsonValueKind.Object) { rejected++; continue; }
                foreach (var entry in region.Value.EnumerateObject())
                {
                    var ip = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
                    if (ip is not null && ParseCidr(entry.Name) is { } cidr && ToUInt(ip) is { } value && (value & cidr.Mask) == cidr.Network)
                        targets.Add(new RegionTarget { Region = region.Name, Ip = ip, Prefix = entry.Name, Source = source, RetrievedAt = now });
                    else rejected++;
                }
            }
        }
        return new AwsParseResult(targets, rejected);
    }

    public static IReadOnlyList<AwsRange> ParseRanges(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<AwsRange>();
        foreach (var p in doc.RootElement.GetProperty("prefixes").EnumerateArray())
            if (p.TryGetProperty("ip_prefix", out var prefix) && p.TryGetProperty("region", out var region)
                && ParseCidr(prefix.GetString() ?? "") is { } c)
                result.Add(new AwsRange(c.Network, c.Mask, region.GetString() ?? ""));
        return result.DistinctBy(r => (r.Network, r.Mask, r.Region)).ToList();
    }

    /// <summary>Longest-prefix region for an address, or null when AWS does not list it.</summary>
    public static string? RegionOf(string ip, IReadOnlyList<AwsRange> ranges)
    {
        if (ToUInt(ip) is not { } v) return null;
        return ranges.Where(r => (v & r.Mask) == r.Network && r.Region != "GLOBAL")
            .OrderByDescending(r => r.Mask).Select(r => r.Region).FirstOrDefault();
    }

    public static IReadOnlyList<RegionTarget> DropMismatched(IReadOnlyList<RegionTarget> targets, IReadOnlyList<AwsRange> ranges) =>
        targets.Where(t => RegionOf(t.Ip, ranges) == t.Region).ToList();

    public static IReadOnlyList<RegionTarget> Seed(RegionCatalog c) => c.Regions
        .SelectMany(r => r.SeedTargets.Select(t => new RegionTarget { Region = r.Code, Ip = t.Ip, Prefix = t.Prefix, Source = c.SeedSource }))
        .ToList();
}

public static class EndpointIo
{
    /// <summary>Whole-file validation: one invalid entry rejects the import.</summary>
    public static (IReadOnlyList<SavedEndpoint>? Endpoints, IReadOnlyList<Msg> Errors) Import(string json, RegionCatalog catalog)
    {
        List<SavedEndpoint>? list;
        try { list = Json.Deserialize<List<SavedEndpoint>>(json); }
        catch (JsonException) { return (null, [new Msg("error.ep_file")]); }
        if (list is null) return (null, [new Msg("error.ep_file")]);

        var errors = list.SelectMany(e => e.Validate(catalog)).Distinct().ToList();
        if (errors.Count > 0) return (null, errors);
        foreach (var e in list)
        {
            e.Id = Guid.NewGuid().ToString("N");
            // A claim made elsewhere is not verification here.
            e.Verified = false;
            if (e.Category == EndpointCategory.ConfirmedGameplay) e.Category = EndpointCategory.ObservedPossible;
        }
        return (list, []);
    }

    public static string Export(IEnumerable<SavedEndpoint> endpoints) => Json.Serialize(endpoints.ToList());
}

public sealed record NetworkRecommendation(string Id, Msg Text, Priority Priority);

public sealed record NetworkContext
{
    public IReadOnlyList<RegionResult> Results { get; init; } = [];
    public ProbeProtocol Protocol { get; init; }
    public LatencyStats? Gateway { get; init; }
    public double? ManualInGamePing { get; init; }
    public bool WiFi { get; init; }
}

public static class NetworkRecommendationService
{
    private const double HighAllMs = 100;
    private const double GatewaySpikeMs = 30;
    private const double GatewayJitterMs = 10;
    private const double InGameGapMs = 40;

    public static IReadOnlyList<NetworkRecommendation> Evaluate(NetworkContext c, Func<string, string> cityOf)
    {
        var o = new List<NetworkRecommendation>();
        var ranked = RegionRanking.Rank(c.Results, c.Protocol);

        if (ranked.Count >= 3 && ranked.All(r => r.Stats!.Average > HighAllMs))
            o.Add(new("all_high", new Msg("net.rec.all_high"), Priority.High));

        if (c.Gateway is { Received: > 0 } g && (g.Max >= GatewaySpikeMs || g.Jitter >= GatewayJitterMs))
            o.Add(new("gateway", new Msg(c.WiFi ? "net.rec.gateway_wifi" : "net.rec.gateway"), Priority.High));

        if (ranked.Count >= 2)
            o.Add(new("best", new Msg("net.rec.best", cityOf(ranked[0].Region)), Priority.Informational));

        if (c.ManualInGamePing is { } ingame && ranked.Count > 0 && ingame - ranked[0].Stats!.Average >= InGameGapMs)
            o.Add(new("ingame_gap", new Msg("net.rec.ingame_gap"), Priority.Medium));

        if (c.Results.Any(r => r.FellBackFromIcmp))
            o.Add(new("icmp_blocked", new Msg("net.rec.icmp_blocked"), Priority.Low));

        if (c.Results.Any(r => r.EndpointUnavailable))
            o.Add(new("unavailable", new Msg("net.rec.unavailable"), Priority.Low));

        return o;
    }
}

public sealed record RouteHop(int Number, string? Address, string? HostName, IReadOnlyList<double?> Rtts, bool Reached)
{
    public double? Best => Rtts.Where(r => r.HasValue).Select(r => r!.Value).Cast<double?>().Min();
    public bool Silent => Address is null;
}

public interface IRouteProber
{
    /// <summary>Sends one TTL-limited echo. Address is null when the hop stays silent.</summary>
    Task<(string? Address, double? Rtt, bool Reached)> ProbeHopAsync(string host, int ttl, TimeSpan timeout, CancellationToken ct);
    Task<string?> ReverseLookupAsync(string address, CancellationToken ct);
}

public sealed class TracerouteService(IRouteProber prober)
{
    public const int MaxHops = 30;
    private const int SamplesPerHop = 3;
    private const int MaxSilentRun = 6;
    private static readonly TimeSpan HopTimeout = TimeSpan.FromMilliseconds(1200);

    public async Task<IReadOnlyList<RouteHop>> TraceAsync(string host, Action<RouteHop> onHop, CancellationToken ct)
    {
        var hops = new List<RouteHop>();
        int silent = 0;
        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            string? address = null;
            bool reached = false;
            var rtts = new List<double?>(SamplesPerHop);
            for (int i = 0; i < SamplesPerHop; i++)
            {
                var (a, rtt, r) = await prober.ProbeHopAsync(host, ttl, HopTimeout, ct).ConfigureAwait(false);
                address ??= a;
                reached |= r;
                rtts.Add(rtt);
            }

            var name = address is null ? null : await prober.ReverseLookupAsync(address, ct).ConfigureAwait(false);
            var hop = new RouteHop(ttl, address, name, rtts, reached);
            hops.Add(hop);
            onHop(hop);

            silent = address is null ? silent + 1 : 0;
            if (reached || silent >= MaxSilentRun) break;
        }
        return hops;
    }
}

public static class RouteAnalyzer
{
    private const double JumpMs = 50;
    private const double HighFinalMs = 150;
    private const int LongPath = 20;

    /// <summary>
    /// Only a delay that persists to later hops counts; a single slow router usually just deprioritizes
    /// replies. Silent hops are never read as loss.
    /// </summary>
    public static IReadOnlyList<Msg> Interpret(IReadOnlyList<RouteHop> hops)
    {
        var notes = new List<Msg>();
        var answered = hops.Where(h => h.Best is not null).ToList();
        if (answered.Count == 0) return [new Msg("net.route.no_data")];

        if (!hops.Any(h => h.Reached)) notes.Add(new Msg("net.route.not_reached"));

        for (int i = 1; i < answered.Count; i++)
        {
            double jump = answered[i].Best!.Value - answered[i - 1].Best!.Value;
            bool persists = answered.Skip(i).All(h => h.Best >= answered[i - 1].Best + JumpMs * 0.7);
            if (jump >= JumpMs && persists)
                notes.Add(new Msg("net.route.jump", answered[i].Number.ToString(), ((int)jump).ToString()));
        }

        if (answered[^1].Best >= HighFinalMs) notes.Add(new Msg("net.route.high", ((int)answered[^1].Best!.Value).ToString()));
        if (hops.Count >= LongPath) notes.Add(new Msg("net.route.long", hops.Count.ToString()));
        if (hops.Any(h => h.Silent)) notes.Add(new Msg("net.route.silent"));
        if (notes.Count == 0) notes.Add(new Msg("net.route.ok"));
        return notes;
    }
}

public sealed record GameTcpEndpoint(string RemoteIp, int RemotePort, string State);

public sealed record GameConnectionSnapshot
{
    public bool GameRunning { get; init; }
    public IReadOnlyList<GameTcpEndpoint> Tcp { get; init; } = [];
    public IReadOnlyList<int> UdpLocalPorts { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; }
}

public interface IGameConnectionObserver
{
    GameConnectionSnapshot Observe();
}

public static class EndpointClassifier
{
    /// <summary>
    /// What a process table can show never proves a gameplay server: BF6 plays over UDP, whose remote peer
    /// Windows does not expose, and TCP sessions go to account and matchmaking services.
    /// </summary>
    public static EndpointCategory Classify(GameTcpEndpoint _) => EndpointCategory.ObservedPossible;

    public static SavedEndpoint ToSaved(GameTcpEndpoint e, string? awsRegion, DateTimeOffset at) => new()
    {
        Name = $"{e.RemoteIp}:{e.RemotePort}",
        Host = e.RemoteIp,
        Port = e.RemotePort,
        Protocol = ProbeProtocol.Tcp,
        Region = awsRegion ?? "",
        Provider = awsRegion is null ? "" : "AWS",
        Source = "GetExtendedTcpTable " + at.ToString("yyyy-MM-dd HH:mm"),
        Category = EndpointCategory.ObservedPossible,
    };
}
