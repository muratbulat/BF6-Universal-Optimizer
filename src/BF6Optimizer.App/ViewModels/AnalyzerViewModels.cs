using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using BF6Optimizer.App.Services;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Diagnostics;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BF6Optimizer.App.ViewModels;

/// <summary>Base for pages that re-render whenever a new audit arrives.</summary>
public abstract partial class AuditBoundViewModel : PageViewModel
{
    protected AuditBoundViewModel(PageServices s) : base(s) => s.State.PropertyChanged += OnStateChanged;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Audit) or nameof(AppState.Hardware)) Rebuild();
    }

    public AppState State => S.State;

    public override Task OnShownAsync()
    {
        Rebuild();
        return Task.CompletedTask;
    }

    public override void OnLanguageChanged() => Rebuild();

    protected abstract void Rebuild();

    [RelayCommand] private Task QuickScan() => Scan(false);

    [RelayCommand] private Task FullScan() => Scan(true);

    private async Task Scan(bool full)
    {
        try { await S.State.RunAuditAsync(full); }
        catch (Exception ex) when (ex is not OperationCanceledException) { await Dialogs.InfoAsync(L["ui.scan"], L["error.unexpected"]); }
    }

    protected async Task ExportAsync(string format)
    {
        if (S.State.Audit is not { } audit)
        {
            await Dialogs.InfoAsync(L["ui.export"], L["ui.no_audit"]);
            return;
        }

        var redactor = PrivacyFilter.Create(S.Detector.GetMachineKey());
        var cmp = AuditReportExporter.Compare(S.State.PreviousAudit(), audit);
        var text = format switch
        {
            "json" => AuditReportExporter.ToJson(audit, cmp, redactor),
            "html" => AuditReportExporter.ToHtml(audit, cmp, L.Get, redactor),
            _ => AuditReportExporter.ToText(audit, cmp, L.Get, redactor),
        };
        if (!await Dialogs.PreviewAsync(L["ui.privacy_review"], text)) return;
        var path = Dialogs.SaveFile($"BF6Optimizer_Audit_{DateTime.Now:yyyyMMdd_HHmm}.{format}", $"{format.ToUpperInvariant()}|*.{format}");
        if (path is null) return;
        await File.WriteAllTextAsync(path, text);
        S.State.Notify(L.Format("ui.saved_to", Path.GetFileName(path)));
    }

    [RelayCommand] private Task Export(string format) => ExportAsync(format);
}

public sealed record CategoryBar(string Name, double Value, string Score, string Note);

public sealed partial class DashboardViewModel(PageServices s, ShellViewModel shell) : AuditBoundViewModel(s)
{
    public ObservableCollection<Fact> Cards { get; } = [];
    public ObservableCollection<CheckRow> Top { get; } = [];
    public ObservableCollection<CategoryBar> Categories { get; } = [];

    [ObservableProperty] public partial bool HasTop { get; set; }
    [ObservableProperty] public partial double ScoreValue { get; set; }
    [ObservableProperty] public partial string CriticalCount { get; set; } = "-";
    [ObservableProperty] public partial string HighCount { get; set; } = "-";
    [ObservableProperty] public partial string OpportunityCount { get; set; } = "-";
    [ObservableProperty] public partial string Score { get; set; } = "-";
    [ObservableProperty] public partial string Coverage { get; set; } = "-";
    [ObservableProperty] public partial string LastScan { get; set; } = "";
    [ObservableProperty] public partial string Bf6Status { get; set; } = "";
    public bool IsSimulated => S.State.Hardware?.IsSimulated == true;

