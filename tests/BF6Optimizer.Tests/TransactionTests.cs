using System.Text;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Verification;

namespace BF6Optimizer.Tests;

public class ApplyTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Success_writes_verifies_records_and_keeps_unrelated_lines()
    {
        var result = await _h.Apply.ApplyAsync(await _h.RequestAsync());

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Contains("GstRender.VSyncMode 0\n", _h.Current);
        Assert.Contains("GstRender.FrameRateLimit 237.000000\n", _h.Current);
        Assert.Contains("GstAudio.Volume_Master 0.800000\n", _h.Current);
        Assert.Contains("GstRender.NvidiaUpscalingTechnique 1\n", _h.Current);
        Assert.Contains("GstKeyBinding.Fire Mouse0\n", _h.Current);
        Assert.Equal(Fixtures.Profile.Split('\n').Length, _h.Current.Split('\n').Length);
        Assert.NotNull(_h.Records.Record);
        Assert.Equal(1, _h.Registry.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);
    }

    [Fact]
    public async Task Creates_baseline_once_with_original_content()
    {
        await _h.Apply.ApplyAsync(await _h.RequestAsync());
        var baseline = _h.Backups.GetBaseline()!;
        var firstHash = baseline.Files[0].Sha256;

        await _h.Apply.ApplyAsync(await _h.RequestAsync(_h.Plan(OptimizationMode.Balanced)));

        var again = _h.Backups.GetBaseline()!;
        Assert.Equal(firstHash, again.Files[0].Sha256);
        Assert.Equal(baseline.Created, again.Created);
        Assert.Equal(Hashing.Sha256(Encoding.UTF8.GetBytes(Fixtures.Profile)), firstHash);
        Assert.True(new FileInfo(Path.Combine(again.Directory, again.Files[0].RelativeBackupPath)).IsReadOnly);
    }

    [Fact]
    public async Task Game_running_blocks_with_no_change()
    {
        _h.Game.Running = true;
        var result = await _h.Apply.ApplyAsync(await _h.RequestAsync());
        Assert.Equal(OperationOutcome.Blocked, result.Outcome);
        Assert.Equal("error.game_running", result.Error!.Key);
        Assert.Equal(Fixtures.Profile, _h.Current);
        Assert.Empty(_h.Backups.List());
        Assert.Null(_h.Records.Record);
    }

    [Fact]
    public async Task File_changed_after_preview_blocks()
    {
        var request = await _h.RequestAsync();
        File.AppendAllText(_h.ProfilePath, "Extra 1\n");
        var result = await _h.Apply.ApplyAsync(request);
        Assert.Equal("error.plan_stale", result.Error!.Key);
        Assert.Equal(0, _h.Files.Writes);
    }

    [Fact]
    public async Task Read_only_file_blocks_and_is_not_forced()
    {
        var request = await _h.RequestAsync();
        File.SetAttributes(_h.ProfilePath, FileAttributes.ReadOnly);
        var result = await _h.Apply.ApplyAsync(request);
        Assert.Equal("error.file_readonly", result.Error!.Key);
        File.SetAttributes(_h.ProfilePath, FileAttributes.Normal);
        Assert.Equal(Fixtures.Profile, _h.Current);
    }

    [Fact]
    public async Task Locked_file_blocks()
    {
        var request = await _h.RequestAsync();
        using var hold = new FileStream(_h.ProfilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await _h.Apply.ApplyAsync(request);
        Assert.Equal("error.file_locked", result.Error!.Key);
    }

    [Fact]
    public async Task Cancel_before_writing_changes_nothing()
    {
        var request = await _h.RequestAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await _h.Apply.ApplyAsync(request, ct: cts.Token);
        Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(Fixtures.Profile, _h.Current);
    }

    [Fact]
    public async Task Write_failure_rolls_back_and_keeps_previous_record()
    {
        var previous = new ApplyRecord { SnapshotId = "old" };
        _h.Records.Record = previous;
        _h.Registry.Values[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = 0;

        // Files are written first, so fail the single target's first write and let rollback writes pass.
        _h.Files.FailWrite = _ => _h.Files.Writes == 1;
        var result = await _h.Apply.ApplyAsync(await _h.RequestAsync());

        Assert.Equal(OperationOutcome.FailedRolledBack, result.Outcome);
        Assert.Equal(Fixtures.Profile, _h.Current);
        Assert.Same(previous, _h.Records.Record);
        Assert.Equal(0, _h.Registry.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);
    }

    [Fact]
    public async Task Verification_failure_rolls_back()
    {
        // Simulates something corrupting the value between write and read-back.
        _h.Files.TamperOnWrite = (_, c) => _h.Files.Writes == 1
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(c).Replace("GstRender.VSyncMode 0", "GstRender.VSyncMode 1"))
            : c;
        var result = await _h.Apply.ApplyAsync(await _h.RequestAsync());

        Assert.Equal(OperationOutcome.FailedRolledBack, result.Outcome);
        Assert.Equal("error.verify_mismatch", result.Error!.Key);
        Assert.Equal(Fixtures.Profile, _h.Current);
        Assert.Null(_h.Records.Record);
    }

    [Fact]
    public async Task Failed_rollback_is_reported_as_such_never_as_success()
    {
        _h.Files.FailWrite = _ => true;
        var result = await _h.Apply.ApplyAsync(await _h.RequestAsync());
        Assert.Equal(OperationOutcome.FailedRollbackFailed, result.Outcome);
        Assert.Null(_h.Records.Record);
    }

    [Fact]
    public async Task Report_failure_after_write_rolls_back()
    {
        var request = await _h.RequestAsync();
        _h.Reports.Fail = true;
        var result = await _h.Apply.ApplyAsync(request);
        Assert.Equal(OperationOutcome.FailedRolledBack, result.Outcome);
        Assert.Equal(Fixtures.Profile, _h.Current);
        Assert.Null(_h.Records.Record);
    }

    [Fact]
    public async Task Blocked_windows_preferences_give_partial_success()
    {
        var request = await _h.RequestAsync();
        _h.Registry.FailWrites = true;
        var result = await _h.Apply.ApplyAsync(request);
        Assert.Equal(OperationOutcome.PartialSuccess, result.Outcome);
        Assert.Contains(result.Warnings, w => w.Key == "warn.pref_failed");
        Assert.Contains("GstRender.VSyncMode 0\n", _h.Current);
    }

    [Fact]
    public async Task Skipped_settings_are_reported()
    {
        File.WriteAllText(_h.ProfilePath, Fixtures.Profile.Replace("GstRender.ShadowQuality 2", "GstRender.ShadowQuality 9"));
        var result = await _h.Apply.ApplyAsync(await _h.RequestAsync());
        Assert.Contains("GstRender.ShadowQuality", result.Report!.SkippedSettings);
        Assert.Contains("GstRender.ShadowQuality 9", _h.Current);
    }

    [Fact]
    public async Task Progress_covers_stages_in_order()
    {
        var stages = new List<ApplyStage>();
        await _h.Apply.ApplyAsync(await _h.RequestAsync(), new Sync(p => stages.Add(p.Stage)));
        Assert.Equal(ApplyStage.Preflight, stages[0]);
        Assert.Equal(ApplyStage.Finished, stages[^1]);
        Assert.True(stages.IndexOf(ApplyStage.Backup) < stages.IndexOf(ApplyStage.WritingFiles));
    }

    private sealed class Sync(Action<ApplyProgress> a) : IProgress<ApplyProgress>
    {
        public void Report(ApplyProgress value) => a(value);
    }
}

