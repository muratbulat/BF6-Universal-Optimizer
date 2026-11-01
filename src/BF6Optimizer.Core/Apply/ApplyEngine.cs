using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Planning;
using Microsoft.Extensions.Logging;

namespace BF6Optimizer.Core.Apply;

/// <summary>
/// The transactional Apply: preflight, backup, write, verify twice, record. Any failure after the first write
/// rolls back and restores the previous success record, so a failed run can never look like a good one.
/// </summary>
public sealed class ApplyEngine(
    IProfileFileStore files,
    IBackupStore backups,
    IWindowsPreferenceStore preferences,
    IGameProcessMonitor game,
    IApplyRecordStore records,
    IReportStore reports,
    RestoreEngine restorer,
    IClock clock,
    ApplyOptions options,
    ILogger<ApplyEngine> logger)
{
    private sealed class Run
    {
        public ApplyStage Stage = ApplyStage.Preflight;
        public RestorePoint? Snapshot;
        public readonly List<Msg> Warnings = [];
        public readonly List<ReportFile> Files = [];
        public readonly List<ExpectedFile> Expected = [];
        public readonly List<ExpectedPreference> Preferences = [];
        public bool ExternalChange;
    }

    public async Task<OperationResult> ApplyAsync(ApplyRequest request, IProgress<ApplyProgress>? progress = null, CancellationToken ct = default)
    {
        var run = new Run();
        void Report(ApplyStage stage, int percent, Msg? detail = null)
        {
            run.Stage = stage;
            progress?.Report(new ApplyProgress(stage, percent, detail));
        }

        logger.LogInformation("Apply started: mode {Mode}, {Count} target files", request.Plan.ResolvedMode, request.Targets.Count);
        Report(ApplyStage.Preflight, 5);

        try
        {
            if (await PreflightAsync(request, run, ct).ConfigureAwait(false) is { } block)
                return await FinishAsync(request, run, OperationOutcome.Blocked, block, null).ConfigureAwait(false);

            Report(ApplyStage.Backup, 15);
            var registry = WindowsPreferences.Capture(preferences);
            await backups.EnsureBaselineAsync(request.AllProfiles, registry, ct).ConfigureAwait(false);
            run.Snapshot = await backups.CreateSnapshotAsync(RestorePointKind.PreApply, request.AllProfiles, registry, ct).ConfigureAwait(false);
            var verified = await backups.VerifyAsync(run.Snapshot, ct).ConfigureAwait(false);
            if (!verified.IsValid)
                return await FinishAsync(request, run, OperationOutcome.Blocked, new Msg("error.backup_invalid"), null).ConfigureAwait(false);
            logger.LogInformation("Pre-Apply snapshot {Id} verified", run.Snapshot.Id);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(request, run, OperationOutcome.Cancelled, null, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apply stopped before any change");
            var error = (ex as OptimizerException)?.UserMessage ?? new Msg("error.backup_failed");
            return await FinishAsync(request, run, OperationOutcome.Blocked, error, ex.Message).ConfigureAwait(false);
        }

        // From here on the transaction runs to a defined end: cancellation would leave mixed state.
        var previous = LoadPreviousRecord(run);
        try
        {
            Report(ApplyStage.WritingFiles, 35);
            int done = 0;
            foreach (var target in request.Targets)
            {
                await WriteAndVerifyAsync(target, run).ConfigureAwait(false);
                Report(ApplyStage.WritingFiles, 35 + 30 * ++done / Math.Max(1, request.Targets.Count));
            }

            Report(ApplyStage.WindowsPreferences, 70);
            ApplyWindowsPreferences(request.Plan, run);

            Report(ApplyStage.Verifying, 80);
            await Task.Delay(options.SettleDelay).ConfigureAwait(false);
            await SettledVerifyAsync(request, run).ConfigureAwait(false);

            Report(ApplyStage.SavingRecord, 92);
            var outcome = run.Warnings.Any(w => w.Key == "warn.pref_failed") ? OperationOutcome.PartialSuccess : OperationOutcome.Success;
            var result = await FinishAsync(request, run, outcome, null, null, requireReport: true).ConfigureAwait(false);
            records.Save(new ApplyRecord
            {
                AppVersion = AppInfo.Version,
                Timestamp = clock.Now,
                MachineKey = request.MachineKey,
                SnapshotId = run.Snapshot!.Id,
                Mode = request.Plan.ResolvedMode,
                FpsCap = request.Plan.FpsCap,
                Files = run.Expected,
                Preferences = run.Preferences,
            });
            Report(ApplyStage.Finished, 100);
            logger.LogInformation("Apply finished: {Outcome}", outcome);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Apply failed at {Stage}; rolling back", run.Stage);
            var error = (ex as OptimizerException)?.UserMessage ?? new Msg("error.apply_failed");
            if (run.ExternalChange) run.Warnings.Add(new Msg("warn.external_overwrite"));
            Report(ApplyStage.RollingBack, 95);

            OperationOutcome outcome;
            try
            {
                await restorer.RestoreCoreAsync(run.Snapshot!, CancellationToken.None).ConfigureAwait(false);
                outcome = OperationOutcome.FailedRolledBack;
                logger.LogInformation("Rollback verified");
            }
            catch (Exception rollbackEx)
            {
                logger.LogCritical(rollbackEx, "Rollback failed");
                outcome = OperationOutcome.FailedRollbackFailed;
            }

            RestorePreviousRecord(previous, run);
            Report(ApplyStage.Finished, 100);
            return await FinishAsync(request, run, outcome, error, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task<Msg?> PreflightAsync(ApplyRequest request, Run run, CancellationToken ct)
    {
        if (game.IsGameRunning()) return new Msg("error.game_running");
        if (request.Targets.Count == 0) return new Msg("error.no_targets");

        foreach (var name in game.RunningLaunchers())
            run.Warnings.Add(new Msg("warn.launcher_running", name));

        foreach (var target in request.Targets)
        {
            switch (files.Inspect(target.Path))
            {
                case FileAccessState.Missing: return new Msg("error.file_missing", target.Path);
                case FileAccessState.ReadOnly: return new Msg("error.file_readonly", target.Path);
                case FileAccessState.Locked: return new Msg("error.file_locked", target.Path);
                case FileAccessState.Denied: return new Msg("error.file_denied", target.Path);
            }

            // The user confirmed a preview of specific bytes; anything else needs a new preview.
            var (_, sha) = await ProfileLoader.LoadAsync(files, target.Path, ct).ConfigureAwait(false);
            if (!Hashing.Same(sha, target.SourceSha256)) return new Msg("error.plan_stale", target.Path);
        }
        return null;
    }

    private async Task WriteAndVerifyAsync(FileChangeSet target, Run run)
    {
        var (doc, before) = await ProfileLoader.LoadAsync(files, target.Path, CancellationToken.None).ConfigureAwait(false);
        var expected = target.Expected;
        var changes = target.Changes.Where(c => c.Status == ChangeStatus.Change).ToDictionary(c => c.Key, c => c.After, StringComparer.Ordinal);

        if (changes.Count > 0)
            await files.WriteAtomicAsync(target.Path, doc.WithChanges(changes), CancellationToken.None).ConfigureAwait(false);

        var (after, afterSha) = await ProfileLoader.LoadAsync(files, target.Path, CancellationToken.None).ConfigureAwait(false);
        if (FirstMismatch(after, expected) is { } bad)
            throw new OptimizerException(new Msg("error.verify_mismatch", target.Path, bad));

        run.Expected.Add(new ExpectedFile(target.Path, afterSha, new Dictionary<string, string>(expected)));
        run.Files.Add(new ReportFile(target.Path, before, afterSha, expected.Count, changes.Count, true));
        logger.LogInformation("Wrote {Changed} of {Checked} settings to {File}", changes.Count, expected.Count, Path.GetFileName(target.Path));
    }

    private void ApplyWindowsPreferences(OptimizationPlan plan, Run run)
    {
        foreach (var p in plan.WindowsPreferences)
        {
            if (!WindowsPreferences.IsAllowed(p.Path, p.Name)) continue;
            try
            {
                preferences.Write(p.Path, p.Name, p.Value);
                if (preferences.Read(p.Path, p.Name) is not { Exists: true } state || state.Value != p.Value)
                    throw new InvalidOperationException("Value did not read back.");
                run.Preferences.Add(new ExpectedPreference(p.Path, p.Name, p.Value));
            }
            catch (Exception ex)
            {
                // Policy can legitimately forbid these; the game settings still stand.
                logger.LogWarning(ex, "Windows preference {Name} not applied", p.Name);
                run.Warnings.Add(new Msg("warn.pref_failed", p.Id));
            }
        }
    }

    private async Task SettledVerifyAsync(ApplyRequest request, Run run)
    {
        foreach (var expected in run.Expected)
        {
            var (doc, sha) = await ProfileLoader.LoadAsync(files, expected.Path, CancellationToken.None).ConfigureAwait(false);
            run.ExternalChange |= !Hashing.Same(sha, expected.Sha256);
            if (FirstMismatch(doc, expected.Settings) is { } bad)
                throw new OptimizerException(new Msg("error.verify_mismatch", expected.Path, bad));
        }
        if (run.ExternalChange) run.Warnings.Add(new Msg("warn.file_changed_after_write"));
    }

    private static string? FirstMismatch(ProfileDocument doc, IReadOnlyDictionary<string, string> expected)
    {
        foreach (var (key, value) in expected)
        {
            var actual = doc.GetAll(key).ToList();
            if (actual.Count == 0 || actual.Any(a => a != value)) return key;
        }
        return null;
    }

    private ApplyRecord? LoadPreviousRecord(Run run)
    {
        try { return records.Load(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Previous apply record unreadable");
            run.Warnings.Add(new Msg("warn.record_unreadable"));
            return null;
        }
    }

    private void RestorePreviousRecord(ApplyRecord? previous, Run run)
    {
        try
        {
            if (previous is null || previous.IsLegacy) records.Clear();
            else records.Save(previous);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore the previous apply record");
            run.Warnings.Add(new Msg("warn.record_unreadable"));
        }
    }

    private async Task<OperationResult> FinishAsync(
        ApplyRequest request, Run run, OperationOutcome outcome, Msg? error, string? detail, bool requireReport = false)
    {
        var report = new OperationReport
        {
            Action = "Apply",
            Timestamp = clock.Now,
            AppVersion = AppInfo.Version,
            Outcome = outcome,
            Mode = request.Plan.ResolvedMode.ToString(),
            RestorePointId = run.Snapshot?.Id,
            Stage = run.Stage.ToString(),
            Error = error,
            ErrorDetail = detail,
            Warnings = run.Warnings,
            Files = run.Files,
            SkippedSettings = request.Targets
                .SelectMany(t => t.Changes.Where(c => c.Status is not (ChangeStatus.Change or ChangeStatus.NoChange)))
                .Select(c => c.Key).Distinct().ToList(),
        };

        try { await reports.SaveAsync(report).ConfigureAwait(false); }
        catch (Exception ex) when (!requireReport)
        {
            logger.LogWarning(ex, "Could not save apply report");
            run.Warnings.Add(new Msg("warn.report_failed"));
        }

        return new OperationResult
        {
            Outcome = outcome,
            Error = error,
            Warnings = run.Warnings,
            RestorePointId = run.Snapshot?.Id,
            Report = report,
        };
    }
}