    protected override void Rebuild()
    {
        var hw = S.State.Hardware;
        var gpu = S.State.Gpu;
        var d = S.State.Display;
        var mem = hw?.Memory;
        var audit = S.State.Audit;
        var rate = audit?.Snapshot.Memory is { } m ? MemoryAnalysis.EffectiveMTs(m.Modules) : null;

        Cards.Replace(
        [
            new(L["ui.cpu"], Format.Or(hw?.Cpu?.Name), hw?.Cpu is { } c ? L.Format("ui.cores", c.PhysicalCores ?? 0, c.LogicalCores ?? 0) : ""),
            new(L["ui.gpu"], Format.Or(gpu?.Name), gpu is null ? "" : $"{Format.Or(gpu.VramGb, " GB VRAM")} · {L["kind." + gpu.Kind]} · {L["form." + gpu.FormFactor]}"),
            new(L["ui.ram"], Format.Bytes(mem?.TotalBytes), rate is { } r ? $"{r} MT/s ({MemoryAnalysis.ClockMHzFromTransferRate(r)} MHz)" : ""),
            new(L["ui.display"], d is null ? L["ui.unavailable"] : $"{d.Width}x{d.Height} @ {Format.Hz(d.RefreshHz)}", L.Format("ui.display_count", hw?.Displays.Count ?? 0)),
            new(L["ui.os"], Format.Or(hw?.Os?.Caption), hw?.Os?.Build ?? ""),
            new(L["ui.storage"], StorageSummary(audit), ""),
            new(L["ui.bf6_config"], S.State.Profiles.Count == 0 ? L["ui.bf6_not_found"] : L.Format("ui.bf6_found", S.State.Profiles.Count(p => p.Compatible), S.State.Profiles.Count), ""),
        ]);

        var record = S.State.LoadRecord();
        Bf6Status = record is null ? L["ui.no_apply"] : L.Format("ui.last_apply", record.Timestamp.ToString("g"), L["mode." + record.Mode]);

        if (audit is null)
        {
            Score = "-";
            Coverage = "-";
            LastScan = L["ui.no_audit"];
            Top.Clear();
            Categories.Clear();
            return;
        }

        Score = audit.Readiness.Score is { } v ? $"{v}/100" : "-";
        ScoreValue = audit.Readiness.Score ?? 0;
        CriticalCount = audit.Checks.Count(c => c.Status == AuditStatus.Critical).ToString();
        HighCount = audit.Checks.Count(c => c.IsActionable && c.Priority == Priority.High).ToString();
        OpportunityCount = audit.Checks.Count(c => c.Status == AuditStatus.OptimizationAvailable).ToString();
        Coverage = $"{audit.Readiness.CoveragePercent} %";
        LastScan = L.Format("ui.last_scan", audit.Snapshot.Timestamp.ToString("g"), L[audit.Snapshot.Full ? "ui.scan_full" : "ui.scan_quick"]);
        Top.Replace(audit.Recommendations.Where(c => c.Priority >= Priority.Medium).Take(5).Select(c => new CheckRow(c)));
        HasTop = Top.Count > 0;
        Categories.Replace(audit.Readiness.Categories.Select(c => new CategoryBar(
            L["cat." + c.Category], c.Score ?? 0, c.Score is { } x ? x.ToString() : "-", L.Format("ui.category_note", c.Applicable, c.Total))));
    }

    private static string StorageSummary(AuditResult? audit) =>
        audit?.Snapshot.Storage is not { } st ? L["ui.full_scan_needed"]
        : string.Join("  ", st.Volumes.Select(v => $"{v.Letter} {Format.Bytes(v.Free)}"));

    [RelayCommand] private void OpenHealth() => shell.NavigateTo("nav.health");

    [RelayCommand] private void OpenOptimizer() => shell.NavigateTo("nav.optimizer");

    [RelayCommand] private void OpenServers() => shell.NavigateTo("nav.servers");
}

public sealed partial class HealthViewModel(PageServices s) : AuditBoundViewModel(s)
{
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<string> Filters { get; } = [];
    public ObservableCollection<Fact> Glossary { get; } = [];

