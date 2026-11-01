using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.Core.Audit;

// Raw facts collected from Windows. A null section means it could not be read; evaluation reports Unknown.

public sealed record MemoryModule(
    string Slot, string Bank, string Manufacturer, string PartNumber, long CapacityBytes,
    int? SmbiosMemoryType, int? ConfiguredMTs, int? RatedMTs, int? FormFactor);

public sealed record PageFile(string Name, long AllocatedMb, long CurrentUsageMb);

public sealed record MemoryFacts
{
    public IReadOnlyList<MemoryModule> Modules { get; init; } = [];
    public long TotalPhysical { get; init; }
    public long AvailablePhysical { get; init; }
    public long CachedBytes { get; init; }
    public long CommitTotal { get; init; }
    public long CommitLimit { get; init; }
    public IReadOnlyList<PageFile> PageFiles { get; init; } = [];
    public bool? PagefileAutoManaged { get; init; }
}

public sealed record CpuFacts
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int? PhysicalCores { get; init; }
    public int? LogicalProcessors { get; init; }
    public int? MaxClockMHz { get; init; }
    public int? CurrentClockMHz { get; init; }
    public int? NumaNodes { get; init; }
    public int? PerformanceCores { get; init; }
    public int? EfficiencyCores { get; init; }
    public int? L3CacheGroups { get; init; }
    public double? IdleLoadPercent { get; init; }
    public bool? AffinityUnrestricted { get; init; }
}

public sealed record PowerFacts
{
    public string? SchemeGuid { get; init; }
    public string? SchemeName { get; init; }
    public bool? OnAc { get; init; }
    public bool? BatterySaver { get; init; }
    public int? BatteryPercent { get; init; }
    public int? ProcessorMinPercentAc { get; init; }
    public int? ProcessorMaxPercentAc { get; init; }
}

public sealed record GpuAuditFacts(string Id, string Name, DateTimeOffset? DriverDate, string? DriverVersion, long? DedicatedBytes, string? PnpId);

public sealed record DisplayMode(int Width, int Height, int RefreshHz);

public sealed record DisplayFacts
{
    public required DisplayInfo Display { get; init; }
    public string Manufacturer { get; init; } = "";
    public IReadOnlyList<DisplayMode> Modes { get; init; } = [];
    public int ScalePercent { get; init; }
    public int Orientation { get; init; }
    public int BitsPerPixel { get; init; }
    public bool? HdrSupported { get; init; }
    public bool? HdrEnabled { get; init; }
    public int? BitsPerColorChannel { get; init; }
}

public sealed record WindowsFacts
{
    public string Caption { get; init; } = "";
    public string Build { get; init; } = "";
    public int? AutoGameMode { get; init; }
    public int? GameDvrEnabled { get; init; }
    public int? AppCaptureEnabled { get; init; }
    public int? HagsMode { get; init; }
    /// <summary>Raw value of DirectXUserGlobalSettings; null when never set.</summary>
    public string? DirectXGlobalSettings { get; init; }
    public string? GamePerAppGpuPreference { get; init; }
    public bool? RebootPending { get; init; }
}

public sealed record SecurityFacts
{
    public bool? SecureBoot { get; init; }
    public bool? TpmPresent { get; init; }
    public bool? VbsRunning { get; init; }
    public bool? HvciRunning { get; init; }
    public bool? HypervisorPresent { get; init; }
    public IReadOnlyList<string> AntivirusProducts { get; init; } = [];
}

public sealed record VolumeFacts(string Letter, string FileSystem, long Total, long Free, bool IsSystem, bool HasGame, string? MediaType);

public sealed record PhysicalDiskFacts(string Name, string MediaType, string BusType, long Size, string Health);

public sealed record StorageFacts
{
    public IReadOnlyList<VolumeFacts> Volumes { get; init; } = [];
    public IReadOnlyList<PhysicalDiskFacts> Disks { get; init; } = [];
    public double? DiskActivePercent { get; init; }
}

public sealed record ProcessFacts(string Name, int Pid, double CpuPercent, long WorkingSet, long PrivateBytes, long IoBytesPerSec, string? Publisher);

public enum StartupCategory { Windows, Driver, Security, GameLauncher, Communication, Peripheral, Other }

public sealed record StartupEntry(string Name, string Command, string Location, bool PerUser, bool Enabled, StartupCategory Category);

public sealed record BackgroundFacts
{
    public IReadOnlyList<ProcessFacts> TopProcesses { get; init; } = [];
    public int ProcessCount { get; init; }
    public double BackgroundCpuPercent { get; init; }
    public IReadOnlyList<StartupEntry> Startup { get; init; } = [];
}

public sealed record AdapterFacts(string Name, string Type, long LinkMbps, bool Ipv4, bool Ipv6, IReadOnlyList<string> Dns, string? Gateway);

public sealed record NetworkFacts
{
    public IReadOnlyList<AdapterFacts> Adapters { get; init; } = [];
}

public sealed record NetworkProbeResult(string Target, int Sent, int Received, double? AverageMs, double? JitterMs, double? DnsMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

public sealed record FirmwareFacts
{
    public string BoardManufacturer { get; init; } = "";
    public string BoardModel { get; init; } = "";
    public string BiosVendor { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public DateTimeOffset? BiosDate { get; init; }
    /// <summary>UEFI, Legacy or null.</summary>
    public string? BootMode { get; init; }
}

public sealed record ThermalFacts(double? GpuCelsius);

public sealed record SystemAuditSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    public bool Full { get; init; }
    public HardwareSnapshot? Hardware { get; init; }
    public string? GamingDisplay { get; init; }
    public string? GamingGpuId { get; init; }
    public MemoryFacts? Memory { get; init; }
    public CpuFacts? Cpu { get; init; }
    public PowerFacts? Power { get; init; }
    public IReadOnlyList<GpuAuditFacts>? Gpus { get; init; }
    public IReadOnlyList<DisplayFacts>? Displays { get; init; }
    public WindowsFacts? Windows { get; init; }
    public SecurityFacts? Security { get; init; }
    public StorageFacts? Storage { get; init; }
    public BackgroundFacts? Background { get; init; }
    public NetworkFacts? Network { get; init; }
    public NetworkProbeResult? NetworkProbe { get; init; }
    public FirmwareFacts? Firmware { get; init; }
    public ThermalFacts? Thermal { get; init; }
}

public interface ISystemAuditCollector
{
    Task<SystemAuditSnapshot> CollectAsync(bool full, string? gamingDisplay, string? gamingGpuId, IProgress<(string Section, int Percent)>? progress, CancellationToken ct);
}

public interface INetworkProbe
{
    /// <summary>Bounded, user-initiated. Never run in the background.</summary>
    Task<NetworkProbeResult> ProbeAsync(string target, int count, CancellationToken ct);
}
