using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Infrastructure.Hardware;

namespace BF6Optimizer.Tests;

public class GpuParsingTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", GpuVendor.Nvidia, "RTX 4080 SUPER")]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", GpuVendor.Nvidia, "RTX 5070 TI")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", GpuVendor.Nvidia, "RTX 4070 TI SUPER")]
    [InlineData("NVIDIA GeForce RTX 2060", GpuVendor.Nvidia, "RTX 2060")]
    [InlineData("NVIDIA GeForce RTX 9090", GpuVendor.Nvidia, "RTX 9090")]
    [InlineData("AMD Radeon RX 7900 XTX", GpuVendor.Amd, "RX 7900 XTX")]
    [InlineData("AMD Radeon(TM) RX 9070 XT", GpuVendor.Amd, "RX 9070 XT")]
    [InlineData("Radeon RX 5700 XT", GpuVendor.Amd, "RX 5700 XT")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel, "ARC A770")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", GpuVendor.Intel, "ARC B580")]
    public void Parses_discrete_models(string name, GpuVendor vendor, string key)
    {
        var id = GpuNameParser.Parse(name);
        Assert.Equal(vendor, id.Vendor);
        Assert.Equal(key, id.LookupKey);
        Assert.Equal(GpuKind.Discrete, GpuNameParser.ClassifyKind(id, null));
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) Arc(TM) Graphics")]        // Meteor Lake iGPU; v1.4.0 treated this as discrete
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)")]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("AMD Radeon RX Vega 11 Graphics")]
    public void Integrated_names_stay_integrated_even_with_large_carveout(string name)
    {
        Assert.Equal(GpuKind.Integrated, GpuNameParser.ClassifyKind(GpuNameParser.Parse(name), 8L * 1024 * 1024 * 1024));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti Laptop GPU", true)]
    [InlineData("AMD Radeon RX 6800S", true)]
    [InlineData("AMD Radeon RX 7600M XT", true)]
    [InlineData("Intel(R) Arc(TM) A370M Graphics", true)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    public void Mobile_parts_are_recognized(string name, bool mobile) =>
        Assert.Equal(mobile, GpuNameParser.Parse(name).MobileName);

    [Fact]
    public void Mobile_suffix_does_not_change_lookup_model() =>
        Assert.Equal("RX 7600 XT", GpuNameParser.Parse("AMD Radeon RX 7600M XT").LookupKey);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Some Future Accelerator 9000")]
    [InlineData("Matrox G200")]
    public void Unknown_names_never_throw(string? name)
    {
        var id = GpuNameParser.Parse(name);
        Assert.Equal(GpuVendor.Unknown, id.Vendor);
        Assert.Null(id.LookupKey);
    }

    [Fact]
    public void Software_adapter_is_detected() =>
        Assert.Equal(GpuKind.Software, GpuNameParser.ClassifyKind(GpuNameParser.Parse("Microsoft Basic Render Driver"), null));

    [Fact]
    public void Unknown_name_uses_memory_evidence()
    {
        var id = GpuNameParser.Parse("Vendor X GPU");
        Assert.Equal(GpuKind.Discrete, GpuNameParser.ClassifyKind(id, 8L << 30));
        Assert.Equal(GpuKind.Integrated, GpuNameParser.ClassifyKind(id, 256L << 20));
        Assert.Equal(GpuKind.Unknown, GpuNameParser.ClassifyKind(id, null));
    }

    [Fact]
    public void Form_factor_prefers_name_then_chassis()
    {
        var mobile = GpuNameParser.Parse("NVIDIA GeForce RTX 4070 Laptop GPU");
        Assert.Equal((FormFactor.Laptop, "name"), GpuNameParser.ResolveFormFactor(mobile, FormFactor.Desktop));
        var plain = GpuNameParser.Parse("NVIDIA GeForce RTX 4070");
        Assert.Equal((FormFactor.Desktop, "chassis"), GpuNameParser.ResolveFormFactor(plain, FormFactor.Desktop));
        Assert.Equal((FormFactor.Unknown, "none"), GpuNameParser.ResolveFormFactor(plain, FormFactor.Unknown));
    }

    [Theory]
    [InlineData(new[] { 3 }, 1, FormFactor.Desktop)]
    [InlineData(new[] { 10 }, 2, FormFactor.Laptop)]
    [InlineData(new[] { 9 }, 1, FormFactor.Laptop)]
    [InlineData(new[] { 2 }, 2, FormFactor.Laptop)]
    // Regression: v1.4.0 read 8/9/10/14 as PCSystemType values, where 8 means "Maximum", not portable.
    [InlineData(new[] { 2 }, 8, FormFactor.Unknown)]
    public void Chassis_classification(int[] chassis, int pcSystemType, FormFactor expected) =>
        Assert.Equal(expected, WindowsHardwareDetector.ClassifyChassis(chassis, pcSystemType));
}

public class GpuSelectionTests
{
    private readonly PerformanceData _data = PerformanceData.LoadEmbedded();

    [Fact]
    public void Discrete_beats_integrated_on_hybrid_laptop()
    {
        var gpus = new[]
        {
            Fixtures.Gpu("AMD Radeon 780M Graphics", 4),
            Fixtures.Gpu("NVIDIA GeForce RTX 4060 Laptop GPU", 8, form: FormFactor.Laptop),
        };
        Assert.Contains("RTX 4060", GpuSelector.Select(gpus, _data)!.Name);
    }

    [Fact]
    public void Faster_of_two_discrete_cards_wins()
    {
        var gpus = new[] { Fixtures.Gpu("NVIDIA GeForce RTX 3060", 12), Fixtures.Gpu("NVIDIA GeForce RTX 4090", 24) };
        Assert.Contains("4090", GpuSelector.Select(gpus, _data)!.Name);
    }

    [Fact]
    public void Software_adapter_is_never_chosen()
    {
        var gpus = new[] { Fixtures.Gpu("Microsoft Basic Render Driver", 0, GpuKind.Software) };
        Assert.Null(GpuSelector.Select(gpus, _data));
    }

    [Fact]
    public void Manual_choice_wins_and_stale_choice_falls_back()
    {
        var gpus = new[] { Fixtures.Gpu("Intel(R) UHD Graphics 770", 1), Fixtures.Gpu("NVIDIA GeForce RTX 3060", 12) };
        Assert.Contains("UHD", GpuSelector.Select(gpus, _data, gpus[0].Id)!.Name);
        Assert.Contains("3060", GpuSelector.Select(gpus, _data, "gone")!.Name);
    }

    [Fact]
    public void Empty_list_gives_null() => Assert.Null(GpuSelector.Select([], _data));
}

public class PerformanceDataTests
{
    private readonly PerformanceData _data = PerformanceData.LoadEmbedded();

    [Fact]
    public void Embedded_data_is_valid_versioned() => Assert.Equal(PerformanceData.SupportedSchemaVersion, _data.SchemaVersion);

    [Fact]
    public void Unknown_future_model_gets_family_default_not_extrapolation()
    {
        var estimate = _data.BaseScore(GpuNameParser.Parse("NVIDIA GeForce RTX 9090"), GpuKind.Discrete);
        Assert.Equal(ScoreConfidence.Family, estimate.Confidence);
        Assert.Equal(2.0, estimate.Score);
    }

    [Fact]
    public void Unidentified_gpu_is_lowest_confidence()
    {
        var estimate = _data.BaseScore(GpuNameParser.Parse("Mystery"), GpuKind.Unknown);
        Assert.Equal(ScoreConfidence.Unknown, estimate.Confidence);
        Assert.Equal(PerformanceTier.VeryLow, _data.Tier(estimate.Score));
    }

    [Theory]
    [InlineData(1920, 1080, 0)]
    [InlineData(3840, 2160, 1.2)]
    public void Resolution_penalty_matches_legacy_formula(int w, int h, double expected) =>
        Assert.Equal(expected, _data.ResolutionPenalty(w, h));

    [Theory]
    [InlineData(150.0, 0.30)]
    [InlineData(115.0, 0.40)]
    [InlineData(90.0, 0.55)]
    [InlineData(60.0, 0.75)]
    [InlineData(null, 0.40)]
    public void Laptop_penalty_uses_power_only_when_known(double? watts, double expected) =>
        Assert.Equal(expected, _data.LaptopPenalty(GpuVendor.Nvidia, watts));

    [Theory]
    [InlineData("""{ "schemaVersion": 99 }""")]
    [InlineData("""{ "schemaVersion": 1, "defaults": { "unknown": 1, "integrated": 1 }, "tiers": { "high": 1, "medium": 2, "low": 3 } }""")]
    [InlineData("""{ "schemaVersion": 1, "defaults": { "unknown": 1 } }""")]
    public void Invalid_imports_are_rejected(string json)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => PerformanceData.Load(ms));
    }
}
