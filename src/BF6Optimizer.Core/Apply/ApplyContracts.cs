using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.Core.Apply;

public enum FileAccessState { Ok, Missing, ReadOnly, Locked, Denied }

public interface IProfileFileStore
{
    FileAccessState Inspect(string path);
    Task<byte[]> ReadAsync(string path, CancellationToken ct);
    /// <summary>Temp file in the same folder, flushed to disk, then atomically swapped in.</summary>
    Task WriteAtomicAsync(string path, byte[] content, CancellationToken ct);
}

public interface IGameProcessMonitor
{
    bool IsGameRunning();
    /// <summary>Launchers that can rewrite profiles or sync them through the cloud.</summary>
    IReadOnlyList<string> RunningLaunchers();
}

public sealed record ExpectedFile(string Path, string Sha256, Dictionary<string, string> Settings);

public sealed record ExpectedPreference(string Path, string Name, int Value);

/// <summary>Written only after verification. Status compares against this, not against a fresh plan.</summary>
public sealed record ApplyRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string MachineKey { get; init; } = "";
    public string SnapshotId { get; init; } = "";
    public OptimizationMode Mode { get; init; }
    public int? FpsCap { get; init; }
    public bool IsLegacy { get; init; }
    public IReadOnlyList<ExpectedFile> Files { get; init; } = [];
    public IReadOnlyList<ExpectedPreference> Preferences { get; init; } = [];
}

public interface IApplyRecordStore
{
    ApplyRecord? Load();
    void Save(ApplyRecord record);
    void Clear();
}

public enum OperationOutcome
{
    Success, PartialSuccess, FailedRolledBack, FailedRollbackFailed, Blocked, Cancelled,
}

public sealed record OperationReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Action { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string AppVersion { get; init; } = "";
    public OperationOutcome Outcome { get; init; }
    public string? Mode { get; init; }
    public string? RestorePointId { get; init; }
    public string? Stage { get; init; }
    public Msg? Error { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<Msg> Warnings { get; init; } = [];
    public IReadOnlyList<ReportFile> Files { get; init; } = [];
    public IReadOnlyList<string> SkippedSettings { get; init; } = [];
}

public sealed record ReportFile(string Path, string? BeforeSha256, string? AfterSha256, int Checked, int Changed, bool Verified);

public interface IReportStore
{
    Task<string> SaveAsync(OperationReport report, CancellationToken ct = default);
    IReadOnlyList<OperationReport> List();
}

public enum ApplyStage { Preflight, Backup, WritingFiles, WindowsPreferences, Verifying, SavingRecord, RollingBack, Finished }

public sealed record ApplyProgress(ApplyStage Stage, int Percent, Msg? Detail = null);

public sealed record ApplyRequest
{
    public required OptimizationPlan Plan { get; init; }
    public required string MachineKey { get; init; }
    public required IReadOnlyList<FileChangeSet> Targets { get; init; }
    /// <summary>All detected profiles, compatible or not: every one is backed up.</summary>
    public required IReadOnlyList<string> AllProfiles { get; init; }
}

public sealed record OperationResult
{
    public OperationOutcome Outcome { get; init; }
    public Msg? Error { get; init; }
    public IReadOnlyList<Msg> Warnings { get; init; } = [];
    public string? RestorePointId { get; init; }
    public OperationReport? Report { get; init; }
}

public sealed class ApplyOptions
{
    /// <summary>Second verification delay, catching launchers or sync clients that rewrite the file.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(750);
}

public static class ProfileNames
{
    public static readonly IReadOnlyList<string> All = ["PROFSAVE_profile", "PROFSAVEbf6mp_profile", "PROFSAVEbf6sp_profile"];

    public static bool IsProfile(string path) =>
        All.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
}

public static class ProfileLoader
{
    public static async Task<(ProfileDocument Doc, string Sha256)> LoadAsync(IProfileFileStore store, string path, CancellationToken ct)
    {
        var bytes = await store.ReadAsync(path, ct).ConfigureAwait(false);
        return (ProfileDocument.Parse(bytes), Hashing.Sha256(bytes));
    }
}
