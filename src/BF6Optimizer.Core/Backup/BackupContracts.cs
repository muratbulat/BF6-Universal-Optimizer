using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.Core.Backup;

public enum RestorePointKind { OriginalBaseline, PreApply, PreRestore }

public sealed record BackupFile(string OriginalPath, string RelativeBackupPath, string Sha256, long SizeBytes);

public sealed record RestorePoint
{
    public required string Id { get; init; }
    public RestorePointKind Kind { get; init; }
    public DateTimeOffset Created { get; init; }
    public string AppVersion { get; init; } = "";
    public string MachineKey { get; init; } = "";
    /// <summary>Created by the v1.4.0 script. Read-only for this application; restored, never rewritten.</summary>
    public bool IsLegacy { get; init; }
    public required string Directory { get; init; }
    public IReadOnlyList<BackupFile> Files { get; init; } = [];
    public IReadOnlyList<RegistryValueState> Registry { get; init; } = [];
}

public sealed record BackupVerification(bool IsValid, IReadOnlyList<Msg> Problems);

public interface IBackupStore
{
    RestorePoint? GetBaseline();
    /// <summary>Creates the baseline once. An existing baseline is returned untouched.</summary>
    Task<RestorePoint> EnsureBaselineAsync(IReadOnlyList<string> paths, IReadOnlyList<RegistryValueState> registry, CancellationToken ct);
    Task<RestorePoint> CreateSnapshotAsync(RestorePointKind kind, IReadOnlyList<string> paths, IReadOnlyList<RegistryValueState> registry, CancellationToken ct);
    IReadOnlyList<RestorePoint> List();
    RestorePoint? Find(string id);
    Task<BackupVerification> VerifyAsync(RestorePoint point, CancellationToken ct);
    Task<byte[]> ReadFileAsync(RestorePoint point, BackupFile file, CancellationToken ct);
}
