using System.Globalization;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Hardware;

namespace BF6Optimizer.Core.Audit;

/// <summary>Pure rules: facts in, evidence-labelled checks out. No Windows access, so all of it is testable.</summary>
public sealed class AuditEvaluator
{
    public const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const double Gb = 1024.0 * 1024 * 1024;

    public static Msg Raw(string text) => new("raw", text);
    private static Msg Val(string key, params string[] args) => new("val." + key, args);
    private static string N(double v, string format = "0.#") => v.ToString(format, CultureInfo.InvariantCulture);
    private static Msg OnOff(bool? v) => v switch { true => Val("enabled"), false => Val("disabled"), _ => Val("unknown") };

    public AuditResult Evaluate(SystemAuditSnapshot s)
    {
        var checks = new List<AuditCheck>();
        Memory(s, checks);
        Cpu(s, checks);
        Gpu(s, checks);
        Display(s, checks);
        Windows(s, checks);
        Security(s, checks);
        Storage(s, checks);
        Background(s, checks);
        Network(s, checks);
        Firmware(s, checks);
        Thermal(s, checks);
        return new AuditResult { Snapshot = s, Checks = checks, Readiness = GamingReadinessCalculator.Calculate(checks) };
    }

    private static AuditCheck Missing(string id, AuditCategory category, bool scored = true) => new()
    {
        Id = id,
        TextId = "section_unavailable",
        Category = category,
        Status = AuditStatus.Unknown,
        Detected = Val("unavailable"),
        Confidence = Confidence.None,
        Evidence = EvidenceKind.Unavailable,
        Scored = scored,
    };

    /// <summary>A quick scan skips some sections; that lowers coverage rather than hiding the gap.</summary>
    private static AuditCheck Skipped(SystemAuditSnapshot s, string id, AuditCategory category) =>
        s.Full ? Missing(id, category) : Missing(id, category) with { TextId = "full_scan_pending" };

    private static void Memory(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Memory is not { } m)
        {
            o.Add(Missing("ram_unavailable", AuditCategory.Memory));
            return;
        }

        long installed = m.Modules.Sum(x => x.CapacityBytes);
        double totalGb = (installed > 0 ? installed : m.TotalPhysical) / Gb;
        o.Add(new AuditCheck
        {
            Id = "ram_capacity",
            Category = AuditCategory.Memory,
            Detected = Raw($"{N(totalGb, "0")} GB"),
            Status = totalGb < 7.5 ? AuditStatus.Warning : totalGb < 15 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
            Priority = totalGb < 15 ? Priority.Medium : Priority.Informational,
            Relevance = Relevance.High,
            Method = "GlobalMemoryStatusEx",
            Reversible = false,
        });

        var effective = MemoryAnalysis.EffectiveMTs(m.Modules);
        var rated = MemoryAnalysis.RatedMTs(m.Modules);
        var generation = m.Modules.Select(x => MemoryAnalysis.DdrGeneration(x.SmbiosMemoryType)).FirstOrDefault(g => g is not null) ?? "";

        o.Add(effective is null
            ? new AuditCheck
            {
                Id = "ram_speed",
                Category = AuditCategory.Memory,
                Detected = Val("unavailable"),
                Status = AuditStatus.Unknown,
                Confidence = Confidence.None,
                Evidence = EvidenceKind.Unavailable,
                Method = "WMI Win32_PhysicalMemory",
            }
            : new AuditCheck
            {
                Id = rated > effective ? "ram_speed_below_rated" : "ram_speed",
                Category = AuditCategory.Memory,
                Detected = rated > effective
                    ? Val("ram_speed_rated", generation, effective.Value.ToString(), rated.Value.ToString())
                    : Val("ram_speed", generation, effective.Value.ToString()),
                // A gap is an investigation opportunity, never proof that a profile is off.
                Status = rated > effective ? AuditStatus.ManualVerification : AuditStatus.Acceptable,
                Priority = rated > effective ? Priority.Medium : Priority.Informational,
                Confidence = Confidence.Medium,
                Evidence = rated > effective ? EvidenceKind.Inference : EvidenceKind.Fact,
                Relevance = Relevance.Medium,
                Method = "WMI Win32_PhysicalMemory",
                RestartRequired = rated > effective,
            });

