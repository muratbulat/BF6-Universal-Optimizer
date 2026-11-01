using System.Runtime.InteropServices;
using System.Text;

namespace BF6Optimizer.Infrastructure.Hardware;

internal sealed record NvmlDevice(string Name, long? MemoryBytes, double? PowerLimitW, double? PowerDefaultW, double? PowerMaxW);

/// <summary>
/// Optional, in-process telemetry from nvml.dll, a part of the NVIDIA driver. It is never shipped or required.
/// Each field is queried on its own: some laptop drivers reject power fields but still report memory.
/// </summary>
internal static class NvmlProbe
{
    private const int NVML_SUCCESS = 0;
    private const double MaxPlausibleWatts = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Shutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCount(out uint count);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetHandle(uint index, out nint device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetName(nint device, [Out] byte[] name, uint length);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMemory(nint device, out NvmlMemory memory);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetEnforcedPowerLimit(nint device, out uint milliwatts);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDefaultPowerLimit(nint device, out uint milliwatts);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetPowerConstraints(nint device, out uint min, out uint max);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetTemperature(nint device, int sensor, out uint celsius);

    public static double? GpuTemperature()
    {
        try
        {
            if (Init() != NVML_SUCCESS) return null;
            try { return GetHandle(0, out var h) == NVML_SUCCESS && GetTemperature(h, 0, out var c) == NVML_SUCCESS && c is > 0 and < 130 ? c : null; }
            finally { Shutdown(); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static IReadOnlyList<NvmlDevice> Query()
    {
        try
        {
            if (Init() != NVML_SUCCESS) return [];
            try
            {
                if (GetCount(out var count) != NVML_SUCCESS) return [];
                var devices = new List<NvmlDevice>();
                for (uint i = 0; i < count; i++)
                    if (GetHandle(i, out var h) == NVML_SUCCESS)
                        devices.Add(Read(h));
                return devices;
            }
            finally { Shutdown(); }
        }
        catch (DllNotFoundException) { return []; }
        catch (EntryPointNotFoundException) { return []; }
        catch (BadImageFormatException) { return []; }
    }

    private static NvmlDevice Read(nint handle)
    {
        var name = new byte[96];
        var text = GetName(handle, name, (uint)name.Length) == NVML_SUCCESS
            ? Encoding.ASCII.GetString(name).TrimEnd('\0').Split('\0')[0]
            : "";
        long? memory = GetMemory(handle, out var m) == NVML_SUCCESS && m.Total > 0 ? (long)m.Total : null;
        double? limit = GetEnforcedPowerLimit(handle, out var l) == NVML_SUCCESS ? Watts(l) : null;
        double? dflt = GetDefaultPowerLimit(handle, out var d) == NVML_SUCCESS ? Watts(d) : null;
        double? max = GetPowerConstraints(handle, out _, out var x) == NVML_SUCCESS ? Watts(x) : null;
        return new NvmlDevice(text, memory, limit, dflt, max);
    }

    private static double? Watts(uint milliwatts)
    {
        var w = milliwatts / 1000.0;
        return w is > 0 and < MaxPlausibleWatts ? w : null;
    }
}
