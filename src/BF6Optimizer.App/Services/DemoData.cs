#if DEBUG
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.App.Services;

/// <summary>
/// Synthetic scan for documentation screenshots. Compiled out of Release, shown under a demo banner,
/// and it reads nothing from the computer it runs on.
/// </summary>
public sealed class DemoAuditCollector(IHardwareDetector hardware, IClock clock) : ISystemAuditCollector
{
    private const long Gb = 1024L * 1024 * 1024;

    public async Task<SystemAuditSnapshot> CollectAsync(bool full, string? gamingDisplay, string? gamingGpuId, IProgress<(string Section, int Percent)>? progress, CancellationToken ct)
    {
        progress?.Report(("hardware", 50));
        var hw = await hardware.DetectAsync(ct);
        progress?.Report(("done", 100));

        MemoryModule Module(string slot) => new(slot, "", "Demo", "DEMO-5600-16G", 16 * Gb, 34, 4800, 5600, 12);
        DisplayFacts Display(DisplayInfo d, int max) => new()
        {
            Display = d,
            Manufacturer = "Demo",
            ScalePercent = 100,
            BitsPerColorChannel = 10,
            HdrSupported = false,
            HdrEnabled = false,
            Modes = [new(d.Width, d.Height, max), new(d.Width, d.Height, 60), new(1920, 1080, 60)],
        };

        return new SystemAuditSnapshot
        {
            Timestamp = clock.Now,
            Full = full,
            Hardware = hw,
            GamingDisplay = gamingDisplay,
            GamingGpuId = gamingGpuId,
            Memory = new MemoryFacts
            {
                Modules = [Module("DIMM_A1"), Module("DIMM_B1")],
                TotalPhysical = 31 * Gb, AvailablePhysical = 19 * Gb, CachedBytes = 9 * Gb,
                CommitTotal = 15 * Gb, CommitLimit = 36 * Gb,
                PagefileAutoManaged = true,
                PageFiles = [new PageFile("pagefile.sys", 4096, 120)],
            },
            Cpu = new CpuFacts
            {
                Name = hw.Cpu?.Name ?? "", Manufacturer = "Demo", Architecture = "x64", PhysicalCores = 8, LogicalProcessors = 16,
                MaxClockMHz = 4500, CurrentClockMHz = 4200, NumaNodes = 1, L3CacheGroups = 1, IdleLoadPercent = 4, AffinityUnrestricted = true,
            },
            Power = new PowerFacts { SchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", SchemeName = "Balanced", OnAc = true, BatterySaver = false, ProcessorMinPercentAc = 5, ProcessorMaxPercentAc = 100 },
            Gpus = hw.Gpus.Select(g => new GpuAuditFacts(g.Id, g.Name, clock.Now.AddMonths(-2), "32.0.0.0000", g.DedicatedVramBytes, null)).ToList(),
            Displays = [Display(hw.Displays[0], 240), Display(hw.Displays[1], 60)],
            Windows = new WindowsFacts { Caption = "Demo Windows 11", Build = "26100", AutoGameMode = 1, GameDvrEnabled = 1, AppCaptureEnabled = 1, HagsMode = 2, RebootPending = false },
            Security = new SecurityFacts { SecureBoot = true, VbsRunning = true, HvciRunning = true, HypervisorPresent = true, AntivirusProducts = ["Demo Antivirus"] },
            Storage = full ? new StorageFacts
            {
                Volumes = [new VolumeFacts("C:", "NTFS", 1000 * Gb, 420 * Gb, true, true, "SSD")],
                Disks = [new PhysicalDiskFacts("Demo NVMe SSD", "SSD", "NVMe", 1000 * Gb, "Healthy")],
                DiskActivePercent = 2,
            } : null,
            Background = full ? new BackgroundFacts { BackgroundCpuPercent = 5, ProcessCount = 180 } : null,
            Network = full ? new NetworkFacts { Adapters = [new AdapterFacts("Demo Ethernet", "Ethernet", 1000, true, false, ["192.0.2.53"], "192.0.2.1")] } : null,
            Firmware = new FirmwareFacts { BoardManufacturer = "Demo", BoardModel = "Demo Board", BiosVendor = "Demo", BiosVersion = "1.0", BootMode = "UEFI" },
            Thermal = new ThermalFacts(null),
        };
    }
}
#endif
