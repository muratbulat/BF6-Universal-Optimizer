using System.Collections.ObjectModel;
using System.IO;
using BF6Optimizer.App.Services;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BF6Optimizer.App.ViewModels;

public sealed partial class Metric(string nameKey, double maximum) : ObservableObject
{
    private readonly BoundedSeries<double> _series = new(300);

    public string Name => Localization.Loc.Instance[nameKey];
    public double Maximum => maximum;

    [ObservableProperty] public partial IReadOnlyList<double>? Values { get; set; }
    [ObservableProperty] public partial string Current { get; set; } = "";

    public void Add(double? value, string text)
    {
        Current = text;
        if (value is null) return;
        _series.Add(value.Value);
        Values = _series.ToList();
    }

    public void Reset()
    {
        while (_series.Count > 0) _series.Add(0);
        Values = null;
        Current = "";
    }
}

public sealed record CoreLoad(string Name, double Percent);

public sealed partial class MonitorViewModel(PageServices s) : PageViewModel(s)
{
    private CancellationTokenSource? _cts;

    public Metric Cpu { get; } = new("ui.cpu_load", 100);
    public Metric Gpu { get; } = new("ui.gpu_load", 100);
    public Metric Ram { get; } = new("ui.ram_load", 100);
    public Metric Disk { get; } = new("ui.disk_active", 100);
    public Metric Net { get; } = new("ui.net_rate", 0);
    public ObservableCollection<CoreLoad> Cores { get; } = [];

    public IReadOnlyList<string> Intervals { get; } = ["500 ms", "1 s", "2 s", "5 s"];
    private static readonly int[] IntervalMs = [500, 1000, 2000, 5000];
    public IReadOnlyList<string> Durations => [L["ui.until_stop"], "30 s", "60 s", "5 min", L["ui.custom"]];
    private static readonly int[] DurationSeconds = [0, 30, 60, 300];

    [ObservableProperty] public partial int IntervalIndex { get; set; } = 1;
    [ObservableProperty] public partial int DurationIndex { get; set; }
    [ObservableProperty] public partial string CustomSeconds { get; set; } = "120";
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Extra { get; set; } = "";

    [RelayCommand]
    private async Task Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        int seconds = DurationIndex < DurationSeconds.Length ? DurationSeconds[DurationIndex]
            : int.TryParse(CustomSeconds, out var c) ? Math.Clamp(c, 5, 3600) : 120;
        if (seconds > 0) _cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        var ui = System.Windows.Application.Current.Dispatcher;
        try
        {
            await SamplingLoop.RunAsync(S.Sampler, TimeSpan.FromMilliseconds(IntervalMs[IntervalIndex]),
                sample => ui.Invoke(() => Show(sample)), _cts.Token);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    public override void OnHidden() => _cts?.Cancel();

    private void Show(PerformanceSample x)
    {
        Cpu.Add(x.CpuPercent, Format.Or(x.CpuPercent, " %"));
        Gpu.Add(x.GpuPercent, Format.Or(x.GpuPercent, " %"));
        Ram.Add(x.RamPercent, $"{Format.Or(x.RamPercent, " %")} · {Format.Bytes(x.RamUsedBytes)}");
        Disk.Add(x.DiskActivePercent, $"{Format.Or(x.DiskActivePercent, " %")} · {Format.Bytes(x.DiskBytesPerSec)}/s");
        Net.Add(x.NetBytesPerSec, $"{Format.Bytes(x.NetBytesPerSec)}/s");
        Cores.Replace(x.PerLogicalCpu.Select((v, i) => new CoreLoad(i.ToString(), v)));
        Extra = L.Format("ui.monitor_extra", Format.Or(x.CpuFrequencyMHz, " MHz"), Format.Bytes(x.GpuMemoryBytes),
            x.GpuTemperatureC is { } t ? $"{t:0} °C" : L["val.temp_unavailable"]);
    }
}

public sealed partial class SessionsViewModel(PageServices s) : PageViewModel(s)
{
    private const int BaselineSeconds = 30;
    private const int MaxSamples = 7200;

    private CancellationTokenSource? _cts;
    private BoundedSeries<PerformanceSample> _baseline = new(MaxSamples);
    private BoundedSeries<PerformanceSample> _active = new(MaxSamples);

    public Metric Cpu { get; } = new("ui.cpu_load", 100);
    public Metric Gpu { get; } = new("ui.gpu_load", 100);
    public ObservableCollection<Fact> Results { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Phase { get; set; } = "";

    [RelayCommand]
    private async Task Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        Results.Clear();
        Notes.Clear();
        Cpu.Reset();
        Gpu.Reset();
        _baseline = new(MaxSamples);
        _active = new(MaxSamples);
        _cts = new CancellationTokenSource();
        var started = S.Clock.Now;
        var ui = System.Windows.Application.Current.Dispatcher;
        Phase = L.Format("ui.session_baseline", BaselineSeconds);

        try
        {
            await SamplingLoop.RunAsync(S.Sampler, TimeSpan.FromSeconds(1), x => ui.Invoke(() =>
            {
                bool baseline = (S.Clock.Now - started).TotalSeconds < BaselineSeconds;
                (baseline ? _baseline : _active).Add(x);
                if (!baseline) Phase = L.Format("ui.session_active", _active.Count);
                Cpu.Add(x.CpuPercent, Format.Or(x.CpuPercent, " %"));
                Gpu.Add(x.GpuPercent, Format.Or(x.GpuPercent, " %"));
            }), _cts.Token);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }

        await FinishAsync();
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    private async Task FinishAsync()
    {
        var report = SessionAggregator.Build(_baseline.ToList(), _active.ToList());
        Phase = L["ui.session_done"];

        void Row(string key, MetricSummary b, MetricSummary a, Func<double?, string> f) =>
            Results.Add(new Fact(L[key], L.Format("ui.session_row", f(b.Average), f(b.Peak), f(a.Average), f(a.Peak))));

        Row("ui.cpu_load", report.Baseline.Cpu, report.Active.Cpu, v => Format.Or(v, " %"));
        Row("ui.max_thread", report.Baseline.MaxSingleCpu, report.Active.MaxSingleCpu, v => Format.Or(v, " %"));
        Row("ui.gpu_load", report.Baseline.Gpu, report.Active.Gpu, v => Format.Or(v, " %"));
        Row("ui.ram", report.Baseline.Ram, report.Active.Ram, Format.Bytes);
        Row("ui.vram_use", report.Baseline.GpuMemory, report.Active.GpuMemory, Format.Bytes);
        Row("ui.disk_rate", report.Baseline.Disk, report.Active.Disk, v => $"{Format.Bytes(v)}/s");
        Row("ui.net_rate", report.Baseline.Network, report.Active.Network, v => $"{Format.Bytes(v)}/s");
        Notes.Replace(report.Notes.Select(L.Get));

        var dir = Path.Combine(S.Paths.Reports, S.Detector.GetMachineKey(), "Sessions");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_session.json"), Json.Serialize(report));
    }
}
