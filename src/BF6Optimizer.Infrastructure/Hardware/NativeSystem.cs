using System.Runtime.InteropServices;
using BF6Optimizer.Core.Audit;

namespace BF6Optimizer.Infrastructure.Hardware;

/// <summary>Read-only kernel32/powrprof/psapi queries used by the audit.</summary>
internal static partial class NativeSystem
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("psapi.dll", SetLastError = true)] public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern bool GetProcessIoCounters(nint process, out IO_COUNTERS counters);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);
    [DllImport("kernel32.dll")] public static extern bool GetFirmwareType(out int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(nint root, ref Guid scheme, nint subgroup, nint setting, nint buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid ProcessorMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    public static PowerFacts ReadPower()
    {
        bool? onAc = null, saver = null;
        int? battery = null;
        if (GetSystemPowerStatus(out var st))
        {
            onAc = st.ACLineStatus switch { 1 => true, 0 => false, _ => null };
            saver = (st.SystemStatusFlag & 1) != 0;
            battery = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : null;
        }

        string? guid = null, name = null;
        int? min = null, max = null;
        if (PowerGetActiveScheme(0, out var ptr) == 0)
        {
            var scheme = Marshal.PtrToStructure<Guid>(ptr);
            LocalFree(ptr);
            guid = scheme.ToString();
            name = FriendlyName(scheme);
            min = ReadProcessor(scheme, ProcessorMin);
            max = ReadProcessor(scheme, ProcessorMax);
        }
        return new PowerFacts
        {
            SchemeGuid = guid,
            SchemeName = name,
            OnAc = onAc,
            BatterySaver = saver,
            BatteryPercent = battery,
            ProcessorMinPercentAc = min,
            ProcessorMaxPercentAc = max,
        };
    }

    private static int? ReadProcessor(Guid scheme, Guid setting)
    {
        var sub = ProcessorSubgroup;
        return PowerReadACValueIndex(0, ref scheme, ref sub, ref setting, out var value) == 0 ? (int)value : null;
    }

    private static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(0, ref scheme, 0, 0, 0, ref size) != 0 || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(0, ref scheme, 0, 0, buffer, ref size) == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public sealed record Topology(int Physical, int Performance, int Efficiency, int L3Groups, int NumaNodes);

    /// <summary>Hybrid split comes from EfficiencyClass; L3 groups reveal multi-CCD layouts.</summary>
    public static Topology? ReadTopology()
    {
        const int RelationAll = 0xFFFF, RelationProcessorCore = 0, RelationNumaNode = 1, RelationCache = 2;
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationAll, 0, ref length);
        if (length == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length)) return null;
            var classes = new List<byte>();
            int l3 = 0, numa = 0;
            for (int offset = 0; offset < length;)
            {
                var item = buffer + offset;
                int relation = Marshal.ReadInt32(item);
                int size = Marshal.ReadInt32(item, 4);
                if (size <= 0) break;
                if (relation == RelationProcessorCore) classes.Add(Marshal.ReadByte(item, 9));
                else if (relation == RelationNumaNode) numa++;
                else if (relation == RelationCache && Marshal.ReadByte(item, 8) == 3) l3++;
                offset += size;
            }
            byte top = classes.Count > 0 ? classes.Max() : (byte)0;
            bool hybrid = classes.Distinct().Count() > 1;
            return new Topology(
                classes.Count,
                hybrid ? classes.Count(c => c == top) : 0,
                hybrid ? classes.Count(c => c != top) : 0,
                l3, numa);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static async Task<double?> MeasureCpuPercentAsync(TimeSpan window, CancellationToken ct)
    {
        if (!GetSystemTimes(out var i1, out var k1, out var u1)) return null;
        await Task.Delay(window, ct).ConfigureAwait(false);
        if (!GetSystemTimes(out var i2, out var k2, out var u2)) return null;
        long total = k2 - k1 + (u2 - u1); // kernel time includes idle
        return total <= 0 ? null : Math.Clamp(100.0 * (total - (i2 - i1)) / total, 0, 100);
    }
}
