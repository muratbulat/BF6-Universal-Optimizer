using System.ComponentModel;
using BF6Optimizer.App.Localization;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace BF6Optimizer.App.ViewModels;

public sealed partial class NavItem(string titleKey, SymbolRegular icon, PageViewModel page) : ObservableObject
{
    public string TitleKey => titleKey;
    public string Title => Loc.Instance[titleKey];
    public SymbolRegular Icon => icon;
    public PageViewModel Page => page;
    [ObservableProperty] public partial bool IsActive { get; set; }

    public void Refresh() => OnPropertyChanged(nameof(Title));
}

public sealed partial class NavGroup(string titleKey, IReadOnlyList<NavItem> items) : ObservableObject
{
    private bool _sidebarCompact;

    public string Id => titleKey;
    public string Title => Loc.Instance[titleKey];
    public IReadOnlyList<NavItem> Items => items;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowItems))] public partial bool IsOpen { get; set; } = true;

    /// <summary>Icon-only mode has no group headers, so items always show there.</summary>
    public bool ShowItems => IsOpen || _sidebarCompact;

    public void SetCompact(bool compact)
    {
        _sidebarCompact = compact;
        OnPropertyChanged(nameof(ShowItems));
    }

    [RelayCommand] private void Toggle() => IsOpen = !IsOpen;

    public void Refresh() => OnPropertyChanged(nameof(Title));
}

public sealed partial class ShellViewModel : ObservableObject
{
    private const double CompactBelow = 1120;
    private const double ExpandedWidth = 216;
    private const double CompactWidth = 54;

    private readonly PageServices _s;
    private readonly ILogger<ShellViewModel> _logger;
    private bool? _userChoice;

    public IReadOnlyList<NavGroup> Groups { get; }
    public NavItem Dashboard { get; }

