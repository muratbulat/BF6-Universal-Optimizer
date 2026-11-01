using System.IO;
using System.Collections.ObjectModel;
using BF6Optimizer.App.Localization;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Profiles;
using BF6Optimizer.Core.Verification;
using BF6Optimizer.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace BF6Optimizer.App.ViewModels;

/// <summary>State shared by every page: the one hardware snapshot and the latest audit.</summary>
public sealed partial class AppState(
    IHardwareDetector detector,
    IProfileScanner scanner,
    ISystemAuditCollector collector,
    AuditEvaluator evaluator,
    IAuditHistoryStore history,
    IApplyRecordStore records,
    AppSettingsStore settingsStore,
    PerformanceData data,
    ILogger<AppState> logger) : ObservableObject
{
    public AppSettings Settings { get; set; } = new();

    [ObservableProperty] public partial HardwareSnapshot? Hardware { get; set; }
    [ObservableProperty] public partial GpuInfo? Gpu { get; set; }
    [ObservableProperty] public partial DisplayInfo? Display { get; set; }
    [ObservableProperty] public partial IReadOnlyList<DetectedProfile> Profiles { get; set; } = [];
    [ObservableProperty] public partial AuditResult? Audit { get; set; }
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial int ScanPercent { get; set; }
    [ObservableProperty] public partial string ScanStage { get; set; } = "";

    public OptimizationPreferences CustomPreferences
    {
        get => Settings.CustomPreferences;
        set
        {
            Settings.CustomPreferences = value;
            SaveSettings();
        }
    }

    [ObservableProperty] public partial string Toast { get; set; } = "";

    private CancellationTokenSource? _toastTimer;

    public async void Notify(string text)
    {
        _toastTimer?.Cancel();
        var timer = _toastTimer = new CancellationTokenSource();
        Toast = text;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), timer.Token);
            Toast = "";
        }
        catch (TaskCanceledException) { }
    }

    public void SaveSettings() => settingsStore.Save(Settings);

    public async Task RefreshHardwareAsync()
    {
        Hardware = await detector.DetectAsync();
        Gpu = GpuSelector.Select(Hardware.Gpus, data, Settings.SelectedGpuId);
        Display = Hardware.Displays.FirstOrDefault(d => d.DeviceName == Settings.SelectedDisplay)
               ?? Hardware.Displays.FirstOrDefault(d => d.IsPrimary)
               ?? Hardware.Displays.FirstOrDefault();
    }

    public async Task RefreshProfilesAsync() => Profiles = await scanner.ScanAsync(Settings.ManualProfileDirectory);

    public void SelectGpu(GpuInfo? gpu)
    {
        if (gpu is null || gpu.Id == Gpu?.Id) return;
        Gpu = gpu;
        Settings.SelectedGpuId = gpu.Id;
        SaveSettings();
    }

    public void SelectDisplay(DisplayInfo? display)
    {
        if (display is null || display.DeviceName == Display?.DeviceName) return;
        Display = display;
        Settings.SelectedDisplay = display.DeviceName;
        SaveSettings();
    }

    public ApplyRecord? LoadRecord()
    {
        try { return records.Load(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            logger.LogWarning(ex, "Apply record unreadable");
            return null;
        }
    }

    public void LoadLastAudit()
    {
        try { Audit = history.List(1).FirstOrDefault(); }
        catch (Exception ex) { logger.LogWarning(ex, "Audit history unreadable"); }
    }

    public AuditResult? PreviousAudit() => history.List(2).Skip(1).FirstOrDefault();

    public async Task RunAuditAsync(bool full, CancellationToken ct = default)
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var progress = new Progress<(string Section, int Percent)>(p =>
            {
                ScanPercent = p.Percent;
                ScanStage = Loc.Instance["scan." + p.Section];
            });
            var snapshot = await collector.CollectAsync(full, Display?.DeviceName, Gpu?.Id, progress, ct);
            Audit = evaluator.Evaluate(snapshot);
            logger.LogInformation("Audit finished ({Kind}): score {Score}, coverage {Coverage} %", full ? "full" : "quick", Audit.Readiness.Score, Audit.Readiness.CoveragePercent);
            history.Save(Audit);
        }
        finally { IsScanning = false; }
    }

    public void WithProbe(NetworkProbeResult probe)
    {
        if (Audit is null) return;
        Audit = evaluator.Evaluate(Audit.Snapshot with { NetworkProbe = probe });
    }
}

