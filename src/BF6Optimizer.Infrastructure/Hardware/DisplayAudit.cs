using System.Runtime.InteropServices;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.Infrastructure.Hardware;

internal static partial class DisplayEnumerator
{
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ADVANCED_COLOR
    {
        public INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdvancedColor(ref ADVANCED_COLOR info);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    public static IReadOnlyList<DisplayFacts> EnumerateFacts()
    {
        var details = QueryPaths();
        var result = new List<DisplayFacts>();
        foreach (var d in Enumerate())
        {
            details.TryGetValue(d.DeviceName, out var path);
            var (mode, modes) = ReadModes(d.DeviceName);

            bool? hdrSupported = null, hdrEnabled = null;
            int? bits = null;
            if (path is not null)
            {
                var color = new ADVANCED_COLOR
                {
                    header = new INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                        size = (uint)Marshal.SizeOf<ADVANCED_COLOR>(),
                        adapterId = path.Adapter,
                        id = path.TargetId,
                    },
                };
                if (GetAdvancedColor(ref color) == 0)
                {
                    hdrSupported = (color.value & 1) != 0;
                    hdrEnabled = (color.value & 2) != 0;
                    bits = (int)color.bitsPerColorChannel;
                }
            }

            result.Add(new DisplayFacts
            {
                Display = d,
                Manufacturer = path is null ? "" : DecodeEdidManufacturer(path.EdidManufacturer),
                Modes = modes,
                ScalePercent = ScaleFor(mode),
                Orientation = mode.dmDisplayOrientation,
                BitsPerPixel = mode.dmBitsPerPel,
                HdrSupported = hdrSupported,
                HdrEnabled = hdrEnabled,
                BitsPerColorChannel = bits,
            });
        }
        return result;
    }

    private static (DEVMODE Current, List<DisplayMode> Modes) ReadModes(string device)
    {
        var current = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref current);

        var seen = new HashSet<DisplayMode>();
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(device, i, ref mode) && i < 4096; i++)
            seen.Add(new DisplayMode(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency));
        return (current, seen.OrderByDescending(m => m.Width * (long)m.Height).ThenByDescending(m => m.RefreshHz).ToList());
    }

    private static int ScaleFor(DEVMODE mode)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var monitor = MonitorFromPoint(new POINT { X = mode.dmPositionX + 1, Y = mode.dmPositionY + 1 }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var x, out _) == 0 ? (int)Math.Round(x * 100 / 96.0) : 0;
    }

    /// <summary>EDID packs three 5-bit letters, big endian: 0x4C2D reads as "SAM".</summary>
    internal static string DecodeEdidManufacturer(ushort value)
    {
        if (value == 0) return "";
        int v = (ushort)((value << 8) | (value >> 8));
        return new string([(char)('A' + ((v >> 10) & 31) - 1), (char)('A' + ((v >> 5) & 31) - 1), (char)('A' + (v & 31) - 1)]);
    }
}
