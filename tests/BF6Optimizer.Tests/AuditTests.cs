using BF6Optimizer.Core.Audit;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Diagnostics;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Infrastructure.Hardware;

namespace BF6Optimizer.Tests;

internal static class AuditFixtures
{
    public const long Gb = 1024L * 1024 * 1024;

    public static MemoryModule Module(string slot = "DIMM_A1", string bank = "", int? cfg = 6000, int? rated = 6000, long gb = 16, string part = "X") =>
        new(slot, bank, "ACME", part, gb * Gb, 34, cfg, rated, 8);

    public static DisplayFacts Display(string name, double hz, int w, int h, bool primary, params (int W, int H, int Hz)[] modes) => new()
    {
        Display = new DisplayInfo(name, name, w, h, hz, primary),
        Modes = modes.Select(m => new DisplayMode(m.W, m.H, m.Hz)).ToList(),
    };

    public static SystemAuditSnapshot Healthy() => new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
        Full = true,
        Hardware = Fixtures.Desktop,
        Memory = new MemoryFacts
        {
            Modules = [Module("DIMM_A2"), Module("DIMM_B2")],
            TotalPhysical = 32 * Gb, AvailablePhysical = 20 * Gb,
            CommitTotal = 16 * Gb, CommitLimit = 40 * Gb, PagefileAutoManaged = true,
            PageFiles = [new PageFile("pagefile.sys", 8192, 100)],
        },
        Cpu = new CpuFacts { Name = "AMD Ryzen 7 7800X3D", PhysicalCores = 8, LogicalProcessors = 16, IdleLoadPercent = 3, AffinityUnrestricted = true, L3CacheGroups = 1 },
        Power = new PowerFacts { SchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", SchemeName = "Balanced", OnAc = true, BatterySaver = false, ProcessorMinPercentAc = 5, ProcessorMaxPercentAc = 100 },
        Displays = [Display("D1", 239.76, 2560, 1440, true, (2560, 1440, 240), (1920, 1080, 240))],
        Windows = new WindowsFacts { Build = "26300", Caption = "Windows 11", AutoGameMode = 1, GameDvrEnabled = 0, AppCaptureEnabled = 0, HagsMode = 2, RebootPending = false },
        Security = new SecurityFacts { SecureBoot = true, TpmPresent = true, VbsRunning = true, HvciRunning = true, HypervisorPresent = true, AntivirusProducts = ["Defender"] },
        Storage = new StorageFacts { Volumes = [new VolumeFacts("C:", "NTFS", 1000 * Gb, 500 * Gb, true, true, "SSD")] },
        Background = new BackgroundFacts { BackgroundCpuPercent = 3, ProcessCount = 200 },
        Network = new NetworkFacts { Adapters = [new AdapterFacts("Ethernet", "Ethernet", 1000, true, false, [], "192.168.1.1")] },
        Firmware = new FirmwareFacts { BiosVersion = "1.0", BootMode = "UEFI" },
    };

    public static AuditResult Eval(SystemAuditSnapshot s) => new AuditEvaluator().Evaluate(s);

    public static AuditCheck Check(AuditResult r, string id) => r.Checks.Single(c => c.Id == id);
}