        o.Add(new AuditCheck
        {
            Id = "ram_xmp",
            Category = AuditCategory.Memory,
            Detected = Val("xmp_unverified"),
            Status = AuditStatus.ManualVerification,
            Confidence = Confidence.None,
            Evidence = EvidenceKind.ManualCheck,
            Priority = Priority.Low,
            Relevance = Relevance.Medium,
            Method = "-",
            RestartRequired = true,
            Scored = false,
        });

        var channels = MemoryAnalysis.AssessChannels(m.Modules);
        o.Add(new AuditCheck
        {
            Id = "ram_channels",
            Category = AuditCategory.Memory,
            Detected = channels switch
            {
                ChannelEvidence.SingleModule => Val("channels_one_module"),
                ChannelEvidence.MultipleChannelsReported => Val("channels_multi", MemoryAnalysis.ChannelsFromLabels(m.Modules).Count.ToString(), m.Modules.Count.ToString()),
                ChannelEvidence.SameChannelReported => Val("channels_same", m.Modules.Count.ToString()),
                _ => Val("channels_unknown", m.Modules.Count.ToString()),
            },
            Status = channels switch
            {
                ChannelEvidence.MultipleChannelsReported => AuditStatus.Acceptable,
                ChannelEvidence.Unknown => AuditStatus.Unknown,
                _ => AuditStatus.ManualVerification,
            },
            Priority = channels is ChannelEvidence.SingleModule or ChannelEvidence.SameChannelReported ? Priority.Medium : Priority.Informational,
            Confidence = channels == ChannelEvidence.Unknown ? Confidence.None : Confidence.Low,
            Evidence = EvidenceKind.Inference,
            Relevance = Relevance.Medium,
            Method = "SMBIOS slot labels",
            Reversible = false,
        });

        if (m.Modules.Count > 1 && !MemoryAnalysis.ModulesMatch(m.Modules))
            o.Add(new AuditCheck
            {
                Id = "ram_mixed",
                Category = AuditCategory.Memory,
                Detected = Val("modules_differ", m.Modules.Count.ToString()),
                Status = AuditStatus.ManualVerification,
                Confidence = Confidence.Medium,
                Priority = Priority.Low,
                Method = "WMI Win32_PhysicalMemory",
                Reversible = false,
            });

        if (m.TotalPhysical > 0)
        {
            double load = 100.0 * (m.TotalPhysical - m.AvailablePhysical) / m.TotalPhysical;
            o.Add(new AuditCheck
            {
                Id = "ram_pressure",
                Category = AuditCategory.Memory,
                Detected = Raw($"{N(load, "0")} %"),
                Status = load >= 90 ? AuditStatus.Warning : load >= 80 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = load >= 90 ? Priority.High : load >= 80 ? Priority.Medium : Priority.Informational,
                Evidence = EvidenceKind.Measurement,
                Relevance = Relevance.High,
                Method = "GlobalMemoryStatusEx",
            });
        }

        if (m.CommitLimit > 0)
        {
            double commit = 100.0 * m.CommitTotal / m.CommitLimit;
            o.Add(new AuditCheck
            {
                Id = "ram_commit",
                Category = AuditCategory.Memory,
                Detected = Raw($"{N(m.CommitTotal / Gb)} / {N(m.CommitLimit / Gb)} GB ({N(commit, "0")} %)"),
                Status = commit >= 90 ? AuditStatus.Warning : commit >= 80 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = commit >= 90 ? Priority.High : commit >= 80 ? Priority.Medium : Priority.Informational,
                Evidence = EvidenceKind.Measurement,
                Relevance = Relevance.High,
                Method = "GetPerformanceInfo",
            });
        }