public class BackupTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<RestorePoint> Snapshot() =>
        _h.Backups.CreateSnapshotAsync(RestorePointKind.PreApply, [_h.ProfilePath], [], default);

    [Fact]
    public async Task Snapshot_hash_matches_and_verifies()
    {
        var point = await Snapshot();
        Assert.Equal(Hashing.Sha256(File.ReadAllBytes(_h.ProfilePath)), point.Files[0].Sha256);
        Assert.True((await _h.Backups.VerifyAsync(point, default)).IsValid);
    }

    [Fact]
    public async Task Tampered_backup_fails_verification()
    {
        var point = await Snapshot();
        File.AppendAllText(Path.Combine(point.Directory, point.Files[0].RelativeBackupPath), "x");
        var result = await _h.Backups.VerifyAsync(point, default);
        Assert.False(result.IsValid);
        Assert.Equal("error.backup_hash", result.Problems[0].Key);
    }

    [Fact]
    public async Task Missing_backup_file_fails_verification()
    {
        var point = await Snapshot();
        File.Delete(Path.Combine(point.Directory, point.Files[0].RelativeBackupPath));
        Assert.Equal("error.backup_missing", (await _h.Backups.VerifyAsync(point, default)).Problems[0].Key);
    }

    [Fact]
    public async Task Path_traversal_in_manifest_is_refused()
    {
        var point = await Snapshot();
        var evil = point with { Files = [point.Files[0] with { RelativeBackupPath = @"..\..\..\evil" }] };
        Assert.False((await _h.Backups.VerifyAsync(evil, default)).IsValid);
    }

    [Fact]
    public async Task Interrupted_snapshot_leaves_no_listed_point()
    {
        await Assert.ThrowsAnyAsync<IOException>(() =>
            _h.Backups.CreateSnapshotAsync(RestorePointKind.PreApply, [_h.Dir.File("does-not-exist")], [], default));
        Assert.Empty(_h.Backups.List());
    }

    [Fact]
    public async Task Baseline_folder_that_cannot_be_read_is_never_replaced()
    {
        var dir = _h.Dir.File("app", "Backups", Harness.Machine, "OriginalBaseline");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "partial.bin"), "keep me");
        var ex = await Assert.ThrowsAsync<OptimizerException>(() => _h.Backups.EnsureBaselineAsync([_h.ProfilePath], [], default));
        Assert.Equal("error.baseline_unreadable", ex.UserMessage.Key);
        Assert.True(File.Exists(Path.Combine(dir, "partial.bin")));
    }
}