/// <summary>Bundle of services for page view models, to keep their constructors readable.</summary>
public sealed class PageServices(
    AppState state,
    SettingCatalog catalog,
    OptimizationPlanner planner,
    IProfileFileStore files,
    ApplyEngine apply,
    RestoreEngine restore,
    StatusVerifier status,
    BF6Optimizer.Core.Backup.IBackupStore backups,
    IReportStore reports,
    IProfileStore profiles,
    IHardwareDetector detector,
    INetworkProbe probe,
    IPerformanceSampler sampler,
    SafePreferenceActions safeActions,
    BF6Optimizer.Core.Audit.IStartupStore startup,
    AppPaths paths,
    FileLoggerProvider logs,
    IClock clock)
{
    public AppState State => state;
    public SettingCatalog Catalog => catalog;
    public OptimizationPlanner Planner => planner;
    public IProfileFileStore Files => files;
    public ApplyEngine Apply => apply;
    public RestoreEngine Restore => restore;
    public StatusVerifier Status => status;
    public BF6Optimizer.Core.Backup.IBackupStore Backups => backups;
    public IReportStore Reports => reports;
    public IProfileStore Profiles => profiles;
    public IHardwareDetector Detector => detector;
    public INetworkProbe Probe => probe;
    public IPerformanceSampler Sampler => sampler;
    public SafePreferenceActions SafeActions => safeActions;
    public BF6Optimizer.Core.Audit.IStartupStore Startup => startup;
    public AppPaths Paths => paths;
    public FileLoggerProvider Logs => logs;
    public IClock Clock => clock;
}

public abstract partial class PageViewModel(PageServices s) : ObservableObject
{
    protected PageServices S => s;
    protected static Loc L => Loc.Instance;

    /// <summary>Called when the page becomes visible.</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;

    /// <summary>Called when the user leaves; monitoring must stop here.</summary>
    public virtual void OnHidden() { }

    public virtual void OnLanguageChanged() { }
}

public sealed record Fact(string Key, string Value, string Note = "");

public sealed class CheckRow(AuditCheck c)
{
    private static Loc L => Loc.Instance;

    public AuditStatus Status => c.Status;
    public AuditCategory Category => c.Category;
    public string Title => L[c.TitleKey];
    public string Detected => L.Get(c.Detected);
    public string StatusText => L["status." + c.Status];
    public string Expected => L.Format("ui.expected_prefix", L[c.ExpectedKey]);
    public string Advice => c.IsActionable ? L[c.AdviceKey] : "";
    public bool HasAdvice => c.IsActionable;
    public string Priority => L["priority." + c.Priority];

    public string Meta => L.Format("ui.check_meta",
        L["evidence." + c.Evidence], L["confidence." + c.Confidence], c.Method,
        L["relevance." + c.Relevance], Yes(c.NeedsAdmin), Yes(c.AutoFix), Yes(c.RestartRequired), Yes(c.Reversible));

    private static string Yes(bool v) => L[v ? "ui.yes" : "ui.no"];
}

public static class Format
{
    public static string Bytes(double? b)
    {
        if (b is null) return Loc.Instance["ui.unavailable"];
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b.Value;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    public static string Or(string? text) => string.IsNullOrWhiteSpace(text) ? Loc.Instance["ui.unavailable"] : text;

    public static string Or(double? v, string suffix = "") => v is null ? Loc.Instance["ui.unavailable"] : $"{v:0.#}{suffix}";

    public static string Hz(double hz) => hz > 0 ? $"{hz:0.##} Hz" : Loc.Instance["ui.unavailable"];
}

public static class Collection
{
    public static void Replace<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