    [ObservableProperty] public partial int FilterIndex { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    partial void OnFilterIndexChanged(int value) => Rebuild();

    private static readonly string[] Terms = ["xmp", "expo", "mts", "vrr", "hags", "vbs", "commit"];

    protected override void Rebuild()
    {
        int keep = FilterIndex;
        if (Filters.Count == 0) Filters.Replace([L["ui.filter_all"], L["ui.filter_actions"], L["ui.filter_unknown"]]);
        if (Glossary.Count == 0 || Glossary[0].Key != L["term.xmp.name"])
            Glossary.Replace(Terms.Select(t => new Fact(L[$"term.{t}.name"], L[$"term.{t}.text"])));
        FilterIndex = keep;

        var audit = S.State.Audit;
        if (audit is null)
        {
            Summary = L["ui.no_audit"];
            Checks.Clear();
            return;
        }

        IEnumerable<AuditCheck> items = audit.Checks;
        items = FilterIndex switch
        {
            1 => audit.Recommendations,
            2 => items.Where(c => c.Status is AuditStatus.Unknown or AuditStatus.Unsupported or AuditStatus.ManualVerification),
            _ => items,
        };
        Checks.Replace(items.Select(c => new CheckRow(c)));
        Summary = L.Format("ui.health_summary", audit.Readiness.Score?.ToString() ?? "-", audit.Readiness.CoveragePercent, audit.Readiness.ScoredChecks, audit.Readiness.EligibleChecks);
    }
}

public partial class AnalyzerViewModel(PageServices s, string titleKey, string subtitleKey, AuditCategory[] categories, Func<PageServices, AuditResult, IEnumerable<Fact>> facts, string? listTitleKey = null, Func<PageServices, AuditResult, IEnumerable<Fact>>? list = null) : AuditBoundViewModel(s)
{
    public string Title => L[titleKey];
    public string Subtitle => L[subtitleKey];
    public string ListTitle => listTitleKey is null ? "" : L[listTitleKey];
    public bool HasList => list is not null;
    public object? Tools { get; init; }

    public ObservableCollection<Fact> Facts { get; } = [];
    public ObservableCollection<Fact> List { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];

    [ObservableProperty] public partial bool HasAudit { get; set; }

    protected override void Rebuild()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ListTitle));
        var audit = S.State.Audit;
        HasAudit = audit is not null;
        if (audit is null)
        {
            Facts.Clear();
            Checks.Clear();
            List.Clear();
            return;
        }
        Facts.Replace(facts(S, audit));
        if (list is not null) List.Replace(list(S, audit));
        Checks.Replace(audit.Checks.Where(c => categories.Contains(c.Category)).OrderByDescending(c => c.IsActionable).ThenByDescending(c => c.Priority).Select(c => new CheckRow(c)));
    }
}

public sealed partial class DisplayTools(PageServices s) : ObservableObject
{
    public AppState State => s.State;
    public IReadOnlyList<DisplayInfo> Displays => s.State.Hardware?.Displays ?? [];

    public DisplayInfo? Selected
    {
        get => s.State.Display;
        set
        {
            s.State.SelectDisplay(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CapText));
        }
    }

    public string CapText
    {
        get
        {
            var d = s.State.Display;
            int hz = d is null ? 0 : (int)Math.Round(d.RefreshHz);
            return hz <= 0 ? "" : Localization.Loc.Instance.Format("ui.cap_note", hz, RefreshAnalysis.StartingCap(hz, true) ?? hz);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Displays));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CapText));
    }

    [RelayCommand] private void OpenDisplaySettings() => Shell.OpenUri("ms-settings:display-advanced");

    [RelayCommand] private void OpenGraphicsSettings() => Shell.OpenUri("ms-settings:display-advancedgraphics");
}

public sealed partial class NetworkTools(PageServices s) : ObservableObject
{
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Result { get; set; } = "";

    private static Localization.Loc L => Localization.Loc.Instance;

