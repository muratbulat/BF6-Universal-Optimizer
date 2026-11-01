using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BF6Optimizer.App.ViewModels;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.App.Views;

public partial class MainWindow
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 4;
    private const uint SWP_NOACTIVATE = 0x10;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private delegate bool MonitorEnum(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    public MainWindow() => InitializeComponent();

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private static PixelRect WorkArea(nint monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
    }

    /// <summary>Runs before the first frame, so the window appears once, already in place.</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var monitors = new List<PixelRect>();
        EnumDisplayMonitors(0, 0, (m, _, _, _) => { monitors.Add(WorkArea(m)); return true; }, 0);

        GetCursorPos(out var cursor);
        var active = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        double scale = GetDpiForMonitor(active, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1;

        var saved = Shell?.State.Settings.Window;
        var rect = WindowFit.Choose(saved is null ? null : new PixelRect(saved.X, saved.Y, saved.Width, saved.Height), monitors, WorkArea(active), scale);

        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, 0, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        if (saved?.Maximized == true) WindowState = WindowState.Maximized;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
        {
            shell.UpdateForWidth(ActualWidth);
            await shell.InitializeAsync();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Shell?.UpdateForWidth(e.NewSize.Width);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Shell is not { } shell) return;
        var previous = shell.State.Settings.Window;
        bool maximized = WindowState == WindowState.Maximized;
        if (!maximized && WindowState == WindowState.Normal && GetWindowRect(new WindowInteropHelper(this).Handle, out var r))
            shell.State.Settings.Window = new SavedWindow { X = r.Left, Y = r.Top, Width = r.Right - r.Left, Height = r.Bottom - r.Top };
        else if (previous is not null)
            previous.Maximized = maximized;

        try { shell.State.SaveSettings(); }
        catch (System.IO.IOException) { }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stops any running sampler or network test so nothing outlives the window.
        Shell?.Current?.OnHidden();
        base.OnClosed(e);
    }
}