public class MemoryAnalysisTests
{
    [Theory]
    [InlineData(6000, 3000)]
    [InlineData(4800, 2400)]
    [InlineData(3200, 1600)]
    public void Mts_is_twice_the_clock(int mts, int mhz)
    {
        Assert.Equal(mhz, MemoryAnalysis.ClockMHzFromTransferRate(mts));
        Assert.Equal(mts, MemoryAnalysis.TransferRateFromClockMHz(mhz));
    }

    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(0, null)]
    [InlineData(null, null)]
    public void Ddr_generation(int? type, string? name) => Assert.Equal(name, MemoryAnalysis.DdrGeneration(type));

    [Fact]
    public void Xmp_is_never_claimed_even_at_high_speed()
    {
        var c = AuditFixtures.Check(AuditFixtures.Eval(AuditFixtures.Healthy()), "ram_xmp");
        Assert.Equal(AuditStatus.ManualVerification, c.Status);
        Assert.Equal("val.xmp_unverified", c.Detected.Key);
        Assert.False(c.Scored);
    }

    [Fact]
    public void Below_rated_is_investigation_not_proof()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Memory = s.Memory! with { Modules = [AuditFixtures.Module(cfg: 4800, rated: 6000)] } };
        var c = AuditFixtures.Check(AuditFixtures.Eval(s), "ram_speed_below_rated");
        Assert.Equal(AuditStatus.ManualVerification, c.Status);
        Assert.Equal(EvidenceKind.Inference, c.Evidence);
        Assert.True(c.Priority <= Priority.Medium);
    }

    [Fact]
    public void Ddr5_4800_at_rated_speed_is_not_a_fault()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Memory = s.Memory! with { Modules = [AuditFixtures.Module(cfg: 4800, rated: 4800)] } };
        Assert.Equal(AuditStatus.Acceptable, AuditFixtures.Check(AuditFixtures.Eval(s), "ram_speed").Status);
    }

    [Fact]
    public void Single_module_never_claims_dual_channel() =>
        Assert.Equal(ChannelEvidence.SingleModule, MemoryAnalysis.AssessChannels([AuditFixtures.Module()]));

    [Fact]
    public void Two_modules_without_labels_stay_unknown()
    {
        var modules = new[] { AuditFixtures.Module("SODIMM1"), AuditFixtures.Module("SODIMM2") };
        Assert.Equal(ChannelEvidence.Unknown, MemoryAnalysis.AssessChannels(modules));
    }

    [Theory]
    [InlineData("DIMM_A2", "DIMM_B2", ChannelEvidence.MultipleChannelsReported)]
    [InlineData("Controller0-ChannelA-DIMM0", "Controller0-ChannelB-DIMM0", ChannelEvidence.MultipleChannelsReported)]
    [InlineData("DIMM_A1", "DIMM_A2", ChannelEvidence.SameChannelReported)]
    public void Labels_supply_channel_evidence(string a, string b, ChannelEvidence expected) =>
        Assert.Equal(expected, MemoryAnalysis.AssessChannels([AuditFixtures.Module(a), AuditFixtures.Module(b)]));

    [Fact]
    public void Mixed_kits_need_verification()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Memory = s.Memory! with { Modules = [AuditFixtures.Module(gb: 8), AuditFixtures.Module(gb: 16)] } };
        Assert.Equal(AuditStatus.ManualVerification, AuditFixtures.Check(AuditFixtures.Eval(s), "ram_mixed").Status);
    }

    [Theory]
    [InlineData(4, AuditStatus.Optimal)]
    [InlineData(5, AuditStatus.OptimizationAvailable)]
    [InlineData(2, AuditStatus.Warning)]
    public void Memory_pressure_thresholds(int availableOf32Tenths, AuditStatus expected)
    {
        var s = AuditFixtures.Healthy();
        long available = availableOf32Tenths switch { 4 => 12, 5 => 5, _ => 2 } * AuditFixtures.Gb;
        s = s with { Memory = s.Memory! with { AvailablePhysical = available } };
        Assert.Equal(expected, AuditFixtures.Check(AuditFixtures.Eval(s), "ram_pressure").Status);
    }

    [Fact]
    public void No_pagefile_warns()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Memory = s.Memory! with { PageFiles = [], PagefileAutoManaged = false } };
        Assert.Equal(AuditStatus.Warning, AuditFixtures.Check(AuditFixtures.Eval(s), "ram_pagefile").Status);
    }
}

