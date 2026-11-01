using System.IO;
using System.Collections.ObjectModel;
using BF6Optimizer.App.Services;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Verification;
using BF6Optimizer.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BF6Optimizer.App.ViewModels;

public sealed partial class ChangeRow(SettingChange c, SettingDefinition? def)
{
    private static Localization.Loc L => Localization.Loc.Instance;

    public string Name => def is null ? c.Key : L["setting." + def.ShortName];
    public string Key => c.Key;
    public string Before => c.Before ?? "—";
    public string After => c.After;
    public string StatusText => L["change." + c.Status];
    public ChangeStatus Status => c.Status;
    public string Purpose => L.Get(c.Reason);
}

public sealed partial class FileDiff(string path, string detail, IReadOnlyList<ChangeRow> rows)
{
    public string Path => path;
    public string Detail => detail;
    public IReadOnlyList<ChangeRow> Rows => rows;
}

public sealed partial class OptimizerViewModel(PageServices s) : PageViewModel(s)
{
    private IReadOnlyList<FileChangeSet> _targets = [];
    private OptimizationPlan? _plan;

    public IReadOnlyList<OptimizationMode> Modes { get; } = Enum.GetValues<OptimizationMode>();
    public IReadOnlyList<string> ModeNames => Modes.Select(m => L["mode." + m]).ToList();
    public IReadOnlyList<string> Objectives => Enum.GetValues<AutoObjective>().Select(o => L["objective." + o]).ToList();

