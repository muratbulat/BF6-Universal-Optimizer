using System.Runtime.InteropServices;
using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.Infrastructure.Hardware;

/// <summary>Every attached display. QueryDisplayConfig adds friendly names and exact rates such as 239.76 Hz.</summary>
internal static partial class DisplayEnumerator
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 1;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 4;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling, refreshNumerator, refreshDenominator, scanLineOrdering, targetAvailable, statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO { public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME
    {
        public INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_NAME
    {
        public INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE info);
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths, ref uint modeCount, [Out] MODE_INFO[] modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SOURCE_NAME info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetName(ref TARGET_NAME info);

    private sealed record PathDetails(string FriendlyName, double RefreshHz, ushort EdidManufacturer, LUID Adapter, uint TargetId);

    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var details = QueryPaths();
        var result = new List<DisplayInfo>();
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            bool attached = (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            if (attached)
            {
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    details.TryGetValue(device.DeviceName, out var d);
                    result.Add(new DisplayInfo(
                        device.DeviceName,
                        string.IsNullOrWhiteSpace(d?.FriendlyName) ? MonitorName(device.DeviceName) : d.FriendlyName,
                        mode.dmPelsWidth, mode.dmPelsHeight,
                        d is { RefreshHz: > 0 } ? Math.Round(d.RefreshHz, 2) : mode.dmDisplayFrequency,
                        (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
                }
            }
            device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return result;
    }

    private static string MonitorName(string adapterDevice)
    {
        var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(adapterDevice, 0, ref monitor, 0) ? monitor.DeviceString : adapterDevice;
    }

    private static Dictionary<string, PathDetails> QueryPaths()
    {
        var map = new Dictionary<string, PathDetails>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return map;

        var paths = new PATH_INFO[pathCount];
        var modes = new MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, 0) != 0) return map;

        foreach (var path in paths.Take((int)pathCount))
        {
            var source = new SOURCE_NAME
            {
                header = new INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<SOURCE_NAME>(),
                    adapterId = path.source.adapterId,
                    id = path.source.id,
                },
            };
            if (GetSourceName(ref source) != 0) continue;

            var target = new TARGET_NAME
            {
                header = new INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<TARGET_NAME>(),
                    adapterId = path.target.adapterId,
                    id = path.target.id,
                },
            };
            bool named = GetTargetName(ref target) == 0;
            var name = named ? target.monitorFriendlyDeviceName : "";
            double hz = path.target.refreshDenominator > 0 ? (double)path.target.refreshNumerator / path.target.refreshDenominator : 0;
            map.TryAdd(source.viewGdiDeviceName, new PathDetails(name, hz, named ? target.edidManufactureId : (ushort)0, path.target.adapterId, path.target.id));
        }
        return map;
    }
}
