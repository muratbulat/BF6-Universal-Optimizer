using BF6Optimizer.Core.Audit;
using BF6Optimizer.Infrastructure.Storage;
using Microsoft.Win32;

namespace BF6Optimizer.Infrastructure.Windows;

/// <summary>
/// Reads Run keys and Startup folders. Disabling uses the StartupApproved flag Task Manager itself writes,
/// so the entry stays intact and re-enabling is exact.
/// </summary>
public sealed class WindowsStartupStore : IStartupStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string HkcuRunLocation = "HKCU Run";

    public IReadOnlyList<StartupEntry> List()
    {
        var result = new List<StartupEntry>();
        ReadRun(Registry.CurrentUser, RunKey, HkcuRunLocation, true, Registry.CurrentUser, result);
        ReadRun(Registry.LocalMachine, RunKey, "HKLM Run", false, Registry.LocalMachine, result);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup", result);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup", result);
        return result.OrderBy(e => e.Category).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRun(RegistryKey hive, string path, string location, bool perUser, RegistryKey approvedHive, List<StartupEntry> into)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        using var approved = approvedHive.OpenSubKey(ApprovedRun);
        foreach (var name in key.GetValueNames())
        {
            var command = key.GetValue(name) as string ?? "";
            bool enabled = approved?.GetValue(name) is not byte[] { Length: > 0 } flag || (flag[0] & 1) == 0;
            into.Add(new StartupEntry(name, command, location, perUser, enabled, StartupClassifier.Classify(name, command)));
        }
    }

    private static void ReadFolder(string folder, string location, List<StartupEntry> into)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            into.Add(new StartupEntry(name, file, location, false, true, StartupClassifier.Classify(name, file)));
        }
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.Location != HkcuRunLocation) throw new InvalidOperationException("Only per-user Run entries can be changed.");
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedRun, writable: true);
        var value = new byte[12];
        value[0] = enabled ? (byte)2 : (byte)3;
        key.SetValue(entry.Name, value, RegistryValueKind.Binary);
    }
}

public sealed class JsonPreferenceBackupStore(AppPaths paths) : BF6Optimizer.Core.Audit.IPreferenceBackupStore
{
    private string FilePath => Path.Combine(paths.Data, "windows-preferences-backup.json");

    public PreferenceBackup? Load() => JsonFile.Read<PreferenceBackup>(FilePath);

    public void Save(PreferenceBackup backup) => JsonFile.Write(FilePath, backup);

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

public sealed class JsonAuditHistoryStore(AppPaths paths, Func<string> machineKey) : IAuditHistoryStore
{
    private const int Keep = 30;

    private string Dir => Path.Combine(paths.Data, machineKey(), "Audits");

    public void Save(AuditResult result)
    {
        JsonFile.Write(Path.Combine(Dir, $"{result.Snapshot.Timestamp:yyyyMMdd_HHmmss_fff}.json"), result);
        foreach (var old in Files().Skip(Keep)) File.Delete(old);
    }

    public IReadOnlyList<AuditResult> List(int max)
    {
        var result = new List<AuditResult>();
        foreach (var file in Files().Take(max))
        {
            try
            {
                if (JsonFile.Read<AuditResult>(file) is { } a) result.Add(a);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        }
        return result;
    }

    private IEnumerable<string> Files() => Directory.Exists(Dir)
        ? Directory.EnumerateFiles(Dir, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
        : [];
}