    public void Prefill()
    {
        if (Target.Length == 0)
            Target = s.State.Audit?.Snapshot.Network?.Adapters.FirstOrDefault(a => a.Gateway is not null)?.Gateway ?? "";
    }

    [RelayCommand]
    private async Task Probe()
    {
        var target = Target.Trim();
        if (target.Length == 0 || IsRunning) return;
        IsRunning = true;
        Result = L["ui.testing"];
        try
        {
            var p = await s.Probe.ProbeAsync(target, 20, CancellationToken.None);
            Result = p.AverageMs is null
                ? L.Format("ui.probe_timeout", p.Target)
                : L.Format("ui.probe_result", p.Target, $"{p.AverageMs:0.#}", $"{p.JitterMs ?? 0:0.#}", $"{p.LossPercent:0}", p.DnsMs is { } d ? $"{d:0} ms" : "-");
            s.State.WithProbe(p);
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.PingException or ArgumentException or InvalidOperationException)
        {
            Result = L.Format("ui.probe_timeout", target);
        }
        finally { IsRunning = false; }
    }
}

public static class AnalyzerFactory
{
    private static Localization.Loc L => Localization.Loc.Instance;

    public static AnalyzerViewModel Memory(PageServices s) => new(s, "nav.memory", "sub.memory", [AuditCategory.Memory], MemoryFacts, "ui.top_memory", TopMemory);

    private static IEnumerable<Fact> MemoryFacts(PageServices s, AuditResult a)
    {
        if (a.Snapshot.Memory is not { } m) yield break;
        yield return new(L["ui.total_ram"], Format.Bytes(m.TotalPhysical));
        yield return new(L["ui.available_ram"], Format.Bytes(m.AvailablePhysical));
        yield return new(L["ui.in_use_ram"], Format.Bytes(m.TotalPhysical - m.AvailablePhysical));
        yield return new(L["ui.cached_ram"], Format.Bytes(m.CachedBytes));
        yield return new(L["ui.commit"], $"{Format.Bytes(m.CommitTotal)} / {Format.Bytes(m.CommitLimit)}");
        foreach (var p in m.PageFiles)
            yield return new(L["ui.pagefile"], $"{p.Name}: {p.CurrentUsageMb} / {p.AllocatedMb} MB");
        if (MemoryAnalysis.EffectiveMTs(m.Modules) is { } rate)
            yield return new(L["ui.effective_rate"], $"{rate} MT/s ({MemoryAnalysis.ClockMHzFromTransferRate(rate)} MHz)");
        yield return new(L["ui.xmp_state"], L["val.xmp_unverified"]);
        foreach (var x in m.Modules)
            yield return new(Format.Or(x.Slot), $"{Format.Bytes(x.CapacityBytes)} {MemoryAnalysis.DdrGeneration(x.SmbiosMemoryType)} · {x.ConfiguredMTs}/{x.RatedMTs} MT/s · {x.Manufacturer} {x.PartNumber}");
    }

    private static IEnumerable<Fact> TopMemory(PageServices s, AuditResult a) =>
        a.Snapshot.Background?.TopProcesses.OrderByDescending(p => p.WorkingSet).Take(12)
            .Select(p => new Fact(p.Name, $"{Format.Bytes(p.WorkingSet)} (private {Format.Bytes(p.PrivateBytes)})")) ?? [new Fact(L["ui.full_scan_needed"], "")];

    public static AnalyzerViewModel Cpu(PageServices s) => new(s, "nav.cpu", "sub.cpu", [AuditCategory.Cpu], CpuFacts, "ui.top_cpu", TopCpu);

