using System.Collections.ObjectModel;
using System.IO;
using BF6Optimizer.App.Services;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Diagnostics;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Profiles;
using BF6Optimizer.Core.Verification;
using BF6Optimizer.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BF6Optimizer.App.ViewModels;

public sealed partial class ProfilesViewModel(PageServices s) : PageViewModel(s)
{
    public ObservableCollection<OptimizationProfile> Profiles { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial OptimizationProfile? Selected { get; set; }
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";

    public bool CanEdit => Selected is { BuiltIn: false };

    partial void OnSelectedChanged(OptimizationProfile? value) => EditName = value?.Name ?? "";

    public override Task OnShownAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    private void Load() => Profiles.Replace(BuiltInProfiles.All.Concat(S.Profiles.LoadUserProfiles()));

    [RelayCommand]
    private void SaveCurrent()
    {
        var p = new OptimizationProfile { Name = L.Format("ui.profile_new", DateTime.Now.ToString("g")), Preferences = S.State.CustomPreferences.Clone() };
        p.Preferences.BaseMode = S.State.CustomPreferences.BaseMode;
        S.Profiles.Save(p);
        Load();
        Selected = Profiles.First(x => x.Id == p.Id);
        Message = L["ui.profile_saved"];
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } src) return;
        var p = new OptimizationProfile { Name = src.Name + " +", Preferences = src.Preferences.Clone() };
        S.Profiles.Save(p);
        Load();
        Selected = Profiles.First(x => x.Id == p.Id);
    }

    [RelayCommand]
    private void Rename()
    {
        if (Selected is not { BuiltIn: false } p) return;
        var old = p.Name;
        p.Name = EditName.Trim();
        if (ProfileValidator.Validate(p, S.Catalog) is { Count: > 0 } errors)
        {
            p.Name = old;
            Message = L.Get(errors[0]);
            return;
        }
        S.Profiles.Save(p);
        var id = p.Id;
        Load();
        Selected = Profiles.First(x => x.Id == id);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { BuiltIn: false } p) return;
        if (!await Dialogs.ConfirmAsync(L["ui.delete"], L.Format("ui.profile_delete", p.Name), L["ui.delete"])) return;
        S.Profiles.Delete(p.Id);
        Load();
    }

    [RelayCommand]
    private void Use()
    {
        if (Selected is null) return;
        S.State.CustomPreferences = Selected.Preferences.Clone();
        if (S.State.CustomPreferences.BaseMode == OptimizationMode.Custom) S.State.CustomPreferences.BaseMode = OptimizationMode.Balanced;
        Message = L.Format("ui.profile_loaded", Selected.Name);
    }

    [RelayCommand]
    private void Export()
    {
        if (Selected is not { } p) return;
        var path = Dialogs.SaveFile(p.Name + ".bf6profile.json", "JSON|*.json");
        if (path is null) return;
        File.WriteAllText(path, ProfileValidator.Export(p));
        Message = L.Format("ui.saved_to", Path.GetFileName(path));
    }

    [RelayCommand]
    private void Import()
    {
        var path = Dialogs.OpenFile("JSON|*.json");
        if (path is null) return;
        var (profile, errors) = ProfileValidator.ParseImport(File.ReadAllText(path), S.Catalog);
        if (profile is null)
        {
            Message = L.Get(errors.FirstOrDefault());
            return;
        }
        S.Profiles.Save(profile);
        Load();
        Message = L.Format("ui.profile_imported", profile.Name);
    }
}

public sealed record RestoreRow(RestorePoint Point, string Kind, string Created, string Version, string Files, string Note);

public sealed partial class BackupViewModel(PageServices s) : PageViewModel(s)
{
    public ObservableCollection<RestoreRow> Points { get; } = [];

