using System.Text.RegularExpressions;

namespace BF6Optimizer.Core.Audit;

public enum ChannelEvidence { Unknown, SingleModule, MultipleChannelsReported, SameChannelReported }

public static partial class MemoryAnalysis
{
    [GeneratedRegex(@"CHANNEL\s*([A-H])\b|CHANNEL([A-H])\b", RegexOptions.IgnoreCase)] private static partial Regex ChannelWord();
    [GeneratedRegex(@"\bDIMM[_\s-]*([A-H])\d?\b|\b([A-H])[12]\b|\b[CP]\d_?CH([A-H])", RegexOptions.IgnoreCase)] private static partial Regex ChannelLabel();

    /// <summary>SMBIOS type 17 memory type to a DDR generation name. 0 means firmware did not say.</summary>
    public static string? DdrGeneration(int? smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR4",
        29 => "LPDDR4X",
        30 => "LPDDR5",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    /// <summary>DDR transfers twice per clock: DDR5-6000 is 6000 MT/s on a 3000 MHz clock.</summary>
    public static int ClockMHzFromTransferRate(int mts) => mts / 2;

    public static int TransferRateFromClockMHz(int mhz) => mhz * 2;

    /// <summary>Channel letters reported by firmware labels. Empty when labels carry no channel information.</summary>
    public static IReadOnlySet<string> ChannelsFromLabels(IEnumerable<MemoryModule> modules)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            var text = m.Slot + " " + m.Bank;
            var word = ChannelWord().Match(text);
            if (word.Success) { result.Add(First(word)); continue; }
            var label = ChannelLabel().Match(text);
            if (label.Success) result.Add(First(label));
        }
        return result;
    }

    private static string First(Match m) =>
        m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.ToUpperInvariant();

    /// <summary>Module count alone never proves a channel mode; only one module rules out multi-channel.</summary>
    public static ChannelEvidence AssessChannels(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return ChannelEvidence.Unknown;
        if (modules.Count == 1) return ChannelEvidence.SingleModule;
        var channels = ChannelsFromLabels(modules);
        return channels.Count switch
        {
            0 => ChannelEvidence.Unknown,
            1 => ChannelEvidence.SameChannelReported,
            _ => ChannelEvidence.MultipleChannelsReported,
        };
    }

    public static bool ModulesMatch(IReadOnlyList<MemoryModule> modules) =>
        modules.Select(m => (m.CapacityBytes, m.RatedMTs, m.PartNumber.Trim())).Distinct().Count() <= 1;

    /// <summary>Lowest reported configured rate: all modules run at one speed.</summary>
    public static int? EffectiveMTs(IReadOnlyList<MemoryModule> modules) =>
        modules.Where(m => m.ConfiguredMTs is > 0).Select(m => m.ConfiguredMTs).Min();

    public static int? RatedMTs(IReadOnlyList<MemoryModule> modules) =>
        modules.Where(m => m.RatedMTs is > 0).Select(m => m.RatedMTs).Min();
}

public static class RefreshAnalysis
{
    public static int ActiveHz(DisplayFacts d) => (int)Math.Round(d.Display.RefreshHz);

    /// <summary>Highest mode at the current resolution; null without a mode list.</summary>
    public static int? MaxHzAtCurrentResolution(DisplayFacts d) => d.Modes
        .Where(m => m.Width == d.Display.Width && m.Height == d.Display.Height)
        .Select(m => (int?)m.RefreshHz).Max();

    public static (int Width, int Height)? LargestMode(DisplayFacts d) =>
        d.Modes.Count == 0 ? null : d.Modes.OrderByDescending(m => (long)m.Width * m.Height).Select(m => (m.Width, m.Height)).First();

    /// <summary>A rate within a few Hz of the top is rounding (59.94/60), not a mismatch.</summary>
    public static bool HasMismatch(DisplayFacts d) =>
        MaxHzAtCurrentResolution(d) is { } max && max - ActiveHz(d) > Math.Max(3, max / 20);

    public static DisplayFacts? GamingDisplay(IReadOnlyList<DisplayFacts> displays, string? selected) =>
        displays.FirstOrDefault(d => d.Display.DeviceName == selected)
        ?? displays.FirstOrDefault(d => d.Display.IsPrimary)
        ?? displays.FirstOrDefault();

    public static int? StartingCap(int refreshHz, bool vrrAssumed) => refreshHz <= 0 ? null
        : vrrAssumed && refreshHz > 60 ? refreshHz - 3
        : refreshHz;
}
