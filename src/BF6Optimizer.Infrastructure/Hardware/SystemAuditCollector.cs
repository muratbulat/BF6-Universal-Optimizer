using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BF6Optimizer.Infrastructure.Hardware;

/// <summary>
/// Each section runs under a guard: a failed query leaves that section null and the rest of the scan continues.
/// </summary>
public sealed partial class SystemAuditCollector(IHardwareDetector hardware, IStartupStore startup, IClock clock, ILogger<SystemAuditCollector> logger) : ISystemAuditCollector
{
    private static readonly string[] DirectXKey = [@"Software\Microsoft\DirectX\UserGpuPreferences"];

    public async Task<SystemAuditSnapshot> CollectAsync(bool full, string? gamingDisplay, string? gamingGpuId, IProgress<(string Section, int Percent)>? progress, CancellationToken ct)
    {
        void P(string section, int percent) => progress?.Report((section, percent));

        P("hardware", 5);
        var hw = await GuardAsync("hardware", () => hardware.DetectAsync(ct)).ConfigureAwait(false);
        P("memory", 20);
        var memory = await Task.Run(() => Guard("memory", ReadMemory), ct).ConfigureAwait(false);
        P("cpu", 30);
        var cpu = await GuardAsync("cpu", () => ReadCpuAsync(hw, ct)).ConfigureAwait(false);
        var power = Guard("power", NativeSystem.ReadPower);
        P("gpu", 45);
        var gpus = await Task.Run(() => Guard("gpu", () => ReadGpus(hw)), ct).ConfigureAwait(false);
        P("display", 55);
        var displays = await Task.Run(() => Guard("display", DisplayEnumerator.EnumerateFacts), ct).ConfigureAwait(false);
        P("windows", 62);
        var windows = await Task.Run(() => Guard("windows", () => ReadWindows(hw)), ct).ConfigureAwait(false);
        var security = await Task.Run(() => Guard("security", ReadSecurity), ct).ConfigureAwait(false);
        var firmware = await Task.Run(() => Guard("firmware", ReadFirmware), ct).ConfigureAwait(false);
        var thermal = new ThermalFacts(Guard("thermal", NvmlProbe.GpuTemperature));

        StorageFacts? storage = null;
        BackgroundFacts? background = null;
        NetworkFacts? network = null;
        if (full)
        {
            ct.ThrowIfCancellationRequested();
            P("storage", 72);
            storage = await Task.Run(() => Guard("storage", ReadStorage), ct).ConfigureAwait(false);
            P("processes", 84);
            background = await GuardAsync("processes", () => ReadBackgroundAsync(ct)).ConfigureAwait(false);
            P("network", 95);
            network = await Task.Run(() => Guard("network", ReadNetwork), ct).ConfigureAwait(false);
        }

        P("done", 100);
        return new SystemAuditSnapshot
        {
            Timestamp = clock.Now,
            Full = full,
            Hardware = hw,
            GamingDisplay = gamingDisplay,
            GamingGpuId = gamingGpuId,
            Memory = memory,
            Cpu = cpu,
            Power = power,
            Gpus = gpus,
            Displays = displays,
            Windows = windows,
            Security = security,
            Storage = storage,
            Background = background,
            Network = network,
            Firmware = firmware,
            Thermal = thermal,
        };
    }

    private T? Guard<T>(string section, Func<T> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (Recoverable(ex))
        {
            logger.LogWarning(ex, "Audit section {Section} unavailable", section);
            return null;
        }
    }

    private double? Guard(string section, Func<double?> read)
    {
        try { return read(); }
        catch (Exception ex) when (Recoverable(ex))
        {
            logger.LogWarning(ex, "Audit section {Section} unavailable", section);
            return null;
        }
    }

