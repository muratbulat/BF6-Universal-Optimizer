using System.Text.Json;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Network;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Profiles;

namespace BF6Optimizer.Infrastructure.Storage;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Language { get; set; } = "tr";
    public string Theme { get; set; } = "Dark";
    public bool WelcomeCompleted { get; set; }
    public string? ManualProfileDirectory { get; set; }
    public string? SelectedGpuId { get; set; }
    public string? SelectedDisplay { get; set; }
    public List<string> ExternalBackupRoots { get; set; } = [];
    public OptimizationPreferences CustomPreferences { get; set; } = new() { BaseMode = OptimizationMode.Balanced };
    public SavedWindow? Window { get; set; }
    public string? NetworkPreset { get; set; }
    public LatencyThresholds NetworkThresholds { get; set; } = new();
}

public sealed class AppSettingsStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Settings, "settings.json");

    public AppSettings Load()
    {
        try { return JsonFile.Read<AppSettings>(FilePath) ?? new AppSettings(); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged settings file should not stop the app; keep it for inspection.
            try { File.Move(FilePath, FilePath + ".corrupt", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) => JsonFile.Write(FilePath, settings);
}

public sealed class JsonProfileStore(AppPaths paths) : IProfileStore
{
    public IReadOnlyList<OptimizationProfile> LoadUserProfiles()
    {
        if (!Directory.Exists(paths.Profiles)) return [];
        var result = new List<OptimizationProfile>();
        foreach (var file in Directory.EnumerateFiles(paths.Profiles, "*.json"))
        {
            try
            {
                if (JsonFile.Read<OptimizationProfile>(file) is { } p) result.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(OptimizationProfile profile) => JsonFile.Write(FileFor(profile.Id), profile);

    public void Delete(string id)
    {
        var f = FileFor(id);
        if (File.Exists(f)) File.Delete(f);
    }

    private string FileFor(string id)
    {
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') throw new ArgumentException("Invalid profile id.", nameof(id));
        return Path.Combine(paths.Profiles, id + ".json");
    }
}

public sealed class JsonReportStore(AppPaths paths, Func<string> machineKey, IClock clock) : IReportStore
{
    private string History => Path.Combine(paths.Reports, machineKey(), "History");

    public string Folder => History;

    public Task<string> SaveAsync(OperationReport report, CancellationToken ct = default)
    {
        var name = $"{clock.Now:yyyyMMdd_HHmmss_fff}_{report.Action}.json";
        var path = Path.Combine(History, name);
        JsonFile.Write(path, report);
        return Task.FromResult(path);
    }

    public IReadOnlyList<OperationReport> List()
    {
        if (!Directory.Exists(History)) return [];
        var result = new List<OperationReport>();
        foreach (var file in Directory.EnumerateFiles(History, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(200))
        {
            try
            {
                // v1.4.0 reports use another shape and have no AppVersion; they stay on disk, unlisted.
                if (JsonFile.Read<OperationReport>(file) is { AppVersion.Length: > 0 } r) result.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return result;
    }
}

/// <summary>Holds the last verified Apply. A v1.4.0 marker is read as a fallback and never rewritten.</summary>
public sealed class JsonApplyRecordStore(AppPaths paths, Func<string> machineKey, Func<IReadOnlyList<string>> externalRoots) : IApplyRecordStore
{
    private string Dir => Path.Combine(paths.Data, machineKey());
    private string RecordPath => Path.Combine(Dir, "LastApply.json");
    private string ClearedPath => Path.Combine(Dir, "LastApply.cleared");

    public ApplyRecord? Load()
    {
        if (File.Exists(RecordPath))
            return Json.Deserialize<ApplyRecord>(File.ReadAllText(RecordPath))
                ?? throw new InvalidDataException("Empty apply record.");
        if (File.Exists(ClearedPath)) return null;

        foreach (var root in new[] { paths.Backups }.Concat(externalRoots()))
        {
            var marker = LegacyFormat.MachineDirectory(root, machineKey()) is { } dir ? Path.Combine(dir, "LastSuccessfulApply.json") : null;
            if (marker is not null && File.Exists(marker)) return LegacyFormat.ReadRecord(marker);
        }
        return null;
    }

    public void Save(ApplyRecord record)
    {
        JsonFile.Write(RecordPath, record);
        if (File.Exists(ClearedPath)) File.Delete(ClearedPath);
    }

    public void Clear()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ClearedPath, "");
        if (File.Exists(RecordPath)) File.Delete(RecordPath);
    }
}

/// <summary>Reads data written by the v1.4.0 PowerShell script, which has a few PowerShell-specific quirks.</summary>
public static class LegacyFormat
{
    public static string? MachineDirectory(string root, string machineKey)
    {
        var direct = Path.Combine(root, machineKey);
        if (Directory.Exists(direct)) return direct;
        var nested = Path.Combine(root, "Backups", machineKey);
        return Directory.Exists(nested) ? nested : null;
    }

    /// <summary>PowerShell 5.1 writes a one-element collection as a bare object.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray(),
        JsonValueKind.Object => [element],
        _ => [],
    };

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static ApplyRecord ReadRecord(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); // ReadAllText drops the UTF-8 BOM PowerShell adds
        var root = doc.RootElement;

        var files = new List<ExpectedFile>();
        if (root.TryGetProperty("Profiles", out var profiles))
            foreach (var p in Items(profiles))
            {
                var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                if (p.TryGetProperty("ExpectedSettings", out var expected) && expected.ValueKind == JsonValueKind.Object)
                    foreach (var kv in expected.EnumerateObject())
                        settings[kv.Name] = kv.Value.ToString();
                if (Str(p, "Path") is { } filePath)
                    files.Add(new ExpectedFile(filePath, Str(p, "SHA256") ?? "", settings));
            }

        var goal = root.TryGetProperty("Plan", out var plan) ? Str(plan, "Goal") : null;
        return new ApplyRecord
        {
            AppVersion = Str(root, "ScriptVersion") ?? "1.4.0",
            Timestamp = DateTimeOffset.TryParse(Str(root, "Timestamp"), out var ts) ? ts : File.GetLastWriteTime(path),
            MachineKey = Str(root, "MachineKey") ?? "",
            SnapshotId = Str(root, "Snapshot") ?? "",
            Mode = Enum.TryParse<OptimizationMode>(goal, out var mode) ? mode : OptimizationMode.Auto,
            IsLegacy = true,
            Files = files,
        };
    }
}
