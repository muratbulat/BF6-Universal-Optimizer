using System.Runtime.InteropServices;
using System.Management;
using System.Text.RegularExpressions;
using BF6Optimizer.Core.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BF6Optimizer.Infrastructure.Hardware;

public sealed partial class WindowsHardwareDetector(ILogger<WindowsHardwareDetector> logger) : IHardwareDetector
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const long FourGb = 4L * 1024 * 1024 * 1024;

    // ChassisTypes per the SMBIOS spec. v1.4.0 tested these against PCSystemType, which uses different numbers.
    private static readonly HashSet<int> MobileChassis = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
    private static readonly HashSet<int> DesktopChassis = [3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24, 34, 35, 36];

    private string? _machineKey;

    [GeneratedRegex(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)] private static partial Regex PciIds();
    [GeneratedRegex(@"[^A-Za-z0-9._-]")] private static partial Regex Unsafe();

    private sealed record WmiGpu(string Name, string? Pnp, string? Driver, long? AdapterRam);

    public Task<HardwareSnapshot> DetectAsync(CancellationToken ct = default) => Task.Run(() => Detect(ct), ct);

    public string GetMachineKey()
    {
        if (_machineKey is not null) return _machineKey;
        var uuid = Wmi("SELECT UUID FROM Win32_ComputerSystemProduct", o => o["UUID"] as string).FirstOrDefault();
        bool usable = !string.IsNullOrWhiteSpace(uuid)
            && !uuid.All(c => c is '0' or '-')
            && !uuid.All(c => c is 'F' or 'f' or '-');
        // Same derivation as v1.4.0, so existing backups stay tied to this computer.
        return _machineKey = "Machine_" + Unsafe().Replace(usable ? uuid! : Environment.MachineName, "_").Trim('_');
    }

    private HardwareSnapshot Detect(CancellationToken ct)
    {
        var warnings = new List<string>();
        var system = Wmi("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem",
            o => (Manufacturer: o["Manufacturer"] as string ?? "", Model: o["Model"] as string ?? "", Type: ToInt(o["PCSystemType"]))).FirstOrDefault();
        ct.ThrowIfCancellationRequested();

        var chassis = Wmi("SELECT ChassisTypes FROM Win32_SystemEnclosure",
            o => (o["ChassisTypes"] as ushort[])?.Select(c => (int)c).ToArray() ?? []).SelectMany(c => c).ToList();
        var formFactor = ClassifyChassis(chassis, system.Type);

        var cpu = Wmi("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor",
            o => new CpuInfo((o["Name"] as string ?? "").Trim(), o["Manufacturer"] as string ?? "",
                ToInt(o["NumberOfCores"]), ToInt(o["NumberOfLogicalProcessors"]), ToInt(o["MaxClockSpeed"]))).FirstOrDefault();

        var modules = Wmi("SELECT Capacity, ConfiguredClockSpeed FROM Win32_PhysicalMemory",
            o => (Size: ToLong(o["Capacity"]) ?? 0, Speed: ToInt(o["ConfiguredClockSpeed"]) ?? 0));
        var total = modules.Sum(m => m.Size);
        var memory = new MemoryInfo(total > 0 ? total : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, modules.Select(m => (int?)m.Speed).Max() is > 0 and var s ? s : null);

        var os = Wmi("SELECT Caption, BuildNumber FROM Win32_OperatingSystem",
            o => new OsInfo((o["Caption"] as string ?? "Windows").Trim(), o["BuildNumber"] as string ?? "")).FirstOrDefault();
        ct.ThrowIfCancellationRequested();

        var gpus = DetectGpus(formFactor, warnings);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<DisplayInfo> displays = [];
        try { displays = DisplayEnumerator.Enumerate(); }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "Display enumeration failed");
            warnings.Add("display");
        }

        logger.LogInformation("Hardware: {Cpu}; GPUs: {Gpus}; displays: {Displays}; form factor {Form}",
            cpu?.Name, string.Join(" | ", gpus.Select(g => $"{g.Name} [{g.Kind}, {g.VramGb:F1} GB, {g.VramSource}]")), displays.Count, formFactor);

        return new HardwareSnapshot
        {
            Manufacturer = system.Manufacturer,
            Model = system.Model,
            SystemFormFactor = formFactor,
            SystemFormFactorEvidence = formFactor == FormFactor.Unknown ? "none" : "chassis",
            Cpu = cpu,
            Memory = memory,
            Gpus = gpus,
            Displays = displays,
            Os = os,
            HagsMode = ReadHags(),
            DetectionWarnings = warnings,
        };
    }

    internal static FormFactor ClassifyChassis(IReadOnlyList<int> chassis, int? pcSystemType)
    {
        if (chassis.Any(MobileChassis.Contains)) return FormFactor.Laptop;
        if (chassis.Any(DesktopChassis.Contains)) return FormFactor.Desktop;
        return pcSystemType switch
        {
            2 => FormFactor.Laptop,
            1 or 3 => FormFactor.Desktop,
            _ => FormFactor.Unknown,
        };
    }

    private List<GpuInfo> DetectGpus(FormFactor system, List<string> warnings)
    {
        var wmi = Wmi("SELECT Name, PNPDeviceID, DriverVersion, AdapterRAM FROM Win32_VideoController",
            o => new WmiGpu(o["Name"] as string ?? "", o["PNPDeviceID"] as string, o["DriverVersion"] as string, ToLong(o["AdapterRAM"])));

        IReadOnlyList<DxgiAdapter> dxgi = [];
        try { dxgi = DxgiAdapters.Enumerate(); }
        catch (Exception ex) when (ex is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "DXGI enumeration failed");
            warnings.Add("dxgi");
        }

        var nvml = NvmlProbe.Query();
        var claimed = new HashSet<WmiGpu>();
        var seen = new Dictionary<string, int>();
        var result = new List<GpuInfo>();

        foreach (var a in dxgi.Where(a => !a.IsSoftware))
        {
            var match = wmi.FirstOrDefault(w => !claimed.Contains(w) && SamePci(w.Pnp, a.VendorId, a.DeviceId))
                ?? wmi.FirstOrDefault(w => !claimed.Contains(w) && w.Name == a.Description);
            if (match is not null) claimed.Add(match);

            var id = $"{a.VendorId:X4}-{a.DeviceId:X4}-{a.SubSysId:X8}";
            seen[id] = seen.GetValueOrDefault(id) + 1;
            if (seen[id] > 1) id += "-" + seen[id];

            result.Add(Build(id, a.Description, a.DedicatedVideoMemory > 0 ? a.DedicatedVideoMemory : null, "DXGI", match?.Driver, nvml, system));
        }

        // Only used when DXGI could not see an adapter, for example during a driver problem.
        foreach (var w in wmi.Where(w => !claimed.Contains(w) && w.Name.Length > 0))
        {
            var (memory, source) = FallbackVram(w);
            result.Add(Build(w.Pnp ?? w.Name, w.Name, memory, source, w.Driver, nvml, system));
        }

        return result;
    }

    private static GpuInfo Build(string id, string name, long? memory, string source, string? driver, IReadOnlyList<NvmlDevice> nvml, FormFactor system)
    {
        var identity = GpuNameParser.Parse(name);
        var nv = identity.Vendor == GpuVendor.Nvidia ? nvml.FirstOrDefault(n => SameName(n.Name, name)) : null;
        if (nv?.MemoryBytes is { } nvMemory)
        {
            memory = nvMemory;
            source = "NVML";
        }

        var kind = GpuNameParser.ClassifyKind(identity, memory);
        var (formFactor, evidence) = GpuNameParser.ResolveFormFactor(identity, system);
        return new GpuInfo
        {
            Id = id,
            Name = name.Trim(),
            Vendor = identity.Vendor,
            Kind = kind,
            FormFactor = kind == GpuKind.Software ? FormFactor.Unknown : formFactor,
            FormFactorEvidence = evidence,
            DedicatedVramBytes = memory,
            VramSource = memory is null ? "None" : source,
            DriverVersion = driver,
            PowerLimitW = nv?.PowerLimitW,
            PowerDefaultW = nv?.PowerDefaultW,
            PowerMaxW = nv?.PowerMaxW,
        };
    }

    private static bool SameName(string a, string b) =>
        GpuNameParser.Normalize(a).Replace("NVIDIA ", "") == GpuNameParser.Normalize(b).Replace("NVIDIA ", "");

    private static bool SamePci(string? pnp, uint vendor, uint device)
    {
        var m = pnp is null ? Match.Empty : PciIds().Match(pnp);
        return m.Success
            && m.Groups[1].Value.Equals(vendor.ToString("X4"), StringComparison.OrdinalIgnoreCase)
            && m.Groups[2].Value.Equals(device.ToString("X4"), StringComparison.OrdinalIgnoreCase);
    }

    private static (long? Memory, string Source) FallbackVram(WmiGpu gpu)
    {
        if (ReadRegistryVram(gpu.Name) is { } registry) return (registry, "Registry");
        // AdapterRAM is 32-bit, so anything near 4 GB may be truncated and is not trusted.
        return gpu.AdapterRam is > 0 and < FourGb - 1024 * 1024 ? (gpu.AdapterRam, "WMI") : (null, "None");
    }

    private static long? ReadRegistryVram(string gpuName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") as string != gpuName) continue;
                var value = key.GetValue("HardwareInformation.qwMemorySize");
                if (value is long l && l > 0) return l;
                if (value is byte[] { Length: >= 8 } b) return BitConverter.ToInt64(b, 0);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    private static int? ReadHags()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            return key?.GetValue("HwSchMode") as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private List<T> Wmi<T>(string query, Func<ManagementBaseObject, T> map)
    {
        var result = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var collection = searcher.Get();
            foreach (var item in collection)
                using (item)
                    result.Add(map(item));
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TypeInitializationException)
        {
            logger.LogWarning(ex, "WMI query failed: {Query}", query);
        }
        return result;
    }

    private static int? ToInt(object? v) => v is null ? null : Convert.ToInt32(v);

    private static long? ToLong(object? v) => v is null ? null : Convert.ToInt64(v);
}