public class DisplayAuditTests
{
    [Fact]
    public void Sixty_on_a_240_panel_is_high_priority()
    {
        var s = AuditFixtures.Healthy() with
        {
            Displays = [AuditFixtures.Display("D1", 60, 2560, 1440, true, (2560, 1440, 240), (2560, 1440, 60))],
        };
        var c = AuditFixtures.Check(AuditFixtures.Eval(s), "disp_refresh:D1");
        Assert.Equal(AuditStatus.OptimizationAvailable, c.Status);
        Assert.Equal(Priority.High, c.Priority);
        Assert.Equal("val.refresh_mismatch", c.Detected.Key);
        Assert.Contains("240", c.Detected.Args);
    }

    [Fact]
    public void Max_rate_is_per_current_resolution()
    {
        var d = AuditFixtures.Display("D1", 144, 3840, 2160, true, (3840, 2160, 144), (1920, 1080, 240));
        Assert.Equal(144, RefreshAnalysis.MaxHzAtCurrentResolution(d));
        Assert.False(RefreshAnalysis.HasMismatch(d));
    }

    [Fact]
    public void Rounding_rate_is_not_a_mismatch() =>
        Assert.False(RefreshAnalysis.HasMismatch(AuditFixtures.Display("D1", 59.94, 1920, 1080, true, (1920, 1080, 60))));

    [Fact]
    public void Mixed_refresh_only_gaming_display_scores_and_gets_high()
    {
        var s = AuditFixtures.Healthy() with
        {
            GamingDisplay = "D2",
            Displays =
            [
                AuditFixtures.Display("D1", 60, 1920, 1080, true, (1920, 1080, 60), (1920, 1080, 75)),
                AuditFixtures.Display("D2", 120, 2560, 1440, false, (2560, 1440, 360)),
            ],
        };
        var r = AuditFixtures.Eval(s);
        Assert.Equal(Priority.High, AuditFixtures.Check(r, "disp_refresh:D2").Priority);
        Assert.False(AuditFixtures.Check(r, "disp_refresh:D1").Scored);
    }

    [Fact]
    public void No_modes_is_unknown_not_failure()
    {
        var s = AuditFixtures.Healthy() with { Displays = [AuditFixtures.Display("D1", 60, 1920, 1080, true)] };
        Assert.Equal(AuditStatus.Unknown, AuditFixtures.Check(AuditFixtures.Eval(s), "disp_refresh:D1").Status);
    }

    [Fact]
    public void Vrr_is_never_confirmed_from_the_registry()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Windows = s.Windows! with { DirectXGlobalSettings = "VRROptimizeEnable=1;" } };
        Assert.Equal(AuditStatus.ManualVerification, AuditFixtures.Check(AuditFixtures.Eval(s), "disp_vrr").Status);
    }

    [Theory]
    [InlineData(240, true, 237)]
    [InlineData(240, false, 240)]
    [InlineData(60, true, 60)]
    public void Starting_cap_is_conditional_on_vrr(int hz, bool vrr, int expected) =>
        Assert.Equal(expected, RefreshAnalysis.StartingCap(hz, vrr));

    [Fact]
    public void Edid_manufacturer_decodes() => Assert.Equal("SAM", DisplayEnumerator.DecodeEdidManufacturer(0x2D4C));
}

public class SystemAuditTests
{
    [Fact]
    public void Security_on_is_not_penalized()
    {
        var on = AuditFixtures.Healthy();
        var off = on with { Security = new SecurityFacts { SecureBoot = false, VbsRunning = false, HvciRunning = false } };
        var a = AuditFixtures.Eval(on);
        Assert.Equal(a.Readiness.Score, AuditFixtures.Eval(off).Readiness.Score);
        Assert.All(a.Checks.Where(c => c.Category == AuditCategory.Security), c => Assert.False(c.IsActionable));
    }

    [Fact]
    public void Older_hardware_with_correct_configuration_is_not_penalized()
    {
        var s = AuditFixtures.Healthy();
        var old = s with
        {
            Cpu = s.Cpu! with { Name = "Intel Core i5-8400", PhysicalCores = 6, LogicalProcessors = 6 },
            Hardware = s.Hardware! with { Gpus = [Fixtures.Gpu("NVIDIA GeForce GTX 1060", 6)] },
        };
        Assert.Equal(AuditFixtures.Eval(s).Readiness.Score, AuditFixtures.Eval(old).Readiness.Score);
    }

