using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.Core.Verification;

public enum SettingState
{
    AppliedCorrectly, ModifiedExternally, MissingSetting, UnsupportedSetting,
    FileInaccessible, BackupUnavailable, VerificationFailure,
}

public enum OverallStatus { NoRecord, Verified, Drifted, Problem }

public sealed record SettingStatus(string Source, string Key, string? Expected, string? Actual, SettingState State);

public sealed record StatusReport
{
    public OverallStatus Overall { get; init; }
    public ApplyRecord? Record { get; init; }
    public IReadOnlyList<SettingStatus> Items { get; init; } = [];
    public SettingState? BackupState { get; init; }

    public int Count(SettingState state) => Items.Count(i => i.State == state);
}

/// <summary>Compares the machine with the last verified Apply. Read only: nothing is ever re-applied here.</summary>
public sealed class StatusVerifier(
    IProfileFileStore files,
    IBackupStore backups,
    IWindowsPreferenceStore preferences,
    IApplyRecordStore records,
    SettingCatalog catalog)
{
    public async Task<StatusReport> CheckAsync(CancellationToken ct = default)
    {
        ApplyRecord? record;
        try { record = records.Load(); }
        catch (Exception)
        {
            return new StatusReport
            {
                Overall = OverallStatus.Problem,
                Items = [new SettingStatus("record", "-", null, null, SettingState.VerificationFailure)],
            };
        }
        if (record is null) return new StatusReport { Overall = OverallStatus.NoRecord };

        var items = new List<SettingStatus>();
        foreach (var file in record.Files)
            items.AddRange(await CheckFileAsync(file, ct).ConfigureAwait(false));

        foreach (var pref in record.Preferences)
        {
            var state = preferences.Read(pref.Path, pref.Name);
            items.Add(new SettingStatus(
                "Windows", pref.Name, pref.Value.ToString(), state.Value?.ToString(),
                !state.Exists ? SettingState.MissingSetting
                : state.Value == pref.Value ? SettingState.AppliedCorrectly
                : SettingState.ModifiedExternally));
        }

        var backupState = await CheckBackupAsync(record, ct).ConfigureAwait(false);

        var worst = items.Any(i => i.State is SettingState.FileInaccessible or SettingState.VerificationFailure) || backupState is not null
            ? OverallStatus.Problem
            : items.Any(i => i.State != SettingState.AppliedCorrectly) ? OverallStatus.Drifted
            : OverallStatus.Verified;

        return new StatusReport { Overall = worst, Record = record, Items = items, BackupState = backupState };
    }

    private async Task<SettingState?> CheckBackupAsync(ApplyRecord record, CancellationToken ct)
    {
        if (backups.Find(record.SnapshotId) is not { } point) return SettingState.BackupUnavailable;
        var result = await backups.VerifyAsync(point, ct).ConfigureAwait(false);
        return result.IsValid ? null : SettingState.VerificationFailure;
    }

    private async Task<IReadOnlyList<SettingStatus>> CheckFileAsync(ExpectedFile file, CancellationToken ct)
    {
        ProfileDocument doc;
        try
        {
            if (files.Inspect(file.Path) is FileAccessState.Missing or FileAccessState.Denied or FileAccessState.Locked)
                return [new SettingStatus(file.Path, "-", null, null, SettingState.FileInaccessible)];
            (doc, _) = await ProfileLoader.LoadAsync(files, file.Path, ct).ConfigureAwait(false);
        }
        catch (OptimizerException) { return [new SettingStatus(file.Path, "-", null, null, SettingState.VerificationFailure)]; }
        catch (IOException) { return [new SettingStatus(file.Path, "-", null, null, SettingState.FileInaccessible)]; }
        catch (UnauthorizedAccessException) { return [new SettingStatus(file.Path, "-", null, null, SettingState.FileInaccessible)]; }

        var result = new List<SettingStatus>(file.Settings.Count);
        foreach (var (key, expected) in file.Settings)
        {
            var values = doc.GetAll(key).ToList();
            var actual = values.FirstOrDefault();
            var def = catalog.Find(key);
            var state =
                values.Count == 0 ? SettingState.MissingSetting
                : values.All(v => v == expected) ? SettingState.AppliedCorrectly
                : def is null || !values.All(def.IsKnownValue) ? SettingState.UnsupportedSetting
                : SettingState.ModifiedExternally;
            result.Add(new SettingStatus(file.Path, key, expected, actual, state));
        }
        return result;
    }
}
