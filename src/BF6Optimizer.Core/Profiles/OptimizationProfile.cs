using System.Text.Json;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.Core.Profiles;

/// <summary>Machine-specific picks, kept apart from preferences and stripped from exports.</summary>
public sealed class MachineBinding
{
    public string? GpuId { get; set; }
    public string? DisplayDevice { get; set; }
}

public sealed class OptimizationProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool BuiltIn { get; set; }
    public OptimizationPreferences Preferences { get; set; } = new();
    public MachineBinding? Machine { get; set; }
}

public interface IProfileStore
{
    IReadOnlyList<OptimizationProfile> LoadUserProfiles();
    void Save(OptimizationProfile profile);
    void Delete(string id);
}

public static class BuiltInProfiles
{
    public static IReadOnlyList<OptimizationProfile> All { get; } =
    [
        Make("builtin-competitive", OptimizationMode.Competitive),
        Make("builtin-balanced", OptimizationMode.Balanced),
        Make("builtin-quality", OptimizationMode.Quality),
        Make("builtin-custom", OptimizationMode.Custom),
    ];

    private static OptimizationProfile Make(string id, OptimizationMode mode) => new()
    {
        Id = id,
        Name = mode.ToString(),
        BuiltIn = true,
        Preferences = new OptimizationPreferences { BaseMode = mode },
    };
}

public static class ProfileValidator
{
    public const int MaxNameLength = 64;

    public static IReadOnlyList<Msg> Validate(OptimizationProfile profile, SettingCatalog catalog)
    {
        var errors = new List<Msg>();
        if (profile.SchemaVersion != OptimizationProfile.CurrentSchemaVersion)
            errors.Add(new Msg("error.profile_schema", profile.SchemaVersion.ToString()));
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > MaxNameLength)
            errors.Add(new Msg("error.profile_name"));

        var prefs = profile.Preferences;
        if (prefs is null) { errors.Add(new Msg("error.profile_invalid")); return errors; }
        if (!Enum.IsDefined(prefs.BaseMode) || !Enum.IsDefined(prefs.FpsCapMode) || !Enum.IsDefined(prefs.Objective))
            errors.Add(new Msg("error.profile_invalid"));
        if (prefs.FpsCapMode == FpsCapMode.Manual && FpsCapValidator.Validate(prefs.ManualFpsCap) is { } fps)
            errors.Add(fps);

        foreach (var (key, value) in prefs.Overrides ?? [])
        {
            var def = catalog.Find(key);
            bool ok = def is not null && def.Kind != SettingKind.Preserved
                && (value == OptimizationPreferences.PreserveValue || def.IsWritableValue(value));
            if (!ok) errors.Add(new Msg("error.profile_value", key));
        }
        return errors;
    }

    /// <summary>Parses an imported file. Any problem rejects the whole profile; nothing is partially loaded.</summary>
    public static (OptimizationProfile? Profile, IReadOnlyList<Msg> Errors) ParseImport(string json, SettingCatalog catalog)
    {
        OptimizationProfile? profile;
        try { profile = Json.Deserialize<OptimizationProfile>(json); }
        catch (JsonException) { return (null, [new Msg("error.profile_invalid")]); }
        if (profile is null) return (null, [new Msg("error.profile_invalid")]);

        var errors = Validate(profile, catalog);
        if (errors.Count > 0) return (null, errors);

        profile.Id = Guid.NewGuid().ToString("N");
        profile.BuiltIn = false;
        profile.Machine = null;
        return (profile, []);
    }

    public static string Export(OptimizationProfile profile) => Json.Serialize(new OptimizationProfile
    {
        Id = profile.Id,
        Name = profile.Name,
        Preferences = profile.Preferences,
    });
}