    [Fact]
    public void Unknown_is_excluded_from_score_and_lowers_coverage()
    {
        var s = AuditFixtures.Healthy();
        var unknown = s with { Cpu = s.Cpu! with { IdleLoadPercent = null, AffinityUnrestricted = null } };
        var a = AuditFixtures.Eval(s).Readiness;
        var b = AuditFixtures.Eval(unknown).Readiness;
        Assert.Equal(100 - (100 - a.Score!.Value), a.Score);
        Assert.True(b.CoveragePercent < a.CoveragePercent);
        Assert.True(b.Score >= a.Score - 1);
    }

    [Fact]
    public void Score_and_coverage_formula()
    {
        AuditCheck C(AuditStatus s) => new() { Id = Guid.NewGuid().ToString(), Category = AuditCategory.Memory, Status = s, Detected = new("raw") };
        var r = GamingReadinessCalculator.Calculate([C(AuditStatus.Optimal), C(AuditStatus.Critical), C(AuditStatus.Unknown), C(AuditStatus.ManualVerification)]);
        Assert.Equal(50, r.Score);
        Assert.Equal(50, r.CoveragePercent);
        Assert.Equal(2, r.ScoredChecks);
        Assert.Equal(4, r.EligibleChecks);
    }

    [Fact]
    public void No_measurable_checks_gives_no_score_rather_than_zero() =>
        Assert.Null(GamingReadinessCalculator.Calculate([]).Score);

    [Fact]
    public void Partial_failure_keeps_other_sections()
    {
        var s = AuditFixtures.Healthy() with { Memory = null, Cpu = null, Storage = null };
        var r = AuditFixtures.Eval(s);
        Assert.Equal(AuditStatus.Unknown, AuditFixtures.Check(r, "ram_unavailable").Status);
        Assert.Contains(r.Checks, c => c.Category == AuditCategory.Display && c.Status == AuditStatus.Optimal);
        Assert.NotNull(r.Readiness.Score);
    }

    [Fact]
    public void Empty_snapshot_evaluates_without_throwing()
    {
        var r = AuditFixtures.Eval(new SystemAuditSnapshot());
        Assert.All(r.Checks, c => Assert.NotEqual(AuditStatus.Optimal, c.Status));
    }