    public ObservableCollection<FileDiff> Diffs { get; } = [];
    public ObservableCollection<string> Reasons { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<Fact> PlanFacts { get; } = [];

    [ObservableProperty] public partial int ModeIndex { get; set; }
    [ObservableProperty] public partial int ObjectiveIndex { get; set; }
    [ObservableProperty] public partial bool ApplyWindows { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply))] public partial bool HasPreview { get; set; }

    public bool CanApply => HasPreview && _targets.Count > 0;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial int Percent { get; set; }
    [ObservableProperty] public partial string Stage { get; set; } = "";
    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial string ProfileNote { get; set; } = "";

    public OptimizationMode Mode
    {
        get => Modes[ModeIndex];
        set => ModeIndex = Array.IndexOf(Modes.ToArray(), value);
    }

    public override async Task OnShownAsync()
    {
        await S.State.RefreshProfilesAsync();
        UpdateProfileNote();
    }

    public override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(ModeNames));
        OnPropertyChanged(nameof(Objectives));
        UpdateProfileNote();
        HasPreview = false;
    }

    /// <summary>Demo mode hides the fixture location so documentation shows no local path.</summary>
    private static string Shown(string path) =>
        TestMode.Current.SimulateHardware && path.IndexOf("Battlefield 6", StringComparison.Ordinal) is >= 0 and var i ? path[i..] : path;

    private void UpdateProfileNote()
    {
        var p = S.State.Profiles;
        ProfileNote = p.Count == 0 ? L["ui.bf6_not_found"] : L.Format("ui.bf6_found", p.Count(x => x.Compatible), p.Count);
        Warnings.Replace(CloudWarnings(p));
    }

    private static IEnumerable<string> CloudWarnings(IReadOnlyList<DetectedProfile> p)
    {
        if (p.Any(x => x.SteamCloud)) yield return L["warn.steam_cloud"];
        if (p.Any(x => x.OneDrive)) yield return L["warn.onedrive"];
    }

    [RelayCommand]
    public async Task Preview()
    {
        Result = "";
        HasPreview = false;
        await S.State.RefreshProfilesAsync();
        UpdateProfileNote();
        if (S.State.Hardware is not { } hw) return;

        var prefs = Mode == OptimizationMode.Custom ? S.State.CustomPreferences.Clone() : new OptimizationPreferences();
        prefs.Objective = (AutoObjective)ObjectiveIndex;
        prefs.ApplyWindowsPreferences = ApplyWindows;

        var plan = S.Planner.Build(new PlanRequest { Mode = Mode, Hardware = hw, Gpu = S.State.Gpu, Display = S.State.Display, Preferences = prefs });

        var targets = new List<FileChangeSet>();
        var diffs = new List<FileDiff>();
        foreach (var p in S.State.Profiles.Where(x => x.Compatible))
        {
            try
            {
                var (doc, sha) = await ProfileLoader.LoadAsync(S.Files, p.Path, CancellationToken.None);
                var set = DiffBuilder.Build(p.Path, sha, doc, plan, S.Catalog);
                targets.Add(set);
                diffs.Add(new FileDiff(Shown(p.Path), $"{set.Encoding}, {set.NewLine} · {L.Format("ui.diff_counts", set.Changes.Count(c => c.Status == ChangeStatus.Change), set.Changes.Count)}",
                    set.Changes.OrderBy(c => c.Status).Select(c => new ChangeRow(c, S.Catalog.Find(c.Key))).ToList()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OptimizerException)
            {
                Warnings.Add(L.Format("ui.profile_skip", p.Path));
            }
        }

        _plan = plan;
        _targets = targets;
        Diffs.Replace(diffs);
        Reasons.Replace(plan.Reasons.Select(L.Get));
        foreach (var w in plan.Warnings) Warnings.Add(L.Get(w));
        PlanFacts.Replace(
        [
            new(L["ui.resolved_mode"], L["mode." + plan.ResolvedMode]),
            new(L["ui.estimated_tier"], L["tier." + plan.Tier]),
            new(L["ui.estimate"], L.Format("ui.estimate_value", plan.EffectiveScore.ToString("0.##"), L["score." + plan.Confidence])),
            new(L["ui.fps_cap"], plan.FpsCap is { } cap ? $"{cap}" : L["ui.preserved"]),
            new(L["ui.windows_prefs"], plan.WindowsPreferences.Count > 0 ? string.Join(", ", plan.WindowsPreferences.Select(p => L[$"pref.{p.Id}"])) : L["ui.preserved"]),
        ]);
        HasPreview = true;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (_plan is null || IsBusy) return;
        int changes = _targets.Sum(t => t.Changes.Count(c => c.Status == ChangeStatus.Change));
        if (!await Dialogs.ConfirmAsync(L["ui.apply_title"], L.Format("ui.apply_confirm", changes, _targets.Count), L["ui.apply"])) return;

        IsBusy = true;
        Result = "";
        var progress = new Progress<ApplyProgress>(p =>
        {
            Percent = p.Percent;
            Stage = L["stage." + p.Stage];
        });
        try
        {
            var result = await S.Apply.ApplyAsync(new ApplyRequest
            {
                Plan = _plan,
                MachineKey = S.Detector.GetMachineKey(),
                Targets = _targets,
                AllProfiles = S.State.Profiles.Select(p => p.Path).ToList(),
            }, progress);

            var lines = new List<string> { L["outcome." + result.Outcome] };
            if (result.Error is not null) lines.Add(L.Get(result.Error));
            lines.AddRange(result.Warnings.Select(L.Get));
            Result = string.Join(Environment.NewLine, lines);
            HasPreview = false;
        }
        finally { IsBusy = false; }
    }
}

public sealed partial class AdvancedRow : ObservableObject
{
    private readonly SettingDefinition _def;

    public AdvancedRow(SettingDefinition def, string? current, string? chosen)
    {
        _def = def;
        Current = current ?? "—";
        Unavailable = current is null || def.Kind == SettingKind.Preserved;
        Options = [new Option(OptimizationPreferences.PreserveValue, Loc["ui.preserve_or_mode"]), new Option("__keep", Loc["ui.preserve"]), .. def.Writable.Select(v => new Option(v, v))];
        Selected = chosen switch
        {
            null => Options[0],
            OptimizationPreferences.PreserveValue => Options[1],
            _ => Options.FirstOrDefault(o => o.Value == chosen) ?? Options[0],
        };
    }

    private static Localization.Loc Loc => Localization.Loc.Instance;

    public sealed record Option(string Value, string Label);

    public string Key => _def.Key;
    public string Name => Loc["setting." + _def.ShortName];
    public string Category => Loc["category." + _def.Category];
    public string Current { get; }
    public bool Unavailable { get; }
    public bool Editable => !Unavailable;
    public string Note => _def.Kind == SettingKind.Preserved ? Loc["ui.preserved_upscaler"] : Current == "—" ? Loc["ui.not_in_profile"] : "";
    public IReadOnlyList<Option> Options { get; }

    [ObservableProperty] public partial Option Selected { get; set; } = null!;
}

public sealed partial class AdvancedViewModel(PageServices s, OptimizerViewModel optimizer, ShellViewModel? shell = null) : PageViewModel(s)
{
    public ObservableCollection<AdvancedRow> Rows { get; } = [];
    public IReadOnlyList<string> FpsModes => Enum.GetValues<FpsCapMode>().Select(m => L["fps." + m]).ToList();
    public IReadOnlyList<string> BaseModes => new[] { OptimizationMode.Competitive, OptimizationMode.Balanced, OptimizationMode.Quality }.Select(m => L["mode." + m]).ToList();

    [ObservableProperty] public partial int FpsModeIndex { get; set; }
    [ObservableProperty] public partial int BaseModeIndex { get; set; } = 1;
    [ObservableProperty] public partial string FpsText { get; set; } = "120";
    [ObservableProperty] public partial string FpsError { get; set; } = "";
    [ObservableProperty] public partial string CapHint { get; set; } = "";

    partial void OnFpsTextChanged(string value) => FpsError = FpsCapValidator.ValidateText(value, out _) is { } m ? L.Get(m) : "";

    public override async Task OnShownAsync()
    {
        await S.State.RefreshProfilesAsync();
        var doc = await FirstDocumentAsync();
        var custom = S.State.CustomPreferences;
        FpsModeIndex = (int)custom.FpsCapMode;
        FpsText = custom.ManualFpsCap.ToString();
        BaseModeIndex = custom.BaseMode switch { OptimizationMode.Competitive => 0, OptimizationMode.Quality => 2, _ => 1 };
        Rows.Replace(S.Catalog.Settings.Where(d => d.Advanced && d.Kind != SettingKind.Fps)
            .Select(d => new AdvancedRow(d, doc?.Get(d.Key), custom.Overrides.GetValueOrDefault(d.Key))));

        var hz = S.State.Display?.RefreshHz ?? 0;
        CapHint = hz > 0 ? L.Format("ui.cap_note", (int)Math.Round(hz), Core.Audit.RefreshAnalysis.StartingCap((int)Math.Round(hz), true) ?? 0) : "";
    }

    private async Task<ProfileDocument?> FirstDocumentAsync()
    {
        foreach (var p in S.State.Profiles.Where(x => x.Compatible))
        {
            try { return (await ProfileLoader.LoadAsync(S.Files, p.Path, CancellationToken.None)).Doc; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OptimizerException) { }
        }
        return null;
    }

    [RelayCommand]
    private async Task Use()
    {
        if (FpsCapValidator.ValidateText(FpsText, out var cap) is not null && FpsModeIndex == (int)FpsCapMode.Manual) return;

        var custom = S.State.CustomPreferences;
        custom.FpsCapMode = (FpsCapMode)FpsModeIndex;
        if (cap > 0) custom.ManualFpsCap = cap;
        custom.BaseMode = BaseModeIndex switch { 0 => OptimizationMode.Competitive, 2 => OptimizationMode.Quality, _ => OptimizationMode.Balanced };
        custom.Overrides.Clear();
        foreach (var r in Rows.Where(r => r.Editable && r.Selected != r.Options[0]))
            custom.Overrides[r.Key] = r.Selected.Value == "__keep" ? OptimizationPreferences.PreserveValue : r.Selected.Value;

        S.State.SaveSettings();
        optimizer.Mode = OptimizationMode.Custom;
        shell?.NavigateTo("nav.optimizer");
        await optimizer.Preview();
    }
}

public sealed partial class StatusViewModel(PageServices s, ShellViewModel? shell = null) : PageViewModel(s)
{
    public ObservableCollection<StatusRow> Rows { get; } = [];
    public ObservableCollection<Fact> Counts { get; } = [];

    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial string CheckedAt { get; set; } = "";
    [ObservableProperty] public partial string Hint { get; set; } = "";
    [ObservableProperty] public partial bool HasRecord { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    [RelayCommand] private void OpenOptimizer() => shell?.NavigateTo("nav.optimizer");

    public sealed record StatusRow(string Source, string Key, string Expected, string Actual, string State);

    public override Task OnShownAsync() => Check();

    [RelayCommand]
    private async Task Check()
    {
        IsBusy = true;
        try
        {
            var r = await S.Status.CheckAsync();
            HasRecord = r.Overall != OverallStatus.NoRecord;
            Hint = HasRecord ? "" : L.Format("ui.status_no_record", S.State.Profiles.Count(p => p.Compatible), S.State.Profiles.Count);
            Headline = L["overall." + r.Overall];
            if (r.Record is { } rec) Headline += " · " + L.Format("ui.last_apply", rec.Timestamp.ToString("g"), L["mode." + rec.Mode]);
            if (r.BackupState is { } b) Headline += " · " + L["state." + b];

            Counts.Replace(Enum.GetValues<SettingState>().Select(st => new Fact(L["state." + st], r.Count(st).ToString())));
            Rows.Replace(r.Items.OrderBy(i => i.State == SettingState.AppliedCorrectly).Select(i => new StatusRow(
                System.IO.Path.GetFileName(i.Source), i.Key, i.Expected ?? "—", i.Actual ?? "—", L["state." + i.State])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OptimizerException)
        {
            HasRecord = false;
            Hint = "";
            Headline = L["error.unexpected"];
        }
        finally
        {
            CheckedAt = L.Format("ui.status_checked", DateTime.Now.ToString("T"));
            IsBusy = false;
        }
    }
}