public class RestoreTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Restore_baseline_after_two_applies()
    {
        _h.Registry.Values[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = 0;
        await _h.Apply.ApplyAsync(await _h.RequestAsync());
        await _h.Apply.ApplyAsync(await _h.RequestAsync(_h.Plan(OptimizationMode.Quality)));
        Assert.NotEqual(Fixtures.Profile, _h.Current);

        var result = await _h.Restore.RestoreAsync(_h.Backups.GetBaseline()!, Harness.Machine);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal(Fixtures.Profile, _h.Current);
        Assert.Equal(0, _h.Registry.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);
        Assert.False(_h.Registry.Read(@"System\GameConfigStore", "GameDVR_Enabled").Exists);
        Assert.Null(_h.Records.Record);
    }

    [Fact]
    public async Task Restore_latest_snapshot_returns_previous_state()
    {
        await _h.Apply.ApplyAsync(await _h.RequestAsync(_h.Plan(OptimizationMode.Balanced)));
        var afterFirst = _h.Current;
        await _h.Apply.ApplyAsync(await _h.RequestAsync(_h.Plan(OptimizationMode.Competitive)));
        var snapshot = _h.Backups.Find(_h.Records.Record!.SnapshotId)!;

        await _h.Restore.RestoreAsync(snapshot, Harness.Machine);
        Assert.Equal(afterFirst, _h.Current);
    }

    [Fact]
    public async Task Tampered_backup_blocks_restore_before_any_write()
    {
        await _h.Apply.ApplyAsync(await _h.RequestAsync());
        var point = _h.Backups.Find(_h.Records.Record!.SnapshotId)!;
        File.AppendAllText(Path.Combine(point.Directory, point.Files[0].RelativeBackupPath), "x");
        var before = _h.Current;
        int writes = _h.Files.Writes;

        var result = await _h.Restore.RestoreAsync(point, Harness.Machine);

        Assert.Equal(OperationOutcome.Blocked, result.Outcome);
        Assert.Equal(before, _h.Current);
        Assert.Equal(writes, _h.Files.Writes);
    }

    [Fact]
    public async Task Other_machine_backup_is_refused()
    {
        await _h.Apply.ApplyAsync(await _h.RequestAsync());
        var result = await _h.Restore.RestoreAsync(_h.Backups.GetBaseline()!, "Machine_OTHER");
        Assert.Equal("error.other_machine", result.Error!.Key);
    }

    [Fact]
    public async Task Game_running_blocks_restore()
    {
        await _h.Apply.ApplyAsync(await _h.RequestAsync());
        _h.Game.Running = true;
        Assert.Equal("error.game_running", (await _h.Restore.RestoreAsync(_h.Backups.GetBaseline()!, Harness.Machine)).Error!.Key);
    }

    [Fact]
    public async Task Failed_restore_is_undone_from_safety_snapshot()
    {
        await _h.Apply.ApplyAsync(await _h.RequestAsync());
        var applied = _h.Current;
        int start = _h.Files.Writes;
        _h.Files.FailWrite = _ => _h.Files.Writes == start + 1;

        var result = await _h.Restore.RestoreAsync(_h.Backups.GetBaseline()!, Harness.Machine);

        Assert.Equal(OperationOutcome.FailedRolledBack, result.Outcome);
        Assert.Equal(applied, _h.Current);
        Assert.NotNull(_h.Records.Record);
    }

    [Fact]
    public async Task Manifest_pointing_outside_profile_names_is_refused()
    {
        var point = await _h.Backups.CreateSnapshotAsync(RestorePointKind.PreApply, [_h.ProfilePath], [], default);
        var evil = point with { Files = [point.Files[0] with { OriginalPath = _h.Dir.File("system.ini") }] };
        var result = await _h.Restore.RestoreAsync(evil, Harness.Machine) ;
        Assert.Equal(OperationOutcome.Blocked, result.Outcome);
    }
}