    private static IEnumerable<Fact> CpuFacts(PageServices s, AuditResult a)
    {
        if (a.Snapshot.Cpu is not { } c) yield break;
        yield return new(L["ui.model"], Format.Or(c.Name));
        yield return new(L["ui.manufacturer"], Format.Or(c.Manufacturer));
        yield return new(L["ui.arch"], Format.Or(c.Architecture));
        yield return new(L["ui.cores_threads"], $"{c.PhysicalCores} / {c.LogicalProcessors}");
        if (c.PerformanceCores is > 0) yield return new(L["ui.hybrid"], $"{c.PerformanceCores}P + {c.EfficiencyCores}E");
        yield return new(L["ui.l3_groups"], c.L3CacheGroups?.ToString() ?? L["ui.unavailable"]);
        yield return new(L["ui.numa"], c.NumaNodes?.ToString() ?? L["ui.unavailable"]);
        yield return new(L["ui.clock"], $"{Format.Or(c.CurrentClockMHz, " MHz")} / {Format.Or(c.MaxClockMHz, " MHz")}");
        yield return new(L["ui.power_plan"], Format.Or(a.Snapshot.Power?.SchemeName));
    }

    private static IEnumerable<Fact> TopCpu(PageServices s, AuditResult a) =>
        a.Snapshot.Background?.TopProcesses.OrderByDescending(p => p.CpuPercent).Take(12)
            .Select(p => new Fact(p.Name, $"{p.CpuPercent:0.#} %")) ?? [new Fact(L["ui.full_scan_needed"], "")];

    public static AnalyzerViewModel Gpu(PageServices s) => new(s, "nav.gpu", "sub.gpu", [AuditCategory.Gpu, AuditCategory.Thermal], GpuFacts);

    private static IEnumerable<Fact> GpuFacts(PageServices s, AuditResult a)
    {
        foreach (var g in a.Snapshot.Hardware?.Gpus ?? [])
        {
            var extra = a.Snapshot.Gpus?.FirstOrDefault(x => x.Id == g.Id);
            yield return new(g.Name, $"{L["kind." + g.Kind]} · {L["form." + g.FormFactor]} · {Format.Or(g.VramGb, " GB")} ({g.VramSource})");
            yield return new(L["ui.driver"], $"{Format.Or(extra?.DriverVersion ?? g.DriverVersion)} {extra?.DriverDate:yyyy-MM-dd}");
            yield return new(L["ui.device_id"], g.Id);
            if (g.PowerMaxW is { } w) yield return new(L["ui.power_limits"], $"{g.PowerLimitW:0} / {g.PowerDefaultW:0} / {w:0} W");
        }
    }

