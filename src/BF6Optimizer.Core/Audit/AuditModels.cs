using System.Text.Json.Serialization;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Audit;

public enum AuditStatus { Optimal, Acceptable, OptimizationAvailable, Warning, Critical, Unknown, Unsupported, ManualVerification }

public enum AuditCategory { Memory, Cpu, Gpu, Display, WindowsGaming, Storage, Background, Network, Security, Firmware, Thermal }

public enum Confidence { None, Low, Medium, High }

/// <summary>Keeps facts, measurements and inferences apart everywhere they are shown.</summary>
public enum EvidenceKind { Fact, Measurement, Inference, ManualCheck, Unavailable }

public enum Priority { Informational, Low, Medium, High, Critical }

public enum Relevance { None, Low, Medium, High }

public sealed record AuditCheck
{
    public required string Id { get; init; }
    public required AuditCategory Category { get; init; }
    public required AuditStatus Status { get; init; }
    public required Msg Detected { get; init; }
    /// <summary>Text key stem; defaults to the id. Per-instance checks share one stem.</summary>
    public string? TextId { get; init; }
    public string Method { get; init; } = "";
    public Confidence Confidence { get; init; } = Confidence.High;
    public EvidenceKind Evidence { get; init; } = EvidenceKind.Fact;
    public Priority Priority { get; init; } = Priority.Informational;
    public Relevance Relevance { get; init; } = Relevance.Low;
    public bool NeedsAdmin { get; init; }
    public bool AutoFix { get; init; }
    public bool RestartRequired { get; init; }
    public bool Reversible { get; init; } = true;
    /// <summary>False for facts that say nothing about configuration: security state, capability, age.</summary>
    public bool Scored { get; init; } = true;

    public string Stem => TextId ?? Id;
    public string TitleKey => $"check.{Stem}.title";
    public string ExpectedKey => $"check.{Stem}.expected";
    public string AdviceKey => $"check.{Stem}.advice";
    public bool IsActionable => Status is AuditStatus.OptimizationAvailable or AuditStatus.Warning or AuditStatus.Critical or AuditStatus.ManualVerification;
}

public sealed record CategoryScore(AuditCategory Category, int? Score, int Applicable, int Total);

public sealed record ReadinessResult(int? Score, int CoveragePercent, IReadOnlyList<CategoryScore> Categories, int ScoredChecks, int EligibleChecks);

public sealed record AuditResult
{
    public int SchemaVersion { get; init; } = 1;
    public required SystemAuditSnapshot Snapshot { get; init; }
    public required IReadOnlyList<AuditCheck> Checks { get; init; }
    public required ReadinessResult Readiness { get; init; }

    [JsonIgnore]
    public IEnumerable<AuditCheck> Recommendations => Checks
        .Where(c => c.IsActionable)
        .OrderByDescending(c => c.Priority).ThenByDescending(c => c.Confidence);
}

public static class GamingReadinessCalculator
{
    public static readonly AuditCategory[] ScoredCategories =
    [
        AuditCategory.Memory, AuditCategory.Cpu, AuditCategory.Gpu, AuditCategory.Display,
        AuditCategory.WindowsGaming, AuditCategory.Storage, AuditCategory.Background, AuditCategory.Network,
    ];

    /// <summary>Measurable checks only: Unknown, Unsupported and Manual stay out of the denominator.</summary>
    private static double? Weight(AuditStatus s) => s switch
    {
        AuditStatus.Optimal => 1.0,
        AuditStatus.Acceptable => 0.9,
        AuditStatus.OptimizationAvailable => 0.5,
        AuditStatus.Warning => 0.25,
        AuditStatus.Critical => 0.0,
        _ => null,
    };

    public static ReadinessResult Calculate(IReadOnlyList<AuditCheck> checks)
    {
        var eligible = checks.Where(c => c.Scored && ScoredCategories.Contains(c.Category)).ToList();
        var measurable = eligible.Where(c => Weight(c.Status) is not null).ToList();

        var categories = ScoredCategories.Select(cat =>
        {
            var all = eligible.Where(c => c.Category == cat).ToList();
            var usable = all.Where(c => Weight(c.Status) is not null).ToList();
            return new CategoryScore(cat, Percent(usable), usable.Count, all.Count);
        }).ToList();

        int coverage = eligible.Count == 0 ? 0 : (int)Math.Round(100.0 * measurable.Count / eligible.Count);
        return new ReadinessResult(Percent(measurable), coverage, categories, measurable.Count, eligible.Count);
    }

    private static int? Percent(List<AuditCheck> usable) =>
        usable.Count == 0 ? null : (int)Math.Round(100.0 * usable.Average(c => Weight(c.Status)!.Value));
}

public interface IAuditHistoryStore
{
    void Save(AuditResult result);
    /// <summary>Most recent first.</summary>
    IReadOnlyList<AuditResult> List(int max);
}
