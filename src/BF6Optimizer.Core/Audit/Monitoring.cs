using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Core.Audit;

/// <summary>One measured sample. Null means the metric is unavailable here, never zero.</summary>
public sealed record PerformanceSample
{
    public DateTimeOffset Timestamp { get; init; }
    public double? CpuPercent { get; init; }
    public IReadOnlyList<double> PerLogicalCpu { get; init; } = [];
    public double? CpuFrequencyMHz { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuMemoryBytes { get; init; }
    public double? GpuTemperatureC { get; init; }
    public double? RamUsedBytes { get; init; }
    public double? RamPercent { get; init; }
    public double? CommitBytes { get; init; }
    public double? DiskBytesPerSec { get; init; }
    public double? DiskActivePercent { get; init; }
    public double? NetBytesPerSec { get; init; }
}

public interface IPerformanceSampler
{
    Task<PerformanceSample> CaptureAsync(CancellationToken ct);
}

/// <summary>Fixed-capacity history so long sessions cannot grow memory.</summary>
public sealed class BoundedSeries<T>(int capacity)
{
    private readonly Queue<T> _items = new(capacity);

    public int Capacity { get; } = capacity;
    public int Count => _items.Count;

    public void Add(T item)
    {
        if (_items.Count == Capacity) _items.Dequeue();
        _items.Enqueue(item);
    }

    public IReadOnlyList<T> ToList() => _items.ToArray();
}

public sealed record MetricSummary(double? Average, double? Peak, int Samples);

public sealed record SessionPhaseSummary(
    int Samples, MetricSummary Cpu, MetricSummary MaxSingleCpu, MetricSummary Gpu, MetricSummary Ram,
    MetricSummary GpuMemory, MetricSummary Disk, MetricSummary Network);

public sealed record SessionReport
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Ended { get; init; }
    public required SessionPhaseSummary Baseline { get; init; }
    public required SessionPhaseSummary Active { get; init; }
    public IReadOnlyList<Msg> Notes { get; init; } = [];
}

public static class SessionAggregator
{
    public static MetricSummary Summarize(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? new MetricSummary(null, null, 0) : new MetricSummary(list.Average(), list.Max(), list.Count);
    }

    public static SessionPhaseSummary Phase(IReadOnlyList<PerformanceSample> s) => new(
        s.Count,
        Summarize(s.Select(x => x.CpuPercent)),
        Summarize(s.Select(x => x.PerLogicalCpu.Count == 0 ? (double?)null : x.PerLogicalCpu.Max())),
        Summarize(s.Select(x => x.GpuPercent)),
        Summarize(s.Select(x => x.RamUsedBytes)),
        Summarize(s.Select(x => x.GpuMemoryBytes)),
        Summarize(s.Select(x => x.DiskBytesPerSec)),
        Summarize(s.Select(x => x.NetBytesPerSec)));

    /// <summary>
    /// Notes stay descriptive. Without frame-time data nothing here can name a bottleneck,
    /// so a pegged thread with an idle GPU is flagged only as something worth a closer look.
    /// </summary>
    public static SessionReport Build(IReadOnlyList<PerformanceSample> baseline, IReadOnlyList<PerformanceSample> active)
    {
        var b = Phase(baseline);
        var a = Phase(active);
        var notes = new List<Msg> { new("session.no_fps") };
        if (a.Gpu.Samples == 0) notes.Add(new Msg("session.no_gpu"));
        if (a.MaxSingleCpu.Average >= 90 && a.Gpu.Average < 85) notes.Add(new Msg("session.thread_saturated"));
        if (a.Cpu.Peak >= 95 && a.MaxSingleCpu.Samples == 0) notes.Add(new Msg("session.cpu_inconclusive"));

        var all = baseline.Concat(active).ToList();
        return new SessionReport
        {
            Started = all.Count > 0 ? all.Min(s => s.Timestamp) : default,
            Ended = all.Count > 0 ? all.Max(s => s.Timestamp) : default,
            Baseline = b,
            Active = a,
            Notes = notes,
        };
    }
}

/// <summary>Runs a sampler at an interval until cancelled. Failed samples are skipped, not invented.</summary>
public static class SamplingLoop
{
    public static async Task RunAsync(IPerformanceSampler sampler, TimeSpan interval, Action<PerformanceSample> onSample, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try { onSample(await sampler.CaptureAsync(ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }
}
