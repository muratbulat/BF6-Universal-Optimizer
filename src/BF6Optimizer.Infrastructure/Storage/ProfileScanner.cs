using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;

namespace BF6Optimizer.Infrastructure.Storage;

public sealed record DetectedProfile
{
    public required string Path { get; init; }
    public string Launcher { get; init; } = "";
    public FileAccessState Access { get; init; }
    public bool Compatible { get; init; }
    public int RecognizedKeys { get; init; }
    public Msg? Problem { get; init; }
    public bool SteamCloud { get; init; }
    public bool OneDrive { get; init; }
    public DateTime LastWrite { get; init; }
}

public interface IProfileScanner
{
    Task<IReadOnlyList<DetectedProfile>> ScanAsync(string? manualDirectory, CancellationToken ct = default);
}

/// <summary>Finds profiles under Documents\Battlefield 6\settings, including redirected and OneDrive folders.</summary>
public sealed class ProfileScanner(IProfileFileStore files, SettingCatalog catalog, Func<IEnumerable<string>>? documentRoots = null) : IProfileScanner
{
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
    private static readonly string[] OneDriveDocuments = ["Documents", "Belgeler"];

    public static IEnumerable<string> DefaultDocumentRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var variable in OneDriveVariables)
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
                foreach (var name in OneDriveDocuments)
                    yield return Path.Combine(root, name);
    }

    public async Task<IReadOnlyList<DetectedProfile>> ScanAsync(string? manualDirectory, CancellationToken ct = default)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(manualDirectory)) roots.Add(manualDirectory);
        foreach (var docs in (documentRoots ?? DefaultDocumentRoots)())
            if (!string.IsNullOrWhiteSpace(docs)) roots.Add(Path.Combine(docs, "Battlefield 6", "settings"));

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4, AttributesToSkip = 0 };
        var paths = roots
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", options))
            .Where(ProfileNames.IsProfile)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        var result = new List<DetectedProfile>();
        foreach (var path in paths)
            result.Add(await InspectAsync(path, ct).ConfigureAwait(false));
        return result;
    }

    private async Task<DetectedProfile> InspectAsync(string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)!;
        var parent = Path.GetFileName(dir);
        var profile = new DetectedProfile
        {
            Path = path,
            Launcher = parent.Equals("settings", StringComparison.OrdinalIgnoreCase) ? "" : parent,
            Access = files.Inspect(path),
            SteamCloud = File.Exists(Path.Combine(dir, "steam_autocloud.vdf")),
            OneDrive = OneDriveVariables.Select(Environment.GetEnvironmentVariable)
                .Any(r => !string.IsNullOrEmpty(r) && path.StartsWith(r, StringComparison.OrdinalIgnoreCase)),
            LastWrite = File.GetLastWriteTime(path),
        };

        try
        {
            var (doc, _) = await ProfileLoader.LoadAsync(files, path, ct).ConfigureAwait(false);
            var compatibility = catalog.Evaluate(doc);
            return profile with
            {
                Compatible = compatibility.IsCompatible,
                RecognizedKeys = compatibility.RecognizedKeys,
                Problem = compatibility.IsCompatible ? null : new Msg("error.profile_unsupported"),
            };
        }
        catch (OptimizerException ex) { return profile with { Problem = ex.UserMessage }; }
        catch (IOException) { return profile with { Problem = new Msg("error.file_locked", path) }; }
        catch (UnauthorizedAccessException) { return profile with { Problem = new Msg("error.file_denied", path) }; }
    }
}
