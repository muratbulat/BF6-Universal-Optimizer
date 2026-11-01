using System.Globalization;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.Core.Planning;

/// <summary>
/// Ports Build-Plan / Plan-Settings from v1.4.0 and adds what that script lacked: an Auto mode that actually
/// looks at the hardware, CPU and RAM awareness, and conservative handling of unknown GPUs and VRAM.
/// </summary>
public sealed class OptimizationPlanner(PerformanceData data, SettingCatalog catalog)
{
    private sealed record Levels(
        string Effects, string Lighting, string LocalShadow, string Shadow, string SunShadow,
        string Volumetric, string Reflection, string Ssr, string Ao);

    private static readonly Levels Low = new("0", "0", "0", "0", "0", "0", "0", "0", "0");
    private static readonly Levels Mid = new("0", "1", "0", "0", "0", "0", "0", "0", "0");
    private static readonly Levels High = new("1", "1", "1", "1", "1", "1", "1", "1", "1");

    public OptimizationPlan Build(PlanRequest request)
    {
        var reasons = new List<Msg>();
        var warnings = new List<Msg>();
        var hw = request.Hardware;
        var prefs = request.Preferences;
        var gpu = request.Gpu;

        var (width, height) = ResolveResolution(request.Display, warnings);

        var identity = GpuNameParser.Parse(gpu?.Name);
        var estimate = gpu is null ? new ScoreEstimate(data.Defaults["unknown"], ScoreConfidence.Unknown)
                                   : data.BaseScore(identity, gpu.Kind);
        double score = estimate.Score;

        bool laptop = gpu?.FormFactor == FormFactor.Laptop || hw.SystemFormFactor == FormFactor.Laptop;
        if (laptop && gpu is not null && gpu.Kind != GpuKind.Integrated)
        {
            score -= data.LaptopPenalty(gpu.Vendor, gpu.PowerMaxW);
            warnings.Add(new Msg("warn.laptop_power"));
        }
        score = Math.Max(0.5, Math.Round(score, 2));

        double penalty = data.ResolutionPenalty(width, height);
        double effective = Math.Max(data.MinimumScore, Math.Round(score - penalty, 2));
        var tier = data.Tier(effective);

        if (gpu is null) warnings.Add(new Msg("warn.no_gpu"));
        else if (estimate.Confidence != ScoreConfidence.Database) warnings.Add(new Msg("warn.gpu_not_in_data", gpu.Name));

        bool cpuLimited = IsCpuLimited(hw.Cpu);
        if (cpuLimited) warnings.Add(new Msg("warn.cpu_limited"));

        int refresh = request.Display is { RefreshHz: > 0 } d ? (int)Math.Round(d.RefreshHz) : 0;

        var resolved = ResolveMode(request.Mode, prefs, tier, laptop, refresh, reasons);

        var settings = new List<PlannedSetting>();
        int? cap = ResolveFpsCap(prefs, request.Mode, refresh, effective, laptop, resolved, cpuLimited, reasons, warnings);
        if (cap is { } c)
        {
            settings.Add(new("GstRender.FrameRateLimit", FpsCapValidator.Format(c), new Msg("reason.fps_cap", c.ToString(CultureInfo.InvariantCulture))));
            settings.Add(new("GstRender.FrameRateLimiterEnable", "1", new Msg("reason.limiter_on")));
        }

        AddModeSettings(settings, resolved, tier, gpu, hw, warnings);

        if (request.Mode == OptimizationMode.Custom) ApplyOverrides(settings, prefs, warnings);

        if (gpu?.Vendor == GpuVendor.Amd) warnings.Add(new Msg("warn.upscaler_preserved_amd"));
        if (gpu?.Vendor == GpuVendor.Intel) warnings.Add(new Msg("warn.upscaler_preserved_intel"));

        return new OptimizationPlan
        {
            RequestedMode = request.Mode,
            ResolvedMode = resolved,
            Tier = tier,
            EstimatedScore = score,
            ResolutionPenalty = penalty,
            EffectiveScore = effective,
            Confidence = estimate.Confidence,
            Width = width,
            Height = height,
            FpsCap = cap,
            PerformanceDataVersion = data.DataVersion,
            Settings = settings,
            WindowsPreferences = prefs.ApplyWindowsPreferences ? WindowsPreferences.All : [],
            Reasons = reasons,
            Warnings = warnings,
        };
    }

    private static (int Width, int Height) ResolveResolution(DisplayInfo? display, List<Msg> warnings)
    {
        if (display is { Width: > 0, Height: > 0 }) return (display.Width, display.Height);
        warnings.Add(new Msg("warn.display_unknown"));
        return (1920, 1080);
    }

    private bool IsCpuLimited(CpuInfo? cpu) =>
        cpu is not null &&
        (cpu.PhysicalCores < data.Cpu.LimitedPhysicalCoresBelow || cpu.LogicalCores < data.Cpu.LimitedLogicalCoresBelow);

