using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.Core.Audit;

public interface IStartupStore
{
    IReadOnlyList<StartupEntry> List();
    /// <summary>Uses Windows' own StartupApproved flag; only per-user Run entries are accepted.</summary>
    void SetEnabled(StartupEntry entry, bool enabled);
}

public sealed record PreferenceBackup(DateTimeOffset Created, IReadOnlyList<RegistryValueState> Values);

public interface IPreferenceBackupStore
{
    PreferenceBackup? Load();
    void Save(PreferenceBackup backup);
    void Clear();
}

public sealed record PreferencePreview(WindowsPreference Preference, int? Before, bool Exists);

public sealed record SafeActionResult(bool Success, IReadOnlyList<Msg> Problems);

/// <summary>Stand-alone per-user preference changes for the Safe Optimization Center.</summary>
public sealed class SafePreferenceActions(IWindowsPreferenceStore store, IPreferenceBackupStore backups, IClock clock)
{
    public IReadOnlyList<PreferencePreview> Preview() => WindowsPreferences.All
        .Select(p => (Pref: p, State: store.Read(p.Path, p.Name)))
        .Select(x => new PreferencePreview(x.Pref, x.State.Value, x.State.Exists))
        .ToList();

    /// <summary>All-or-nothing: backs up, writes, reads back, and undoes everything on any mismatch.</summary>
    public SafeActionResult Apply()
    {
        var before = WindowsPreferences.Capture(store);
        // An earlier backup holds the true original values; keep it across repeat applies.
        if (backups.Load() is null) backups.Save(new PreferenceBackup(clock.Now, before));
        try
        {
            foreach (var p in WindowsPreferences.All)
            {
                store.Write(p.Path, p.Name, p.Value);
                if (store.Read(p.Path, p.Name).Value != p.Value)
                    throw new InvalidOperationException(p.Name);
            }
            return new SafeActionResult(true, []);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or System.Security.SecurityException)
        {
            Write(before);
            return new SafeActionResult(false, [new Msg("error.pref_apply")]);
        }
    }

    public SafeActionResult Restore()
    {
        if (backups.Load() is not { } backup) return new SafeActionResult(false, [new Msg("error.no_pref_backup")]);
        Write(backup.Values);
        backups.Clear();
        return new SafeActionResult(true, []);
    }

    private void Write(IEnumerable<RegistryValueState> values)
    {
        foreach (var v in values.Where(v => WindowsPreferences.IsAllowed(v.Path, v.Name)))
        {
            if (v is { Exists: true, Value: { } value }) store.Write(v.Path, v.Name, value);
            else store.Remove(v.Path, v.Name);
        }
    }
}

public static class StartupClassifier
{
    private static readonly (StartupCategory Category, string[] Words)[] Rules =
    [
        (StartupCategory.Security, ["defender", "securityhealth", "antivirus", "kaspersky", "eset", "bitdefender", "norton", "malwarebytes"]),
        (StartupCategory.GameLauncher, ["steam", "epicgames", "eadesktop", "eabackground", "battle.net", "ubisoft", "gog galaxy", "riot"]),
        (StartupCategory.Communication, ["discord", "teams", "slack", "skype", "zoom", "telegram", "whatsapp"]),
        (StartupCategory.Peripheral, ["icue", "razer", "logitech", "lghub", "steelseries", "corsair", "openrgb", "armoury", "nzxt", "wooting"]),
        (StartupCategory.Driver, ["nvidia", "nvcontainer", "radeon", "amdrsserv", "igfx", "realtek", "rtkaud", "synaptics", "intel"]),
        (StartupCategory.Windows, ["onedrive", "windows"]),
    ];

    public static StartupCategory Classify(string name, string command)
    {
        var text = (name + " " + command).ToLowerInvariant();
        foreach (var (category, words) in Rules)
            if (words.Any(text.Contains)) return category;
        return StartupCategory.Other;
    }
}
