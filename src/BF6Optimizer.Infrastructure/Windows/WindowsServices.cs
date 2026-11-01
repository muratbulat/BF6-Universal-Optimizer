using System.Diagnostics;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Planning;
using Microsoft.Win32;

namespace BF6Optimizer.Infrastructure.Windows;

/// <summary>HKEY_CURRENT_USER only, restricted to the planner allowlist. No HKLM write path exists.</summary>
public sealed class HkcuPreferenceStore : IWindowsPreferenceStore
{
    public RegistryValueState Read(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        var value = key?.GetValue(name);
        return value is int i
            ? new RegistryValueState(path, name, true, i)
            : new RegistryValueState(path, name, value is not null, null);
    }

    public void Write(string path, string name, int value)
    {
        Guard(path, name);
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void Remove(string path, string name)
    {
        Guard(path, name);
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void Guard(string path, string name)
    {
        if (!WindowsPreferences.IsAllowed(path, name))
            throw new InvalidOperationException($"Registry value is not on the allowlist: {name}");
    }
}

public sealed class GameProcessMonitor : IGameProcessMonitor
{
    private static readonly string[] Launchers = ["EADesktop", "EALauncher", "EABackgroundService", "steam", "EpicGamesLauncher"];

    public bool IsGameRunning() => Running("bf6");

    public IReadOnlyList<string> RunningLaunchers() => Launchers.Where(Running).ToList();

    private static bool Running(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }
}
