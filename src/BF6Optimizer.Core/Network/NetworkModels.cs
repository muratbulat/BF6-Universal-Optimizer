using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Network;

public enum ProbeProtocol { Tcp, Icmp, Http }

public enum LatencyQuality { Untested, Excellent, Good, Fair, HighLatency, Unreachable, Unsupported }

public enum EndpointCategory { UserDefined, RegionalTest, ObservedPossible, ConfirmedGameplay }

public sealed class SeedTarget
{
    public string Ip { get; set; } = "";
    public string Prefix { get; set; } = "";
}

/// <summary>A candidate location. Nothing here claims Battlefield 6 runs in this region.</summary>
public sealed class CandidateRegion
{
    public string Code { get; set; } = "";
    public string Group { get; set; } = "";
    public string City { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2, used for the flag.</summary>
    public string Country { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Provenance { get; set; } = "";
    public string ProvenanceDate { get; set; } = "";
    public Confidence Confidence { get; set; }
    public List<SeedTarget> SeedTargets { get; set; } = [];
}

public sealed class RegionCatalog
{
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; set; }
    public string CatalogVersion { get; set; } = "";
    public string SeedSource { get; set; } = "";
    public string SeedRetrieved { get; set; } = "";
    public Dictionary<string, List<string>> Presets { get; set; } = [];
    public List<CandidateRegion> Regions { get; set; } = [];

    public CandidateRegion? Find(string code) => Regions.FirstOrDefault(r => r.Code == code);

    public static RegionCatalog LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("bf6-regions.json")
            ?? throw new InvalidOperationException("bf6-regions.json is not embedded.");
        var catalog = JsonSerializer.Deserialize<RegionCatalog>(s, Json.Options)
            ?? throw new InvalidDataException("Empty region catalog.");
        if (catalog.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported region catalog schema {catalog.SchemaVersion}.");
        return catalog;
    }

    /// <summary>Test order only. Unknown countries start with every region; measurements decide the result.</summary>
    public string DefaultPreset(string? countryCode) => countryCode?.ToUpperInvariant() switch
    {
        "TR" => "Turkey",
        "US" or "CA" or "MX" => "NorthAmerica",
        "BR" or "AR" or "CL" or "CO" or "PE" => "SouthAmerica",
        "JP" or "KR" or "SG" or "HK" or "TW" or "TH" or "VN" or "MY" or "PH" or "ID" or "IN" => "Asia",
        "AU" or "NZ" => "Oceania",
        "DE" or "FR" or "GB" or "IE" or "SE" or "NL" or "ES" or "IT" or "PL" or "NO" or "FI" or "DK" or "AT" or "CH" or "BE" or "PT" => "Europe",
        _ => "All",
    };
}

/// <summary>Provider network probe in one region. It is never a Battlefield server.</summary>
public sealed record RegionTarget
{
    public required string Region { get; init; }
    public required string Ip { get; init; }
    public required string Prefix { get; init; }
    public string Source { get; init; } = "";
    public DateTimeOffset? RetrievedAt { get; init; }
    public DateTimeOffset? ValidatedAt { get; init; }

    /// <summary>The per-IP hostname the AWS reachability page itself loads.</summary>
    public string HttpUrl => $"http://{Ip.Replace('.', '-')}.ipv4.ec2-reachability.amazonaws.com/green-icon.gif";
}

public sealed record TargetSet
{
    public int SchemaVersion { get; init; } = 1;
    public string SourceUrl { get; init; } = "";
    public DateTimeOffset? RetrievedAt { get; init; }
    public bool RangesChecked { get; init; }
    public IReadOnlyList<RegionTarget> Targets { get; init; } = [];
}

public interface ITargetStore
{
    TargetSet? Load();
    void Save(TargetSet set);
}

public sealed record ProbeTarget(string Host, int Port = 80, string? HttpUrl = null);

public sealed record MeasureOptions
{
    public ProbeProtocol Protocol { get; init; } = ProbeProtocol.Tcp;
    public int Samples { get; init; } = 10;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>Statistics over valid replies only. Missing replies are counted, never imputed.</summary>
public sealed record LatencyStats(int Sent, int Received, double? Min, double? Average, double? Median, double? Max, double? Jitter)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    public static LatencyStats From(int sent, IReadOnlyList<double> rtts)
    {
        if (rtts.Count == 0) return new LatencyStats(sent, 0, null, null, null, null, null);
        var sorted = rtts.Order().ToList();
        int mid = sorted.Count / 2;
        double median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        return new LatencyStats(sent, rtts.Count, sorted[0], rtts.Average(), median, sorted[^1], ComputeJitter(rtts));
    }

