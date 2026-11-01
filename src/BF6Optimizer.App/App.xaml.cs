using System.IO;
using System.Windows;
using BF6Optimizer.App.Localization;
using BF6Optimizer.App.Services;
using BF6Optimizer.App.ViewModels;
using BF6Optimizer.App.Views;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Network;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Profiles;
using BF6Optimizer.Core.Verification;
using BF6Optimizer.Infrastructure.Hardware;
using BF6Optimizer.Infrastructure.Storage;
using BF6Optimizer.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BF6Optimizer.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        TestMode.Parse(e.Args);
        var test = TestMode.Current;
        var paths = test.DataDir is { } dataDir ? new AppPaths(dataDir) : AppPaths.Resolve();
        try { paths.EnsureCreated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Loc.Instance.Format("error.data_folder", paths.Root), "BF6 Optimizer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var logs = new FileLoggerProvider(paths);
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Information));
        Register(services, paths, logs);
        _services = services.BuildServiceProvider();
        _logger = _services.GetRequiredService<ILogger<App>>();

        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(Loc.Instance["error.unexpected"], "BF6 Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var settings = _services.GetRequiredService<AppSettingsStore>().Load();
        _services.GetRequiredService<AppState>().Settings = settings;
        Loc.Instance.SetLanguage(settings.Language);
        ThemeService.Apply(settings.Theme);

        _logger.LogInformation("Started {Version} ({Storage})", AppInfo.Version, paths.Mode);
        var window = new MainWindow { DataContext = _services.GetRequiredService<ShellViewModel>() };
        MainWindow = window;
        window.Show();
    }

    private static void Register(ServiceCollection s, AppPaths paths, FileLoggerProvider logs)
    {
        s.AddSingleton(paths);
        s.AddSingleton(logs);
        s.AddSingleton<IClock, SystemClock>();
        s.AddSingleton(SettingCatalog.LoadEmbedded());
        s.AddSingleton(sp => PerformanceDataLoader.Load(paths, sp.GetRequiredService<ILogger<App>>()));
        s.AddSingleton<OptimizationPlanner>();
        s.AddSingleton<AuditEvaluator>();
        s.AddSingleton(new ApplyOptions());

        var test = TestMode.Current;
        s.AddSingleton(RegionCatalog.LoadEmbedded());
#if DEBUG
        if (test.SimulateHardware) s.AddSingleton<IHardwareDetector, SimulatedHardwareDetector>();
        else s.AddSingleton<IHardwareDetector, WindowsHardwareDetector>();
        if (test.MockNetwork)
        {
            s.AddSingleton<INetworkProber, MockNetworkProber>();
            s.AddSingleton<IRouteProber, MockRouteProber>();
        }
        else
        {
            s.AddSingleton<INetworkProber, WindowsNetworkProber>();
            s.AddSingleton<IRouteProber, WindowsRouteProber>();
        }
        if (test.MockSystem)
        {
            s.AddSingleton<IWindowsPreferenceStore, MemoryPreferenceStore>();
            s.AddSingleton<IStartupStore, MemoryStartupStore>();
        }
        else
        {
            s.AddSingleton<IWindowsPreferenceStore, HkcuPreferenceStore>();
            s.AddSingleton<IStartupStore, WindowsStartupStore>();
        }
#else
        s.AddSingleton<IHardwareDetector, WindowsHardwareDetector>();
        s.AddSingleton<INetworkProber, WindowsNetworkProber>();
        s.AddSingleton<IRouteProber, WindowsRouteProber>();
        s.AddSingleton<IWindowsPreferenceStore, HkcuPreferenceStore>();
        s.AddSingleton<IStartupStore, WindowsStartupStore>();
#endif

        Func<IServiceProvider, Func<string>> machine = sp => sp.GetRequiredService<IHardwareDetector>().GetMachineKey;
        s.AddSingleton<AppSettingsStore>();
        s.AddSingleton<IProfileFileStore, FileProfileStore>();
        s.AddSingleton<IProfileScanner>(sp => new ProfileScanner(
            sp.GetRequiredService<IProfileFileStore>(), sp.GetRequiredService<SettingCatalog>(),
            test.Bf6Dir is { } bf6 ? () => [bf6] : null));
        s.AddSingleton<IGameProcessMonitor, GameProcessMonitor>();
        s.AddSingleton<IPreferenceBackupStore, JsonPreferenceBackupStore>();
        s.AddSingleton<IProfileStore, JsonProfileStore>();
        s.AddSingleton<INetworkProbe, IcmpNetworkProbe>();
        s.AddSingleton<IPerformanceSampler, WmiPerformanceSampler>();
        s.AddSingleton<ISystemAuditCollector, SystemAuditCollector>();
        s.AddSingleton<IAuditHistoryStore>(sp => new JsonAuditHistoryStore(paths, machine(sp)));
        s.AddSingleton<SafePreferenceActions>();

        Func<IServiceProvider, Func<IReadOnlyList<string>>> roots = sp => () => sp.GetRequiredService<AppState>().Settings.ExternalBackupRoots;
        s.AddSingleton<IBackupStore>(sp => new FileBackupStore(paths, machine(sp), roots(sp), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<FileBackupStore>>()));
        s.AddSingleton<IApplyRecordStore>(sp => new JsonApplyRecordStore(paths, machine(sp), roots(sp)));
        s.AddSingleton<JsonReportStore>(sp => new JsonReportStore(paths, machine(sp), sp.GetRequiredService<IClock>()));
        s.AddSingleton<IReportStore>(sp => sp.GetRequiredService<JsonReportStore>());
        s.AddSingleton<RestoreEngine>();
        s.AddSingleton<ApplyEngine>();
        s.AddSingleton<StatusVerifier>();

        s.AddSingleton<NetworkLatencyService>();
        s.AddSingleton<TracerouteService>();
        s.AddSingleton<ITargetStore, JsonTargetStore>();
        s.AddSingleton<IEndpointStore, JsonEndpointStore>();
        s.AddSingleton<INetworkHistoryStore, JsonNetworkHistoryStore>();
        s.AddSingleton<AwsTargetProvider>();
        s.AddSingleton<IGameConnectionObserver, IpHelperConnectionObserver>();
        s.AddSingleton<NetServices>();

        s.AddSingleton<AppState>();
        s.AddSingleton<PageServices>();
        s.AddSingleton<ShellViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
