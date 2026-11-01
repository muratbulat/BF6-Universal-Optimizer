namespace BF6Optimizer.Core.Hardware;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

public enum GpuKind { Unknown, Discrete, Integrated, Software }

public enum FormFactor { Unknown, Desktop, Laptop }

public sealed record GpuInfo
{
    /// <summary>Stable within a machine: PCI vendor/device/subsystem identifiers.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public GpuVendor Vendor { get; init; }
    public GpuKind Kind { get; init; }
    public FormFactor FormFactor { get; init; }
    /// <summary>What the form factor is based on: name, chassis or none.</summary>
    public string FormFactorEvidence { get; init; } = "none";
    public long? DedicatedVramBytes { get; init; }
    /// <summary>DXGI, NVML, Registry or None.</summary>
    public string VramSource { get; init; } = "None";
    public string? DriverVersion { get; init; }
    public double? PowerLimitW { get; init; }
    public double? PowerDefaultW { get; init; }
    public double? PowerMaxW { get; init; }

    public double? VramGb => DedicatedVramBytes is > 0 ? DedicatedVramBytes.Value / (1024.0 * 1024 * 1024) : null;
}

public sealed record CpuInfo(string Name, string Manufacturer, int? PhysicalCores, int? LogicalCores, int? MaxClockMHz);

public sealed record MemoryInfo(long? TotalBytes, int? SpeedMTs)
{
    public double? TotalGb => TotalBytes is > 0 ? TotalBytes.Value / (1024.0 * 1024 * 1024) : null;
}

public sealed record DisplayInfo(string DeviceName, string FriendlyName, int Width, int Height, double RefreshHz, bool IsPrimary);

public sealed record OsInfo(string Caption, string Build);

public sealed record HardwareSnapshot
{
    /// <summary>True only for development simulation; the UI must show a banner when set.</summary>
    public bool IsSimulated { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public FormFactor SystemFormFactor { get; init; }
    public string SystemFormFactorEvidence { get; init; } = "none";
    public CpuInfo? Cpu { get; init; }
    public MemoryInfo? Memory { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public OsInfo? Os { get; init; }
    /// <summary>HKLM HwSchMode, read only. Never written by this application.</summary>
    public int? HagsMode { get; init; }
    public IReadOnlyList<string> DetectionWarnings { get; init; } = [];
}

public interface IHardwareDetector
{
    Task<HardwareSnapshot> DetectAsync(CancellationToken ct = default);
    string GetMachineKey();
}
