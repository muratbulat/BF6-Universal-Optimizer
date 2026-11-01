using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using BF6Optimizer.App.Controls;
using BF6Optimizer.App.Localization;
using BF6Optimizer.App.Services;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Network;
using BF6Optimizer.Infrastructure.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BF6Optimizer.App.ViewModels;

public sealed class NetServices(
    RegionCatalog catalog, NetworkLatencyService latency, TracerouteService trace, AwsTargetProvider targets, ITargetStore targetStore,
    IEndpointStore endpoints, INetworkHistoryStore history, IGameConnectionObserver observer, INetworkProber prober)
{
    public RegionCatalog Catalog => catalog;
    public NetworkLatencyService Latency => latency;
    public TracerouteService Trace => trace;
    public AwsTargetProvider Targets => targets;
    public ITargetStore TargetStore => targetStore;
    public IEndpointStore Endpoints => endpoints;
    public INetworkHistoryStore History => history;
    public IGameConnectionObserver Observer => observer;
    public INetworkProber Prober => prober;
}

public sealed partial class RegionRow(CandidateRegion r, string firstTarget) : ObservableObject
{
    private static Loc L => Loc.Instance;

    public CandidateRegion Region => r;
    public string Code => r.Code;
    public string Country => r.Country;
    public string City => r.City;
    public string Group => r.Group;
    public string EndpointType => L["net.type.probe"];
    public string Provider => r.Provider;
    public string ConfidenceText => L["confidence." + r.Confidence];
    public string Provenance => $"{r.Provenance} ({r.ProvenanceDate})";

    [ObservableProperty] public partial bool Selected { get; set; }
    [ObservableProperty] public partial string Ip { get; set; } = firstTarget;
    [ObservableProperty] public partial string Protocol { get; set; } = "—";
    [ObservableProperty] public partial double? Min { get; set; }
    [ObservableProperty] public partial double? Average { get; set; }
    [ObservableProperty] public partial double? Max { get; set; }
    [ObservableProperty] public partial double? Jitter { get; set; }
    [ObservableProperty] public partial LatencyQuality Quality { get; set; } = LatencyQuality.Untested;
    [ObservableProperty] public partial string QualityText { get; set; } = L["quality.Untested"];
    [ObservableProperty] public partial string LastTested { get; set; } = "—";
    [ObservableProperty] public partial string Note { get; set; } = "";

    public RegionResult? Result { get; private set; }

    public void Apply(RegionResult x, LatencyThresholds t)
    {
        Result = x;
        Protocol = x.Protocol.ToString().ToUpperInvariant();
        Ip = x.TargetIp ?? Ip;
        Min = Round(x.Stats?.Min);
        Average = Round(x.Stats?.Average);
        Max = Round(x.Stats?.Max);
        Jitter = Round(x.Stats?.Jitter);
        Quality = t.Classify(x.Stats?.Average);
        QualityText = x.EndpointUnavailable ? L["net.endpoint_unavailable"] : L["quality." + Quality];
        LastTested = x.TestedAt?.ToString("T") ?? "—";
        Note = x.FellBackFromIcmp ? L["net.fallback_note"] : x.Stats is { LossPercent: > 0 } s ? L.Format("net.no_reply_note", $"{s.Sent - s.Received}", s.Sent) : "";
    }

    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);
}

public sealed record HopRow(int Number, string Address, string Host, string Rtts, string State);

public sealed record ObservedRow(GameTcpEndpoint Endpoint, string Ip, int Port, string Protocol, string State, string Provider, string Region, string Classification, string Method);

public sealed record HistoryRow(string Time, string Region, string Protocol, string Average, string Jitter, string Replies, string Source);

public sealed partial class Bf6ServersViewModel : PageViewModel
{
    private readonly NetServices _n;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, RegionRow> _byCode;
    private double? _manualPing;
    private LatencyStats? _gateway;

    private static readonly string[] Presets = ["Turkey", "Europe", "NorthAmerica", "SouthAmerica", "Asia", "Oceania", "All"];
    private static readonly int[] SampleCounts = [5, 10, 20];

