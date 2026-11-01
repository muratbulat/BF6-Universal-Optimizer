using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Configuration;

public enum SettingKind { Toggle, Level, Float, Fps, Preserved }

public sealed class SettingDefinition
{
    public string Key { get; set; } = "";
    public string Category { get; set; } = "";
    public SettingKind Kind { get; set; }
    public List<string> Known { get; set; } = [];
    public List<string> Writable { get; set; } = [];
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool Advanced { get; set; }

    /// <summary>Localization stem, e.g. "TextureQuality".</summary>
    public string ShortName => Key.StartsWith("GstRender.", StringComparison.Ordinal) ? Key["GstRender.".Length..] : Key;

    public bool IsKnownValue(string? value)
    {
        if (value is null || Kind == SettingKind.Preserved) return false;
        if (Kind is SettingKind.Float or SettingKind.Fps)
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d >= (Min ?? double.MinValue) && d <= (Max ?? double.MaxValue);
        return Known.Contains(value, StringComparer.Ordinal);
    }

    public bool IsWritableValue(string value)
    {
        if (Kind == SettingKind.Preserved) return false;
        if (Kind == SettingKind.Fps)
            return FpsCapValidator.TryParseProfileValue(value, out var fps) && FpsCapValidator.Validate(fps) is null;
        return Writable.Contains(value, StringComparer.Ordinal);
    }
}

public sealed record ProfileCompatibility(bool IsCompatible, int RecognizedKeys);

public sealed class SettingCatalog
{
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; set; }
    public string CatalogVersion { get; set; } = "";
    public List<string> SignatureKeys { get; set; } = [];
    public int MinimumRecognizedKeys { get; set; }
    public List<SettingDefinition> Settings { get; set; } = [];

    private Dictionary<string, SettingDefinition>? _byKey;

    public SettingDefinition? Find(string key) =>
        (_byKey ??= Settings.ToDictionary(s => s.Key, StringComparer.Ordinal)).GetValueOrDefault(key);

    public static SettingCatalog LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("settings-catalog.json")
            ?? throw new InvalidOperationException("settings-catalog.json is not embedded.");
        var catalog = JsonSerializer.Deserialize<SettingCatalog>(s, Json.Options)
            ?? throw new InvalidDataException("Empty settings catalog.");
        if (catalog.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported catalog schema {catalog.SchemaVersion}.");
        return catalog;
    }

    /// <summary>A profile is only touched if it carries a known signature and enough recognized render keys.</summary>
    public ProfileCompatibility Evaluate(ProfileDocument doc)
    {
        int recognized = Settings.Count(s => s.Kind != SettingKind.Preserved && doc.Contains(s.Key));
        return new ProfileCompatibility(SignatureKeys.Any(doc.Contains) && recognized >= MinimumRecognizedKeys, recognized);
    }
}

public static class FpsCapValidator
{
    public const int Minimum = 30;
    public const int Maximum = 1000;

    /// <summary>Returns a validation message, or null when the cap is acceptable.</summary>
    public static Msg? Validate(int cap) =>
        cap is < Minimum or > Maximum ? new Msg("error.fps_range", Minimum.ToString(), Maximum.ToString()) : null;

    public static Msg? ValidateText(string? text, out int cap)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out cap))
            return new Msg("error.fps_number");
        return Validate(cap);
    }

    public static string Format(int cap) => cap.ToString("F6", CultureInfo.InvariantCulture);

    public static bool TryParseProfileValue(string value, out int cap)
    {
        cap = 0;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        if (d != Math.Floor(d) || d > int.MaxValue || d < int.MinValue) return false;
        cap = (int)d;
        return true;
    }
}