    private async Task<T?> GuardAsync<T>(string section, Func<Task<T>> read) where T : class
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (Recoverable(ex))
        {
            logger.LogWarning(ex, "Audit section {Section} unavailable", section);
            return null;
        }
    }

    private static bool Recoverable(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    internal static List<T> Wmi<T>(string query, Func<ManagementBaseObject, T> map, string scope = @"root\cimv2")
    {
        var result = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var items = searcher.Get();
            foreach (var item in items)
                using (item)
                    result.Add(map(item));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }
        return result;
    }

    private static int? I(object? v) => v is null ? null : Convert.ToInt32(v);
    private static long? L(object? v) => v is null ? null : Convert.ToInt64(v);

    private static MemoryFacts ReadMemory()
    {
        var status = new NativeSystem.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeSystem.MEMORYSTATUSEX>() };
        NativeSystem.GlobalMemoryStatusEx(ref status);
        var perf = new NativeSystem.PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<NativeSystem.PERFORMANCE_INFORMATION>() };
        bool perfOk = NativeSystem.GetPerformanceInfo(ref perf, perf.cb);
        long page = perfOk ? (long)perf.PageSize : 4096;

        var modules = Wmi("SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, SMBIOSMemoryType, ConfiguredClockSpeed, Speed, FormFactor FROM Win32_PhysicalMemory",
            o => new MemoryModule(
                (o["DeviceLocator"] as string ?? "").Trim(), (o["BankLabel"] as string ?? "").Trim(),
                (o["Manufacturer"] as string ?? "").Trim(), (o["PartNumber"] as string ?? "").Trim(),
                L(o["Capacity"]) ?? 0, I(o["SMBIOSMemoryType"]), I(o["ConfiguredClockSpeed"]), I(o["Speed"]), I(o["FormFactor"])));

        var pageFiles = Wmi("SELECT Name, AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage",
            o => new PageFile(Path.GetFileName(o["Name"] as string ?? ""), L(o["AllocatedBaseSize"]) ?? 0, L(o["CurrentUsage"]) ?? 0));
        var auto = Wmi("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem", o => o["AutomaticManagedPagefile"] as bool?).FirstOrDefault();

        return new MemoryFacts
        {
            Modules = modules,
            TotalPhysical = (long)status.ullTotalPhys,
            AvailablePhysical = (long)status.ullAvailPhys,
            CachedBytes = perfOk ? (long)perf.SystemCache * page : 0,
            CommitTotal = perfOk ? (long)perf.CommitTotal * page : 0,
            CommitLimit = perfOk ? (long)perf.CommitLimit * page : 0,
            PageFiles = pageFiles,
            PagefileAutoManaged = auto,
        };
    }

    private static async Task<CpuFacts> ReadCpuAsync(HardwareSnapshot? hw, CancellationToken ct)
    {
        var wmi = await Task.Run(() => Wmi("SELECT Architecture, CurrentClockSpeed FROM Win32_Processor",
            o => (Arch: I(o["Architecture"]), Clock: I(o["CurrentClockSpeed"]))).FirstOrDefault(), ct).ConfigureAwait(false);
        var topology = NativeSystem.ReadTopology();
        var idle = await NativeSystem.MeasureCpuPercentAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        bool? affinity = NativeSystem.GetProcessAffinityMask(NativeSystem.GetCurrentProcess(), out var mask, out var system) ? mask == system : null;

        return new CpuFacts
        {
            Name = hw?.Cpu?.Name ?? "",
            Manufacturer = hw?.Cpu?.Manufacturer ?? "",
            Architecture = wmi.Arch switch { 9 => "x64", 12 => "ARM64", 0 => "x86", _ => "" },
            PhysicalCores = topology?.Physical ?? hw?.Cpu?.PhysicalCores,
            LogicalProcessors = hw?.Cpu?.LogicalCores ?? Environment.ProcessorCount,
            MaxClockMHz = hw?.Cpu?.MaxClockMHz,
            CurrentClockMHz = wmi.Clock,
            NumaNodes = topology?.NumaNodes,
            PerformanceCores = topology?.Performance,
            EfficiencyCores = topology?.Efficiency,
            L3CacheGroups = topology?.L3Groups,
            IdleLoadPercent = idle,
            AffinityUnrestricted = affinity,
        };
    }

    private static IReadOnlyList<GpuAuditFacts> ReadGpus(HardwareSnapshot? hw)
    {
        var rows = Wmi("SELECT Name, DriverDate, DriverVersion, PNPDeviceID FROM Win32_VideoController",
            o => (Name: o["Name"] as string ?? "", Date: o["DriverDate"] as string, Version: o["DriverVersion"] as string));
        var result = new List<GpuAuditFacts>();
        foreach (var g in hw?.Gpus ?? [])
        {
            var row = rows.FirstOrDefault(r => r.Name.Trim() == g.Name);
            DateTimeOffset? date = null;
            try { if (row.Date is { } d) date = ManagementDateTimeConverter.ToDateTime(d); }
            catch (ArgumentOutOfRangeException) { }
            result.Add(new GpuAuditFacts(g.Id, g.Name, date, row.Version ?? g.DriverVersion, g.DedicatedVramBytes, null));
        }
        return result;
    }

    private static int? RegInt(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as int?;
    }

    private static bool KeyExists(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is not null;
    }

    private static WindowsFacts ReadWindows(HardwareSnapshot? hw)
    {
        var cu = Registry.CurrentUser;
        string? global = null, appPref = null;
        using (var dx = cu.OpenSubKey(DirectXKey[0]))
            if (dx is not null)
            {
                global = dx.GetValue("DirectXUserGlobalSettings") as string;
                var name = dx.GetValueNames().FirstOrDefault(n => n.EndsWith(@"\bf6.exe", StringComparison.OrdinalIgnoreCase));
                if (name is not null) appPref = dx.GetValue(name) as string;
            }

        bool? pending = null;
        try
        {
            pending = KeyExists(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                || KeyExists(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        }
        catch (System.Security.SecurityException) { }

        return new WindowsFacts
        {
            Caption = hw?.Os?.Caption ?? "",
            Build = hw?.Os?.Build ?? "",
            AutoGameMode = RegInt(cu, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
            GameDvrEnabled = RegInt(cu, @"System\GameConfigStore", "GameDVR_Enabled"),
            AppCaptureEnabled = RegInt(cu, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
            HagsMode = hw?.HagsMode,
            DirectXGlobalSettings = global,
            GamePerAppGpuPreference = appPref,
            RebootPending = pending,
        };
    }

    private static SecurityFacts ReadSecurity()
    {
        var guard = Wmi("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard",
            o => (Vbs: I(o["VirtualizationBasedSecurityStatus"]), Services: o["SecurityServicesRunning"] as uint[] ?? []),
            @"root\Microsoft\Windows\DeviceGuard");

        // Win32_Tpm usually needs elevation; without it the state stays Unknown rather than "absent".
        var tpm = Wmi("SELECT IsEnabled_InitialValue FROM Win32_Tpm", o => true, @"root\CIMV2\Security\MicrosoftTpm");

        return new SecurityFacts
        {
            SecureBoot = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") is { } sb ? sb == 1 : null,
            TpmPresent = tpm.Count > 0 ? true : null,
            VbsRunning = guard.Count > 0 ? guard[0].Vbs == 2 : null,
            HvciRunning = guard.Count > 0 ? guard[0].Services.Contains(2u) : null,
            HypervisorPresent = Wmi("SELECT HypervisorPresent FROM Win32_ComputerSystem", o => o["HypervisorPresent"] as bool?).FirstOrDefault(),
            AntivirusProducts = Wmi("SELECT displayName FROM AntiVirusProduct", o => o["displayName"] as string ?? "", @"root\SecurityCenter2")
                .Where(n => n.Length > 0).Distinct().ToList(),
        };
    }

    private static FirmwareFacts ReadFirmware()
    {
        var board = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard", o => (M: o["Manufacturer"] as string ?? "", P: o["Product"] as string ?? "")).FirstOrDefault();
        var bios = Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            o => (M: o["Manufacturer"] as string ?? "", V: o["SMBIOSBIOSVersion"] as string ?? "", D: o["ReleaseDate"] as string)).FirstOrDefault();

        DateTimeOffset? date = null;
        try { if (bios.D is { } d) date = ManagementDateTimeConverter.ToDateTime(d); }
        catch (ArgumentOutOfRangeException) { }

        string? mode = NativeSystem.GetFirmwareType(out var type) ? type switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => null } : null;
        return new FirmwareFacts
        {
            BoardManufacturer = board.M ?? "",
            BoardModel = board.P ?? "",
            BiosVendor = bios.M ?? "",
            BiosVersion = bios.V ?? "",
            BiosDate = date,
            BootMode = mode,
        };
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)] private static partial Regex VdfPath();

    private static IEnumerable<string> GameFolders()
    {
        using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (steam?.GetValue("SteamPath") is string root)
            {
                var libraries = new List<string> { root.Replace('/', '\\') };
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    libraries.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
                foreach (var lib in libraries)
                    yield return Path.Combine(lib, "steamapps", "common", "Battlefield 6");
            }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(drive.Name, "Program Files", "EA Games", "Battlefield 6");
            yield return Path.Combine(drive.Name, "EA Games", "Battlefield 6");
        }
    }

    private static StorageFacts ReadStorage()
    {
        const string StorageScope = @"root\Microsoft\Windows\Storage";
        var disks = Wmi("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", o =>
        (
            Id: o["DeviceId"]?.ToString() ?? "",
            Facts: new PhysicalDiskFacts(
                o["FriendlyName"] as string ?? "",
                I(o["MediaType"]) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                I(o["BusType"]) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", var b => b?.ToString() ?? "?" },
                L(o["Size"]) ?? 0,
                I(o["HealthStatus"]) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" })
        ), StorageScope);

        var partitions = Wmi("SELECT DiskNumber, DriveLetter FROM MSFT_Partition",
            o => (Disk: o["DiskNumber"]?.ToString() ?? "", Letter: Convert.ToChar(Convert.ToInt32(o["DriveLetter"]))), StorageScope);

        var games = GameFolders().Where(Directory.Exists).Select(p => char.ToUpperInvariant(p[0])).ToHashSet();
        char system = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);

        var volumes = new List<VolumeFacts>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            char letter = char.ToUpperInvariant(d.Name[0]);
            var disk = partitions.Where(p => p.Letter == letter).Select(p => p.Disk).FirstOrDefault();
            var media = disks.FirstOrDefault(x => x.Id == disk).Facts?.MediaType;
            volumes.Add(new VolumeFacts(letter + ":", d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, letter == system, games.Contains(letter), media));
        }

        var activity = Wmi("SELECT PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'", o => (double?)(100 - Convert.ToDouble(o["PercentIdleTime"]))).FirstOrDefault();
        return new StorageFacts { Volumes = volumes, Disks = disks.Select(d => d.Facts).ToList(), DiskActivePercent = activity is { } a ? Math.Clamp(a, 0, 100) : null };
    }

    private async Task<BackgroundFacts> ReadBackgroundAsync(CancellationToken ct)
    {
        var list = await ProcessInspector.SampleAsync(TimeSpan.FromSeconds(1), 40, ct).ConfigureAwait(false);
        return new BackgroundFacts
        {
            TopProcesses = list.Top,
            ProcessCount = list.Count,
            BackgroundCpuPercent = list.TotalCpu,
            Startup = startup.List(),
        };
    }

    private static NetworkFacts ReadNetwork()
    {
        var adapters = new List<AdapterFacts>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up
                || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var ip = n.GetIPProperties();
            adapters.Add(new AdapterFacts(
                n.Name,
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : n.NetworkInterfaceType.ToString(),
                n.Speed / 1_000_000,
                ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork),
                ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6),
                ip.DnsAddresses.Select(a => a.ToString()).ToList(),
                ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()));
        }
        return new NetworkFacts { Adapters = adapters.OrderByDescending(a => a.Gateway is not null).ToList() };
    }
}