    public static AnalyzerViewModel Display(PageServices s)
    {
        var tools = new DisplayTools(s);
        s.State.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Hardware)) tools.Refresh(); };
        return new AnalyzerViewModel(s, "nav.display", "sub.display", [AuditCategory.Display], DisplayFacts) { Tools = tools };
    }

    private static IEnumerable<Fact> DisplayFacts(PageServices s, AuditResult a)
    {
        foreach (var d in a.Snapshot.Displays ?? [])
        {
            var name = Format.Or(d.Display.FriendlyName);
            yield return new(name, $"{d.Display.Width}x{d.Display.Height} @ {Format.Hz(d.Display.RefreshHz)}{(d.Display.IsPrimary ? " · " + L["ui.primary"] : "")}");
            yield return new(L["ui.max_refresh"], RefreshAnalysis.MaxHzAtCurrentResolution(d) is { } max ? $"{max} Hz" : L["ui.unavailable"]);
            yield return new(L["ui.manufacturer"], Format.Or(d.Manufacturer));
            yield return new(L["ui.scale"], d.ScalePercent > 0 ? $"{d.ScalePercent} %" : L["ui.unavailable"]);
            yield return new(L["ui.color"], d.BitsPerColorChannel is > 0 ? $"{d.BitsPerColorChannel} bit/ch" : L["ui.unavailable"]);
            yield return new(L["ui.modes"], d.Modes.Count.ToString());
        }
    }

    public static AnalyzerViewModel Windows(PageServices s) => new(s, "nav.windows", "sub.windows", [AuditCategory.WindowsGaming, AuditCategory.Security], WindowsFacts);

    private static IEnumerable<Fact> WindowsFacts(PageServices s, AuditResult a)
    {
        yield return new(L["ui.os"], $"{a.Snapshot.Windows?.Caption} ({a.Snapshot.Windows?.Build})");
        var p = a.Snapshot.Power;
        yield return new(L["ui.power_plan"], Format.Or(p?.SchemeName));
        yield return new(L["ui.power_source"], p?.OnAc switch { true => "AC", false => L.Format("ui.battery", p.BatteryPercent ?? 0), _ => L["ui.unavailable"] });
    }

    public static AnalyzerViewModel Storage(PageServices s) => new(s, "nav.storage", "sub.storage", [AuditCategory.Storage], StorageFacts);

    private static IEnumerable<Fact> StorageFacts(PageServices s, AuditResult a)
    {
        if (a.Snapshot.Storage is not { } st)
        {
            yield return new(L["ui.full_scan_needed"], "");
            yield break;
        }
        foreach (var v in st.Volumes)
            yield return new(v.Letter, $"{Format.Bytes(v.Free)} / {Format.Bytes(v.Total)} · {v.FileSystem} · {v.MediaType}{(v.IsSystem ? " · " + L["ui.system_volume"] : "")}{(v.HasGame ? " · BF6" : "")}");
        foreach (var d in st.Disks)
            yield return new(d.Name, $"{d.MediaType} / {d.BusType} · {Format.Bytes(d.Size)} · {d.Health}");
    }

    public static AnalyzerViewModel Network(PageServices s)
    {
        var tools = new NetworkTools(s);
        s.State.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Audit)) tools.Prefill(); };
        return new AnalyzerViewModel(s, "nav.network", "sub.network", [AuditCategory.Network], NetworkFacts) { Tools = tools };
    }

    private static IEnumerable<Fact> NetworkFacts(PageServices s, AuditResult a)
    {
        if (a.Snapshot.Network is not { } n)
        {
            yield return new(L["ui.full_scan_needed"], "");
            yield break;
        }
        foreach (var x in n.Adapters)
        {
            yield return new(x.Name, $"{x.Type} · {x.LinkMbps} Mbps · IPv4 {(x.Ipv4 ? "✓" : "✗")} · IPv6 {(x.Ipv6 ? "✓" : "✗")}");
            yield return new("DNS", string.Join(", ", x.Dns));
        }
    }

    public static AnalyzerViewModel Firmware(PageServices s) => new(s, "nav.bios", "sub.bios", [AuditCategory.Firmware], FirmwareFacts);

    private static IEnumerable<Fact> FirmwareFacts(PageServices s, AuditResult a)
    {
        if (a.Snapshot.Firmware is not { } f) yield break;
        yield return new(L["ui.board"], $"{f.BoardManufacturer} {f.BoardModel}");
        yield return new(L["ui.bios"], $"{f.BiosVendor} {f.BiosVersion}");
        yield return new(L["ui.bios_date"], f.BiosDate?.ToString("yyyy-MM-dd") ?? L["ui.unavailable"]);
        yield return new(L["ui.boot_mode"], Format.Or(f.BootMode));
        yield return new(L["ui.secure_boot"], a.Snapshot.Security?.SecureBoot switch { true => L["val.enabled"], false => L["val.disabled"], _ => L["val.unknown"] });
        yield return new(L["ui.cpu"], Format.Or(a.Snapshot.Cpu?.Name));
        if (a.Snapshot.Memory is { } m && MemoryAnalysis.EffectiveMTs(m.Modules) is { } rate)
            yield return new(L["ui.effective_rate"], $"{rate} MT/s");
    }
}

public sealed partial class HardwareViewModel(PageServices s) : PageViewModel(s)
{
    public ObservableCollection<Fact> Facts { get; } = [];
    public ObservableCollection<GpuInfo> Gpus { get; } = [];
    public ObservableCollection<DisplayInfo> Displays { get; } = [];

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string HagsNote { get; set; } = "";