    [ObservableProperty] public partial RestoreRow? Selected { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public override Task OnShownAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    public override void OnLanguageChanged() => Load();

    private void Load()
    {
        Points.Replace(S.Backups.List().Select(p => new RestoreRow(
            p, L["point." + p.Kind], p.Created.ToString("g"), p.AppVersion,
            string.Join(", ", p.Files.Select(f => Path.GetFileName(f.OriginalPath))),
            p.IsLegacy ? L["ui.legacy_backup"] : "")));
        if (Points.Count == 0) Message = L["ui.no_backups"];
    }

    [RelayCommand]
    private async Task Verify()
    {
        if (Selected is not { } r) return;
        var v = await S.Backups.VerifyAsync(r.Point, CancellationToken.None);
        Message = v.IsValid ? L["ui.backup_ok"] : L.Get(v.Problems[0]);
    }

    [RelayCommand] private Task RestoreSelected() => Selected is { } r ? RestoreAsync(r.Point) : Task.CompletedTask;

    [RelayCommand]
    private Task RestoreLatest()
    {
        var id = S.State.LoadRecord()?.SnapshotId;
        var point = id is null ? null : S.Backups.Find(id);
        if (point is not null) return RestoreAsync(point);
        Message = L["ui.no_snapshot"];
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RestoreBaseline()
    {
        if (S.Backups.GetBaseline() is { } b) return RestoreAsync(b);
        Message = L["ui.no_baseline"];
        return Task.CompletedTask;
    }

    private async Task RestoreAsync(RestorePoint point)
    {
        var text = L.Format("ui.restore_confirm", point.Created.ToString("g"), string.Join(Environment.NewLine, point.Files.Select(f => f.OriginalPath)));
        if (!await Dialogs.ConfirmAsync(L["ui.restore"], text, L["ui.restore"])) return;

        IsBusy = true;
        try
        {
            var r = await S.Restore.RestoreAsync(point, S.Detector.GetMachineKey());
            Message = L["outcome." + r.Outcome] + (r.Error is null ? "" : " " + L.Get(r.Error));
            Load();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void AddLegacyFolder()
    {
        var dir = Dialogs.PickFolder();
        if (dir is null) return;
        S.State.Settings.ExternalBackupRoots.Add(dir);
        S.State.SaveSettings();
        Load();
    }

    [RelayCommand] private void OpenFolder() => Shell.OpenFolder(S.Paths.Backups);
}

public sealed partial class ReportsViewModel(PageServices s) : PageViewModel(s)
{
    public ObservableCollection<Fact> Reports { get; } = [];

    [ObservableProperty] public partial string LogText { get; set; } = "";

    public override Task OnShownAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    public override void OnLanguageChanged() => Load();

    private void Load()
    {
        Reports.Replace(S.Reports.List().Select(r => new Fact(
            $"{r.Timestamp:g} · {r.Action}",
            L["outcome." + r.Outcome] + (r.Error is null ? "" : " " + L.Get(r.Error)))));
        LogText = string.Join(Environment.NewLine, S.Logs.ReadRecent(200));
    }

    [RelayCommand] private void OpenLogs() => Shell.OpenFolder(S.Paths.Logs);

    [RelayCommand] private void OpenReports() => Shell.OpenFolder(S.Paths.Reports);

    [RelayCommand] private void OpenBackups() => Shell.OpenFolder(S.Paths.Backups);

    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        StatusReport? status = null;
        try { status = await S.Status.CheckAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var json = DiagnosticReportBuilder.Build(S.State.Hardware, status, S.Reports.List(), S.Logs.ReadRecent(100),
            PrivacyFilter.Create(S.Detector.GetMachineKey()), S.Clock.Now);
        json = AuditReportExporter.RedactNetwork(json);
        if (!await Dialogs.PreviewAsync(L["ui.privacy_review"], json)) return;
        var path = Dialogs.SaveFile($"BF6Optimizer_Diagnostics_{DateTime.Now:yyyyMMdd_HHmm}.json", "JSON|*.json");
        if (path is null) return;
        await File.WriteAllTextAsync(path, json);
        S.State.Notify(L.Format("ui.saved_to", Path.GetFileName(path)));
    }

    [RelayCommand]
    private async Task ExportAudit(string format)
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
}

public sealed partial class SettingsViewModel(PageServices s, ShellViewModel shell) : PageViewModel(s)
{
    public IReadOnlyList<string> Languages { get; } = ["Türkçe", "English"];
    public IReadOnlyList<string> Themes => [L["ui.theme_dark"], L["ui.theme_light"]];

    public int LanguageIndex
    {
        get => S.State.Settings.Language == "en" ? 1 : 0;
        set
        {
            S.State.Settings.Language = value == 1 ? "en" : "tr";
            S.State.SaveSettings();
            Localization.Loc.Instance.SetLanguage(S.State.Settings.Language);
            shell.OnLanguageChanged();
            OnPropertyChanged(nameof(Themes));
            OnPropertyChanged();
        }
    }

    public int ThemeIndex
    {
        get => S.State.Settings.Theme == "Light" ? 1 : 0;
        set
        {
            S.State.Settings.Theme = value == 1 ? "Light" : "Dark";
            S.State.SaveSettings();
            ThemeService.Apply(S.State.Settings.Theme);
            OnPropertyChanged();
        }
    }

    [ObservableProperty] public partial string Message { get; set; } = "";

    public string ProductLine => $"{AppInfo.ProductName} {AppInfo.Version}";
    public string AuthorLine => L.Format("ui.author", AppInfo.Author);
    public string DataFolder => S.Paths.Root;
    public string StorageMode => L[S.Paths.Mode == Infrastructure.Storage.StorageMode.Portable ? "ui.storage_portable" : "ui.storage_local"];
    public bool IsPortable => S.Paths.Mode == Infrastructure.Storage.StorageMode.Portable;
    public string ManualFolder => S.State.Settings.ManualProfileDirectory ?? L["ui.auto_detect"];

    [RelayCommand]
    private void PickProfileFolder()
    {
        var dir = Dialogs.PickFolder();
        if (dir is null) return;
        S.State.Settings.ManualProfileDirectory = dir;
        S.State.SaveSettings();
        OnPropertyChanged(nameof(ManualFolder));
    }

    [RelayCommand]
    private void ClearProfileFolder()
    {
        S.State.Settings.ManualProfileDirectory = null;
        S.State.SaveSettings();
        OnPropertyChanged(nameof(ManualFolder));
    }

    [RelayCommand]
    private async Task TogglePortable()
    {
        if (IsPortable)
        {
            AppPaths.DisablePortable();
            Message = L["ui.restart_needed"];
            return;
        }
        if (!await Dialogs.ConfirmAsync(L["ui.storage_mode"], L["ui.portable_confirm"], L["ui.enable"])) return;
        Message = AppPaths.TryEnablePortable() ? L["ui.restart_needed"] : L["ui.portable_failed"];
    }

    [RelayCommand]
    private void ImportPerformanceData()
    {
        var path = Dialogs.OpenFile("JSON|*.json");
        if (path is null) return;
        try
        {
            using (var f = File.OpenRead(path)) PerformanceData.Load(f);
            File.Copy(path, Path.Combine(S.Paths.Data, PerformanceDataLoader.OverrideName), overwrite: true);
            Message = L["ui.restart_needed"];
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            Message = L["error.data_invalid"];
        }
    }

    [RelayCommand] private void OpenDataFolder() => Shell.OpenFolder(S.Paths.Root);

    [RelayCommand] private void ShowWelcome() => shell.ReplayWelcome();
}