public class StatusTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly StatusVerifier _verifier;

    public StatusTests() => _verifier = new StatusVerifier(_h.Files, _h.Backups, _h.Registry, _h.Records, _h.Catalog);

    public void Dispose() => _h.Dispose();

    private async Task Applied() => await _h.Apply.ApplyAsync(await _h.RequestAsync());

    private void Edit(string from, string to) => File.WriteAllText(_h.ProfilePath, _h.Current.Replace(from, to));

    [Fact]
    public async Task No_record_is_not_success() =>
        Assert.Equal(OverallStatus.NoRecord, (await _verifier.CheckAsync()).Overall);

    [Fact]
    public async Task Fresh_apply_verifies()
    {
        await Applied();
        var s = await _verifier.CheckAsync();
        Assert.Equal(OverallStatus.Verified, s.Overall);
        Assert.All(s.Items, i => Assert.Equal(SettingState.AppliedCorrectly, i.State));
    }

    [Fact]
    public async Task External_change_is_detected()
    {
        await Applied();
        Edit("GstRender.VSyncMode 0", "GstRender.VSyncMode 1");
        var s = await _verifier.CheckAsync();
        Assert.Equal(OverallStatus.Drifted, s.Overall);
        var item = Assert.Single(s.Items, i => i.Key == "GstRender.VSyncMode");
        Assert.Equal((SettingState.ModifiedExternally, "0", "1"), (item.State, item.Expected, item.Actual));
    }

    [Fact]
    public async Task Compares_with_record_not_a_new_plan()
    {
        // A new plan would differ, but the status question is whether the last Apply still holds.
        await _h.Apply.ApplyAsync(await _h.RequestAsync(_h.Plan(OptimizationMode.Balanced)));
        Assert.Equal(OverallStatus.Verified, (await _verifier.CheckAsync()).Overall);
    }

    [Fact]
    public async Task Removed_key_is_missing()
    {
        await Applied();
        Edit("GstRender.VSyncMode 0\n", "");
        Assert.Contains((await _verifier.CheckAsync()).Items, i => i.Key == "GstRender.VSyncMode" && i.State == SettingState.MissingSetting);
    }

    [Fact]
    public async Task Value_outside_catalog_is_unsupported()
    {
        await Applied();
        Edit("GstRender.VSyncMode 0", "GstRender.VSyncMode 77");
        Assert.Contains((await _verifier.CheckAsync()).Items, i => i.Key == "GstRender.VSyncMode" && i.State == SettingState.UnsupportedSetting);
    }

    [Fact]
    public async Task Deleted_file_is_inaccessible()
    {
        await Applied();
        File.Delete(_h.ProfilePath);
        var s = await _verifier.CheckAsync();
        Assert.Equal(OverallStatus.Problem, s.Overall);
        Assert.Equal(SettingState.FileInaccessible, s.Items[0].State);
    }

    [Fact]
    public async Task Missing_snapshot_is_backup_unavailable()
    {
        await Applied();
        _h.Records.Record = _h.Records.Record! with { SnapshotId = "gone" };
        var s = await _verifier.CheckAsync();
        Assert.Equal(SettingState.BackupUnavailable, s.BackupState);
        Assert.Equal(OverallStatus.Problem, s.Overall);
    }

    [Fact]
    public async Task Changed_windows_preference_is_detected()
    {
        await Applied();
        _h.Registry.Values[@"System\GameConfigStore\GameDVR_Enabled"] = 1;
        Assert.Contains((await _verifier.CheckAsync()).Items, i => i.Key == "GameDVR_Enabled" && i.State == SettingState.ModifiedExternally);
    }

    [Fact]
    public async Task Status_never_writes()
    {
        await Applied();
        Edit("GstRender.VSyncMode 0", "GstRender.VSyncMode 1");
        var before = _h.Current;
        int writes = _h.Files.Writes;
        await _verifier.CheckAsync();
        Assert.Equal(before, _h.Current);
        Assert.Equal(writes, _h.Files.Writes);
    }
}
