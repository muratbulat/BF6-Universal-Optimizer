namespace BF6Optimizer.Infrastructure.Storage;

public enum StorageMode { LocalAppData, Portable }

/// <summary>Where user data lives. Separate from where the executable sits.</summary>
public sealed class AppPaths
{
    public const string PortableMarkerName = "BF6Optimizer.portable";
    public const string PortableFolderName = "BF6OptimizerData";
    public const string LocalFolderName = "BF6UniversalOptimizer";

    public string Root { get; }
    public StorageMode Mode { get; }
    /// <summary>Portable data was requested but the executable's folder cannot be written.</summary>
    public bool PortableFallback { get; }

    public string Data => Path.Combine(Root, "Data");
    public string Backups => Path.Combine(Root, "Backups");
    public string Reports => Path.Combine(Root, "Reports");
    public string Logs => Path.Combine(Root, "Logs");
    public string Profiles => Path.Combine(Root, "Profiles");
    public string Settings => Path.Combine(Root, "Settings");

    public AppPaths(string root, StorageMode mode = StorageMode.LocalAppData, bool portableFallback = false)
    {
        Root = root;
        Mode = mode;
        PortableFallback = portableFallback;
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Data, Backups, Reports, Logs, Profiles, Settings })
            Directory.CreateDirectory(dir);
    }

    public static AppPaths Resolve() =>
        Resolve(AppContext.BaseDirectory, Environment.ProcessPath,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static AppPaths Resolve(string baseDirectory, string? processPath, string localAppData)
    {
        var local = Path.Combine(localAppData, LocalFolderName);

        // In a single-file app the process folder is where the user placed the executable.
        var exeDir = Path.GetDirectoryName(processPath) ?? baseDirectory;
        if (!File.Exists(Path.Combine(exeDir, PortableMarkerName))) return new AppPaths(local);

        return IsWritable(exeDir)
            ? new AppPaths(Path.Combine(exeDir, PortableFolderName), StorageMode.Portable)
            : new AppPaths(local, StorageMode.LocalAppData, portableFallback: true);
    }

    public static bool IsWritable(string directory)
    {
        var probe = Path.Combine(directory, ".bf6write_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryEnablePortable(string? exeDirectory = null)
    {
        var dir = exeDirectory ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (!IsWritable(dir)) return false;
        File.WriteAllText(Path.Combine(dir, PortableMarkerName), "BF6 Optimizer portable data mode marker. Delete to return to LocalAppData storage.\n");
        return true;
    }

    public static void DisablePortable(string? exeDirectory = null)
    {
        var dir = exeDirectory ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        File.Delete(Path.Combine(dir, PortableMarkerName));
    }
}