    public Bf6ServersViewModel(PageServices s, NetServices n) : base(s)
    {
        _n = n;
        var current = n.Targets.Current();
        Rows = new(n.Catalog.Regions.Select(r => new RegionRow(r, current.FirstOrDefault(t => t.Region == r.Code)?.Ip ?? "—")));
        _byCode = Rows.ToDictionary(r => r.Code);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is RegionRow r && Matches(r);

        var preset = s.State.Settings.NetworkPreset ?? n.Catalog.DefaultPreset(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        PresetIndex = Math.Max(0, Array.IndexOf(Presets, preset));
        ApplyPreset();
        var t = s.State.Settings.NetworkThresholds;
        (ExcellentText, GoodText, FairText) = (t.ExcellentMs.ToString(), t.GoodMs.ToString(), t.FairMs.ToString());
        LoadEndpoints();
    }

    public ObservableCollection<RegionRow> Rows { get; }
    public ICollectionView View { get; }
    public ObservableCollection<Fact> Connection { get; } = [];
    public ObservableCollection<string> Recommendations { get; } = [];
    public IReadOnlyList<string> PresetNames => Presets.Select(p => L["preset." + p]).ToList();
    public IReadOnlyList<string> Protocols => Enum.GetValues<ProbeProtocol>().Select(p => L["net.protocol." + p]).ToList();
    public IReadOnlyList<string> SampleNames { get; } = ["5", "10", "20"];

    [ObservableProperty] public partial int PresetIndex { get; set; }
    [ObservableProperty] public partial int ProtocolIndex { get; set; }
    [ObservableProperty] public partial int SamplesIndex { get; set; } = 1;
    [ObservableProperty] public partial bool IsTesting { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string Progress { get; set; } = "";
    [ObservableProperty] public partial string Best { get; set; } = "";
    [ObservableProperty] public partial string TargetsInfo { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<BarItem>? AverageBars { get; set; }
    [ObservableProperty] public partial IReadOnlyList<BarItem>? JitterBars { get; set; }
    [ObservableProperty] public partial string ExcellentText { get; set; } = "";
    [ObservableProperty] public partial string GoodText { get; set; } = "";
    [ObservableProperty] public partial string FairText { get; set; } = "";

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnPresetIndexChanged(int value)
    {
        if (Rows is null) return;
        ApplyPreset();
        S.State.Settings.NetworkPreset = Presets[value];
        S.State.SaveSettings();
    }

    private bool Matches(RegionRow r) => Search.Length == 0
        || r.City.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
        || r.Code.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || r.Group.Contains(Search, StringComparison.CurrentCultureIgnoreCase);

    private void ApplyPreset()
    {
        var name = Presets[PresetIndex];
        var codes = _n.Catalog.Presets.GetValueOrDefault(name);
        foreach (var r in Rows) r.Selected = codes is null || codes.Contains(r.Code);
    }

    private ProbeProtocol Protocol => Enum.GetValues<ProbeProtocol>()[ProtocolIndex];

    /// <summary>Demo mode shows documentation addresses, never the real network.</summary>
    private static ConnectionSummary? CurrentConnection() => TestMode.Current.SimulateHardware
        ? new ConnectionSummary("Demo Ethernet", "Ethernet", 1000, "192.0.2.1", ["192.0.2.53"], true, false)
        : ConnectionInfo.Read();

    public override async Task OnShownAsync()
    {
        var c = CurrentConnection();
        Connection.Replace(c is null ? [new Fact(L["ui.unavailable"], "")] :
        [
            new(L["net.adapter"], $"{c.Adapter} ({c.Type}, {c.LinkMbps} Mbps)"),
            new(L["net.gateway"], c.Gateway ?? "—"),
            new("DNS", string.Join(", ", c.Dns)),
            new("IPv4 / IPv6", $"{(c.Ipv4 ? "✓" : "✗")} / {(c.Ipv6 ? "✓" : "✗")}"),
            new(L["net.isp"], L["net.isp_unknown"]),
        ]);
        ShowTargetInfo();
        LoadHistory();
        Live.Refresh(_n, Rows);
        await Task.CompletedTask;
    }

    public override void OnHidden()
    {
        _cts?.Cancel();
        _liveCts?.Cancel();
        _routeCts?.Cancel();
    }

    public override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(PresetNames));
        OnPropertyChanged(nameof(Protocols));
        OnPropertyChanged(nameof(CategoryNames));
    }

    private void ShowTargetInfo()
    {
        TargetSet? set = null;
        try { set = _n.TargetStore.Load(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        TargetsInfo = set is { RetrievedAt: { } at }
            ? L.Format("net.targets_info", at.ToString("g"), set.Targets.Count, set.SourceUrl)
            : L.Format("net.targets_seed", _n.Catalog.SeedRetrieved);
    }

    [RelayCommand(AllowConcurrentExecutions = false)] private Task TestAll() => RunAsync(Rows.Select(r => r.Code).ToList());

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task TestSelected()
    {
        var chosen = Rows.Where(r => r.Selected).Select(r => r.Code).ToList();
        if (chosen.Count > 0) return RunAsync(chosen);
        Progress = L["net.none_selected"];
        return Task.CompletedTask;
    }

    [RelayCommand] private void StopTest() => _cts?.Cancel();

    private async Task RunAsync(IReadOnlyList<string> regions)
    {
        if (IsTesting) return;
        IsTesting = true;
        _cts = new CancellationTokenSource();
        int done = 0;
        var all = _n.Targets.Current();
        var results = new List<RegionResult>();
        var options = new MeasureOptions { Protocol = Protocol, Samples = SampleCounts[SamplesIndex] };
        var thresholds = S.State.Settings.NetworkThresholds;
        var ui = Application.Current.Dispatcher;
        Progress = L.Format("net.testing", 0, regions.Count);

        try
        {
            if (CurrentConnection()?.Gateway is { } gateway)
                _gateway = await _n.Latency.MeasureAsync(new ProbeTarget(gateway), new MeasureOptions { Protocol = ProbeProtocol.Icmp, Samples = 5 }, null, _cts.Token);

            await _n.Latency.TestRegionsAsync(regions, code => all.Where(t => t.Region == code).ToList(), options, r => ui.Invoke(() =>
            {
                results.Add(r);
                _byCode[r.Region].Apply(r, thresholds);
                Progress = L.Format("net.testing", ++done, regions.Count);
                UpdateCharts();
            }), _cts.Token);
            Progress = L.Format("net.finished", done);
        }
        catch (OperationCanceledException) { Progress = L.Format("net.stopped", done, regions.Count); }
        finally
        {
            IsTesting = false;
            _cts.Dispose();
            _cts = null;
        }

        Persist(results, all);
        UpdateRanking();
        LoadHistory();
    }

    private void Persist(IReadOnlyList<RegionResult> results, IReadOnlyList<RegionTarget> all)
    {
        if (results.Count == 0) return;
        var used = results.Where(r => r.UsedTarget is not null).ToDictionary(r => r.UsedTarget!.Ip, r => r.UsedTarget!);
        var stored = TryLoadSet();
        _n.TargetStore.Save(new TargetSet
        {
            SourceUrl = stored?.SourceUrl ?? _n.Catalog.SeedSource,
            RetrievedAt = stored?.RetrievedAt,
            RangesChecked = stored?.RangesChecked ?? false,
            Targets = all.Select(t => used.GetValueOrDefault(t.Ip, t)).ToList(),
        });
        _n.History.Append(results.Select(r => new NetworkTestRecord
        {
            Timestamp = r.TestedAt ?? S.Clock.Now,
            Region = r.Region,
            EndpointId = r.TargetIp ?? "",
            Protocol = r.Protocol,
            Stats = r.Stats,
            Error = r.EndpointUnavailable ? "EndpointUnavailable" : null,
            Source = r.UsedTarget?.Source ?? "",
            Confidence = _n.Catalog.Find(r.Region)?.Confidence ?? Confidence.Low,
        }));
    }

    private TargetSet? TryLoadSet()
    {
        try { return _n.TargetStore.Load(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { return null; }
    }

    private void UpdateCharts()
    {
        var tested = Rows.Where(r => r.Average is not null).OrderBy(r => r.Average).ToList();
        AverageBars = tested.Select(r => new BarItem(r.City, r.Average!.Value, $"{r.Average:0.#} ms")).ToList();
        JitterBars = tested.Where(r => r.Jitter is not null).OrderBy(r => r.Jitter).Select(r => new BarItem(r.City, r.Jitter!.Value, $"{r.Jitter:0.#} ms")).ToList();
    }

    private void UpdateRanking()
    {
        var results = Rows.Where(r => r.Result is not null).Select(r => r.Result!).ToList();
        var ranked = RegionRanking.Rank(results, Protocol);
        Best = ranked.Count == 0 ? "" : L.Format("net.best", _n.Catalog.Find(ranked[0].Region)!.City, $"{ranked[0].Stats!.Average:0.#}", Protocol.ToString().ToUpperInvariant());

        var context = new NetworkContext
        {
            Results = results,
            Protocol = Protocol,
            Gateway = _gateway,
            ManualInGamePing = _manualPing,
            WiFi = CurrentConnection()?.Type == "Wi-Fi",
        };
        Recommendations.Replace(NetworkRecommendationService.Evaluate(context, c => _n.Catalog.Find(c)?.City ?? c).Select(r => L.Get(r.Text)));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshTargets()
    {
        Progress = L["net.refreshing"];
        try
        {
            var (count, rejected, ranges) = await _n.Targets.RefreshAsync(CancellationToken.None);
            Progress = L.Format("net.refreshed", count, rejected, L[ranges ? "ui.yes" : "ui.no"]);
            ShowTargetInfo();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            Progress = L["net.refresh_failed"];
        }
    }

    [RelayCommand]
    private async Task ExportResults()
    {
        var data = Rows.Where(r => r.Result is not null).Select(r => new
        {
            r.Code, r.City, r.Provider, EndpointType = "RegionalProbeNotBf6Server", r.Ip, r.Protocol, r.Min, r.Average, r.Max, r.Jitter,
            Quality = r.Quality.ToString(), r.Result!.EndpointUnavailable, r.Result.FellBackFromIcmp, Tested = r.Result.TestedAt,
        }).ToList();
        if (data.Count == 0)
        {
            S.State.Notify(L["net.no_results"]);
            return;
        }
        var path = Dialogs.SaveFile($"BF6_Network_{DateTime.Now:yyyyMMdd_HHmm}.json", "JSON|*.json");
        if (path is null) return;
        await File.WriteAllTextAsync(path, Json.Serialize(data));
        S.State.Notify(L.Format("ui.saved_to", Path.GetFileName(path)));
    }

    [RelayCommand]
    private void SaveThresholds()
    {
        if (int.TryParse(ExcellentText, out var e) && int.TryParse(GoodText, out var g) && int.TryParse(FairText, out var f))
        {
            var t = new LatencyThresholds { ExcellentMs = e, GoodMs = g, FairMs = f };
            if (t.IsValid)
            {
                S.State.Settings.NetworkThresholds = t;
                S.State.SaveSettings();
                foreach (var r in Rows.Where(x => x.Result is not null)) r.Apply(r.Result!, t);
                S.State.Notify(L["net.thresholds_saved"]);
                return;
            }
        }
        S.State.Notify(L["net.thresholds_invalid"]);
    }

    [RelayCommand]
    private void CopyIp(RegionRow? row)
    {
        if (row is null) return;
        Clipboard.SetText(row.Ip);
        S.State.Notify(L.Format("ui.copied", row.Ip));
    }

    // Live monitoring

    private CancellationTokenSource? _liveCts;

    public LiveMonitor Live { get; } = new();

    [ObservableProperty] public partial bool IsLive { get; set; }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartLive()
    {
        if (IsLive || Live.Selected is not { } target) return;
        IsLive = true;
        _liveCts = new CancellationTokenSource();
        Live.Begin();
        var ui = Application.Current.Dispatcher;
        var protocol = Protocol == ProbeProtocol.Http ? ProbeProtocol.Tcp : Protocol;
        try
        {
            while (!_liveCts.Token.IsCancellationRequested)
            {
                var rtt = await _n.Prober.ProbeAsync(target.Target, protocol, TimeSpan.FromMilliseconds(1500), _liveCts.Token);
                ui.Invoke(() => Live.Add(rtt));
                await Task.Delay(1000, _liveCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsLive = false;
            _liveCts.Dispose();
            _liveCts = null;
        }
    }

    [RelayCommand] private void StopLive() => _liveCts?.Cancel();

    // Route analysis

    private CancellationTokenSource? _routeCts;

    public ObservableCollection<HopRow> Hops { get; } = [];
    public ObservableCollection<string> RouteNotes { get; } = [];

    [ObservableProperty] public partial string RouteTarget { get; set; } = "";
    [ObservableProperty] public partial bool IsTracing { get; set; }
    [ObservableProperty] public partial string RouteError { get; set; } = "";

    [RelayCommand]
    private void RouteFromRow(RegionRow? row)
    {
        if (row is not null) RouteTarget = row.Ip;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartTrace()
    {
        RouteError = "";
        var target = RouteTarget.Trim();
        if (!SavedEndpoint.IsValidHost(target))
        {
            RouteError = L["error.ep_host"];
            return;
        }

        IsTracing = true;
        Hops.Clear();
        RouteNotes.Clear();
        _routeCts = new CancellationTokenSource();
        var ui = Application.Current.Dispatcher;
        var hops = new List<RouteHop>();
        try
        {
            await _n.Trace.TraceAsync(target, h => ui.Invoke(() =>
            {
                hops.Add(h);
                Hops.Add(new HopRow(h.Number, h.Address ?? "*", h.HostName ?? "",
                    string.Join("  ", h.Rtts.Select(r => r is { } v ? $"{v:0} ms" : "*")),
                    L[h.Reached ? "net.hop_reached" : h.Silent ? "net.hop_silent" : "net.hop_ok"]));
            }), _routeCts.Token);
        }
        catch (OperationCanceledException) { RouteError = L["net.trace_stopped"]; }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.PingException or ArgumentException or InvalidOperationException) { RouteError = L["net.trace_failed"]; }
        finally
        {
            IsTracing = false;
            _routeCts.Dispose();
            _routeCts = null;
        }
        if (hops.Count > 0) RouteNotes.Replace(RouteAnalyzer.Interpret(hops).Select(L.Get));
    }

    [RelayCommand] private void StopTrace() => _routeCts?.Cancel();

    // Saved endpoints

    public ObservableCollection<SavedEndpoint> Endpoints { get; } = [];
    public IReadOnlyList<string> CategoryNames => Enum.GetValues<EndpointCategory>().Select(c => L["net.category." + c]).ToList();
    public IReadOnlyList<string> EndpointProtocols { get; } = ["ICMP", "TCP"];
    public IReadOnlyList<string> RegionCodes => ["", .. _n.Catalog.Regions.Select(r => r.Code)];

    [ObservableProperty] public partial SavedEndpoint? SelectedEndpoint { get; set; }
    [ObservableProperty] public partial string EpName { get; set; } = "";
    [ObservableProperty] public partial string EpHost { get; set; } = "";
    [ObservableProperty] public partial string EpPort { get; set; } = "";
    [ObservableProperty] public partial int EpProtocolIndex { get; set; }
    [ObservableProperty] public partial string EpRegion { get; set; } = "";
    [ObservableProperty] public partial int EpCategoryIndex { get; set; }
    [ObservableProperty] public partial string EpDescription { get; set; } = "";
    [ObservableProperty] public partial string EpSource { get; set; } = "";
    [ObservableProperty] public partial string EpEvidence { get; set; } = "";
    [ObservableProperty] public partial string EpProvider { get; set; } = "";
    [ObservableProperty] public partial bool EpVerified { get; set; }
    [ObservableProperty] public partial string EpMessage { get; set; } = "";

    private void LoadEndpoints() => Endpoints.Replace(_n.Endpoints.Load());

    partial void OnSelectedEndpointChanged(SavedEndpoint? value)
    {
        if (value is null) return;
        EpName = value.Name;
        EpHost = value.Host;
        EpPort = value.Port?.ToString() ?? "";
        EpProtocolIndex = value.Protocol == ProbeProtocol.Tcp ? 1 : 0;
        EpRegion = value.Region;
        EpCategoryIndex = (int)value.Category;
        EpDescription = value.Description;
        EpSource = value.Source;
        EpEvidence = value.Evidence;
        EpProvider = value.Provider;
        EpVerified = value.Verified;
    }

    [RelayCommand]
    private void NewEndpoint()
    {
        SelectedEndpoint = null;
        EpName = EpHost = EpPort = EpRegion = EpDescription = EpSource = EpEvidence = EpProvider = EpMessage = "";
        EpProtocolIndex = EpCategoryIndex = 0;
        EpVerified = false;
    }

    [RelayCommand]
    private void SaveEndpoint()
    {
        var e = SelectedEndpoint ?? new SavedEndpoint();
        var draft = new SavedEndpoint
        {
            Id = e.Id,
            Name = EpName.Trim(),
            Host = EpHost.Trim(),
            Protocol = EpProtocolIndex == 1 ? ProbeProtocol.Tcp : ProbeProtocol.Icmp,
            Region = EpRegion ?? "",
            Category = (EndpointCategory)EpCategoryIndex,
            Description = EpDescription,
            Source = EpSource,
            Evidence = EpEvidence,
            Provider = EpProvider,
            Verified = EpVerified,
            LastSuccess = e.LastSuccess,
        };
        if (EpPort.Trim().Length > 0)
        {
            if (!int.TryParse(EpPort.Trim(), out var port))
            {
                EpMessage = L["error.ep_port"];
                return;
            }
            draft.Port = port;
        }

        if (draft.Validate(_n.Catalog) is { Count: > 0 } errors)
        {
            EpMessage = L.Get(errors[0]);
            return;
        }

        var list = Endpoints.Where(x => x.Id != draft.Id).Append(draft).ToList();
        _n.Endpoints.Save(list);
        LoadEndpoints();
        EpMessage = L["net.ep_saved"];
    }

    [RelayCommand]
    private async Task DeleteEndpoint()
    {
        if (SelectedEndpoint is not { } e)
        {
            EpMessage = L["net.ep_select"];
            return;
        }
        if (!await Dialogs.ConfirmAsync(L["ui.delete"], L.Format("ui.profile_delete", e.Name), L["ui.delete"])) return;
        _n.Endpoints.Save(Endpoints.Where(x => x.Id != e.Id).ToList());
        LoadEndpoints();
        NewEndpoint();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task TestEndpoint()
    {
        if (SelectedEndpoint is not { } e)
        {
            EpMessage = L["net.ep_select"];
            return;
        }
        EpMessage = L["ui.testing"];
        var options = new MeasureOptions { Protocol = e.Protocol, Samples = SampleCounts[SamplesIndex] };
        var stats = await _n.Latency.MeasureAsync(new ProbeTarget(e.Host, e.Port ?? 80), options, null, CancellationToken.None);
        EpMessage = stats.Received == 0
            ? L["net.ep_no_reply"]
            : L.Format("net.ep_result", $"{stats.Min:0.#}", $"{stats.Average:0.#}", $"{stats.Max:0.#}", $"{stats.Jitter ?? 0:0.#}", stats.Received, stats.Sent);
        if (stats.Received > 0)
        {
            e.LastSuccess = S.Clock.Now;
            _n.Endpoints.Save(Endpoints.ToList());
        }
        _n.History.Append([new NetworkTestRecord
        {
            Timestamp = S.Clock.Now, Region = e.Region, EndpointId = e.Host, Protocol = e.Protocol, Stats = stats,
            Error = stats.Received == 0 ? "NoReply" : null, Source = e.Category.ToString(), Confidence = e.Verified ? Confidence.Medium : Confidence.Low,
        }]);
        LoadHistory();
    }

    [RelayCommand]
    private void ImportEndpoints()
    {
        var path = Dialogs.OpenFile("JSON|*.json");
        if (path is null) return;
        var (list, errors) = EndpointIo.Import(File.ReadAllText(path), _n.Catalog);
        if (list is null)
        {
            EpMessage = L.Get(errors.FirstOrDefault());
            return;
        }
        _n.Endpoints.Save(Endpoints.Concat(list).ToList());
        LoadEndpoints();
        EpMessage = L.Format("net.ep_imported", list.Count);
    }

    [RelayCommand]
    private void ExportEndpoints()
    {
        var path = Dialogs.SaveFile("bf6-endpoints.json", "JSON|*.json");
        if (path is null) return;
        File.WriteAllText(path, EndpointIo.Export(Endpoints));
        S.State.Notify(L.Format("ui.saved_to", Path.GetFileName(path)));
    }

    // Active connection

    public ObservableCollection<ObservedRow> Observed { get; } = [];

    [ObservableProperty] public partial ObservedRow? SelectedObserved { get; set; }
    [ObservableProperty] public partial string ActiveStatus { get; set; } = "";

    [RelayCommand]
    private void Observe()
    {
        var snapshot = _n.Observer.Observe();
        if (!snapshot.GameRunning)
        {
            Observed.Clear();
            ActiveStatus = L["net.game_not_running"];
            return;
        }

        var ranges = _n.Targets.LoadRanges();
        Observed.Replace(snapshot.Tcp.Select(t =>
        {
            var region = AwsTargetParser.RegionOf(t.RemoteIp, ranges);
            return new ObservedRow(t, t.RemoteIp, t.RemotePort, "TCP", t.State, region is null ? L["val.unknown"] : "AWS", region ?? "—",
                L["net.category." + EndpointClassifier.Classify(t)], "GetExtendedTcpTable");
        }));
        ActiveStatus = L.Format("net.observed", snapshot.Tcp.Count, snapshot.UdpLocalPorts.Count, snapshot.ObservedAt.ToString("T"));
        _n.History.Append(snapshot.Tcp.Select(t => new NetworkTestRecord
        {
            Timestamp = snapshot.ObservedAt,
            EndpointId = $"{t.RemoteIp}:{t.RemotePort}",
            Source = "ObservedPossible",
            Confidence = Confidence.Low,
        }));
    }

    [RelayCommand]
    private void SaveObserved()
    {
        if (SelectedObserved is not { } o)
        {
            ActiveStatus = L["net.ep_select"];
            return;
        }
        var region = o.Region == "—" ? null : o.Region;
        var e = EndpointClassifier.ToSaved(o.Endpoint, _n.Catalog.Find(region ?? "") is null ? null : region, S.Clock.Now);
        _n.Endpoints.Save(Endpoints.Append(e).ToList());
        LoadEndpoints();
        S.State.Notify(L["net.ep_saved"]);
    }

    // History and in-game ping

    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<double>? HistoryValues { get; set; }
    [ObservableProperty] public partial string HistoryRegion { get; set; } = "";
    [ObservableProperty] public partial string ManualPingText { get; set; } = "";
    [ObservableProperty] public partial string ManualMessage { get; set; } = "";

    partial void OnHistoryRegionChanged(string value) => LoadHistory();

    private void LoadHistory()
    {
        var all = _n.History.Load();
        var protocol = Protocol;
        var region = string.IsNullOrEmpty(HistoryRegion) ? all.LastOrDefault(r => r.Stats is not null)?.Region ?? "" : HistoryRegion;
        HistoryValues = all.Where(r => r.Region == region && r.Protocol == protocol && r.Stats?.Average is not null)
            .TakeLast(60).Select(r => r.Stats!.Average!.Value).ToList();

        History.Replace(all.TakeLast(100).Reverse().Select(r => new HistoryRow(
            r.Timestamp.ToString("g"),
            r.ManualInGamePingMs is not null ? L["net.manual_entry"] : r.Region,
            r.Protocol?.ToString().ToUpperInvariant() ?? "—",
            r.ManualInGamePingMs is { } m ? $"{m:0} ms" : r.Stats?.Average is { } a ? $"{a:0.#} ms" : "—",
            r.Stats?.Jitter is { } j ? $"{j:0.#} ms" : "—",
            r.Stats is { } s ? $"{s.Received}/{s.Sent}" : "—",
            r.Error ?? r.Source)));
    }

    [RelayCommand]
    private void SaveManualPing()
    {
        if (!double.TryParse(ManualPingText, NumberStyles.Float, CultureInfo.CurrentCulture, out var ping) || ping is <= 0 or > 2000)
        {
            ManualMessage = L["net.manual_invalid"];
            return;
        }
        _manualPing = ping;
        _n.History.Append([new NetworkTestRecord { Timestamp = S.Clock.Now, ManualInGamePingMs = ping, Source = "Manual" }]);
        ManualMessage = L["net.manual_saved"];
        UpdateRanking();
        LoadHistory();
    }

    [RelayCommand] private void OpenEaHelp() => Shell.OpenUri("https://help.ea.com/en/articles/battlefield/battlefield-6/troubleshoot-ping/");
}

public sealed record LiveTarget(string Label, ProbeTarget Target)
{
    public override string ToString() => Label;
}

public sealed partial class LiveMonitor : ObservableObject
{
    private const int Capacity = 300;
    private const int InterruptionRun = 3;

    private readonly BoundedSeries<double> _series = new(Capacity);
    private readonly List<double> _session = [];
    private DateTimeOffset _started;
    private int _timeouts;
    private int _run;
    private int _interruptions;

    public ObservableCollection<LiveTarget> Targets { get; } = [];

    [ObservableProperty] public partial LiveTarget? Selected { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? Values { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    public void Refresh(NetServices n, IEnumerable<RegionRow> rows)
    {
        var keep = Selected?.Label;
        Targets.Replace(rows.Where(r => r.Ip != "—").Select(r => new LiveTarget($"{r.City} ({r.Ip})", new ProbeTarget(r.Ip)))
            .Concat(n.Endpoints.Load().Select(e => new LiveTarget($"{e.Name} ({e.Host})", new ProbeTarget(e.Host, e.Port ?? 80)))));
        Selected = Targets.FirstOrDefault(t => t.Label == keep) ?? Targets.FirstOrDefault();
    }

    public void Begin()
    {
        while (_series.Count > 0 && Values is { Count: > 0 }) { Values = null; break; }
        _session.Clear();
        (_timeouts, _run, _interruptions) = (0, 0, 0);
        _started = DateTimeOffset.Now;
        Summary = "";
    }

    public void Add(double? rtt)
    {
        if (rtt is { } v)
        {
            _series.Add(v);
            _session.Add(v);
            _run = 0;
        }
        else
        {
            _series.Add(double.NaN);
            _timeouts++;
            if (++_run == InterruptionRun) _interruptions++;
        }
        Values = _series.ToList();
        var s = LatencyStats.From(_session.Count + _timeouts, _session);
        Summary = Loc.Instance.Format("net.live_summary",
            s.Average is { } a ? $"{a:0.#}" : "—", s.Max is { } m ? $"{m:0.#}" : "—", s.Jitter is { } j ? $"{j:0.#}" : "—",
            _timeouts, _interruptions, (DateTimeOffset.Now - _started).ToString(@"mm\:ss"));
    }
}
