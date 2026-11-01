using System.Reflection;
using System.Text.Json;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Hardware;

public enum ScoreConfidence { Database, Family, Unknown }

public enum PerformanceTier { VeryLow, Low, Medium, High }

public sealed record ScoreEstimate(double Score, ScoreConfidence Confidence);

/// <summary>Versioned, embedded classification data. Estimates choose settings; they are not benchmarks.</summary>
public sealed class PerformanceData
{
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; set; }
    public string DataVersion { get; set; } = "";
    public Dictionary<string, double> Defaults { get; set; } = [];
    public TierThresholds Tiers { get; set; } = new();
    public double MinimumScore { get; set; }
    public double ResolutionPenaltyPerDoubling { get; set; }
    public LaptopData Laptop { get; set; } = new();
    public FpsCapData FpsCap { get; set; } = new();
    public CpuData Cpu { get; set; } = new();
    public MemoryData Memory { get; set; } = new();
    public TextureData Texture { get; set; } = new();
    public Dictionary<string, double> Gpus { get; set; } = [];

    public sealed class TierThresholds { public double High { get; set; } public double Medium { get; set; } public double Low { get; set; } }
    public sealed class LaptopData
    {
        public double DefaultPenalty { get; set; }
        public double NonNvidiaPenalty { get; set; }
        public List<PowerPenalty> NvidiaByMaxPowerW { get; set; } = [];
    }
    public sealed class PowerPenalty { public double MinWatts { get; set; } public double Penalty { get; set; } }
    public sealed class FpsCapData
    {
        public int Margin { get; set; }
        public int QualityDesktop { get; set; }
        public int QualityLaptop { get; set; }
        public int BalancedHigh { get; set; }
        public int BalancedMedium { get; set; }
        public int BalancedLow { get; set; }
        public double BalancedHighScore { get; set; }
        public double BalancedMediumScore { get; set; }
        public int CpuLimitedCeiling { get; set; }
    }
    public sealed class CpuData { public int LimitedPhysicalCoresBelow { get; set; } public int LimitedLogicalCoresBelow { get; set; } }
    public sealed class MemoryData { public double LowRamGbBelow { get; set; } }
    public sealed class TextureData { public double UltraMinVramGb { get; set; } public double HighMinVramGb { get; set; } public double MediumMinVramGb { get; set; } }

    public static PerformanceData LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("performance-data.json")
            ?? throw new InvalidOperationException("performance-data.json is not embedded.");
        return Load(s);
    }

    /// <summary>Loads and validates data; also the gate for optional local imports.</summary>
    public static PerformanceData Load(Stream stream)
    {
        var data = JsonSerializer.Deserialize<PerformanceData>(stream, Json.Options)
            ?? throw new InvalidDataException("Empty performance data.");
        if (data.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported performance data schema {data.SchemaVersion}.");
        foreach (var key in new[] { "unknown", "integrated" })
            if (!data.Defaults.ContainsKey(key)) throw new InvalidDataException($"Missing default '{key}'.");
        if (!(data.Tiers.High > data.Tiers.Medium && data.Tiers.Medium > data.Tiers.Low && data.Tiers.Low > 0))
            throw new InvalidDataException("Tier thresholds must be descending and positive.");
        if (data.Gpus.Values.Concat(data.Defaults.Values).Any(v => v is <= 0 or > 20 || double.IsNaN(v)))
            throw new InvalidDataException("GPU scores must be between 0 and 20.");
        if (data.FpsCap.Margin is < 0 or > 30)
            throw new InvalidDataException("FPS margin is out of range.");
        return data;
    }

    /// <summary>Estimate for a GPU. Unknown models get a conservative family default, never an extrapolation.</summary>
    public ScoreEstimate BaseScore(GpuIdentity id, GpuKind kind)
    {
        if (kind == GpuKind.Integrated) return new ScoreEstimate(Defaults["integrated"], ScoreConfidence.Family);
        if (id.LookupKey is { } key && Gpus.TryGetValue(key, out var score)) return new ScoreEstimate(score, ScoreConfidence.Database);
        if (id.Family.Length > 0 && Defaults.TryGetValue(id.Family, out var family)) return new ScoreEstimate(family, ScoreConfidence.Family);
        return new ScoreEstimate(Defaults["unknown"], ScoreConfidence.Unknown);
    }

    public double LaptopPenalty(GpuVendor vendor, double? powerMaxW)
    {
        if (vendor != GpuVendor.Nvidia) return Laptop.NonNvidiaPenalty;
        if (powerMaxW is > 0)
            foreach (var row in Laptop.NvidiaByMaxPowerW.OrderByDescending(r => r.MinWatts))
                if (powerMaxW >= row.MinWatts) return row.Penalty;
        return Laptop.DefaultPenalty;
    }

    public double ResolutionPenalty(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        var factor = (double)width * height / (1920.0 * 1080.0);
        return factor <= 1 ? 0 : Math.Round(Math.Log2(factor) * ResolutionPenaltyPerDoubling, 2);
    }

    public PerformanceTier Tier(double effectiveScore) =>
        effectiveScore >= Tiers.High ? PerformanceTier.High
        : effectiveScore >= Tiers.Medium ? PerformanceTier.Medium
        : effectiveScore >= Tiers.Low ? PerformanceTier.Low
        : PerformanceTier.VeryLow;
}

public static class GpuSelector
{
    /// <summary>Chooses the likely gaming GPU; a still-present manual choice always wins.</summary>
    public static GpuInfo? Select(IReadOnlyList<GpuInfo> gpus, PerformanceData data, string? manualId = null)
    {
        if (manualId is not null && gpus.FirstOrDefault(g => g.Id == manualId) is { } manual) return manual;

        return gpus
            .Where(g => g.Kind != GpuKind.Software)
            .OrderByDescending(g => g.Kind switch { GpuKind.Discrete => 2, GpuKind.Unknown => 1, _ => 0 })
            .ThenByDescending(g => data.BaseScore(GpuNameParser.Parse(g.Name), g.Kind).Score)
            .ThenByDescending(g => g.DedicatedVramBytes ?? 0)
            .FirstOrDefault();
    }
}