    private static OptimizationMode ResolveMode(
        OptimizationMode mode, OptimizationPreferences prefs, PerformanceTier tier, bool laptop, int refresh, List<Msg> reasons)
    {
        if (mode == OptimizationMode.Custom)
        {
            if (prefs.BaseMode is OptimizationMode.Custom or OptimizationMode.Auto)
                return ResolveAuto(prefs.Objective, tier, laptop, refresh, reasons);
            return prefs.BaseMode;
        }
        return mode == OptimizationMode.Auto ? ResolveAuto(prefs.Objective, tier, laptop, refresh, reasons) : mode;
    }

    private static OptimizationMode ResolveAuto(AutoObjective objective, PerformanceTier tier, bool laptop, int refresh, List<Msg> reasons)
    {
        OptimizationMode Pick(OptimizationMode m, string reason)
        {
            reasons.Add(new Msg(reason));
            return m;
        }

        if (objective == AutoObjective.LowestLatency) return Pick(OptimizationMode.Competitive, "auto.objective_latency");
        if (objective == AutoObjective.BestVisuals)
            return tier >= PerformanceTier.Medium
                ? Pick(OptimizationMode.Quality, "auto.objective_visuals")
                : Pick(OptimizationMode.Balanced, "auto.visuals_limited");

        if (tier <= PerformanceTier.Low) return Pick(OptimizationMode.Competitive, "auto.low_tier");
        if (laptop && tier == PerformanceTier.Medium) return Pick(OptimizationMode.Balanced, "auto.laptop_medium");
        if (refresh >= 144) return Pick(OptimizationMode.Competitive, "auto.high_refresh");
        if (tier == PerformanceTier.High && refresh is > 0 and <= 100) return Pick(OptimizationMode.Quality, "auto.headroom");
        return Pick(OptimizationMode.Balanced, "auto.default_balanced");
    }

    private int? ResolveFpsCap(
        OptimizationPreferences prefs, OptimizationMode requested, int refresh, double effective, bool laptop,
        OptimizationMode resolved, bool cpuLimited, List<Msg> reasons, List<Msg> warnings)
    {
        // Manual and Preserve come from the Custom profile or advanced page; other modes always use Auto.
        var mode = requested == OptimizationMode.Custom ? prefs.FpsCapMode : FpsCapMode.Auto;
        if (mode == FpsCapMode.Preserve) return null;
        if (mode == FpsCapMode.Manual)
        {
            if (FpsCapValidator.Validate(prefs.ManualFpsCap) is { } error)
            {
                warnings.Add(error);
                return null;
            }
            return prefs.ManualFpsCap;
        }

        if (refresh <= 0)
        {
            warnings.Add(new Msg("warn.refresh_unknown"));
            return null;
        }
        reasons.Add(new Msg("reason.cap_input", refresh.ToString(CultureInfo.InvariantCulture)));
        return RecommendCap(refresh, effective, laptop, resolved, cpuLimited);
    }

    public int RecommendCap(int refresh, double effective, bool laptop, OptimizationMode mode, bool cpuLimited)
    {
        var f = data.FpsCap;
        if (refresh <= 60) return Math.Max(refresh, FpsCapValidator.Minimum);
        int ceiling = Math.Max(60, refresh - f.Margin);
        int target = mode switch
        {
            OptimizationMode.Competitive => ceiling,
            OptimizationMode.Quality => laptop ? f.QualityLaptop : f.QualityDesktop,
            _ => effective >= f.BalancedHighScore ? f.BalancedHigh
               : effective >= f.BalancedMediumScore ? f.BalancedMedium
               : f.BalancedLow,
        };
        if (cpuLimited) target = Math.Min(target, f.CpuLimitedCeiling);
        return Math.Min(ceiling, target);
    }