    [ObservableProperty] public partial PageViewModel? Current { get; set; }
    [ObservableProperty] public partial bool ShowWelcome { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SidebarWidth))] public partial bool IsSidebarCompact { get; set; }
    [ObservableProperty] public partial string HeaderText { get; set; } = "";

    public double SidebarWidth => IsSidebarCompact ? CompactWidth : ExpandedWidth;
    public AppState State => _s.State;
    public string StorageNote => _s.Paths.PortableFallback ? Loc.Instance["warn.portable_fallback"] : "";

    public ShellViewModel(PageServices s, NetServices net, ILogger<ShellViewModel> logger)
    {
        _s = s;
        _logger = logger;

        var optimizer = new OptimizerViewModel(s);
        NavItem N(string key, SymbolRegular icon, PageViewModel page) => new(key, icon, page);
        Dashboard = N("nav.dashboard", SymbolRegular.Home24, new DashboardViewModel(s, this));

        Groups =
        [
            new NavGroup("nav.group.overview",
            [
                Dashboard,
                N("nav.hardware", SymbolRegular.Desktop24, new HardwareViewModel(s)),
            ]),
            new NavGroup("nav.group.bf6",
            [
                N("nav.optimizer", SymbolRegular.Games24, optimizer),
                N("nav.advanced", SymbolRegular.Options24, new AdvancedViewModel(s, optimizer, this)),
                N("nav.status", SymbolRegular.CheckmarkCircle24, new StatusViewModel(s, this)),
                N("nav.profiles", SymbolRegular.DocumentBulletList24, new ProfilesViewModel(s)),
                N("nav.backup", SymbolRegular.History24, new BackupViewModel(s)),
                N("nav.servers", SymbolRegular.Globe24, new Bf6ServersViewModel(s, net)),
            ]),
            new NavGroup("nav.group.system",
            [
                N("nav.health", SymbolRegular.HeartPulse24, new HealthViewModel(s)),
                N("nav.monitor", SymbolRegular.Pulse24, new MonitorViewModel(s)),
                N("nav.memory", SymbolRegular.Server24, AnalyzerFactory.Memory(s)),
                N("nav.cpu", SymbolRegular.DeveloperBoard24, AnalyzerFactory.Cpu(s)),
                N("nav.gpu", SymbolRegular.Flash24, AnalyzerFactory.Gpu(s)),
                N("nav.display", SymbolRegular.DesktopPulse24, AnalyzerFactory.Display(s)),
                N("nav.windows", SymbolRegular.Window24, AnalyzerFactory.Windows(s)),
                N("nav.storage", SymbolRegular.Database24, AnalyzerFactory.Storage(s)),
                N("nav.network", SymbolRegular.Wifi124, AnalyzerFactory.Network(s)),
                N("nav.processes", SymbolRegular.Apps24, new ProcessesViewModel(s)),
                N("nav.bios", SymbolRegular.Wrench24, AnalyzerFactory.Firmware(s)),
                N("nav.sessions", SymbolRegular.Timer24, new SessionsViewModel(s)),
                N("nav.safe", SymbolRegular.ShieldCheckmark24, new SafeCenterViewModel(s)),
            ]),
            new NavGroup("nav.group.data",
            [
                N("nav.reports", SymbolRegular.DocumentText24, new ReportsViewModel(s)),
                N("nav.settings", SymbolRegular.Settings24, new SettingsViewModel(s, this)),
            ]),
        ];

        ShowWelcome = !s.State.Settings.WelcomeCompleted;
        s.State.PropertyChanged += OnStateChanged;
        Navigate(Dashboard);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Audit) or nameof(AppState.Profiles) or nameof(AppState.IsScanning)) UpdateHeader();
    }

    private void UpdateHeader()
    {
        var l = Loc.Instance;
        var p = State.Profiles;
        var scan = State.IsScanning ? l["ui.header_scanning"]
            : State.Audit is { } a ? l.Format("ui.header_last", a.Snapshot.Timestamp.ToString("g"))
            : l["ui.no_audit"];
        var bf6 = p.Count == 0 ? l["ui.header_bf6_none"] : l.Format("ui.header_bf6", p.Count(x => x.Compatible));
        HeaderText = $"v{AppInfo.Version}  ·  {bf6}  ·  {scan}";
    }

    public void UpdateForWidth(double width) => ApplySidebar(_userChoice ?? width < CompactBelow);

    private void ApplySidebar(bool compact)
    {
        IsSidebarCompact = compact;
        foreach (var g in Groups) g.SetCompact(compact);
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        _userChoice = !IsSidebarCompact;
        ApplySidebar(_userChoice.Value);
    }

    [RelayCommand] private void OpenSettings() => NavigateTo("nav.settings");

    [RelayCommand]
    public void Navigate(NavItem item)
    {
        Current?.OnHidden();
        foreach (var n in Groups.SelectMany(g => g.Items)) n.IsActive = n == item;
        Current = item.Page;
        _ = ShowAsync(item.Page);
    }

    public void NavigateTo(string titleKey) => Navigate(Groups.SelectMany(g => g.Items).First(n => n.TitleKey == titleKey));

    private async Task ShowAsync(PageViewModel page)
    {
        try { await page.OnShownAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Page load failed: {Page}", page.GetType().Name); }
    }

    public async Task InitializeAsync()
    {
        try
        {
            await State.RefreshHardwareAsync();
            await State.RefreshProfilesAsync();
            State.LoadLastAudit();
        }
        catch (Exception ex) { _logger.LogError(ex, "Startup detection failed"); }
        finally
        {
            IsLoading = false;
            UpdateHeader();
        }

        try
        {
            if (Current is not null) await Current.OnShownAsync();
            if (State.Audit is null) await State.RunAuditAsync(full: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Initial scan failed"); }
    }

    [RelayCommand]
    private void CompleteWelcome()
    {
        ShowWelcome = false;
        State.Settings.WelcomeCompleted = true;
        State.SaveSettings();
    }

    public void ReplayWelcome() => ShowWelcome = true;

    public void OnLanguageChanged()
    {
        foreach (var g in Groups) g.Refresh();
        foreach (var n in Groups.SelectMany(g => g.Items))
        {
            n.Refresh();
            n.Page.OnLanguageChanged();
        }
        UpdateHeader();
    }
}
