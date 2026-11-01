using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Network;
using BF6Optimizer.Infrastructure.Storage;
using BF6Optimizer.Infrastructure.Windows;

namespace BF6Optimizer.Tests;

internal sealed class FakeProber(Func<ProbeTarget, ProbeProtocol, double?> answer) : INetworkProber
{
    public int Calls;

    public Task<double?> ProbeAsync(ProbeTarget target, ProbeProtocol protocol, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Calls);
        return Task.FromResult(answer(target, protocol));
    }
}

public class NetworkFixtures
{
    internal static readonly MeasureOptions Fast = new() { Samples = 5, Interval = TimeSpan.Zero };

    internal static RegionTarget Target(string ip, string region = "eu-central-1", DateTimeOffset? validated = null) =>
        new() { Region = region, Ip = ip, Prefix = "3.64.0.0/12", ValidatedAt = validated };

    internal static RegionResult Result(string region, double avg, ProbeProtocol p = ProbeProtocol.Tcp, bool fallback = false) => new()
    {
        Region = region,
        Protocol = p,
        FellBackFromIcmp = fallback,
        Stats = new LatencyStats(5, 5, avg, avg, avg, avg, 1),
    };
}

public class RegionCatalogTests
{
    private readonly RegionCatalog _c = RegionCatalog.LoadEmbedded();

    [Fact]
    public void Loads_all_seventeen_candidates_with_provenance()
    {
        Assert.Equal(17, _c.Regions.Count);
        Assert.All(_c.Regions, r =>
        {
            Assert.Matches(@"^[a-z]{2}-[a-z]+-\d$", r.Code);
            Assert.NotEmpty(r.Provenance);
            Assert.NotEmpty(r.ProvenanceDate);
            Assert.Equal(Confidence.Low, r.Confidence);
            Assert.NotEmpty(r.SeedTargets);
        });
    }

    [Fact]
    public void Every_seed_target_is_inside_its_published_prefix()
    {
        foreach (var t in _c.Regions.SelectMany(r => r.SeedTargets))
        {
            var cidr = AwsTargetParser.ParseCidr(t.Prefix)!.Value;
            Assert.Equal(cidr.Network, AwsTargetParser.ToUInt(t.Ip)!.Value & cidr.Mask);
        }
    }

    [Fact]
    public void Preset_regions_exist()
    {
        foreach (var code in _c.Presets.Values.SelectMany(v => v)) Assert.NotNull(_c.Find(code));
    }

    [Fact]
    public void Turkey_order_is_a_preset_not_a_global_default()
    {
        Assert.Equal("eu-central-1", _c.Presets["Turkey"][0]);
        Assert.Equal("Turkey", _c.DefaultPreset("TR"));
        Assert.Equal("NorthAmerica", _c.DefaultPreset("US"));
        Assert.Equal("All", _c.DefaultPreset("ZA"));
        Assert.Equal("All", _c.DefaultPreset(null));
    }
}

public class LatencyMathTests
{
    [Fact]
    public void Statistics_use_valid_samples_only()
    {
        var s = LatencyStats.From(5, [10, 20, 30, 40]);
        Assert.Equal((5, 4), (s.Sent, s.Received));
        Assert.Equal((10.0, 25.0, 25.0, 40.0), (s.Min!.Value, s.Average!.Value, s.Median!.Value, s.Max!.Value));
        Assert.Equal(20, s.LossPercent);
    }

    [Fact]
    public void Jitter_is_mean_absolute_consecutive_difference()
    {
        Assert.Equal(10, LatencyStats.ComputeJitter([10, 20, 10, 20]));
        Assert.Equal(4, LatencyStats.ComputeJitter([10, 14]));
    }

    [Fact]
    public void Jitter_needs_two_samples() => Assert.Null(LatencyStats.ComputeJitter([10]));

    [Fact]
    public void No_replies_gives_no_numbers()
    {
        var s = LatencyStats.From(5, []);
        Assert.Null(s.Average);
        Assert.Null(s.Jitter);
        Assert.Equal(100, s.LossPercent);
    }

    [Theory]
    [InlineData(20.0, LatencyQuality.Excellent)]
    [InlineData(45.0, LatencyQuality.Good)]
    [InlineData(80.0, LatencyQuality.Fair)]
    [InlineData(200.0, LatencyQuality.HighLatency)]
    [InlineData(null, LatencyQuality.Unreachable)]
    public void Default_bands(double? avg, LatencyQuality expected) =>
        Assert.Equal(expected, new LatencyThresholds().Classify(avg));

