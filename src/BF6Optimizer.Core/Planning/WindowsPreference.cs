namespace BF6Optimizer.Core.Planning;

/// <summary>A per-user (HKCU) DWORD. Path is relative to HKEY_CURRENT_USER.</summary>
public sealed record WindowsPreference(string Id, string Path, string Name, int Value);

public sealed record RegistryValueState(string Path, string Name, bool Exists, int? Value);

public interface IWindowsPreferenceStore
{
    RegistryValueState Read(string path, string name);
    void Write(string path, string name, int value);
    void Remove(string path, string name);
}

/// <summary>The complete allowlist of Windows values this application may touch. Nothing else is writable.</summary>
public static class WindowsPreferences
{
    public static readonly IReadOnlyList<WindowsPreference> All =
    [
        new("AllowAutoGameMode", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
        new("AutoGameModeEnabled", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
        new("GameDvrEnabled", @"System\GameConfigStore", "GameDVR_Enabled", 0),
        new("AppCaptureEnabled", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
    ];

    public static bool IsAllowed(string path, string name) =>
        All.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                  && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<RegistryValueState> Capture(IWindowsPreferenceStore store) =>
        All.Select(p => store.Read(p.Path, p.Name)).ToList();

    /// <summary>"HKCU:\Software\..." (v1.4.0 form) to a relative path; anything not under HKCU gives null.</summary>
    public static string? NormalizeLegacyPath(string? path)
    {
        const string Prefix = @"HKCU:\";
        return path is not null && path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? path[Prefix.Length..] : null;
    }
}
