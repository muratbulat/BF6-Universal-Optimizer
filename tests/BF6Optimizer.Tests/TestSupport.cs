using System.Text;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BF6Optimizer.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bf6opt_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}

internal sealed class FakeRegistry : IWindowsPreferenceStore
{
    public readonly Dictionary<string, int> Values = new(StringComparer.OrdinalIgnoreCase);
    public bool FailWrites;

    private static string K(string p, string n) => p + "\\" + n;

    public RegistryValueState Read(string path, string name) =>
        Values.TryGetValue(K(path, name), out var v) ? new(path, name, true, v) : new(path, name, false, null);

    public void Write(string path, string name, int value)
    {
        if (FailWrites) throw new UnauthorizedAccessException("policy");
        Values[K(path, name)] = value;
    }

    public void Remove(string path, string name) => Values.Remove(K(path, name));
}

internal sealed class FakeGame : IGameProcessMonitor
{
    public bool Running;

    public bool IsGameRunning() => Running;

    public IReadOnlyList<string> RunningLaunchers() => [];
}

internal sealed class MemoryRecords : IApplyRecordStore
{
    public ApplyRecord? Record;

    public ApplyRecord? Load() => Record;

    public void Save(ApplyRecord record) => Record = record;

    public void Clear() => Record = null;
}

internal sealed class MemoryReports : IReportStore
{
    public readonly List<OperationReport> Saved = [];
    public bool Fail;

    public Task<string> SaveAsync(OperationReport report, CancellationToken ct = default)
    {
        if (Fail) throw new IOException("disk full");
        Saved.Add(report);
        return Task.FromResult("report.json");
    }

    public IReadOnlyList<OperationReport> List() => Saved;
}

/// <summary>Real file store with injectable faults for rollback paths.</summary>
internal sealed class FaultyFiles(IProfileFileStore inner) : IProfileFileStore
{
    public Func<string, bool>? FailWrite;
    public Func<string, byte[], byte[]>? TamperOnWrite;
    public int Writes;

    public FileAccessState Inspect(string path) => inner.Inspect(path);

    public Task<byte[]> ReadAsync(string path, CancellationToken ct) => inner.ReadAsync(path, ct);

    public Task WriteAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        Writes++;
        if (FailWrite?.Invoke(path) == true) throw new IOException("simulated write failure");
        return inner.WriteAtomicAsync(path, TamperOnWrite?.Invoke(path, content) ?? content, ct);
    }
}

internal sealed class Harness : IDisposable
{
    public const string Machine = "Machine_TEST";

    public readonly TempDir Dir = new();
    public readonly FaultyFiles Files = new(new FileProfileStore());
    public readonly FakeRegistry Registry = new();
    public readonly FakeGame Game = new();
    public readonly MemoryRecords Records = new();
    public readonly MemoryReports Reports = new();
    public readonly SettingCatalog Catalog = SettingCatalog.LoadEmbedded();
    public readonly PerformanceData Data = PerformanceData.LoadEmbedded();
    public readonly FileBackupStore Backups;
    public readonly RestoreEngine Restore;
    public readonly ApplyEngine Apply;
    public readonly string ProfilePath;

    public Harness()
    {
        var paths = new AppPaths(Dir.File("app"));
        paths.EnsureCreated();
        Backups = new FileBackupStore(paths, () => Machine, () => [], new SystemClock(), NullLogger<FileBackupStore>.Instance);
        Restore = new RestoreEngine(Files, Backups, Registry, Game, Records, Reports, new SystemClock(), NullLogger<RestoreEngine>.Instance);
        Apply = new ApplyEngine(Files, Backups, Registry, Game, Records, Reports, Restore, new SystemClock(),
            new ApplyOptions { SettleDelay = TimeSpan.Zero }, NullLogger<ApplyEngine>.Instance);

        ProfilePath = Dir.File("settings", "steam", "PROFSAVEbf6mp_profile");
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, Fixtures.Profile, new UTF8Encoding(false));
    }

    public OptimizationPlan Plan(OptimizationMode mode = OptimizationMode.Competitive, OptimizationPreferences? prefs = null) =>
        new OptimizationPlanner(Data, Catalog).Build(new PlanRequest
        {
            Mode = mode,
            Hardware = Fixtures.Desktop,
            Gpu = Fixtures.Desktop.Gpus[0],
            Display = Fixtures.Desktop.Displays[0],
            Preferences = prefs ?? new OptimizationPreferences(),
        });

    public async Task<ApplyRequest> RequestAsync(OptimizationPlan? plan = null)
    {
        plan ??= Plan();
        var (doc, sha) = await ProfileLoader.LoadAsync(Files, ProfilePath, default);
        return new ApplyRequest
        {
            Plan = plan,
            MachineKey = Machine,
            Targets = [DiffBuilder.Build(ProfilePath, sha, doc, plan, Catalog)],
            AllProfiles = [ProfilePath],
        };
    }

    public string Current => File.ReadAllText(ProfilePath);

    public void Dispose() => Dir.Dispose();
}

internal static class Fixtures
{
    /// <summary>Key names and values mirror a real v1.x profile; audio and key-binding lines stand in for unrelated data.</summary>
    public const string Profile =
        "GstAudio.Volume_Master 0.800000\n" +
        "GstInput.MouseSensitivity 0.120000\n" +
        "GstRender.AmbientOcclusion 1\n" +
        "GstRender.ChromaticAberration 1\n" +
        "GstRender.DRSEnabled 1\n" +
        "GstRender.EffectsQuality 2\n" +
        "GstRender.FilmGrain 1\n" +
        "GstRender.FrameRateLimit 120.000000\n" +
        "GstRender.FrameRateLimiterEnable 0\n" +
        "GstRender.HUD-Accent -493867777\n" +
        "GstRender.MeshQuality 3\n" +
        "GstRender.MotionBlurWorld 0.500000\n" +
        "GstRender.NVIDIAFrameGenerationEnabled 1\n" +
        "GstRender.NvidiaLowLatency 0\n" +
        "GstRender.NvidiaUpscalingTechnique 1\n" +
        "GstRender.ResolutionWidth 2560\n" +
        "GstRender.ShadowQuality 2\n" +
        "GstRender.TextureQuality 3\n" +
        "GstRender.VSyncMode 1\n" +
        "GstRender.VolumetricQuality 2\n" +
        "GstKeyBinding.Fire Mouse0\n";

    public static GpuInfo Gpu(string name, double vramGb, GpuKind? kind = null, FormFactor form = FormFactor.Desktop, double? powerMax = null)
    {
        long bytes = (long)(vramGb * 1024 * 1024 * 1024);
        var id = GpuNameParser.Parse(name);
        return new GpuInfo
        {
            Id = name,
            Name = name,
            Vendor = id.Vendor,
            Kind = kind ?? GpuNameParser.ClassifyKind(id, bytes),
            FormFactor = form,
            DedicatedVramBytes = bytes > 0 ? bytes : null,
            PowerMaxW = powerMax,
        };
    }

    public static readonly HardwareSnapshot Desktop = new()
    {
        SystemFormFactor = FormFactor.Desktop,
        Cpu = new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, 4200),
        Memory = new MemoryInfo(32L * 1024 * 1024 * 1024, 6000),
        Gpus = [Gpu("NVIDIA GeForce RTX 4080 SUPER", 16)],
        Displays = [new DisplayInfo(@"\\.\DISPLAY1", "Test", 2560, 1440, 239.76, true)],
    };
}