    [Fact]
    public void Bands_are_configurable_and_validated()
    {
        Assert.Equal(LatencyQuality.Excellent, new LatencyThresholds { ExcellentMs = 50, GoodMs = 80, FairMs = 120 }.Classify(45));
        Assert.False(new LatencyThresholds { ExcellentMs = 90, GoodMs = 60 }.IsValid);
    }
}

public class LatencyEngineTests
{
    private static NetworkLatencyService Service(INetworkProber p) => new(p, new SystemClock());

    [Fact]
    public async Task Timeouts_stay_missing()
    {
        int i = 0;
        var s = await Service(new FakeProber((_, _) => i++ % 2 == 0 ? 10 : null))
            .MeasureAsync(new ProbeTarget("x"), NetworkFixtures.Fast, null, default);
        Assert.Equal((5, 3), (s.Sent, s.Received));
        Assert.Equal(10, s.Average);
    }

    [Fact]
    public async Task Skips_silent_targets()
    {
        var r = await Service(new FakeProber((t, _) => t.Host == "b" ? 40 : null))
            .TestRegionAsync("eu-central-1", [NetworkFixtures.Target("a"), NetworkFixtures.Target("b")], NetworkFixtures.Fast, default);
        Assert.Equal("b", r.TargetIp);
        Assert.NotNull(r.UsedTarget!.ValidatedAt);
    }

    [Fact]
    public async Task No_answer_is_unavailable_with_no_numbers()
    {
        var r = await Service(new FakeProber((_, _) => null))
            .TestRegionAsync("me-south-1", [NetworkFixtures.Target("a")], NetworkFixtures.Fast, default);
        Assert.True(r.EndpointUnavailable);
        Assert.Null(r.Stats);
    }

    [Fact]
    public async Task No_targets_is_unavailable_not_a_substitute()
    {
        var p = new FakeProber((_, _) => 5);
        var r = await Service(p).TestRegionAsync("eu-central-1", [], NetworkFixtures.Fast, default);
        Assert.True(r.EndpointUnavailable);
        Assert.Equal(0, p.Calls);
    }

    [Fact]
    public async Task Icmp_block_falls_back_to_tcp_and_says_so()
    {
        var r = await Service(new FakeProber((_, proto) => proto == ProbeProtocol.Tcp ? 270 : null))
            .TestRegionAsync("ap-northeast-1", [NetworkFixtures.Target("a")], NetworkFixtures.Fast with { Protocol = ProbeProtocol.Icmp }, default);
        Assert.True(r.FellBackFromIcmp);
        Assert.Equal(ProbeProtocol.Tcp, r.Protocol);
    }

    [Fact]
    public async Task Tcp_does_not_fall_back_to_other_metrics()
    {
        var r = await Service(new FakeProber((_, proto) => proto == ProbeProtocol.Icmp ? 5 : null))
            .TestRegionAsync("eu-central-1", [NetworkFixtures.Target("a")], NetworkFixtures.Fast, default);
        Assert.True(r.EndpointUnavailable);
    }

    [Fact]
    public async Task Fresh_validated_target_goes_first_and_stale_is_not_preferred()
    {
        var now = DateTimeOffset.Now;
        var order = Service(new FakeProber((_, _) => 1)).Order(
        [
            NetworkFixtures.Target("stale", validated: now.AddDays(-30)),
            NetworkFixtures.Target("never"),
            NetworkFixtures.Target("fresh", validated: now.AddHours(-1)),
        ]).ToList();
        Assert.Equal("fresh", order[0].Ip);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new FakeProber((_, _) => 1))
            .TestRegionsAsync(["eu-central-1"], _ => [NetworkFixtures.Target("a")], NetworkFixtures.Fast, _ => { }, cts.Token));
    }

    [Fact]
    public async Task Tests_all_regions()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Service(new FakeProber((_, _) => 1)).TestRegionsAsync(["a", "b", "c", "d", "e"],
            r => [NetworkFixtures.Target("1.1.1.1", r)], NetworkFixtures.Fast, x => seen.Add(x.Region), default);
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task A_failing_probe_recovers_on_the_next_sample()
    {
        int i = 0;
        var s = await Service(new FakeProber((_, _) => ++i == 1 ? null : 20))
            .MeasureAsync(new ProbeTarget("x"), NetworkFixtures.Fast, null, default);
        Assert.Equal(4, s.Received);
    }
}

public class RankingTests
{
    [Fact]
    public void Orders_by_average()
    {
        var r = RegionRanking.Rank([NetworkFixtures.Result("b", 60), NetworkFixtures.Result("a", 45)], ProbeProtocol.Tcp);
        Assert.Equal("a", r[0].Region);
    }