    [Fact]
    public void Unknown_gpu_and_missing_telemetry_are_handled()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Hardware = s.Hardware! with { Gpus = [Fixtures.Gpu("Mystery GPU", 0, GpuKind.Unknown)] } };
        var r = AuditFixtures.Eval(s);
        Assert.Equal(AuditStatus.Unknown, AuditFixtures.Check(r, "gpu_vram").Status);
        Assert.Equal(AuditStatus.Unknown, AuditFixtures.Check(r, "gpu_driver_age").Status);
        Assert.Equal("val.temp_unavailable", AuditFixtures.Check(r, "th_gpu").Detected.Key);
    }

    [Fact]
    public void Software_only_adapter_is_critical()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Hardware = s.Hardware! with { Gpus = [Fixtures.Gpu("Microsoft Basic Render Driver", 0, GpuKind.Software)] } };
        Assert.Equal(AuditStatus.Critical, AuditFixtures.Check(AuditFixtures.Eval(s), "gpu_present").Status);
    }

    [Fact]
    public void Hags_either_way_is_not_a_finding()
    {
        foreach (var mode in new int?[] { 1, 2 })
        {
            var s = AuditFixtures.Healthy();
            var c = AuditFixtures.Check(AuditFixtures.Eval(s with { Windows = s.Windows! with { HagsMode = mode } }), "gpu_hags");
            Assert.Equal(AuditStatus.Acceptable, c.Status);
            Assert.False(c.Scored);
            Assert.Equal(EvidenceKind.Inference, c.Evidence);
        }
    }

    [Theory]
    [InlineData(true, null, AuditStatus.Optimal)]
    [InlineData(false, null, AuditStatus.Warning)]
    [InlineData(true, AuditEvaluator.PowerSaverGuid, AuditStatus.OptimizationAvailable)]
    public void Power_assessment(bool onAc, string? guid, AuditStatus expected)
    {
        var s = AuditFixtures.Healthy();
        s = s with { Power = s.Power! with { OnAc = onAc, SchemeGuid = guid ?? s.Power!.SchemeGuid } };
        Assert.Equal(expected, AuditFixtures.Check(AuditFixtures.Eval(s), "win_power").Status);
    }

    [Fact]
    public void Balanced_plan_is_not_flagged_as_needing_high_performance() =>
        Assert.Equal(AuditStatus.Optimal, AuditFixtures.Check(AuditFixtures.Eval(AuditFixtures.Healthy()), "win_power").Status);

    [Fact]
    public void Game_mode_unset_is_inference_not_failure()
    {
        var s = AuditFixtures.Healthy();
        var c = AuditFixtures.Check(AuditFixtures.Eval(s with { Windows = s.Windows! with { AutoGameMode = null } }), "win_gamemode");
        Assert.Equal(EvidenceKind.Inference, c.Evidence);
        Assert.False(c.IsActionable);
    }

    [Fact]
    public void Missing_admin_access_leaves_tpm_unknown()
    {
        var s = AuditFixtures.Healthy();
        var c = AuditFixtures.Check(AuditFixtures.Eval(s with { Security = s.Security! with { TpmPresent = null } }), "sec_tpm");
        Assert.Equal(AuditStatus.Unknown, c.Status);
    }

    [Theory]
    [InlineData(2, 500, AuditStatus.Critical)]
    [InlineData(15, 500, AuditStatus.Warning)]
    [InlineData(40, 500, AuditStatus.OptimizationAvailable)]
    [InlineData(100, 500, AuditStatus.Optimal)]
    public void Disk_space_thresholds(int freeGb, int totalGb, AuditStatus expected) =>
        Assert.Equal(expected, AuditEvaluator.FreeSpace(freeGb * AuditFixtures.Gb, totalGb * AuditFixtures.Gb).Status);

    [Fact]
    public void Game_on_hdd_is_flagged()
    {
        var s = AuditFixtures.Healthy() with
        {
            Storage = new StorageFacts { Volumes = [new VolumeFacts("D:", "NTFS", 2000 * AuditFixtures.Gb, 900 * AuditFixtures.Gb, false, true, "HDD")] },
        };
        Assert.Equal(Priority.High, AuditFixtures.Check(AuditFixtures.Eval(s), "sto_game_media").Priority);
    }

    [Fact]
    public void Probe_timeout_is_unknown()
    {
        var s = AuditFixtures.Healthy() with { NetworkProbe = new NetworkProbeResult("1.1.1.1", 5, 0, null, null, null) };
        Assert.Equal(AuditStatus.Unknown, AuditFixtures.Check(AuditFixtures.Eval(s), "net_latency").Status);
    }

    [Fact]
    public void Packet_loss_warns()
    {
        var s = AuditFixtures.Healthy() with { NetworkProbe = new NetworkProbeResult("gw", 20, 18, 3, 1, null) };
        Assert.Equal(AuditStatus.Warning, AuditFixtures.Check(AuditFixtures.Eval(s), "net_latency").Status);
    }

    [Fact]
    public void Recommendations_are_sorted_and_contain_only_actionable()
    {
        var s = AuditFixtures.Healthy() with { Displays = [AuditFixtures.Display("D1", 60, 2560, 1440, true, (2560, 1440, 240))] };
        var recs = AuditFixtures.Eval(s).Recommendations.ToList();
        Assert.Equal("disp_refresh:D1", recs[0].Id);
        Assert.All(recs, c => Assert.True(c.IsActionable));
    }

    [Fact]
    public void Multi_ccd_x3d_is_checked_without_assuming_topology()
    {
        var s = AuditFixtures.Healthy();
        var dual = s with { Cpu = s.Cpu! with { Name = "AMD Ryzen 9 7950X3D", L3CacheGroups = 2 } };
        Assert.Equal(AuditStatus.ManualVerification, AuditFixtures.Check(AuditFixtures.Eval(dual), "cpu_x3d").Status);
        Assert.Equal(AuditStatus.Acceptable, AuditFixtures.Check(AuditFixtures.Eval(s), "cpu_x3d").Status);
    }
}