public sealed record ProcessSample(IReadOnlyList<ProcessFacts> Top, int Count, double TotalCpu);

public static class ProcessInspector
{
    /// <summary>Two passes over process CPU time. Protected processes simply report what they allow.</summary>
    public static async Task<ProcessSample> SampleAsync(TimeSpan window, int top, CancellationToken ct)
    {
        var first = Read();
        var watch = Stopwatch.StartNew();
        await Task.Delay(window, ct).ConfigureAwait(false);
        var second = Read();
        double elapsedMs = watch.Elapsed.TotalMilliseconds * Environment.ProcessorCount;

        int self = Environment.ProcessId;
        var rows = new List<(int Pid, string Name, double Cpu, long Ws, long Priv, long Io)>();
        foreach (var (pid, s) in second)
        {
            if (pid is 0) continue;
            var before = first.GetValueOrDefault(pid);
            double cpu = s.Cpu >= before.Cpu ? 100 * (s.Cpu - before.Cpu) / elapsedMs : 0;
            long io = (long)(Math.Max(0, s.Io - before.Io) / window.TotalSeconds);
            rows.Add((pid, s.Name, cpu, s.Ws, s.Priv, io));
        }

        var selected = rows.OrderByDescending(r => r.Cpu).Take(top / 2)
            .Concat(rows.OrderByDescending(r => r.Ws).Take(top / 2))
            .DistinctBy(r => r.Pid)
            .OrderByDescending(r => r.Cpu).ThenByDescending(r => r.Ws)
            .Select(r => new ProcessFacts(r.Name, r.Pid, Math.Round(r.Cpu, 1), r.Ws, r.Priv, r.Io, Publisher(r.Pid)))
            .ToList();

        return new ProcessSample(selected, rows.Count, Math.Round(rows.Where(r => r.Pid != self).Sum(r => r.Cpu), 1));
    }

