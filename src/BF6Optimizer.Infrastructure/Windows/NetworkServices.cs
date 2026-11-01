using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Network;
using BF6Optimizer.Infrastructure.Storage;

namespace BF6Optimizer.Infrastructure.Windows;

public sealed class WindowsNetworkProber : INetworkProber
{
    // Pooling off: each HTTP sample includes a fresh connection, so samples stay comparable.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero, AllowAutoRedirect = false });

    public Task<double?> ProbeAsync(ProbeTarget target, ProbeProtocol protocol, TimeSpan timeout, CancellationToken ct) => protocol switch
    {
        ProbeProtocol.Icmp => IcmpAsync(target.Host, timeout, ct),
        ProbeProtocol.Tcp => TcpAsync(target.Host, target.Port, timeout, ct),
        _ => HttpAsync(target.HttpUrl, timeout, ct),
    };

    private static async Task<double?> IcmpAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeout, cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }

    private static async Task<double?> TcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<double?> HttpAsync(string? url, TimeSpan timeout, CancellationToken ct)
    {
        if (url is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? watch.Elapsed.TotalMilliseconds : null;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}

public sealed class WindowsRouteProber : IRouteProber
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<(string? Address, double? Rtt, bool Reached)> ProbeHopAsync(string host, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        var watch = Stopwatch.StartNew();
        try
        {
            var reply = await ping.SendPingAsync(host, timeout, Payload, new PingOptions(ttl, true), ct).ConfigureAwait(false);
            // .NET reports RoundtripTime only for Success, so the hop time comes from the stopwatch.
            double rtt = watch.Elapsed.TotalMilliseconds;
            return reply.Status switch
            {
                IPStatus.Success => (reply.Address.ToString(), rtt, true),
                IPStatus.TtlExpired or IPStatus.TimeExceeded => (reply.Address.ToString(), rtt, false),
                _ => (null, null, false),
            };
        }
        catch (PingException) { return (null, null, false); }
    }

    public async Task<string?> ReverseLookupAsync(string address, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        try { return (await Dns.GetHostEntryAsync(address, cts.Token).ConfigureAwait(false)).HostName; }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}

/// <summary>Optional refresh of AWS's published probe addresses. Failure leaves stored targets in place.</summary>
public sealed class AwsTargetProvider(ITargetStore store, RegionCatalog catalog, AppPaths paths, IClock clock)
{
    public const string TargetsUrl = "http://ec2-reachability.amazonaws.com/prefixes-ipv4.json";
    public const string RangesUrl = "https://ip-ranges.amazonaws.com/ip-ranges.json";
    private const int KeepPerRegion = 12;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string RangesPath => Path.Combine(paths.Data, "aws-ranges.json");

    public IReadOnlyList<RegionTarget> Current()
    {
        try
        {
            if (store.Load() is { Targets.Count: > 0 } set) return set.Targets;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        return AwsTargetParser.Seed(catalog);
    }

    public IReadOnlyList<AwsRange> LoadRanges()
    {
        try { return JsonFile.Read<List<AwsRange>>(RangesPath) ?? []; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { return []; }
    }

    /// <summary>Returns the stored target count. Throws only for failures the caller should report.</summary>
    public async Task<(int Targets, int Rejected, bool RangesChecked)> RefreshAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var parsed = AwsTargetParser.Parse(await Http.GetStringAsync(TargetsUrl, ct).ConfigureAwait(false), catalog.Regions.Select(r => r.Code), TargetsUrl, now);
        if (parsed.Targets.Count == 0) throw new InvalidDataException("No usable targets.");

        var targets = parsed.Targets;
        bool checkedRanges = false;
        try
        {
            var ranges = AwsTargetParser.ParseRanges(await Http.GetStringAsync(RangesUrl, ct).ConfigureAwait(false));
            JsonFile.Write(RangesPath, ranges);
            targets = AwsTargetParser.DropMismatched(targets, ranges);
            checkedRanges = true;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }

        var validated = Current().Where(t => t.ValidatedAt is not null).ToDictionary(t => t.Ip, t => t.ValidatedAt);
        var kept = targets.GroupBy(t => t.Region)
            .SelectMany(g => g.Take(KeepPerRegion))
            .Select(t => t with { ValidatedAt = validated.GetValueOrDefault(t.Ip) })
            .ToList();
        store.Save(new TargetSet { SourceUrl = TargetsUrl, RetrievedAt = now, RangesChecked = checkedRanges, Targets = kept });
        return (kept.Count, parsed.Rejected, checkedRanges);
    }
}

public sealed class JsonTargetStore(AppPaths paths) : ITargetStore
{
    private string FilePath => Path.Combine(paths.Data, "network-targets.json");

    public TargetSet? Load() => JsonFile.Read<TargetSet>(FilePath);

    public void Save(TargetSet set) => JsonFile.Write(FilePath, set);
}

public sealed class JsonEndpointStore(AppPaths paths) : IEndpointStore
{
    private string FilePath => Path.Combine(paths.Data, "network-endpoints.json");

    public IReadOnlyList<SavedEndpoint> Load()
    {
        try { return JsonFile.Read<List<SavedEndpoint>>(FilePath) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public void Save(IReadOnlyList<SavedEndpoint> endpoints) => JsonFile.Write(FilePath, endpoints);
}

public sealed class JsonNetworkHistoryStore(AppPaths paths) : INetworkHistoryStore
{
    private const int Keep = 3000;

    private string FilePath => Path.Combine(paths.Data, "network-history.json");

    public IReadOnlyList<NetworkTestRecord> Load()
    {
        try { return JsonFile.Read<List<NetworkTestRecord>>(FilePath) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public void Append(IEnumerable<NetworkTestRecord> records)
    {
        var all = Load().Concat(records).OrderBy(r => r.Timestamp).TakeLast(Keep).ToList();
        JsonFile.Write(FilePath, all);
    }
}

public static class ConnectionInfo
{
    public static ConnectionSummary? Read()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var ip = n.GetIPProperties();
            var gateway = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is null) continue;
            return new ConnectionSummary(
                n.Name,
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : n.NetworkInterfaceType.ToString(),
                n.Speed / 1_000_000,
                gateway.ToString(),
                ip.DnsAddresses.Select(a => a.ToString()).ToList(),
                ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork),
                ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal));
        }
        return null;
    }
}

/// <summary>
/// Reads the documented IP Helper tables. TCP rows carry the remote peer; UDP rows carry only the local port,
/// so no remote UDP endpoint is ever reported.
/// </summary>
public sealed class IpHelperConnectionObserver(IClock clock) : IGameConnectionObserver
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int cls, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(nint table, ref int size, bool order, int family, int cls, uint reserved);

    private delegate uint TableReader(nint table, ref int size);

    public GameConnectionSnapshot Observe()
    {
        var pids = Process.GetProcessesByName("bf6").Select(p => { using (p) return p.Id; }).ToHashSet();
        if (pids.Count == 0) return new GameConnectionSnapshot { ObservedAt = clock.Now };

        var tcp = new List<GameTcpEndpoint>();
        Read((nint t, ref int s) => GetExtendedTcpTable(t, ref s, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0), 24, (row) =>
        {
            int state = Marshal.ReadInt32(row, 0);
            int pid = Marshal.ReadInt32(row, 20);
            uint remote = (uint)Marshal.ReadInt32(row, 12);
            if (!pids.Contains(pid) || remote == 0) return;
            int port = (ushort)IPAddress.NetworkToHostOrder(unchecked((short)(Marshal.ReadInt32(row, 16) & 0xFFFF)));
            tcp.Add(new GameTcpEndpoint(new IPAddress(remote).ToString(), port, state == 5 ? "Established" : state.ToString()));
        });

        var udp = new List<int>();
        Read((nint t, ref int s) => GetExtendedUdpTable(t, ref s, false, AF_INET, UDP_TABLE_OWNER_PID, 0), 12, (row) =>
        {
            if (pids.Contains(Marshal.ReadInt32(row, 8)))
                udp.Add((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(Marshal.ReadInt32(row, 4) & 0xFFFF))));
        });

        return new GameConnectionSnapshot { GameRunning = true, Tcp = tcp.Distinct().ToList(), UdpLocalPorts = udp.Distinct().ToList(), ObservedAt = clock.Now };
    }

    private static void Read(TableReader reader, int rowSize, Action<nint> onRow)
    {
        int size = 0;
        reader(0, ref size);
        if (size == 0) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (reader(buffer, ref size) != 0) return;
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++) onRow(buffer + 4 + i * rowSize);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
