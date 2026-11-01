using System.Text.Json;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Planning;
using Microsoft.Extensions.Logging;

namespace BF6Optimizer.Infrastructure.Storage;

/// <summary>
/// Layout: Backups\Machine_x\OriginalBaseline and Snapshots\&lt;id&gt;, the same tree v1.4.0 uses.
/// Point folders are built under a temp name, verified and then renamed, so a folder that exists is complete.
/// Folders with a v1.4.0 State.json are read, listed and restored, and never rewritten.
/// </summary>
public sealed class FileBackupStore(
    AppPaths paths,
    Func<string> machineKey,
    Func<IReadOnlyList<string>> externalRoots,
    IClock clock,
    ILogger<FileBackupStore> logger) : IBackupStore
{
    private const string ManifestName = "Manifest.json";
    private const string LegacyStateName = "State.json";
    private const string LegacyMapName = "GameConfigMap.json";
    private const string BaselineFolder = "OriginalBaseline";

    private sealed record Manifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string Id { get; init; } = "";
        public RestorePointKind Kind { get; init; }
        public DateTimeOffset Created { get; init; }
        public string AppVersion { get; init; } = "";
        public string MachineKey { get; init; } = "";
        public List<BackupFile> Files { get; init; } = [];
        public List<RegistryValueState> Registry { get; init; } = [];
    }

    private string MachineDir => Path.Combine(paths.Backups, machineKey());
    private string BaselineDir => Path.Combine(MachineDir, BaselineFolder);
    private string SnapshotsDir => Path.Combine(MachineDir, "Snapshots");

    public RestorePoint? GetBaseline() => List().FirstOrDefault(p => p.Kind == RestorePointKind.OriginalBaseline);

    public async Task<RestorePoint> EnsureBaselineAsync(IReadOnlyList<string> paths, IReadOnlyList<RegistryValueState> registry, CancellationToken ct)
    {
        if (GetBaseline() is { } existing) return existing;

        // A folder that cannot be read as a baseline is still never replaced.
        if (Directory.Exists(BaselineDir)) throw new OptimizerException(new Msg("error.baseline_unreadable", BaselineDir));

        var point = await CreateAsync(RestorePointKind.OriginalBaseline, BaselineFolder, BaselineDir, paths, registry, ct).ConfigureAwait(false);
        foreach (var file in Directory.EnumerateFiles(point.Directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        logger.LogInformation("Original baseline created with {Count} files", point.Files.Count);
        return point;
    }

    public Task<RestorePoint> CreateSnapshotAsync(RestorePointKind kind, IReadOnlyList<string> paths, IReadOnlyList<RegistryValueState> registry, CancellationToken ct)
    {
        var id = $"{clock.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}";
        return CreateAsync(kind, id, Path.Combine(SnapshotsDir, id), paths, registry, ct);
    }

    private async Task<RestorePoint> CreateAsync(
        RestorePointKind kind, string id, string target, IReadOnlyList<string> sources, IReadOnlyList<RegistryValueState> registry, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(parent, ".tmp_" + Guid.NewGuid().ToString("N"));
        var gameConfigs = Path.Combine(temp, "GameConfigs");
        Directory.CreateDirectory(gameConfigs);
        try
        {
            var files = new List<BackupFile>();
            int index = 0;
            foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var name = $"{++index:D2}_{Path.GetFileName(source)}";
                var bytes = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(gameConfigs, name), bytes, ct).ConfigureAwait(false);
                files.Add(new BackupFile(source, Path.Combine("GameConfigs", name), Hashing.Sha256(bytes), bytes.Length));
            }

            var manifest = new Manifest
            {
                Id = id,
                Kind = kind,
                Created = clock.Now,
                AppVersion = AppInfo.Version,
                MachineKey = machineKey(),
                Files = files,
                Registry = registry.ToList(),
            };
            JsonFile.Write(Path.Combine(temp, ManifestName), manifest);

            var point = ToPoint(manifest, temp);
            var check = await VerifyAsync(point, ct).ConfigureAwait(false);
            if (!check.IsValid) throw new OptimizerException(new Msg("error.backup_invalid"));

            Directory.Move(temp, target);
            return point with { Directory = target };
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    public IReadOnlyList<RestorePoint> List()
    {
        var result = new List<RestorePoint>();
        Collect(MachineDir, "", result);

        foreach (var root in externalRoots())
            if (LegacyFormat.MachineDirectory(root, machineKey()) is { } dir)
                Collect(dir, "ext" + Hashing.Sha256(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant()))[..8] + "_", result);

        return result
            .OrderBy(p => p.Kind == RestorePointKind.OriginalBaseline ? 0 : 1)
            .ThenByDescending(p => p.Created)
            .ToList();
    }

    private void Collect(string machineDir, string idPrefix, List<RestorePoint> into)
    {
        if (!Directory.Exists(machineDir)) return;
        var baseline = Path.Combine(machineDir, BaselineFolder);
        if (TryLoad(baseline, idPrefix) is { } b) into.Add(b);

        var snapshots = Path.Combine(machineDir, "Snapshots");
        if (!Directory.Exists(snapshots)) return;
        foreach (var dir in Directory.EnumerateDirectories(snapshots))
            if (!Path.GetFileName(dir).StartsWith(".tmp_", StringComparison.Ordinal) && TryLoad(dir, idPrefix) is { } p)
                into.Add(p);
    }

    private RestorePoint? TryLoad(string dir, string idPrefix)
    {
        try
        {
            var manifestPath = Path.Combine(dir, ManifestName);
            if (File.Exists(manifestPath))
            {
                var m = Json.Deserialize<Manifest>(File.ReadAllText(manifestPath));
                return m is null ? null : ToPoint(m, dir) with { Id = idPrefix + m.Id };
            }
            return File.Exists(Path.Combine(dir, LegacyStateName)) ? LoadLegacy(dir, idPrefix) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Unreadable restore point skipped: {Folder}", Path.GetFileName(dir));
            return null;
        }
    }

    private static RestorePoint ToPoint(Manifest m, string dir) => new()
    {
        Id = m.Id,
        Kind = m.Kind,
        Created = m.Created,
        AppVersion = m.AppVersion,
        MachineKey = m.MachineKey,
        Directory = dir,
        Files = m.Files,
        Registry = m.Registry,
    };

    private static RestorePoint LoadLegacy(string dir, string idPrefix)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, LegacyStateName)));
        var root = state.RootElement;

        var files = new List<BackupFile>();
        var mapPath = Path.Combine(dir, LegacyMapName);
        if (File.Exists(mapPath))
        {
            using var map = JsonDocument.Parse(File.ReadAllText(mapPath));
            foreach (var entry in LegacyFormat.Items(map.RootElement))
            {
                var original = LegacyFormat.Str(entry, "Original");
                var backup = LegacyFormat.Str(entry, "Backup");
                var sha = LegacyFormat.Str(entry, "SHA256");
                if (original is null || backup is null || sha is null) continue;

                // Stored paths are absolute and go stale when the package moves; only the name is trusted.
                var relative = Path.Combine("GameConfigs", Path.GetFileName(backup.Replace('/', '\\')));
                var full = Path.Combine(dir, relative);
                files.Add(new BackupFile(original, relative, sha, File.Exists(full) ? new FileInfo(full).Length : 0));
            }
        }

        var registry = new List<RegistryValueState>();
        if (root.TryGetProperty("Registry", out var reg))
            foreach (var r in LegacyFormat.Items(reg))
            {
                // v1.3.x HKLM/HAGS entries normalize to null and are dropped here.
                var path = WindowsPreferences.NormalizeLegacyPath(LegacyFormat.Str(r, "Path"));
                var name = LegacyFormat.Str(r, "Name");
                if (path is null || name is null) continue;
                bool exists = r.TryGetProperty("Exists", out var ex) && ex.ValueKind == JsonValueKind.True;
                int? value = r.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
                registry.Add(new RegistryValueState(path, name, exists, value));
            }

        var isBaseline = LegacyFormat.Str(root, "Kind") == BaselineFolder;
        return new RestorePoint
        {
            Id = idPrefix + Path.GetFileName(dir),
            Kind = isBaseline ? RestorePointKind.OriginalBaseline : RestorePointKind.PreApply,
            Created = DateTimeOffset.TryParse(LegacyFormat.Str(root, "Created"), out var c) ? c : Directory.GetCreationTime(dir),
            AppVersion = LegacyFormat.Str(root, "ScriptVersion") ?? "1.x",
            MachineKey = LegacyFormat.Str(root, "MachineKey") ?? "",
            IsLegacy = true,
            Directory = dir,
            Files = files,
            Registry = registry,
        };
    }

    public RestorePoint? Find(string id) => List().FirstOrDefault(p => p.Id == id);

    public async Task<BackupVerification> VerifyAsync(RestorePoint point, CancellationToken ct)
    {
        var problems = new List<Msg>();
        foreach (var file in point.Files)
        {
            try
            {
                var bytes = await ReadFileAsync(point, file, ct).ConfigureAwait(false);
                if (!Hashing.Same(Hashing.Sha256(bytes), file.Sha256))
                    problems.Add(new Msg("error.backup_hash", file.OriginalPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OptimizerException)
            {
                problems.Add(new Msg("error.backup_missing", file.OriginalPath));
            }
        }
        return new BackupVerification(problems.Count == 0, problems);
    }

    public Task<byte[]> ReadFileAsync(RestorePoint point, BackupFile file, CancellationToken ct)
    {
        var root = Path.GetFullPath(point.Directory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, file.RelativeBackupPath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new OptimizerException(new Msg("error.backup_invalid"));
        return File.ReadAllBytesAsync(full, ct);
    }
}
