using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.Core.Planning;

public enum OptimizationMode { Auto, Competitive, Balanced, Quality, Custom }

/// <summary>User objective consulted by Auto mode.</summary>
public enum AutoObjective { Recommended, LowestLatency, BestVisuals }

public enum FpsCapMode { Auto, Preserve, Manual }

/// <summary>Hardware-independent preferences; safe to export and share between machines.</summary>
public sealed class OptimizationPreferences
{
    public const string PreserveValue = "preserve";

    public OptimizationMode BaseMode { get; set; } = OptimizationMode.Auto;
    public AutoObjective Objective { get; set; } = AutoObjective.Recommended;
    public FpsCapMode FpsCapMode { get; set; } = FpsCapMode.Auto;
    public int ManualFpsCap { get; set; } = 120;
    /// <summary>Key to target value, or <see cref="PreserveValue"/>. Honoured only for Custom mode.</summary>
    public Dictionary<string, string> Overrides { get; set; } = [];
    public bool ApplyWindowsPreferences { get; set; } = true;

    public OptimizationPreferences Clone() => new()
    {
        BaseMode = BaseMode,
        Objective = Objective,
        FpsCapMode = FpsCapMode,
        ManualFpsCap = ManualFpsCap,
        Overrides = new Dictionary<string, string>(Overrides),
        ApplyWindowsPreferences = ApplyWindowsPreferences,
    };
}

public sealed record PlanRequest
{
    public OptimizationMode Mode { get; init; } = OptimizationMode.Auto;
    public required HardwareSnapshot Hardware { get; init; }
    public GpuInfo? Gpu { get; init; }
    public DisplayInfo? Display { get; init; }
    public OptimizationPreferences Preferences { get; init; } = new();
}

public sealed record PlannedSetting(string Key, string Value, Msg Reason);

public sealed record OptimizationPlan
{
    public OptimizationMode RequestedMode { get; init; }
    public OptimizationMode ResolvedMode { get; init; }
    public PerformanceTier Tier { get; init; }
    public double EstimatedScore { get; init; }
    public double ResolutionPenalty { get; init; }
    public double EffectiveScore { get; init; }
    public ScoreConfidence Confidence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int? FpsCap { get; init; }
    public string PerformanceDataVersion { get; init; } = "";
    public IReadOnlyList<PlannedSetting> Settings { get; init; } = [];
    public IReadOnlyList<WindowsPreference> WindowsPreferences { get; init; } = [];
    public IReadOnlyList<Msg> Reasons { get; init; } = [];
    public IReadOnlyList<Msg> Warnings { get; init; } = [];
}

public enum ChangeStatus { Change, NoChange, MissingInProfile, UnknownCurrentValue, NotVerified }

public sealed record SettingChange(string Key, string? Before, string After, ChangeStatus Status, Msg Reason);

public sealed record FileChangeSet
{
    public required string Path { get; init; }
    public required string SourceSha256 { get; init; }
    public ProfileEncoding Encoding { get; init; }
    public NewLineStyle NewLine { get; init; }
    public IReadOnlyList<SettingChange> Changes { get; init; } = [];

    public IEnumerable<SettingChange> Writable =>
        Changes.Where(c => c.Status is ChangeStatus.Change or ChangeStatus.NoChange);

    public IReadOnlyDictionary<string, string> Expected =>
        Writable.ToDictionary(c => c.Key, c => c.After, StringComparer.Ordinal);
}

public static class DiffBuilder
{
    private const string LimitKey = "GstRender.FrameRateLimit";
    private const string LimiterEnableKey = "GstRender.FrameRateLimiterEnable";

    /// <summary>Compares a plan with a parsed profile. Nothing outside the existing, known values becomes writable.</summary>
    public static FileChangeSet Build(string path, string sha256, ProfileDocument doc, OptimizationPlan plan, SettingCatalog catalog)
    {
        var changes = new List<SettingChange>(plan.Settings.Count);
        foreach (var s in plan.Settings)
            changes.Add(Compare(s, doc, catalog));

        // Enabling the limiter is only meaningful alongside a limit we can actually set.
        var limit = changes.FirstOrDefault(c => c.Key == LimitKey);
        if (limit is not null && limit.Status is not (ChangeStatus.Change or ChangeStatus.NoChange))
        {
            int i = changes.FindIndex(c => c.Key == LimiterEnableKey);
            if (i >= 0)
                changes[i] = changes[i] with { Status = ChangeStatus.NotVerified, Reason = new Msg("reason.limiter_without_limit") };
        }

        return new FileChangeSet
        {
            Path = path,
            SourceSha256 = sha256,
            Encoding = doc.FileEncoding,
            NewLine = doc.NewLine,
            Changes = changes,
        };
    }

    private static SettingChange Compare(PlannedSetting s, ProfileDocument doc, SettingCatalog catalog)
    {
        var current = doc.GetAll(s.Key).ToList();
        if (current.Count == 0) return new SettingChange(s.Key, null, s.Value, ChangeStatus.MissingInProfile, s.Reason);

        var def = catalog.Find(s.Key);
        if (def is null || !def.IsWritableValue(s.Value))
            return new SettingChange(s.Key, current[0], s.Value, ChangeStatus.NotVerified, s.Reason);
        if (!current.All(def.IsKnownValue))
            return new SettingChange(s.Key, current[0], s.Value, ChangeStatus.UnknownCurrentValue, s.Reason);

        // Duplicate lines are aligned to one value; none are added or removed.
        var status = current.All(v => v == s.Value) ? ChangeStatus.NoChange : ChangeStatus.Change;
        return new SettingChange(s.Key, current[0], s.Value, status, s.Reason);
    }
}