public class AuditReportTests
{
    private static string Tr(Msg m) => m.Key;

    [Fact]
    public void Reports_drop_ips_and_paths()
    {
        var s = AuditFixtures.Healthy() with
        {
            Windows = AuditFixtures.Healthy().Windows! with { Caption = @"C:\Users\samplename 192.168.1.20" },
        };
        var r = AuditFixtures.Eval(s);
        var redactor = new Redactor([(@"C:\Users\samplename", "%USERPROFILE%")]);
        var cmp = AuditReportExporter.Compare(null, r);
        foreach (var text in new[] { AuditReportExporter.ToJson(r, cmp, redactor), AuditReportExporter.ToHtml(r, cmp, Tr, redactor), AuditReportExporter.ToText(r, cmp, Tr, redactor) })
        {
            Assert.DoesNotContain("192.168.1.20", text);
            Assert.DoesNotContain("samplename", text);
        }
    }

    [Fact]
    public void Html_is_self_contained_and_escaped()
    {
        var s = AuditFixtures.Healthy();
        s = s with { Hardware = s.Hardware! with { Gpus = [Fixtures.Gpu("<script>x</script>", 8)] } };
        var r = AuditFixtures.Eval(s);
        var html = AuditReportExporter.ToHtml(r, AuditReportExporter.Compare(null, r), m => m.Args.Length > 0 ? m.Args[0] : m.Key, new Redactor([]));
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("http", html);
    }

    [Fact]
    public void Comparison_lists_changed_checks()
    {
        var before = AuditFixtures.Eval(AuditFixtures.Healthy());
        var s = AuditFixtures.Healthy();
        var after = AuditFixtures.Eval(s with { Windows = s.Windows! with { AutoGameMode = 0 } });
        var cmp = AuditReportExporter.Compare(before, after);
        Assert.Contains(cmp.Changes, c => c.Id == "win_gamemode" && c.After == AuditStatus.OptimizationAvailable);
    }

    [Fact]
    public void Audit_result_survives_json_round_trip()
    {
        var r = AuditFixtures.Eval(AuditFixtures.Healthy());
        var back = Json.Deserialize<AuditResult>(Json.Serialize(r))!;
        Assert.Equal(r.Checks.Count, back.Checks.Count);
        Assert.Equal(r.Readiness.Score, back.Readiness.Score);
        Assert.Equal(r.Checks[0].Detected.Key, back.Checks[0].Detected.Key);
    }
}

public class MonitoringTests
{
    private sealed class StepSampler : IPerformanceSampler
    {
        public int Calls;
        public int ThrowOn = -1;