    private readonly record struct Raw(string Name, double Cpu, long Ws, long Priv, long Io);

    private static Dictionary<int, Raw> Read()
    {
        var map = new Dictionary<int, Raw>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                double cpu = 0;
                long io = 0, ws = 0, priv = 0;
                try
                {
                    ws = p.WorkingSet64;
                    priv = p.PrivateMemorySize64;
                    cpu = p.TotalProcessorTime.TotalMilliseconds;
                    if (NativeSystem.GetProcessIoCounters(p.Handle, out var c)) io = (long)(c.ReadTransferCount + c.WriteTransferCount);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                map[p.Id] = new Raw(p.ProcessName, cpu, ws, priv, io);
            }
        }
        return map;
    }

    private static string? Publisher(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileVersionInfo.CompanyName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }
}

public sealed class IcmpNetworkProbe : INetworkProbe
{
    private const int TimeoutMs = 1000;
    public const int MaxCount = 50;

    public async Task<NetworkProbeResult> ProbeAsync(string target, int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, MaxCount);
        double? dnsMs = null;
        if (!IPAddress.TryParse(target, out _))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
                dnsMs = watch.Elapsed.TotalMilliseconds;
            }
            catch (SocketException) { return new NetworkProbeResult(target, count, 0, null, null, null); }
        }

        using var ping = new Ping();
        var rtts = new List<double>();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(TimeoutMs), cancellationToken: ct).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) rtts.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        double? avg = rtts.Count > 0 ? rtts.Average() : null;
        double? jitter = rtts.Count > 1 ? rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(a - b)).Average() : null;
        return new NetworkProbeResult(target, count, rtts.Count, avg, jitter, dnsMs);
    }
}

