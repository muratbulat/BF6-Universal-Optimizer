using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;

namespace BF6Optimizer.Tests;

public class PlannerTests
{
    private readonly OptimizationPlanner _planner = new(PerformanceData.LoadEmbedded(), SettingCatalog.LoadEmbedded());
    private readonly SettingCatalog _catalog = SettingCatalog.LoadEmbedded();

    private OptimizationPlan Build(OptimizationMode mode, GpuInfo? gpu = null, double hz = 240, int w = 2560, int h = 1440,
        OptimizationPreferences? prefs = null, HardwareSnapshot? hw = null)
    {
        hw ??= Fixtures.Desktop;
        return _planner.Build(new PlanRequest
        {
            Mode = mode,
            Hardware = hw,
            Gpu = gpu ?? hw.Gpus[0],
            Display = new DisplayInfo("d", "d", w, h, hz, true),
            Preferences = prefs ?? new OptimizationPreferences(),
        });
    }

    private static string? Value(OptimizationPlan p, string key) => p.Settings.FirstOrDefault(s => s.Key == "GstRender." + key)?.Value;

    [Fact]
    public void Every_planned_value_is_in_the_catalog()
    {
        foreach (var mode in new[] { OptimizationMode.Auto, OptimizationMode.Competitive, OptimizationMode.Balanced, OptimizationMode.Quality })
        foreach (var gpu in new[] { Fixtures.Gpu("NVIDIA GeForce RTX 5090", 24), Fixtures.Gpu("Intel(R) UHD Graphics", 0.5), Fixtures.Gpu("Mystery", 0) })
        foreach (var s in Build(mode, gpu).Settings)
        {
            var def = _catalog.Find(s.Key);
            Assert.NotNull(def);
            Assert.True(def.IsWritableValue(s.Value), $"{s.Key}={s.Value}");
        }
    }

    [Fact]
    public void Competitive_matches_legacy_for_240hz()
    {
        var p = Build(OptimizationMode.Competitive);
        Assert.Equal("237.000000", Value(p, "FrameRateLimit"));
        Assert.Equal("1", Value(p, "FrameRateLimiterEnable"));
        Assert.Equal("0", Value(p, "VSyncMode"));
        Assert.Equal("0", Value(p, "FutureFrameRendering"));
        Assert.Equal("0", Value(p, "ShadowQuality"));
        Assert.Equal("1", Value(p, "NvidiaLowLatency"));
        Assert.Null(Value(p, "AMDLowLatency"));
    }

    [Fact]
    public void Competitive_is_not_all_zero_on_capable_hardware()
    {
        var p = Build(OptimizationMode.Competitive);
        Assert.Equal("2", Value(p, "MeshQuality"));
        Assert.NotEqual("0", Value(p, "TextureQuality"));
    }

    [Fact]
    public void Texture_follows_vram()
    {
        Assert.Equal("3", Value(Build(OptimizationMode.Competitive, Fixtures.Gpu("NVIDIA GeForce RTX 4090", 24), w: 1920, h: 1080), "TextureQuality"));
        Assert.Equal("2", Value(Build(OptimizationMode.Competitive, Fixtures.Gpu("NVIDIA GeForce RTX 3060", 12)), "TextureQuality"));
        Assert.Equal("1", Value(Build(OptimizationMode.Competitive, Fixtures.Gpu("NVIDIA GeForce RTX 3070", 8)), "TextureQuality"));
        Assert.Equal("0", Value(Build(OptimizationMode.Competitive, Fixtures.Gpu("NVIDIA GeForce RTX 3050", 6)), "TextureQuality"));
    }

    [Fact]
    public void Unknown_vram_is_conservative_and_warned()
    {
        var p = Build(OptimizationMode.Balanced, Fixtures.Gpu("NVIDIA GeForce RTX 4090", 0));
        Assert.Equal("0", Value(p, "TextureQuality"));
        Assert.Contains(p.Warnings, w => w.Key == "warn.vram_unknown");
    }

    [Fact]
    public void Unknown_gpu_is_conservative_and_warned()
    {
        var p = Build(OptimizationMode.Auto, Fixtures.Gpu("Mystery Accelerator", 12, GpuKind.Discrete));
        Assert.True(p.Tier <= PerformanceTier.Low);
        Assert.Equal("1", Value(p, "MeshQuality"));
        Assert.Contains(p.Warnings, w => w.Key == "warn.gpu_not_in_data");
    }

    [Fact]
    public void Missing_gpu_still_plans()
    {
        var hw = Fixtures.Desktop with { Gpus = [] };
        var p = _planner.Build(new PlanRequest { Hardware = hw, Mode = OptimizationMode.Auto });
        Assert.Contains(p.Warnings, w => w.Key == "warn.no_gpu");
    }

