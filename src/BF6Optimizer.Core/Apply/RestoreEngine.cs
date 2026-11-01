using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Planning;
using Microsoft.Extensions.Logging;

namespace BF6Optimizer.Core.Apply;

public sealed class RestoreEngine(
    IProfileFileStore files,
    IBackupStore backups,
    IWindowsPreferenceStore preferences,
    IGameProcessMonitor game,
    IApplyRecordStore records,
    IReportStore reports,
    IClock clock,
    ILogger<RestoreEngine> logger)
{
    public async Task<OperationResult> RestoreAsync(RestorePoint point, string machineKey, CancellationToken ct = default)
    {
        Msg? block = await PreflightAsync(point, machineKey, ct).ConfigureAwait(false);
        if (block is not null)
        {
            logger.LogWarning("Restore of {Id} blocked: {Reason}", point.Id, block);
            return await FinishAsync(point, OperationOutcome.Blocked, block, null, []).ConfigureAwait(false);
        }

        // A safety snapshot lets a half-finished restore be undone.
        var existing = point.Files.Select(f => f.OriginalPath)
            .Where(p => files.Inspect(p) != FileAccessState.Missing).ToList();
        var safety = await backups.CreateSnapshotAsync(
            RestorePointKind.PreRestore, existing, WindowsPreferences.Capture(preferences), ct).ConfigureAwait(false);

        try
        {
            var written = await RestoreCoreAsync(point, CancellationToken.None).ConfigureAwait(false);
            records.Clear();
            logger.LogInformation("Restore of {Id} verified ({Count} files)", point.Id, written.Count);
            return await FinishAsync(point, OperationOutcome.Success, null, null, written).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore of {Id} failed; undoing", point.Id);
            var error = (ex as OptimizerException)?.UserMessage ?? new Msg("error.restore_failed");
            try
            {
                await RestoreCoreAsync(safety, CancellationToken.None).ConfigureAwait(false);
                return await FinishAsync(point, OperationOutcome.FailedRolledBack, error, ex.Message, []).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                logger.LogCritical(rollbackEx, "Undo of failed restore failed");
                return await FinishAsync(point, OperationOutcome.FailedRollbackFailed, error, ex.Message, []).ConfigureAwait(false);
            }
        }
    }

    private async Task<Msg?> PreflightAsync(RestorePoint point, string machineKey, CancellationToken ct)
    {
        if (game.IsGameRunning()) return new Msg("error.game_running");
        if (!string.Equals(point.MachineKey, machineKey, StringComparison.OrdinalIgnoreCase))
            return new Msg("error.other_machine");

        var verification = await backups.VerifyAsync(point, ct).ConfigureAwait(false);
        if (!verification.IsValid) return verification.Problems.FirstOrDefault() ?? new Msg("error.backup_invalid");

        foreach (var f in point.Files)
        {
            if (!ProfileNames.IsProfile(f.OriginalPath)) return new Msg("error.backup_invalid");
            switch (files.Inspect(f.OriginalPath))
            {
                case FileAccessState.ReadOnly: return new Msg("error.file_readonly", f.OriginalPath);
                case FileAccessState.Locked: return new Msg("error.file_locked", f.OriginalPath);
                case FileAccessState.Denied: return new Msg("error.file_denied", f.OriginalPath);
            }
        }
        return null;
    }

    /// <summary>
    /// Writes a verified restore point back. Also used for automatic rollback, so it re-checks every hash
    /// and only touches allowlisted per-user values; HKLM entries in old v1.3 backups are ignored.
    /// </summary>
    internal async Task<IReadOnlyList<ReportFile>> RestoreCoreAsync(RestorePoint point, CancellationToken ct)
    {
        var result = new List<ReportFile>();
        foreach (var f in point.Files)
        {
            if (!ProfileNames.IsProfile(f.OriginalPath)) throw new OptimizerException(new Msg("error.backup_invalid"));
            var content = await backups.ReadFileAsync(point, f, ct).ConfigureAwait(false);
            if (!Hashing.Same(Hashing.Sha256(content), f.Sha256))
                throw new OptimizerException(new Msg("error.backup_hash", f.OriginalPath));

            await files.WriteAtomicAsync(f.OriginalPath, content, ct).ConfigureAwait(false);

            var after = Hashing.Sha256(await files.ReadAsync(f.OriginalPath, ct).ConfigureAwait(false));
            if (!Hashing.Same(after, f.Sha256))
                throw new OptimizerException(new Msg("error.restore_verify", f.OriginalPath));
            result.Add(new ReportFile(f.OriginalPath, null, after, 0, 0, true));
        }

        foreach (var r in point.Registry.Where(r => WindowsPreferences.IsAllowed(r.Path, r.Name)))
        {
            if (r is { Exists: true, Value: { } value }) preferences.Write(r.Path, r.Name, value);
            else preferences.Remove(r.Path, r.Name);
        }
        return result;
    }

    private async Task<OperationResult> FinishAsync(
        RestorePoint point, OperationOutcome outcome, Msg? error, string? detail, IReadOnlyList<ReportFile> written)
    {
        var report = new OperationReport
        {
            Action = point.Kind == RestorePointKind.OriginalBaseline ? "RestoreOriginal" : "RestoreSnapshot",
            Timestamp = clock.Now,
            AppVersion = AppInfo.Version,
            Outcome = outcome,
            RestorePointId = point.Id,
            Error = error,
            ErrorDetail = detail,
            Files = written,
        };
        try { await reports.SaveAsync(report).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not save restore report"); }
        return new OperationResult { Outcome = outcome, Error = error, RestorePointId = point.Id, Report = report };
    }
}
