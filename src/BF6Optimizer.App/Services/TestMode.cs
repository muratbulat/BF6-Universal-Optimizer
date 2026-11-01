using System.IO;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Network;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.App.Services;

/// <summary>
/// Options for UI automation. They parse only in Debug builds, so the Release EXE has no test switches.
/// With them, a test run touches a temp data folder, a fixture BF6 folder and an in-memory registry only.
/// </summary>
public sealed class TestMode
{
    public string? DataDir { get; private init; }
    public string? Bf6Dir { get; private init; }
    public string? ExportDir { get; private init; }
    public bool MockNetwork { get; private init; }
    public bool MockSystem { get; private init; }
    public bool SimulateHardware { get; private init; }

    public static TestMode Current { get; private set; } = new();

    public static void Parse(string[] args)
    {
#if DEBUG
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        Current = new TestMode
        {
            DataDir = Value("--data-dir"),
            Bf6Dir = Value("--bf6-dir"),
            ExportDir = Value("--export-dir"),
            MockNetwork = args.Contains("--mock-network"),
            MockSystem = args.Contains("--mock-system"),
            SimulateHardware = args.Contains("--simulate-hardware"),
        };
#endif
    }
}

#if DEBUG
/// <summary>Deterministic network: 3 regions fail differently so every UI state is reachable offline.</summary>
public sealed class MockNetworkProber : INetworkProber
{
    public async Task<double?> ProbeAsync(ProbeTarget target, ProbeProtocol protocol, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(40, ct);
        int seed = target.Host.Split('.')[0] is var first && int.TryParse(first, out var n) ? n : 10;
        if (target.Host.StartsWith("157.241", StringComparison.Ordinal) || target.Host.StartsWith("15.185", StringComparison.Ordinal)) return null;
        return protocol == ProbeProtocol.Icmp && seed % 5 == 0 ? null : 20 + seed % 90;
    }
}

public sealed class MockRouteProber : IRouteProber
{
    public async Task<(string? Address, double? Rtt, bool Reached)> ProbeHopAsync(string host, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(30, ct);
        return ttl switch
        {
            3 => (null, null, false),
            5 => (host, 45, true),
            _ => ($"10.0.0.{ttl}", ttl * 8, false),
        };
    }

    public Task<string?> ReverseLookupAsync(string address, CancellationToken ct) => Task.FromResult<string?>(null);
}

public sealed class MemoryPreferenceStore : IWindowsPreferenceStore
{
    private readonly Dictionary<string, int> _values = [];

    public RegistryValueState Read(string path, string name) =>
        _values.TryGetValue(path + name, out var v) ? new(path, name, true, v) : new(path, name, false, null);

    public void Write(string path, string name, int value) => _values[path + name] = value;

    public void Remove(string path, string name) => _values.Remove(path + name);
}

public sealed class MemoryStartupStore : IStartupStore
{
    private readonly List<StartupEntry> _items =
    [
        new("TestUtility", @"C:\Test\tool.exe", "HKCU Run", true, true, StartupCategory.Other),
    ];

    public IReadOnlyList<StartupEntry> List() => _items.ToList();

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        int i = _items.FindIndex(e => e.Name == entry.Name);
        _items[i] = _items[i] with { Enabled = enabled };
    }
}
#endif