    [Fact]
    public void Different_protocols_never_compete()
    {
        var r = RegionRanking.Rank(
            [NetworkFixtures.Result("icmp", 10, ProbeProtocol.Icmp), NetworkFixtures.Result("tcp", 50), NetworkFixtures.Result("http", 5, ProbeProtocol.Http)],
            ProbeProtocol.Tcp);
        Assert.Equal("tcp", Assert.Single(r).Region);
    }

    [Fact]
    public void Fallback_and_unavailable_rows_are_excluded()
    {
        var r = RegionRanking.Rank(
            [NetworkFixtures.Result("fb", 1, fallback: true), new RegionResult { Region = "x", EndpointUnavailable = true }, NetworkFixtures.Result("ok", 50)],
            ProbeProtocol.Tcp);
        Assert.Equal("ok", Assert.Single(r).Region);
    }
}

public class AwsTargetTests
{
    private const string Doc = """
        [ { "eu-central-1": { "3.64.0.0/12": "3.64.0.0", "18.156.0.0/14": "18.156.0.2" } },
          { "cn-north-1": { "52.80.0.0/15": "52.80.0.1" } } ]
        """;

    private static readonly string[] Regions = ["eu-central-1"];

    [Fact]
    public void Parses_only_wanted_regions_with_provenance()
    {
        var r = AwsTargetParser.Parse(Doc, Regions, "src", DateTimeOffset.Now);
        Assert.Equal(2, r.Targets.Count);
        Assert.All(r.Targets, t => Assert.Equal("src", t.Source));
    }

    [Theory]
    [InlineData("""[{"eu-central-1":{"3.64.0.0/12":"999.1.1.1"}}]""")]
    [InlineData("""[{"eu-central-1":{"3.64.0.0/12":"8.8.8.8"}}]""")]
    [InlineData("""[{"eu-central-1":{"bad":"3.64.0.0"}}]""")]
    [InlineData("""[{"eu-central-1":{"3.64.0.0/12":5}}]""")]
    public void Malformed_or_foreign_addresses_are_rejected(string json)
    {
        var r = AwsTargetParser.Parse(json, Regions, "s", DateTimeOffset.Now);
        Assert.Empty(r.Targets);
        Assert.Equal(1, r.Rejected);
    }

    [Fact]
    public void Changed_format_is_an_error_not_empty_success() =>
        Assert.Throws<InvalidDataException>(() => AwsTargetParser.Parse("""{"new":"shape"}""", Regions, "s", DateTimeOffset.Now));

    [Fact]
    public void Addresses_are_never_generated_from_a_prefix()
    {
        var r = AwsTargetParser.Parse("""[{"eu-central-1":{"3.64.0.0/12":"3.64.0.0"}}]""", Regions, "s", DateTimeOffset.Now);
        Assert.Equal("3.64.0.0", Assert.Single(r.Targets).Ip);
    }

    [Fact]
    public void Ranges_cross_check_drops_wrong_region()
    {
        var ranges = AwsTargetParser.ParseRanges("""{"prefixes":[{"ip_prefix":"3.64.0.0/12","region":"eu-west-1"}]}""");
        var kept = AwsTargetParser.DropMismatched([NetworkFixtures.Target("3.64.0.0")], ranges);
        Assert.Empty(kept);
    }

    [Fact]
    public void Longest_prefix_wins_and_global_is_ignored()
    {
        var ranges = AwsTargetParser.ParseRanges("""
            {"prefixes":[{"ip_prefix":"3.0.0.0/8","region":"GLOBAL"},{"ip_prefix":"3.64.0.0/12","region":"us-east-1"},{"ip_prefix":"3.64.0.0/16","region":"eu-central-1"}]}
            """);
        Assert.Equal("eu-central-1", AwsTargetParser.RegionOf("3.64.1.1", ranges));
        Assert.Null(AwsTargetParser.RegionOf("9.9.9.9", ranges));
    }

    [Fact]
    public void Http_url_matches_the_aws_page_pattern() =>
        Assert.Equal("http://3-64-0-0.ipv4.ec2-reachability.amazonaws.com/green-icon.gif", NetworkFixtures.Target("3.64.0.0").HttpUrl);
}

public class EndpointTests
{
    private readonly RegionCatalog _c = RegionCatalog.LoadEmbedded();

    private static SavedEndpoint Valid() => new() { Name = "x", Host = "1.2.3.4" };