        public Task<PerformanceSample> CaptureAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++Calls == ThrowOn) throw new InvalidOperationException();
            return Task.FromResult(new PerformanceSample { CpuPercent = Calls });
        }
    }

    [Fact]
    public void Aggregation_ignores_unavailable_values()
    {
        var m = SessionAggregator.Summarize([10, null, 30]);
        Assert.Equal((20.0, 30.0, 2), (m.Average!.Value, m.Peak!.Value, m.Samples));
        Assert.Null(SessionAggregator.Summarize([null]).Average);
    }

    [Fact]
    public void Session_never_claims_fps_and_flags_missing_gpu()
    {
        var report = SessionAggregator.Build([], [new PerformanceSample { CpuPercent = 50 }]);
        Assert.Contains(report.Notes, n => n.Key == "session.no_fps");
        Assert.Contains(report.Notes, n => n.Key == "session.no_gpu");
    }

    [Fact]
    public void High_total_cpu_alone_names_no_bottleneck()
    {
        var report = SessionAggregator.Build([], [new PerformanceSample { CpuPercent = 99, GpuPercent = 99 }]);
        Assert.DoesNotContain(report.Notes, n => n.Key == "session.thread_saturated");
    }

    [Fact]
    public void Saturated_thread_with_free_gpu_is_noted()
    {
        var report = SessionAggregator.Build([], [new PerformanceSample { PerLogicalCpu = [99, 10], GpuPercent = 50 }]);
        Assert.Contains(report.Notes, n => n.Key == "session.thread_saturated");
    }

    [Fact]
    public void Series_is_bounded()
    {
        var s = new BoundedSeries<int>(3);
        for (int i = 0; i < 10; i++) s.Add(i);
        Assert.Equal([7, 8, 9], s.ToList());
    }

    [Fact]
    public async Task Sampling_stops_on_cancel_and_survives_failed_sample()
    {
        var sampler = new StepSampler { ThrowOn = 2 };
        var samples = new List<PerformanceSample>();
        using var cts = new CancellationTokenSource();
        await SamplingLoop.RunAsync(sampler, TimeSpan.FromMilliseconds(5), s =>
        {
            samples.Add(s);
            if (samples.Count == 3) cts.Cancel();
        }, cts.Token);
        Assert.Equal(3, samples.Count);
        Assert.Equal(4, sampler.Calls);
    }
}

public class SafeActionTests
{
    private sealed class MemoryBackup : IPreferenceBackupStore
    {
        public PreferenceBackup? Value;

        public PreferenceBackup? Load() => Value;

        public void Save(PreferenceBackup backup) => Value = backup;

        public void Clear() => Value = null;
    }

    private readonly FakeRegistry _reg = new();
    private readonly MemoryBackup _backup = new();
    private SafePreferenceActions Actions => new(_reg, _backup, new SystemClock());

    [Fact]
    public void Apply_then_restore_returns_exact_previous_state()
    {
        _reg.Values[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = 0;
        Assert.True(Actions.Apply().Success);
        Assert.Equal(1, _reg.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);

        Assert.True(Actions.Restore().Success);
        Assert.Equal(0, _reg.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);
        Assert.False(_reg.Read(@"System\GameConfigStore", "GameDVR_Enabled").Exists);
    }

    [Fact]
    public void Second_apply_keeps_the_original_backup()
    {
        _reg.Values[@"Software\Microsoft\GameBar\AutoGameModeEnabled"] = 0;
        Actions.Apply();
        Actions.Apply();
        Actions.Restore();
        Assert.Equal(0, _reg.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);
    }

    [Fact]
    public void Restore_without_backup_fails_cleanly() => Assert.False(Actions.Restore().Success);

    [Fact]
    public void Preview_shows_before_values()
    {
        _reg.Values[@"System\GameConfigStore\GameDVR_Enabled"] = 1;
        var p = Actions.Preview().Single(x => x.Preference.Id == "GameDvrEnabled");
        Assert.Equal(1, p.Before);
    }

    [Theory]
    [InlineData("Steam", @"C:\Steam\steam.exe -silent", StartupCategory.GameLauncher)]
    [InlineData("Discord", "x", StartupCategory.Communication)]
    [InlineData("SecurityHealth", "x", StartupCategory.Security)]
    [InlineData("Whatever", @"D:\tool.exe", StartupCategory.Other)]
    public void Startup_categories(string name, string cmd, StartupCategory expected) =>
        Assert.Equal(expected, StartupClassifier.Classify(name, cmd));
}