/// <summary>WMI formatted counters: class names are not localized, unlike PerformanceCounter names.</summary>
public sealed class WmiPerformanceSampler(IClock clock) : IPerformanceSampler
{
    private long _prevIdle, _prevKernel, _prevUser;

    public Task<PerformanceSample> CaptureAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();

        double? cpu = null;
        if (NativeSystem.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long total = kernel - _prevKernel + (user - _prevUser);
            if (_prevKernel != 0 && total > 0) cpu = Math.Clamp(100.0 * (total - (idle - _prevIdle)) / total, 0, 100);
            (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        }

        var perCore = SystemAuditCollector.Wmi("SELECT Name, PercentProcessorTime, ProcessorFrequency, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation",
            o => (Name: o["Name"] as string ?? "", Load: Convert.ToDouble(o["PercentProcessorTime"]), Freq: Convert.ToDouble(o["ProcessorFrequency"]), Perf: Convert.ToDouble(o["PercentProcessorPerformance"])));
        var cores = perCore.Where(c => !c.Name.Contains("_Total")).Select(c => c.Load).ToList();
        var coreTotal = perCore.FirstOrDefault(c => c.Name == "_Total");

        var engines = SystemAuditCollector.Wmi("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D'",
            o => (Name: o["Name"] as string ?? "", Load: Convert.ToDouble(o["UtilizationPercentage"])));
        double? gpu = engines.Count > 0
            ? engines.GroupBy(e => e.Name[(e.Name.IndexOf("_luid", StringComparison.Ordinal) + 1)..]).Max(g => g.Sum(e => e.Load))
            : null;
        var gpuMemory = SystemAuditCollector.Wmi("SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory", o => Convert.ToDouble(o["DedicatedUsage"]));

        var disk = SystemAuditCollector.Wmi("SELECT DiskBytesPersec, PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'",
            o => (Bytes: Convert.ToDouble(o["DiskBytesPersec"]), Idle: Convert.ToDouble(o["PercentIdleTime"]))).Cast<(double Bytes, double Idle)?>().FirstOrDefault();
        var net = SystemAuditCollector.Wmi("SELECT BytesTotalPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface", o => Convert.ToDouble(o["BytesTotalPersec"]));

        var status = new NativeSystem.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeSystem.MEMORYSTATUSEX>() };
        bool mem = NativeSystem.GlobalMemoryStatusEx(ref status);

        return new PerformanceSample
        {
            Timestamp = clock.Now,
            CpuPercent = cpu,
            PerLogicalCpu = cores,
            CpuFrequencyMHz = coreTotal.Freq > 0 ? coreTotal.Freq * (coreTotal.Perf > 0 ? coreTotal.Perf / 100 : 1) : null,
            GpuPercent = gpu is { } g ? Math.Min(g, 100) : null,
            GpuMemoryBytes = gpuMemory.Count > 0 ? gpuMemory.Max() : null,
            GpuTemperatureC = NvmlProbe.GpuTemperature(),
            RamUsedBytes = mem ? status.ullTotalPhys - status.ullAvailPhys : null,
            RamPercent = mem ? status.dwMemoryLoad : null,
            DiskBytesPerSec = disk?.Bytes,
            DiskActivePercent = disk is { } d ? Math.Clamp(100 - d.Idle, 0, 100) : null,
            NetBytesPerSec = net.Count > 0 ? net.Sum() : null,
        };
    }, ct);
}