    [Theory]
    [InlineData("1.2.3.4", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("server.example.com", true)]
    [InlineData("localhost", true)]
    [InlineData("999.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("bad host", false)]
    [InlineData("-bad.com", false)]
    [InlineData("", false)]
    public void Host_validation(string host, bool ok) => Assert.Equal(ok, SavedEndpoint.IsValidHost(host));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Invalid_ports_rejected(int port)
    {
        var e = Valid();
        e.Port = port;
        Assert.Contains(e.Validate(_c), m => m.Key == "error.ep_port");
    }

    [Fact]
    public void Tcp_needs_a_port()
    {
        var e = Valid();
        e.Protocol = ProbeProtocol.Tcp;
        Assert.NotEmpty(e.Validate(_c));
    }

    [Fact]
    public void Confirmed_needs_evidence()
    {
        var e = Valid();
        e.Category = EndpointCategory.ConfirmedGameplay;
        Assert.Contains(e.Validate(_c), m => m.Key == "error.ep_evidence");
        e.Evidence = "Shown by the in-game overlay";
        Assert.Empty(e.Validate(_c));
    }

    [Fact]
    public void Regional_test_needs_the_real_provider()
    {
        var e = Valid();
        e.Category = EndpointCategory.RegionalTest;
        Assert.Contains(e.Validate(_c), m => m.Key == "error.ep_provider");
    }

    [Fact]
    public void Unknown_region_rejected()
    {
        var e = Valid();
        e.Region = "mars-1";
        Assert.Contains(e.Validate(_c), m => m.Key == "error.ep_region");
    }

    [Fact]
    public void Import_round_trip_does_not_carry_trust()
    {
        var e = Valid();
        e.Category = EndpointCategory.ConfirmedGameplay;
        e.Evidence = "x";
        e.Verified = true;
        var (list, errors) = EndpointIo.Import(EndpointIo.Export([e]), _c);
        Assert.Empty(errors);
        Assert.Equal(EndpointCategory.ObservedPossible, list![0].Category);
        Assert.False(list[0].Verified);
        Assert.NotEqual(e.Id, list[0].Id);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("""[{"name":"x","host":"999.1.1.1"}]""")]
    public void Bad_import_rejected_whole(string json) => Assert.Null(EndpointIo.Import(json, _c).Endpoints);

    [Fact]
    public void Store_round_trip()
    {
        using var d = new TempDir();
        var p = new AppPaths(d.Path);
        p.EnsureCreated();
        new JsonEndpointStore(p).Save([Valid()]);
        Assert.Equal("1.2.3.4", Assert.Single(new JsonEndpointStore(p).Load()).Host);
    }
}

public class EndpointClassificationTests
{
    [Fact]
    public void Observed_aws_address_is_only_possible()
    {
        var e = new GameTcpEndpoint("3.64.0.1", 443, "Established");
        Assert.Equal(EndpointCategory.ObservedPossible, EndpointClassifier.Classify(e));
        var saved = EndpointClassifier.ToSaved(e, "eu-central-1", DateTimeOffset.Now);
        Assert.Equal(EndpointCategory.ObservedPossible, saved.Category);
        Assert.Equal("AWS", saved.Provider);
        Assert.False(saved.Verified);
    }

    [Fact]
    public void Regional_targets_carry_no_bf6_claim() =>
        Assert.All(RegionCatalog.LoadEmbedded().Regions, r => Assert.Equal("AWS", r.Provider));
}

public class HistoryTests
{
    [Fact]
    public void Persists_and_bounds_records()
    {
        using var d = new TempDir();
        var p = new AppPaths(d.Path);
        p.EnsureCreated();
        var store = new JsonNetworkHistoryStore(p);
        store.Append([new NetworkTestRecord { Timestamp = DateTimeOffset.Now, Region = "eu-central-1", Protocol = ProbeProtocol.Tcp, Stats = LatencyStats.From(2, [10, 20]) }]);
        var back = Assert.Single(new JsonNetworkHistoryStore(p).Load());
        Assert.Equal(15, back.Stats!.Average);
        store.Append(Enumerable.Range(0, 3100).Select(i => new NetworkTestRecord { Timestamp = DateTimeOffset.Now.AddSeconds(i) }));
        Assert.Equal(3000, store.Load().Count);
    }

    [Fact]
    public void Corrupt_history_recovers()
    {
        using var d = new TempDir();
        var p = new AppPaths(d.Path);
        p.EnsureCreated();
        File.WriteAllText(Path.Combine(p.Data, "network-history.json"), "{bad");
        Assert.Empty(new JsonNetworkHistoryStore(p).Load());
    }
}

public class NetworkRecommendationTests
{
    private static string City(string r) => r;

    private static IReadOnlyList<string> Ids(NetworkContext c) =>
        NetworkRecommendationService.Evaluate(c, City).Select(r => r.Id).ToList();

    [Fact]
    public void All_regions_high()
    {
        var c = new NetworkContext { Results = [NetworkFixtures.Result("a", 150), NetworkFixtures.Result("b", 160), NetworkFixtures.Result("c", 170)] };
        Assert.Contains("all_high", Ids(c));
    }

    [Fact]
    public void Gateway_spikes_point_to_the_local_network()
    {
        var c = new NetworkContext { Gateway = new LatencyStats(5, 5, 1, 12, 2, 80, 30), WiFi = true };
        var r = Assert.Single(NetworkRecommendationService.Evaluate(c, City));
        Assert.Equal("net.rec.gateway_wifi", r.Text.Key);
    }

    [Fact]
    public void Best_region_is_named_as_a_probe_result()
    {
        var c = new NetworkContext { Results = [NetworkFixtures.Result("Frankfurt", 45), NetworkFixtures.Result("Paris", 53)] };
        var best = NetworkRecommendationService.Evaluate(c, City).Single(r => r.Id == "best");
        Assert.Equal("Frankfurt", best.Text.Args[0]);
    }

    [Fact]
    public void Poor_in_game_ping_against_good_probe()
    {
        var c = new NetworkContext { Results = [NetworkFixtures.Result("a", 45)], ManualInGamePing = 120 };
        Assert.Contains("ingame_gap", Ids(c));
    }

    [Fact]
    public void Icmp_failure_is_not_an_outage()
    {
        var c = new NetworkContext { Results = [NetworkFixtures.Result("a", 270, fallback: true), new RegionResult { Region = "b", EndpointUnavailable = true }] };
        var ids = Ids(c);
        Assert.Contains("icmp_blocked", ids);
        Assert.Contains("unavailable", ids);
        Assert.DoesNotContain("all_high", ids);
    }
}

public class RouteTests
{
    private sealed class FakeRoute((string?, double?, bool)[] hops) : IRouteProber
    {
        public Task<(string? Address, double? Rtt, bool Reached)> ProbeHopAsync(string host, int ttl, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ttl <= hops.Length ? hops[ttl - 1] : (null, null, false));
        }

        public Task<string?> ReverseLookupAsync(string address, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private static RouteHop Hop(int n, double? rtt, bool reached = false) =>
        new(n, rtt is null ? null : "1.1.1." + n, null, [rtt], reached);

    [Fact]
    public async Task Stops_at_destination()
    {
        var hops = await new TracerouteService(new FakeRoute([("1.1.1.1", 1, false), ("2.2.2.2", 40, true)])).TraceAsync("x", _ => { }, default);
        Assert.Equal(2, hops.Count);
        Assert.True(hops[1].Reached);
    }

    [Fact]
    public async Task Gives_up_after_a_silent_run()
    {
        var hops = await new TracerouteService(new FakeRoute([("1.1.1.1", 1, false)])).TraceAsync("x", _ => { }, default);
        Assert.Equal(7, hops.Count);
    }

    [Fact]
    public async Task Cancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TracerouteService(new FakeRoute([])).TraceAsync("x", _ => { }, cts.Token));
    }

    [Fact]
    public void Silent_middle_hop_is_not_called_loss()
    {
        var notes = RouteAnalyzer.Interpret([Hop(1, 1), Hop(2, null), Hop(3, 20, true)]);
        Assert.Contains(notes, n => n.Key == "net.route.silent");
        Assert.DoesNotContain(notes, n => n.Key == "net.route.jump");
    }

    [Fact]
    public void Persistent_jump_is_reported()
    {
        var notes = RouteAnalyzer.Interpret([Hop(1, 5), Hop(2, 10), Hop(3, 90), Hop(4, 95, true)]);
        Assert.Contains(notes, n => n.Key == "net.route.jump" && n.Args[0] == "3");
    }

    [Fact]
    public void Single_slow_router_is_not_a_jump()
    {
        var notes = RouteAnalyzer.Interpret([Hop(1, 5), Hop(2, 150), Hop(3, 12, true)]);
        Assert.DoesNotContain(notes, n => n.Key == "net.route.jump");
    }

    [Fact]
    public void No_replies_gives_no_conclusions() =>
        Assert.Equal("net.route.no_data", Assert.Single(RouteAnalyzer.Interpret([Hop(1, null)])).Key);

    [Fact]
    public void Clean_route_says_so() =>
        Assert.Equal("net.route.ok", Assert.Single(RouteAnalyzer.Interpret([Hop(1, 1), Hop(2, 20, true)])).Key);
}