    public GpuInfo? SelectedGpu
    {
        get => S.State.Gpu;
        set { S.State.SelectGpu(value); OnPropertyChanged(); }
    }

    public DisplayInfo? SelectedDisplay
    {
        get => S.State.Display;
        set { S.State.SelectDisplay(value); OnPropertyChanged(); }
    }

    public override Task OnShownAsync()
    {
        Fill();
        return Task.CompletedTask;
    }

    public override void OnLanguageChanged() => Fill();

    private void Fill()
    {
        var hw = S.State.Hardware;
        if (hw is null) return;
        Gpus.Replace(hw.Gpus);
        Displays.Replace(hw.Displays);
        OnPropertyChanged(nameof(SelectedGpu));
        OnPropertyChanged(nameof(SelectedDisplay));

        var facts = new List<Fact>
        {
            new(L["ui.computer"], $"{hw.Manufacturer} {hw.Model}"),
            new(L["ui.form_factor"], $"{L["form." + hw.SystemFormFactor]} ({L["evidence_src." + hw.SystemFormFactorEvidence]})"),
            new(L["ui.cpu"], Format.Or(hw.Cpu?.Name)),
            new(L["ui.cores_threads"], $"{hw.Cpu?.PhysicalCores} / {hw.Cpu?.LogicalCores}"),
            new(L["ui.ram"], Format.Bytes(hw.Memory?.TotalBytes)),
            new(L["ui.os"], $"{hw.Os?.Caption} ({hw.Os?.Build})"),
        };
        foreach (var g in hw.Gpus)
            facts.Add(new(g.Name, $"{L["kind." + g.Kind]} · {L["form." + g.FormFactor]} ({L["evidence_src." + g.FormFactorEvidence]}) · {Format.Or(g.VramGb, " GB")} · {g.VramSource}"));
        foreach (var d in hw.Displays)
            facts.Add(new(Format.Or(d.FriendlyName), $"{d.Width}x{d.Height} @ {Format.Hz(d.RefreshHz)}"));
        Facts.Replace(facts);
        HagsNote = L.Format("ui.hags_readonly", hw.HagsMode switch { 2 => L["val.hags_on"], 1 => L["val.hags_off"], _ => L["ui.unavailable"] });
    }

    [RelayCommand]
    private async Task Rescan()
    {
        IsBusy = true;
        try
        {
            await S.State.RefreshHardwareAsync();
            Fill();
        }
        finally { IsBusy = false; }
    }
}

public sealed partial class ProcessRow(ProcessFacts p)
{
    public string Name => p.Name;
    public int Pid => p.Pid;
    public string Cpu => $"{p.CpuPercent:0.#} %";
    public double CpuValue => p.CpuPercent;
    public string Memory => Format.Bytes(p.WorkingSet);
    public long MemoryValue => p.WorkingSet;
    public string Private => Format.Bytes(p.PrivateBytes);
    public string Disk => $"{Format.Bytes(p.IoBytesPerSec)}/s";
    public long DiskValue => p.IoBytesPerSec;
    public string Publisher => p.Publisher ?? "-";
}

public sealed partial class StartupRow(StartupEntry e, Func<StartupRow, Task> toggle) : ObservableObject
{
    private static Localization.Loc L => Localization.Loc.Instance;

    public StartupEntry Entry => e;
    public string Name => e.Name;
    public string Category => L["startup." + e.Category];
    public string Location => e.Location;
    public string State => L[e.Enabled ? "val.enabled" : "val.disabled"];
    public bool CanToggle => e.Location == "HKCU Run";
    public string ToggleText => L[e.Enabled ? "ui.disable" : "ui.enable"];

    [RelayCommand] private Task Toggle() => toggle(this);
}