    private void AddModeSettings(
        List<PlannedSetting> s, OptimizationMode mode, PerformanceTier tier, GpuInfo? gpu, HardwareSnapshot hw, List<Msg> warnings)
    {
        var levels = mode switch
        {
            OptimizationMode.Quality => High,
            OptimizationMode.Balanced => Mid,
            _ => Low,
        };

        void Add(string key, string value, string reason) => s.Add(new PlannedSetting(key, value, new Msg(reason)));

        if (mode != OptimizationMode.Quality) Add("GstRender.VSyncMode", "0", "reason.vsync_off");
        Add("GstRender.DRSEnabled", "0", "reason.drs_off");

        Add("GstRender.AmbientOcclusion", levels.Ao, "reason.mode_level");
        Add("GstRender.RaytracingAmbientOcclusion", "0", "reason.rtao_off");
        Add("GstRender.EffectsQuality", levels.Effects, "reason.mode_level");
        Add("GstRender.LightingQuality", levels.Lighting, "reason.mode_level");
        Add("GstRender.LocalLightShadowQuality", levels.LocalShadow, "reason.mode_level");
        Add("GstRender.ShadowQuality", levels.Shadow, "reason.mode_level");
        Add("GstRender.SunShadowQuality", levels.SunShadow, "reason.mode_level");
        Add("GstRender.TransparentShadows", "0", "reason.transparent_shadows");
        Add("GstRender.VolumetricQuality", levels.Volumetric, "reason.mode_level");
        Add("GstRender.ReflectionQuality", levels.Reflection, "reason.mode_level");
        Add("GstRender.ScreenSpaceReflections", levels.Ssr, "reason.mode_level");

        var (mesh, terrain, significance) = tier switch
        {
            PerformanceTier.High => ("2", "2", "2"),
            PerformanceTier.Medium => ("2", "1", "2"),
            _ => ("1", "0", "1"),
        };
        Add("GstRender.TextureQuality", ChooseTexture(mode, tier, gpu, hw, warnings), "reason.texture_vram");
        Add("GstRender.TextureFiltering", "2", "reason.texture_filter");
        Add("GstRender.MeshQuality", mesh, "reason.tier_level");
        Add("GstRender.TerrainQuality", terrain, "reason.tier_level");
        Add("GstRender.SignificanceQuality", significance, "reason.tier_level");

        // Quality mode leaves look-and-feel choices to the player; v1.4.0 forced these everywhere.
        if (mode != OptimizationMode.Quality)
        {
            Add("GstRender.UndergrowthQuality", "0", "reason.visibility");
            Add("GstRender.PostProcessQuality", "0", "reason.visibility");
            Add("GstRender.MotionBlurWeapon", "0.000000", "reason.clarity");
            Add("GstRender.MotionBlurWorld", "0.000000", "reason.clarity");
            Add("GstRender.ChromaticAberration", "0", "reason.clarity");
            Add("GstRender.FilmGrain", "0", "reason.clarity");
            Add("GstRender.Vignette", "0", "reason.clarity");
            Add("GstRender.LensDistortion", "0", "reason.clarity");
            Add("GstRender.WeaponDOF", "0", "reason.clarity");
        }

        if (mode == OptimizationMode.Competitive)
        {
            Add("GstRender.FutureFrameRendering", "0", "reason.future_frames");
            foreach (var key in new[]
            {
                "GstRender.NVIDIAFrameGenerationEnabled", "GstRender.NVIDIADynamicFrameGenerationEnabled",
                "GstRender.NvidiaMultiFrameGeneration", "GstRender.AMDFrameGenerationEnabled",
                "GstRender.IntelFrameGenerationEnabled", "GstRender.IntelMultiFrameGeneration",
            })
                Add(key, "0", "reason.framegen_off");

            if (gpu?.Vendor == GpuVendor.Nvidia) Add("GstRender.NvidiaLowLatency", "1", "reason.low_latency");
            else if (gpu?.Vendor == GpuVendor.Amd) Add("GstRender.AMDLowLatency", "1", "reason.low_latency");
            else if (gpu is { Vendor: GpuVendor.Intel, Kind: GpuKind.Discrete }) Add("GstRender.IntelLowLatency", "1", "reason.low_latency");
        }
    }

    private string ChooseTexture(OptimizationMode mode, PerformanceTier tier, GpuInfo? gpu, HardwareSnapshot hw, List<Msg> warnings)
    {
        var vram = gpu?.VramGb;
        if (vram is null) warnings.Add(new Msg("warn.vram_unknown"));

        int level = vram switch
        {
            null => 0,
            _ when vram >= data.Texture.UltraMinVramGb && tier == PerformanceTier.High => 3,
            _ when vram >= data.Texture.HighMinVramGb => 2,
            _ when vram >= data.Texture.MediumMinVramGb => 1,
            _ => 0,
        };

        // Quality never pushes past what VRAM supports beyond the one-step floor v1.4.0 used.
        if (mode == OptimizationMode.Quality && level < 1 && vram >= 4) level = 1;

        if (hw.Memory?.TotalGb < data.Memory.LowRamGbBelow)
        {
            warnings.Add(new Msg("warn.low_ram"));
            level = Math.Min(level, 1);
        }
        return level.ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyOverrides(List<PlannedSetting> settings, OptimizationPreferences prefs, List<Msg> warnings)
    {
        foreach (var (key, value) in prefs.Overrides)
        {
            var def = catalog.Find(key);
            if (def is null || def.Kind == SettingKind.Preserved)
            {
                warnings.Add(new Msg("warn.override_rejected", key));
                continue;
            }
            int i = settings.FindIndex(p => p.Key == key);
            if (value == OptimizationPreferences.PreserveValue)
            {
                if (i >= 0) settings.RemoveAt(i);
                continue;
            }
            if (!def.IsWritableValue(value))
            {
                warnings.Add(new Msg("warn.override_rejected", key));
                continue;
            }
            var planned = new PlannedSetting(key, value, new Msg("reason.user_override"));
            if (i >= 0) settings[i] = planned;
            else settings.Add(planned);
        }
    }
}