        o.Add(new AuditCheck
        {
            Id = "ram_pagefile",
            Category = AuditCategory.Memory,
            Detected = m.PageFiles.Count == 0 && m.PagefileAutoManaged != true
                ? Val("pagefile_none")
                : m.PagefileAutoManaged == true ? Val("pagefile_auto") : Val("pagefile_custom", N(m.PageFiles.Sum(p => p.AllocatedMb) / 1024.0)),
            Status = m.PageFiles.Count == 0 && m.PagefileAutoManaged != true ? AuditStatus.Warning
                : m.PagefileAutoManaged == true ? AuditStatus.Optimal : AuditStatus.Acceptable,
            Priority = m.PageFiles.Count == 0 && m.PagefileAutoManaged != true ? Priority.High : Priority.Informational,
            Relevance = Relevance.Medium,
            Method = "WMI Win32_ComputerSystem / Win32_PageFileUsage",
            NeedsAdmin = true,
            RestartRequired = true,
        });
    }

    private static void Cpu(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Cpu is not { } c)
        {
            o.Add(Missing("cpu_unavailable", AuditCategory.Cpu));
            return;
        }

        o.Add(new AuditCheck
        {
            Id = "cpu_topology",
            Category = AuditCategory.Cpu,
            Detected = Topology(c),
            Status = c.PhysicalCores is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
            Confidence = Confidence.High,
            Method = "GetLogicalProcessorInformationEx",
            Relevance = Relevance.Medium,
            Scored = false,
            Reversible = false,
        });

        o.Add(new AuditCheck
        {
            Id = "cpu_smt",
            Category = AuditCategory.Cpu,
            Detected = c.LogicalProcessors > c.PhysicalCores ? Val("enabled")
                : c.PerformanceCores is null ? Val("smt_unconfirmed") : Val("disabled"),
            Status = c.LogicalProcessors > c.PhysicalCores ? AuditStatus.Acceptable : AuditStatus.Unknown,
            Confidence = c.LogicalProcessors > c.PhysicalCores ? Confidence.High : Confidence.Low,
            Method = "Logical / physical count",
            Scored = false,
        });

        if (c.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase))
            o.Add(new AuditCheck
            {
                Id = "cpu_x3d",
                Category = AuditCategory.Cpu,
                Detected = c.L3CacheGroups is > 1 ? Val("x3d_multi_ccd", c.L3CacheGroups.Value.ToString()) : Val("x3d_single"),
                Status = c.L3CacheGroups is > 1 ? AuditStatus.ManualVerification : AuditStatus.Acceptable,
                Priority = Priority.Low,
                Confidence = Confidence.Medium,
                Evidence = EvidenceKind.Inference,
                Method = "Processor name + L3 topology",
                Scored = false,
            });

        o.Add(c.IdleLoadPercent is { } idle
            ? new AuditCheck
            {
                Id = "cpu_idle",
                Category = AuditCategory.Cpu,
                Detected = Raw($"{N(idle, "0")} %"),
                Status = idle >= 40 ? AuditStatus.Warning : idle >= 20 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = idle >= 40 ? Priority.High : idle >= 20 ? Priority.Medium : Priority.Informational,
                Evidence = EvidenceKind.Measurement,
                Confidence = Confidence.Medium,
                Relevance = Relevance.High,
                Method = "GetSystemTimes (1 s sample)",
            }
            : Missing("cpu_idle", AuditCategory.Cpu) with { Status = AuditStatus.Unknown, TextId = "cpu_idle" });

        o.Add(new AuditCheck
        {
            Id = "cpu_affinity",
            Category = AuditCategory.Cpu,
            Detected = c.AffinityUnrestricted switch { true => Val("all_processors"), false => Val("restricted"), _ => Val("unknown") },
            Status = c.AffinityUnrestricted switch { true => AuditStatus.Optimal, false => AuditStatus.Warning, _ => AuditStatus.Unknown },
            Priority = c.AffinityUnrestricted == false ? Priority.High : Priority.Informational,
            Evidence = EvidenceKind.Measurement,
            Method = "GetProcessAffinityMask",
        });

        var p = s.Power;
        o.Add(p?.ProcessorMaxPercentAc is { } max
            ? new AuditCheck
            {
                Id = "cpu_power_state",
                Category = AuditCategory.Cpu,
                Detected = Raw($"{p.ProcessorMinPercentAc} % - {max} %"),
                Status = max < 100 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = max < 100 ? Priority.High : Priority.Informational,
                Relevance = Relevance.High,
                Method = "PowerReadACValueIndex",
            }
            : Missing("cpu_power_state", AuditCategory.Cpu) with { TextId = "cpu_power_state" });

        o.Add(new AuditCheck
        {
            Id = "cpu_bios",
            Category = AuditCategory.Cpu,
            Detected = Val("not_verifiable"),
            Status = AuditStatus.ManualVerification,
            Confidence = Confidence.None,
            Evidence = EvidenceKind.ManualCheck,
            Method = "-",
            RestartRequired = true,
            Scored = false,
        });
    }

    private static Msg Topology(CpuFacts c)
    {
        string baseText = $"{c.PhysicalCores?.ToString() ?? "?"}C / {c.LogicalProcessors?.ToString() ?? "?"}T";
        if (c.PerformanceCores is > 0 && c.EfficiencyCores is > 0) baseText += $", {c.PerformanceCores}P + {c.EfficiencyCores}E";
        if (c.L3CacheGroups is > 1) baseText += $", L3×{c.L3CacheGroups}";
        if (c.NumaNodes is > 1) baseText += $", NUMA×{c.NumaNodes}";
        return Raw(baseText);
    }

    private static void Gpu(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        var gpus = s.Hardware?.Gpus;
        if (gpus is null)
        {
            o.Add(Missing("gpu_unavailable", AuditCategory.Gpu));
            return;
        }

        var real = gpus.Where(g => g.Kind != GpuKind.Software).ToList();
        o.Add(new AuditCheck
        {
            Id = "gpu_present",
            Category = AuditCategory.Gpu,
            Detected = real.Count == 0 ? Val("gpu_none") : Raw(string.Join(" + ", real.Select(g => g.Name))),
            Status = real.Count == 0 ? AuditStatus.Critical : AuditStatus.Optimal,
            Priority = real.Count == 0 ? Priority.Critical : Priority.Informational,
            Relevance = Relevance.High,
            Method = "DXGI",
            NeedsAdmin = real.Count == 0,
        });

        var gaming = real.FirstOrDefault(g => g.Id == s.GamingGpuId) ?? real.FirstOrDefault(g => g.Kind == GpuKind.Discrete) ?? real.FirstOrDefault();
        var facts = s.Gpus?.FirstOrDefault(g => g.Id == gaming?.Id);
        if (gaming is not null)
        {
            if (facts?.DriverDate is { } date)
            {
                int months = (int)((s.Timestamp - date).TotalDays / 30.4);
                o.Add(new AuditCheck
                {
                    Id = "gpu_driver_age",
                    Category = AuditCategory.Gpu,
                    Detected = Val("driver_age", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), months.ToString(), facts.DriverVersion ?? ""),
                    Status = months >= 18 ? AuditStatus.OptimizationAvailable : months >= 9 ? AuditStatus.Acceptable : AuditStatus.Optimal,
                    Priority = months >= 18 ? Priority.Medium : Priority.Informational,
                    Relevance = Relevance.Medium,
                    Method = "WMI Win32_VideoController",
                });
            }
            else
                o.Add(Missing("gpu_driver_age", AuditCategory.Gpu) with { TextId = "gpu_driver_age" });

            o.Add(new AuditCheck
            {
                Id = "gpu_vram",
                Category = AuditCategory.Gpu,
                Detected = gaming.VramGb is { } gb ? Raw($"{N(gb)} GB ({gaming.VramSource})") : Val("unavailable"),
                Status = gaming.VramGb is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
                Confidence = gaming.VramGb is null ? Confidence.None : Confidence.High,
                Method = gaming.VramSource,
                Scored = false,
                Reversible = false,
            });

            var hybrid = real.Count > 1;
            o.Add(new AuditCheck
            {
                Id = "gpu_app_preference",
                Category = AuditCategory.Gpu,
                Detected = s.Windows?.GamePerAppGpuPreference is { } pref ? Raw(pref) : Val("not_set"),
                Status = !hybrid ? AuditStatus.Acceptable
                    : s.Windows?.GamePerAppGpuPreference is null ? AuditStatus.ManualVerification : AuditStatus.Acceptable,
                Priority = hybrid && s.Windows?.GamePerAppGpuPreference is null ? Priority.Medium : Priority.Informational,
                Confidence = Confidence.Medium,
                Method = "HKCU DirectX UserGpuPreferences",
                Scored = false,
            });
        }

        var hags = s.Windows?.HagsMode;
        o.Add(new AuditCheck
        {
            Id = "gpu_hags",
            Category = AuditCategory.Gpu,
            Detected = hags switch { 2 => Val("hags_on"), 1 => Val("hags_off"), _ => Val("not_set") },
            Status = hags is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
            Confidence = Confidence.Low,
            Evidence = EvidenceKind.Inference,
            Method = "HKLM HwSchMode (read only)",
            NeedsAdmin = true,
            RestartRequired = true,
            Scored = false,
        });

        o.Add(new AuditCheck
        {
            Id = "gpu_vendor_panel",
            Category = AuditCategory.Gpu,
            Detected = Val("vendor_panel"),
            Status = AuditStatus.ManualVerification,
            Confidence = Confidence.None,
            Evidence = EvidenceKind.ManualCheck,
            Method = "-",
            Scored = false,
        });
    }

    private static void Display(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Displays is not { Count: > 0 } displays)
        {
            o.Add(Missing("disp_unavailable", AuditCategory.Display));
            return;
        }

        var gaming = RefreshAnalysis.GamingDisplay(displays, s.GamingDisplay);
        foreach (var d in displays)
        {
            bool isGaming = ReferenceEquals(d, gaming);
            int active = RefreshAnalysis.ActiveHz(d);
            var max = RefreshAnalysis.MaxHzAtCurrentResolution(d);
            bool mismatch = RefreshAnalysis.HasMismatch(d);
            var name = string.IsNullOrWhiteSpace(d.Display.FriendlyName) ? d.Display.DeviceName : d.Display.FriendlyName;

            o.Add(new AuditCheck
            {
                Id = "disp_refresh:" + d.Display.DeviceName,
                TextId = "disp_refresh",
                Category = AuditCategory.Display,
                Detected = mismatch
                    ? Val("refresh_mismatch", name, max!.Value.ToString(), active.ToString())
                    : Val("refresh_ok", name, active.ToString(), (max ?? active).ToString()),
                Status = max is null ? AuditStatus.Unknown
                    : !mismatch ? AuditStatus.Optimal
                    : AuditStatus.OptimizationAvailable,
                Priority = mismatch ? (isGaming ? Priority.High : Priority.Low) : Priority.Informational,
                Relevance = isGaming ? Relevance.High : Relevance.Low,
                Scored = isGaming,
                Method = "EnumDisplaySettings",
            });

            if (isGaming && RefreshAnalysis.LargestMode(d) is { } largest
                && largest.Width * largest.Height > d.Display.Width * d.Display.Height)
                o.Add(new AuditCheck
                {
                    Id = "disp_resolution",
                    Category = AuditCategory.Display,
                    Detected = Raw($"{d.Display.Width}x{d.Display.Height} / {largest.Width}x{largest.Height}"),
                    Status = AuditStatus.ManualVerification,
                    Priority = Priority.Low,
                    Confidence = Confidence.Medium,
                    Method = "EnumDisplaySettings",
                    Scored = false,
                });

            o.Add(new AuditCheck
            {
                Id = "disp_hdr:" + d.Display.DeviceName,
                TextId = "disp_hdr",
                Category = AuditCategory.Display,
                Detected = Val("hdr_state", name, d.HdrSupported switch { true => "✓", false => "✗", _ => "?" }, d.HdrEnabled switch { true => "✓", false => "✗", _ => "?" }),
                Status = d.HdrSupported is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
                Confidence = d.HdrSupported is null ? Confidence.None : Confidence.High,
                Method = "DisplayConfigGetDeviceInfo",
                Scored = false,
            });
        }

        o.Add(new AuditCheck
        {
            Id = "disp_vrr",
            Category = AuditCategory.Display,
            Detected = s.Windows?.DirectXGlobalSettings?.Contains("VRROptimizeEnable=1", StringComparison.Ordinal) == true
                ? Val("vrr_opt_on") : Val("vrr_unverified"),
            Status = AuditStatus.ManualVerification,
            Priority = Priority.Low,
            Confidence = Confidence.Low,
            Evidence = EvidenceKind.ManualCheck,
            Method = "HKCU DirectXUserGlobalSettings",
            Scored = false,
        });
    }

    private static void Windows(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Windows is { } w)
        {
            // An absent value means Windows' own default (on), which is an inference, not a reading.
            o.Add(new AuditCheck
            {
                Id = "win_gamemode",
                Category = AuditCategory.WindowsGaming,
                Detected = w.AutoGameMode switch { 1 => Val("enabled"), 0 => Val("disabled"), _ => Val("default_on") },
                Status = w.AutoGameMode == 0 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = w.AutoGameMode == 0 ? Priority.Medium : Priority.Informational,
                Confidence = w.AutoGameMode is null ? Confidence.Medium : Confidence.High,
                Evidence = w.AutoGameMode is null ? EvidenceKind.Inference : EvidenceKind.Fact,
                Relevance = Relevance.Medium,
                Method = "HKCU GameBar",
                AutoFix = true,
            });

            bool capture = w.GameDvrEnabled == 1 || w.AppCaptureEnabled == 1;
            bool known = w.GameDvrEnabled is not null || w.AppCaptureEnabled is not null;
            o.Add(new AuditCheck
            {
                Id = "win_capture",
                Category = AuditCategory.WindowsGaming,
                Detected = !known ? Val("not_set") : capture ? Val("enabled") : Val("disabled"),
                Status = !known ? AuditStatus.Unknown : capture ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = capture ? Priority.Low : Priority.Informational,
                Confidence = known ? Confidence.High : Confidence.None,
                Relevance = Relevance.Low,
                Method = "HKCU GameConfigStore / GameDVR",
                AutoFix = true,
            });

            if (int.TryParse(w.Build, out var build))
                o.Add(new AuditCheck
                {
                    Id = "win_build",
                    Category = AuditCategory.WindowsGaming,
                    Detected = Raw($"{w.Caption} ({w.Build})"),
                    Status = build < 19045 ? AuditStatus.Warning : AuditStatus.Optimal,
                    Priority = build < 19045 ? Priority.Medium : Priority.Informational,
                    Method = "WMI Win32_OperatingSystem",
                    RestartRequired = build < 19045,
                    Reversible = false,
                });

            o.Add(new AuditCheck
            {
                Id = "win_reboot",
                Category = AuditCategory.WindowsGaming,
                Detected = OnOff(w.RebootPending),
                Status = w.RebootPending switch { true => AuditStatus.OptimizationAvailable, false => AuditStatus.Optimal, _ => AuditStatus.Unknown },
                Priority = w.RebootPending == true ? Priority.Medium : Priority.Informational,
                Method = "HKLM servicing keys (read only)",
                RestartRequired = w.RebootPending == true,
            });
        }
        else o.Add(Missing("win_unavailable", AuditCategory.WindowsGaming));

        if (s.Power is { } p)
        {
            bool saverPlan = string.Equals(p.SchemeGuid, PowerSaverGuid, StringComparison.OrdinalIgnoreCase);
            var status = p.OnAc == false ? AuditStatus.Warning
                : p.BatterySaver == true || saverPlan ? AuditStatus.OptimizationAvailable
                : p.SchemeGuid is null ? AuditStatus.Unknown
                : AuditStatus.Optimal;
            o.Add(new AuditCheck
            {
                Id = "win_power",
                Category = AuditCategory.WindowsGaming,
                Detected = Val("power_state", p.SchemeName ?? "?", p.OnAc switch { true => "AC", false => $"{p.BatteryPercent} %", _ => "?" }),
                Status = status,
                Priority = status is AuditStatus.Warning ? Priority.High : status == AuditStatus.OptimizationAvailable ? Priority.Medium : Priority.Informational,
                Relevance = Relevance.High,
                Method = "PowerGetActiveScheme / GetSystemPowerStatus",
            });
        }
    }

    private static void Security(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Security is not { } x)
        {
            o.Add(Missing("sec_unavailable", AuditCategory.Security, scored: false));
            return;
        }

        void Info(string id, bool? value, string method) => o.Add(new AuditCheck
        {
            Id = id,
            Category = AuditCategory.Security,
            Detected = OnOff(value),
            // An enabled protection is simply a state, never a fault.
            Status = value is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
            Confidence = value is null ? Confidence.None : Confidence.High,
            Method = method,
            Scored = false,
            Reversible = false,
        });

        Info("sec_secureboot", x.SecureBoot, "HKLM SecureBoot\\State");
        Info("sec_tpm", x.TpmPresent, "WMI Win32_Tpm");
        Info("sec_vbs", x.VbsRunning, "WMI Win32_DeviceGuard");
        Info("sec_hvci", x.HvciRunning, "WMI Win32_DeviceGuard");
        Info("sec_hypervisor", x.HypervisorPresent, "WMI Win32_ComputerSystem");
        o.Add(new AuditCheck
        {
            Id = "sec_av",
            Category = AuditCategory.Security,
            Detected = x.AntivirusProducts.Count > 0 ? Raw(string.Join(", ", x.AntivirusProducts)) : Val("unknown"),
            Status = x.AntivirusProducts.Count > 0 ? AuditStatus.Acceptable : AuditStatus.Unknown,
            Method = "WMI SecurityCenter2",
            Scored = false,
            Reversible = false,
        });
    }

    private static void Storage(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Storage is not { } st)
        {
            o.Add(Skipped(s, "sto_unavailable", AuditCategory.Storage));
            return;
        }

        foreach (var v in st.Volumes)
        {
            var (status, priority) = FreeSpace(v.Free, v.Total);
            bool relevant = v.IsSystem || v.HasGame;
            o.Add(new AuditCheck
            {
                Id = "sto_free:" + v.Letter,
                TextId = "sto_free",
                Category = AuditCategory.Storage,
                Detected = Val("free_space", v.Letter, N(v.Free / Gb, "0"), N(v.Total / Gb, "0"), v.FileSystem),
                Status = status,
                Priority = relevant ? priority : Priority.Low,
                Relevance = relevant ? Relevance.High : Relevance.Low,
                Scored = relevant,
                Evidence = EvidenceKind.Measurement,
                Method = "DriveInfo",
            });

            if (v.HasGame)
                o.Add(new AuditCheck
                {
                    Id = "sto_game_media",
                    Category = AuditCategory.Storage,
                    Detected = v.MediaType is null ? Val("unknown") : Raw($"{v.Letter} {v.MediaType}"),
                    Status = v.MediaType switch
                    {
                        null or "Unspecified" => AuditStatus.Unknown,
                        "HDD" => AuditStatus.OptimizationAvailable,
                        _ => AuditStatus.Optimal,
                    },
                    Priority = v.MediaType == "HDD" ? Priority.High : Priority.Informational,
                    Relevance = Relevance.High,
                    Confidence = Confidence.Medium,
                    Method = "MSFT_PhysicalDisk",
                    Reversible = false,
                });
        }

        foreach (var d in st.Disks)
            o.Add(new AuditCheck
            {
                Id = "sto_health:" + d.Name,
                TextId = "sto_health",
                Category = AuditCategory.Storage,
                Detected = Raw($"{d.Name} ({d.MediaType}/{d.BusType}): {d.Health}"),
                Status = d.Health switch
                {
                    "Healthy" => AuditStatus.Optimal,
                    "Warning" => AuditStatus.Warning,
                    "Unhealthy" => AuditStatus.Critical,
                    _ => AuditStatus.Unknown,
                },
                Priority = d.Health switch { "Warning" => Priority.High, "Unhealthy" => Priority.Critical, _ => Priority.Informational },
                Relevance = Relevance.Medium,
                Method = "MSFT_PhysicalDisk",
                Reversible = false,
            });

        if (st.DiskActivePercent is { } active)
            o.Add(new AuditCheck
            {
                Id = "sto_activity",
                Category = AuditCategory.Storage,
                Detected = Raw($"{N(active, "0")} %"),
                Status = active >= 80 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
                Priority = active >= 80 ? Priority.Medium : Priority.Informational,
                Evidence = EvidenceKind.Measurement,
                Confidence = Confidence.Medium,
                Method = "WMI PerfDisk",
            });
    }

    public static (AuditStatus Status, Priority Priority) FreeSpace(long free, long total)
    {
        double gb = free / Gb;
        double pct = total > 0 ? 100.0 * free / total : 0;
        if (gb < 5) return (AuditStatus.Critical, Priority.Critical);
        if (gb < 20) return (AuditStatus.Warning, Priority.High);
        if (gb < 50 && pct < 15) return (AuditStatus.OptimizationAvailable, Priority.Medium);
        return (AuditStatus.Optimal, Priority.Informational);
    }

    private static void Background(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Background is not { } b)
        {
            o.Add(Skipped(s, "bg_unavailable", AuditCategory.Background));
            return;
        }

        o.Add(new AuditCheck
        {
            Id = "bg_load",
            Category = AuditCategory.Background,
            Detected = Val("bg_load", N(b.BackgroundCpuPercent, "0"), b.ProcessCount.ToString()),
            Status = b.BackgroundCpuPercent >= 40 ? AuditStatus.Warning : b.BackgroundCpuPercent >= 20 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
            Priority = b.BackgroundCpuPercent >= 40 ? Priority.High : b.BackgroundCpuPercent >= 20 ? Priority.Medium : Priority.Informational,
            Evidence = EvidenceKind.Measurement,
            Relevance = Relevance.High,
            Confidence = Confidence.Medium,
            Method = "Process CPU time (1 s sample)",
        });

        int enabled = b.Startup.Count(e => e.Enabled);
        o.Add(new AuditCheck
        {
            Id = "bg_startup",
            Category = AuditCategory.Background,
            Detected = Raw(enabled.ToString()),
            Status = enabled >= 20 ? AuditStatus.OptimizationAvailable : AuditStatus.Optimal,
            Priority = enabled >= 20 ? Priority.Low : Priority.Informational,
            Method = "HKCU/HKLM Run, Startup folders",
            AutoFix = enabled >= 20,
        });
    }

    private static void Network(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Network is not { } n)
        {
            o.Add(Skipped(s, "net_unavailable", AuditCategory.Network));
            return;
        }

        var active = n.Adapters.FirstOrDefault(a => a.Gateway is not null) ?? n.Adapters.FirstOrDefault();
        o.Add(new AuditCheck
        {
            Id = "net_adapter",
            Category = AuditCategory.Network,
            Detected = active is null ? Val("net_none") : Raw($"{active.Name} ({active.Type}, {active.LinkMbps} Mbps)"),
            Status = active is null ? AuditStatus.Warning
                : active.LinkMbps is > 0 and < 100 ? AuditStatus.OptimizationAvailable
                : active.Type == "Wi-Fi" ? AuditStatus.Acceptable
                : AuditStatus.Optimal,
            Priority = active is null ? Priority.High : active.LinkMbps is > 0 and < 100 ? Priority.Medium : Priority.Informational,
            Relevance = Relevance.Medium,
            Method = "System.Net.NetworkInformation",
            Reversible = false,
        });

        if (s.NetworkProbe is { } p)
        {
            var status = p.AverageMs is null ? AuditStatus.Unknown
                : p.LossPercent >= 2 ? AuditStatus.Warning
                : p.AverageMs > 20 || p.JitterMs > 10 ? AuditStatus.OptimizationAvailable
                : AuditStatus.Optimal;
            o.Add(new AuditCheck
            {
                Id = "net_latency",
                Category = AuditCategory.Network,
                Detected = p.AverageMs is null
                    ? Val("timeout")
                    : Val("latency", N(p.AverageMs.Value), N(p.JitterMs ?? 0), N(p.LossPercent, "0")),
                Status = status,
                Priority = status == AuditStatus.Warning ? Priority.High : status == AuditStatus.OptimizationAvailable ? Priority.Medium : Priority.Informational,
                Evidence = EvidenceKind.Measurement,
                Relevance = Relevance.Medium,
                Method = "ICMP echo (user started)",
            });
        }
    }

    private static void Firmware(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        if (s.Firmware is not { } f)
        {
            o.Add(Missing("fw_unavailable", AuditCategory.Firmware, scored: false));
            return;
        }

        o.Add(new AuditCheck
        {
            Id = "fw_boot",
            Category = AuditCategory.Firmware,
            Detected = f.BootMode is null ? Val("unknown") : Raw(f.BootMode),
            Status = f.BootMode is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
            Method = "GetFirmwareType",
            Scored = false,
            Reversible = false,
        });

        o.Add(new AuditCheck
        {
            Id = "fw_bios_age",
            Category = AuditCategory.Firmware,
            Detected = f.BiosDate is { } d ? Raw($"{f.BiosVersion} ({d:yyyy-MM-dd})") : Raw(f.BiosVersion),
            Status = f.BiosDate is null ? AuditStatus.Unknown : AuditStatus.Acceptable,
            Method = "WMI Win32_BIOS",
            Scored = false,
            Reversible = false,
        });

        foreach (var id in new[] { "fw_rebar", "fw_above4g", "fw_boost" })
            o.Add(new AuditCheck
            {
                Id = id,
                Category = AuditCategory.Firmware,
                Detected = Val("not_verifiable"),
                Status = AuditStatus.ManualVerification,
                Confidence = Confidence.None,
                Evidence = EvidenceKind.ManualCheck,
                Method = "-",
                RestartRequired = true,
                Scored = false,
            });
    }

    private static void Thermal(SystemAuditSnapshot s, List<AuditCheck> o)
    {
        var gpu = s.Thermal?.GpuCelsius;
        o.Add(new AuditCheck
        {
            Id = "th_gpu",
            Category = AuditCategory.Thermal,
            Detected = gpu is { } c ? Raw($"{N(c, "0")} °C") : Val("temp_unavailable"),
            Status = gpu is null ? AuditStatus.Unknown : gpu >= 90 ? AuditStatus.Warning : AuditStatus.Acceptable,
            Confidence = gpu is null ? Confidence.None : Confidence.High,
            Evidence = gpu is null ? EvidenceKind.Unavailable : EvidenceKind.Measurement,
            Method = gpu is null ? "-" : "NVML",
            Scored = false,
        });
    }
}
