using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BF6Optimizer.App.Localization;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Diagnostics;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using WpfUi = Wpf.Ui.Controls;

namespace BF6Optimizer.App.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        bool light = theme == "Light";
        var appTheme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(appTheme, WpfUi.WindowBackdropType.None, false);
        var accent = light ? Color.FromRgb(0xC9, 0x4E, 0x1B) : Color.FromRgb(0xF3, 0x6B, 0x32);
        ApplicationAccentColorManager.Apply(accent, accent, accent, accent);

        var merged = Application.Current.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Dark.xaml") == true
                                               || d.Source?.OriginalString.Contains("Themes/Light.xaml") == true);
        var next = new ResourceDictionary { Source = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative) };
        if (existing is null) merged.Add(next);
        else merged[merged.IndexOf(existing)] = next;
    }
}

public static class Dialogs
{
    /// <summary>Long text scrolls inside the dialog, so its buttons always stay on screen.</summary>
    private static ScrollViewer Scrollable(string message) => new()
    {
        MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height - 240),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
    };

    public static async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var box = new WpfUi.MessageBox
        {
            Title = title,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MaxHeight = SystemParameters.WorkArea.Height,
            Content = Scrollable(message),
            PrimaryButtonText = confirm,
            CloseButtonText = Loc.Instance["ui.cancel"],
        };
        return await box.ShowDialogAsync() == WpfUi.MessageBoxResult.Primary;
    }

    public static async Task InfoAsync(string title, string message)
    {
        var box = new WpfUi.MessageBox
        {
            Title = title,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MaxHeight = SystemParameters.WorkArea.Height,
            Content = Scrollable(message),
            CloseButtonText = Loc.Instance["ui.close"],
        };
        await box.ShowDialogAsync();
    }

    /// <summary>Privacy review: the user sees the exact text before it is written anywhere.</summary>
    public static async Task<bool> PreviewAsync(string title, string text)
    {
        var box = new WpfUi.MessageBox
        {
            Title = title,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Min(780, SystemParameters.WorkArea.Width - 40),
            MaxHeight = SystemParameters.WorkArea.Height,
            Content = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Height = Math.Min(420, SystemParameters.WorkArea.Height - 220),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = Loc.Instance["ui.save_as"],
            CloseButtonText = Loc.Instance["ui.cancel"],
        };
        return await box.ShowDialogAsync() == WpfUi.MessageBoxResult.Primary;
    }

    public static string? SaveFile(string defaultName, string filter)
    {
        if (TestMode.Current.ExportDir is { } dir) return Path.Combine(dir, defaultName);
        var d = new Microsoft.Win32.SaveFileDialog { FileName = defaultName, Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string? OpenFile(string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string? PickFolder()
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        return d.ShowDialog() == true ? d.FolderName : null;
    }
}

public static class Shell
{
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    public static void Reveal(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();

    public static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
}

public static class PerformanceDataLoader
{
    public const string OverrideName = "performance-data.override.json";

    /// <summary>An optional local import wins if valid. A bad file falls back; startup never depends on it.</summary>
    public static PerformanceData Load(AppPaths paths, ILogger logger)
    {
        var file = Path.Combine(paths.Data, OverrideName);
        if (File.Exists(file))
        {
            try
            {
                using var s = File.OpenRead(file);
                return PerformanceData.Load(s);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
            {
                logger.LogWarning(ex, "Ignoring invalid performance data override");
            }
        }
        return PerformanceData.LoadEmbedded();
    }
}

public static class PrivacyFilter
{
    public static Redactor Create(string machineKey)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Redactor(
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "%DOCUMENTS%"),
            (profile, "%USERPROFILE%"),
            (Environment.UserName, "<user>"),
            (Environment.MachineName, "<pc>"),
            (machineKey, "<machine>"),
        ]);
    }
}

#if DEBUG
/// <summary>Development-only hardware. Compiled out of Release; the shell shows a banner while active.</summary>
public sealed class SimulatedHardwareDetector : IHardwareDetector
{
    public string GetMachineKey() => "Machine_SIMULATED";

    public Task<HardwareSnapshot> DetectAsync(CancellationToken ct = default) => Task.FromResult(new HardwareSnapshot
    {
        IsSimulated = true,
        Manufacturer = "Simulated",
        Model = "Mixed GPU laptop",
        SystemFormFactor = FormFactor.Laptop,
        SystemFormFactorEvidence = "chassis",
        Cpu = new CpuInfo("Simulated CPU", "Simulated", 8, 16, 4500),
        Memory = new MemoryInfo(32L << 30, 5600),
        Os = new OsInfo("Simulated Windows", "26100"),
        Gpus =
        [
            new GpuInfo { Id = "sim-1", Name = "NVIDIA GeForce RTX 4070 Laptop GPU", Vendor = GpuVendor.Nvidia, Kind = GpuKind.Discrete, FormFactor = FormFactor.Laptop, FormFactorEvidence = "name", DedicatedVramBytes = 8L << 30, VramSource = "Simulated", PowerMaxW = 115 },
            new GpuInfo { Id = "sim-2", Name = "AMD Radeon 780M Graphics", Vendor = GpuVendor.Amd, Kind = GpuKind.Integrated, FormFactor = FormFactor.Laptop, DedicatedVramBytes = 2L << 30, VramSource = "Simulated" },
        ],
        Displays =
        [
            new DisplayInfo("SIM1", "Simulated 240 Hz", 2560, 1440, 240, true),
            new DisplayInfo("SIM2", "Simulated 60 Hz", 1920, 1080, 60, false),
        ],
    });
}
#endif