public sealed partial class ProcessesViewModel(PageServices s) : PageViewModel(s)
{
    private IReadOnlyList<ProcessFacts> _all = [];

    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ObservableCollection<StartupRow> Startup { get; } = [];
    public IReadOnlyList<string> Sorts => [L["ui.sort_cpu"], L["ui.sort_memory"], L["ui.sort_disk"]];

    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Assessment { get; set; } = "";

    partial void OnSortIndexChanged(int value) => Sort();

    public override async Task OnShownAsync()
    {
        LoadStartup();
        if (_all.Count == 0) await Refresh();
    }

    private void LoadStartup() => Startup.Replace(S.Startup.List().Select(e => new StartupRow(e, ToggleAsync)));

    private void Sort()
    {
        var rows = _all.Select(p => new ProcessRow(p));
        Processes.Replace(SortIndex switch
        {
            1 => rows.OrderByDescending(r => r.MemoryValue),
            2 => rows.OrderByDescending(r => r.DiskValue),
            _ => rows.OrderByDescending(r => r.CpuValue),
        });
    }

    [RelayCommand]
    private async Task Refresh()
    {
        IsBusy = true;
        try
        {
            var sample = await Infrastructure.Hardware.ProcessInspector.SampleAsync(TimeSpan.FromSeconds(1), 40, CancellationToken.None);
            _all = sample.Top;
            Sort();
            Assessment = L.Format("ui.background_load", $"{sample.TotalCpu:0}", sample.Count);
        }
        finally { IsBusy = false; }
    }

    private async Task ToggleAsync(StartupRow row)
    {
        var e = row.Entry;
        if (!await Dialogs.ConfirmAsync(L["ui.startup_confirm_title"], L.Format("ui.startup_confirm", e.Name, row.ToggleText), row.ToggleText)) return;
        S.Startup.SetEnabled(e, !e.Enabled);
        LoadStartup();
    }

    [RelayCommand] private void OpenStartupSettings() => Shell.OpenUri("ms-settings:startupapps");

    [RelayCommand]
    private void OpenLocation(StartupRow? row)
    {
        if (row is null) return;
        var exe = ExtractExe(row.Entry.Command);
        if (exe is not null && File.Exists(exe)) Shell.Reveal(exe);
    }

    private static string? ExtractExe(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command).Trim();
        if (c.StartsWith('"')) return c.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        int i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? c[..(i + 4)] : c;
    }
}

public sealed partial class SafeRow(PreferencePreview p)
{
    private static Localization.Loc L => Localization.Loc.Instance;

    public string Name => L[$"pref.{p.Preference.Id}"];
    public string Before => p.Exists ? p.Before?.ToString() ?? "?" : L["val.not_set"];
    public string After => p.Preference.Value.ToString();
    public string Scope => "HKCU\\" + p.Preference.Path;
}

public sealed partial class SafeCenterViewModel(PageServices s) : PageViewModel(s)
{
    public ObservableCollection<SafeRow> Rows { get; } = [];

    [ObservableProperty] public partial string Message { get; set; } = "";

    public override Task OnShownAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    public override void OnLanguageChanged() => Load();

    private void Load() => Rows.Replace(S.SafeActions.Preview().Select(p => new SafeRow(p)));

    [RelayCommand]
    private async Task Apply()
    {
        if (!await Dialogs.ConfirmAsync(L["ui.safe_apply"], L["ui.safe_confirm"], L["ui.apply"])) return;
        var r = S.SafeActions.Apply();
        Message = r.Success ? L["ui.safe_done"] : L.Get(r.Problems.FirstOrDefault());
        Load();
    }

    [RelayCommand]
    private async Task Restore()
    {
        if (!await Dialogs.ConfirmAsync(L["ui.safe_restore"], L["ui.safe_restore_confirm"], L["ui.restore"])) return;
        var r = S.SafeActions.Restore();
        Message = r.Success ? L["ui.safe_restored"] : L.Get(r.Problems.FirstOrDefault());
        Load();
    }
}
