using System.Text.RegularExpressions;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Hardware;
using BF6Optimizer.Core.Verification;

namespace BF6Optimizer.Core.Diagnostics;

/// <summary>Removes identifying strings from exported text.</summary>
public sealed partial class Redactor(IEnumerable<(string Secret, string Replacement)> secrets)
{
    private readonly List<(string Secret, string Replacement)> _secrets = secrets
        .Where(s => !string.IsNullOrWhiteSpace(s.Secret) && s.Secret.Length >= 3)
        .OrderByDescending(s => s.Secret.Length)
        .ToList();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b")]
    private static partial Regex Guid();

    public string Apply(string text)
    {
        foreach (var (secret, replacement) in _secrets)
        {
            text = text.Replace(secret, replacement, StringComparison.OrdinalIgnoreCase);
            // JSON-escaped form of the same path.
            text = text.Replace(secret.Replace("\\", "\\\\"), replacement, StringComparison.OrdinalIgnoreCase);
        }
        return Guid().Replace(text, "<guid>");
    }
}

public sealed record DiagnosticReport
{
    public int SchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = AppInfo.Version;
    public DateTimeOffset Generated { get; init; }
    public bool SimulatedHardware { get; init; }
    public string? Os { get; init; }
    public string? Cpu { get; init; }
    public double? RamGb { get; init; }
    public IReadOnlyList<object> Gpus { get; init; } = [];
    public IReadOnlyList<object> Displays { get; init; } = [];
    public OverallStatus? Status { get; init; }
    public int StatusIssueCount { get; init; }
    public IReadOnlyList<OperationReport> RecentOperations { get; init; } = [];
    public IReadOnlyList<string> RecentLogLines { get; init; } = [];
}

public static class DiagnosticReportBuilder
{
    /// <summary>Produces redacted JSON. System manufacturer, model and BIOS stay out by construction.</summary>
    public static string Build(
        HardwareSnapshot? hardware, StatusReport? status, IReadOnlyList<OperationReport> operations,
        IReadOnlyList<string> logLines, Redactor redactor, DateTimeOffset now)
    {
        var report = new DiagnosticReport
        {
            Generated = now,
            SimulatedHardware = hardware?.IsSimulated ?? false,
            Os = hardware?.Os is { } os ? $"{os.Caption} ({os.Build})" : null,
            Cpu = hardware?.Cpu?.Name,
            RamGb = hardware?.Memory?.TotalGb is { } gb ? Math.Round(gb, 1) : null,
            Gpus = hardware?.Gpus.Select(g => (object)new { g.Name, g.Vendor, g.Kind, g.FormFactor, g.VramGb, g.VramSource, g.DriverVersion }).ToList() ?? [],
            Displays = hardware?.Displays.Select(d => (object)new { d.Width, d.Height, d.RefreshHz, d.IsPrimary }).ToList() ?? [],
            Status = status?.Overall,
            StatusIssueCount = status?.Items.Count(i => i.State != SettingState.AppliedCorrectly) ?? 0,
            RecentOperations = operations.Take(10).ToList(),
            RecentLogLines = logLines,
        };
        return redactor.Apply(Json.Serialize(report));
    }
}