    /// <summary>Mean absolute difference between consecutive valid RTTs. One sample gives no jitter.</summary>
    public static double? ComputeJitter(IReadOnlyList<double> rtts) =>
        rtts.Count < 2 ? null : rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(a - b)).Average();
}

/// <summary>App-defined bands in ms. They are not EA standards.</summary>
public sealed class LatencyThresholds
{
    public int ExcellentMs { get; set; } = 30;
    public int GoodMs { get; set; } = 60;
    public int FairMs { get; set; } = 100;

    public LatencyQuality Classify(double? average) => average switch
    {
        null => LatencyQuality.Unreachable,
        _ when average <= ExcellentMs => LatencyQuality.Excellent,
        _ when average <= GoodMs => LatencyQuality.Good,
        _ when average <= FairMs => LatencyQuality.Fair,
        _ => LatencyQuality.HighLatency,
    };

    public bool IsValid => ExcellentMs > 0 && ExcellentMs < GoodMs && GoodMs < FairMs && FairMs <= 5000;
}

public sealed record RegionResult
{
    public required string Region { get; init; }
    public string? TargetIp { get; init; }
    /// <summary>The protocol actually measured; differs from the request after an ICMP fallback.</summary>
    public ProbeProtocol Protocol { get; init; }
    public bool FellBackFromIcmp { get; init; }
    public bool EndpointUnavailable { get; init; }
    public LatencyStats? Stats { get; init; }
    public DateTimeOffset? TestedAt { get; init; }
    public RegionTarget? UsedTarget { get; init; }
}

public sealed partial class SavedEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    public ProbeProtocol Protocol { get; set; } = ProbeProtocol.Icmp;
    public string Region { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>What proves a Confirmed endpoint is a gameplay server.</summary>
    public string Evidence { get; set; } = "";
    public string Provider { get; set; } = "";
    public EndpointCategory Category { get; set; } = EndpointCategory.UserDefined;
    public bool Verified { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex HostName();

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (IPAddress.TryParse(host, out var ip)) return ip.ToString() == host || host.Contains(':');
        // All-numeric labels such as 999.1.1.1 are failed IPs, not host names.
        return HostName().IsMatch(host) && !host.Split('.').All(l => l.All(char.IsAsciiDigit));
    }

    public IReadOnlyList<Msg> Validate(RegionCatalog catalog)
    {
        var errors = new List<Msg>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add(new Msg("error.ep_name"));
        if (!IsValidHost(Host)) errors.Add(new Msg("error.ep_host"));
        if (Port is < 1 or > 65535) errors.Add(new Msg("error.ep_port"));
        if (Protocol == ProbeProtocol.Tcp && Port is null) errors.Add(new Msg("error.ep_port"));
        if (Protocol == ProbeProtocol.Http) errors.Add(new Msg("error.ep_protocol"));
        if (Region.Length > 0 && catalog.Find(Region) is null) errors.Add(new Msg("error.ep_region"));
        if (Category == EndpointCategory.ConfirmedGameplay && string.IsNullOrWhiteSpace(Evidence)) errors.Add(new Msg("error.ep_evidence"));
        if (Category == EndpointCategory.RegionalTest && string.IsNullOrWhiteSpace(Provider)) errors.Add(new Msg("error.ep_provider"));
        return errors;
    }
}

public interface IEndpointStore
{
    IReadOnlyList<SavedEndpoint> Load();
    void Save(IReadOnlyList<SavedEndpoint> endpoints);
}

public sealed record NetworkTestRecord
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset Timestamp { get; init; }
    public string Region { get; init; } = "";
    public string EndpointId { get; init; } = "";
    public ProbeProtocol? Protocol { get; init; }
    public LatencyStats? Stats { get; init; }
    public string? Error { get; init; }
    public string Source { get; init; } = "";
    public Confidence Confidence { get; init; }
    /// <summary>Set for a ping the user read from the in-game overlay and typed in.</summary>
    public double? ManualInGamePingMs { get; init; }
}

public interface INetworkHistoryStore
{
    void Append(IEnumerable<NetworkTestRecord> records);
    IReadOnlyList<NetworkTestRecord> Load();
}

public sealed record ConnectionSummary(string Adapter, string Type, long LinkMbps, string? Gateway, IReadOnlyList<string> Dns, bool Ipv4, bool Ipv6);

public interface INetworkProber
{
    /// <summary>One probe. Returns the RTT in ms, or null on timeout or refusal. Honors cancellation.</summary>
    Task<double?> ProbeAsync(ProbeTarget target, ProbeProtocol protocol, TimeSpan timeout, CancellationToken ct);
}