    [Fact]
    public void Auto_uses_hardware_not_always_competitive()
    {
        Assert.Equal(OptimizationMode.Competitive, Build(OptimizationMode.Auto, hz: 240).ResolvedMode);
        Assert.Equal(OptimizationMode.Quality, Build(OptimizationMode.Auto, Fixtures.Gpu("NVIDIA GeForce RTX 5090", 24), hz: 60, w: 1920, h: 1080).ResolvedMode);
        Assert.Equal(OptimizationMode.Competitive, Build(OptimizationMode.Auto, Fixtures.Gpu("NVIDIA GeForce RTX 3050", 6), hz: 60).ResolvedMode);
    }

    [Fact]
    public void Auto_respects_objective()
    {
        var latency = new OptimizationPreferences { Objective = AutoObjective.LowestLatency };
        Assert.Equal(OptimizationMode.Competitive, Build(OptimizationMode.Auto, Fixtures.Gpu("NVIDIA GeForce RTX 5090", 24), hz: 60, prefs: latency).ResolvedMode);
        var visuals = new OptimizationPreferences { Objective = AutoObjective.BestVisuals };
        Assert.Equal(OptimizationMode.Quality, Build(OptimizationMode.Auto, hz: 240, prefs: visuals).ResolvedMode);
        Assert.Equal(OptimizationMode.Balanced, Build(OptimizationMode.Auto, Fixtures.Gpu("NVIDIA GeForce RTX 3050", 6), prefs: visuals).ResolvedMode);
    }

    [Fact]
    public void Quality_preserves_taste_settings_and_framegen()
    {
        var p = Build(OptimizationMode.Quality);
        Assert.Null(Value(p, "MotionBlurWorld"));
        Assert.Null(Value(p, "VSyncMode"));
        Assert.Null(Value(p, "NVIDIAFrameGenerationEnabled"));
        Assert.Equal("1", Value(p, "ShadowQuality"));
    }

    [Fact]
    public void Upscaler_keys_are_never_planned()
    {
        foreach (var mode in Enum.GetValues<OptimizationMode>())
            Assert.DoesNotContain(Build(mode).Settings, s => s.Key.Contains("Upscaling"));
    }

    [Fact]
    public void Laptop_lowers_estimate()
    {
        var hw = Fixtures.Desktop with { SystemFormFactor = FormFactor.Laptop };
        var laptop = Build(OptimizationMode.Balanced, Fixtures.Gpu("NVIDIA GeForce RTX 4080 SUPER", 16, form: FormFactor.Laptop, powerMax: 90), hw: hw);
        Assert.True(laptop.EffectiveScore < Build(OptimizationMode.Balanced).EffectiveScore);
        Assert.Contains(laptop.Warnings, w => w.Key == "warn.laptop_power");
    }

    [Fact]
    public void Weak_cpu_limits_cap_and_warns()
    {
        var hw = Fixtures.Desktop with { Cpu = new CpuInfo("i3", "Intel", 4, 4, 3000) };
        var p = Build(OptimizationMode.Competitive, hw: hw);
        Assert.Equal(144, p.FpsCap);
        Assert.Contains(p.Warnings, w => w.Key == "warn.cpu_limited");
    }

    [Fact]
    public void Low_ram_limits_textures()
    {
        var hw = Fixtures.Desktop with { Memory = new MemoryInfo(8L << 30, null) };
        Assert.Equal("1", Value(Build(OptimizationMode.Quality, hw: hw), "TextureQuality"));
    }

    [Fact]
    public void Unknown_refresh_preserves_limiter()
    {
        var p = Build(OptimizationMode.Competitive, hz: 0);
        Assert.Null(p.FpsCap);
        Assert.Null(Value(p, "FrameRateLimit"));
        Assert.Contains(p.Warnings, w => w.Key == "warn.refresh_unknown");
    }

    [Theory]
    [InlineData(60, OptimizationMode.Competitive, 60)]
    [InlineData(144, OptimizationMode.Competitive, 141)]
    [InlineData(360, OptimizationMode.Competitive, 357)]
    [InlineData(240, OptimizationMode.Quality, 144)]
    [InlineData(100, OptimizationMode.Quality, 97)]
    public void Cap_recommendation(int hz, OptimizationMode mode, int expected) =>
        Assert.Equal(expected, _planner.RecommendCap(hz, 4, false, mode, false));

    [Fact]
    public void Custom_override_replaces_preserves_and_rejects()
    {
        var prefs = new OptimizationPreferences
        {
            BaseMode = OptimizationMode.Competitive,
            FpsCapMode = FpsCapMode.Manual,
            ManualFpsCap = 200,
            Overrides =
            {
                ["GstRender.ShadowQuality"] = "1",
                ["GstRender.VSyncMode"] = OptimizationPreferences.PreserveValue,
                ["GstRender.EffectsQuality"] = "9",
                ["GstRender.NvidiaUpscalingTechnique"] = "1",
                ["GstRender.MadeUpKey"] = "1",
            },
        };
        var p = Build(OptimizationMode.Custom, prefs: prefs);
        Assert.Equal("200.000000", Value(p, "FrameRateLimit"));
        Assert.Equal("1", Value(p, "ShadowQuality"));
        Assert.Null(Value(p, "VSyncMode"));
        Assert.Equal("0", Value(p, "EffectsQuality"));
        Assert.Null(Value(p, "NvidiaUpscalingTechnique"));
        Assert.Null(Value(p, "MadeUpKey"));
        Assert.Equal(3, p.Warnings.Count(w => w.Key == "warn.override_rejected"));
    }

    [Fact]
    public void Windows_preferences_are_optional_and_never_hags()
    {
        Assert.Empty(Build(OptimizationMode.Balanced, prefs: new OptimizationPreferences { ApplyWindowsPreferences = false }).WindowsPreferences);
        Assert.DoesNotContain(WindowsPreferences.All, p => p.Name.Contains("HwSch"));
        Assert.False(WindowsPreferences.IsAllowed(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"));
    }
}

public class FpsCapTests
{
    [Theory]
    [InlineData("237", 237)]
    [InlineData(" 30 ", 30)]
    [InlineData("1000", 1000)]
    public void Accepts_valid(string text, int expected)
    {
        Assert.Null(FpsCapValidator.ValidateText(text, out var cap));
        Assert.Equal(expected, cap);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("29")]
    [InlineData("1001")]
    [InlineData("99999999999999")]
    public void Rejects_invalid(string? text) => Assert.NotNull(FpsCapValidator.ValidateText(text, out _));

    [Fact]
    public void Formats_like_the_game() => Assert.Equal("237.000000", FpsCapValidator.Format(237));
}

public class DiffTests
{
    private readonly SettingCatalog _catalog = SettingCatalog.LoadEmbedded();

    private static OptimizationPlan Plan(params (string Key, string Value)[] settings) => new()
    {
        Settings = settings.Select(s => new PlannedSetting(s.Key, s.Value, new("r"))).ToList(),
    };

    private FileChangeSet Diff(string text, OptimizationPlan plan) =>
        DiffBuilder.Build("p", "sha", ProfileDocument.Parse(System.Text.Encoding.UTF8.GetBytes(text)), plan, _catalog);

    [Fact]
    public void Classifies_change_nochange_missing()
    {
        var d = Diff("GstRender.ShadowQuality 2\nGstRender.VSyncMode 0\n",
            Plan(("GstRender.ShadowQuality", "0"), ("GstRender.VSyncMode", "0"), ("GstRender.FilmGrain", "0")));
        Assert.Equal(ChangeStatus.Change, d.Changes[0].Status);
        Assert.Equal("2", d.Changes[0].Before);
        Assert.Equal(ChangeStatus.NoChange, d.Changes[1].Status);
        Assert.Equal(ChangeStatus.MissingInProfile, d.Changes[2].Status);
        Assert.Null(d.Changes[2].Before);
        Assert.Equal(2, d.Expected.Count);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("abc")]
    public void Unknown_current_enum_is_left_alone(string current)
    {
        var d = Diff($"GstRender.ShadowQuality {current}\n", Plan(("GstRender.ShadowQuality", "0")));
        Assert.Equal(ChangeStatus.UnknownCurrentValue, d.Changes[0].Status);
        Assert.Empty(d.Expected);
    }

    [Fact]
    public void Value_outside_writable_set_is_refused()
    {
        var d = Diff("GstRender.ShadowQuality 2\n", Plan(("GstRender.ShadowQuality", "3")));
        Assert.Equal(ChangeStatus.NotVerified, d.Changes[0].Status);
    }

    [Fact]
    public void Key_outside_catalog_is_refused()
    {
        var d = Diff("GstRender.Invented 2\n", Plan(("GstRender.Invented", "0")));
        Assert.Equal(ChangeStatus.NotVerified, d.Changes[0].Status);
    }

    [Fact]
    public void Limiter_not_enabled_without_a_settable_limit()
    {
        var d = Diff("GstRender.FrameRateLimiterEnable 0\n",
            Plan(("GstRender.FrameRateLimit", "141.000000"), ("GstRender.FrameRateLimiterEnable", "1")));
        Assert.Equal(ChangeStatus.NotVerified, d.Changes[1].Status);
    }

    [Fact]
    public void Disagreeing_duplicates_count_as_a_change()
    {
        var d = Diff("GstRender.ShadowQuality 0\nGstRender.ShadowQuality 2\n", Plan(("GstRender.ShadowQuality", "0")));
        Assert.Equal(ChangeStatus.Change, d.Changes[0].Status);
    }
}
