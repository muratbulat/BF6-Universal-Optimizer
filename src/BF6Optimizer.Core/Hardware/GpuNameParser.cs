using System.Text.RegularExpressions;

namespace BF6Optimizer.Core.Hardware;

/// <summary>Structured identity parsed from a marketing name. Model-agnostic: unseen models still parse.</summary>
public sealed record GpuIdentity
{
    public GpuVendor Vendor { get; init; }
    /// <summary>RTX, GTX, GT, MX, RX, ARC or empty.</summary>
    public string Family { get; init; } = "";
    /// <summary>"5070", "A770", "B580"...</summary>
    public string Model { get; init; } = "";
    /// <summary>TI, SUPER, TI SUPER, XT, XTX, GRE or empty.</summary>
    public string Suffix { get; init; } = "";
    public bool MobileName { get; init; }
    public bool IntegratedName { get; init; }
    public bool SoftwareName { get; init; }

    /// <summary>Performance-data lookup key such as "RTX 5070 TI".</summary>
    public string? LookupKey => Model.Length == 0
        ? null
        : string.Join(' ', new[] { Family, Model, Suffix }.Where(s => s.Length > 0));
}

public static partial class GpuNameParser
{
    [GeneratedRegex(@"\((R|TM)\)|®|™", RegexOptions.IgnoreCase)] private static partial Regex Marks();
    [GeneratedRegex(@"\b(RTX|GTX|GT|MX)\s*(\d{3,4})(?:\s*(TI\s*SUPER|TI|SUPER))?\b")] private static partial Regex Nvidia();
    // Letter suffixes glued to the number (6800S, 7600M) mark mobile parts.
    [GeneratedRegex(@"\bRX\s*(\d{3,4})([MS])?(?:\s*(XTX|XT|GRE))?\b")] private static partial Regex AmdRx();
    [GeneratedRegex(@"\bRADEON\s+(?:GRAPHICS\b|(?:RX\s+)?VEGA\b|\d{3,4}M\b)")] private static partial Regex AmdIntegrated();
    [GeneratedRegex(@"\bVEGA\s*\d+\s*GRAPHICS\b")] private static partial Regex AmdVegaGraphics();
    [GeneratedRegex(@"\bARC\s*(?:PRO\s*)?([AB]\d{2,3})(M)?\b")] private static partial Regex ArcDiscrete();
    [GeneratedRegex(@"\bARC\b")] private static partial Regex ArcAny();
    [GeneratedRegex(@"\b(UHD|IRIS|HD\s+GRAPHICS)\b")] private static partial Regex IntelIntegrated();
    [GeneratedRegex(@"\b(LAPTOP|MOBILE|NOTEBOOK|MAX-Q)\b")] private static partial Regex MobileToken();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    public static string Normalize(string? name)
    {
        var s = Marks().Replace(name ?? "", " ").ToUpperInvariant();
        return Spaces().Replace(s, " ").Trim();
    }

    public static GpuIdentity Parse(string? rawName)
    {
        var n = Normalize(rawName);
        if (n.Length == 0) return new GpuIdentity();

        if (n.Contains("MICROSOFT BASIC") || n.Contains("BASIC RENDER") || n.Contains("WARP"))
            return new GpuIdentity { SoftwareName = true };

        bool mobile = MobileToken().IsMatch(n);

        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || Nvidia().IsMatch(n))
        {
            var m = Nvidia().Match(n);
            return new GpuIdentity
            {
                Vendor = GpuVendor.Nvidia,
                Family = m.Success ? m.Groups[1].Value : "",
                Model = m.Success ? m.Groups[2].Value : "",
                Suffix = m.Success ? Spaces().Replace(m.Groups[3].Value, " ") : "",
                MobileName = mobile,
            };
        }

        if (n.Contains("RADEON") || n.Contains("AMD"))
        {
            if (AmdIntegrated().IsMatch(n) || AmdVegaGraphics().IsMatch(n))
                return new GpuIdentity { Vendor = GpuVendor.Amd, IntegratedName = true };

            var m = AmdRx().Match(n);
            return new GpuIdentity
            {
                Vendor = GpuVendor.Amd,
                Family = m.Success ? "RX" : "",
                Model = m.Success ? m.Groups[1].Value : "",
                Suffix = m.Success ? m.Groups[3].Value : "",
                MobileName = mobile || (m.Success && m.Groups[2].Success),
            };
        }

        if (n.Contains("INTEL") || ArcAny().IsMatch(n))
        {
            var d = ArcDiscrete().Match(n);
            if (d.Success)
                return new GpuIdentity
                {
                    Vendor = GpuVendor.Intel,
                    Family = "ARC",
                    Model = d.Groups[1].Value,
                    MobileName = mobile || d.Groups[2].Success,
                };

            // "Intel Arc Graphics" and "Arc 140V" are CPU-integrated despite the Arc brand.
            return new GpuIdentity
            {
                Vendor = GpuVendor.Intel,
                IntegratedName = ArcAny().IsMatch(n) || IntelIntegrated().IsMatch(n),
            };
        }

        return new GpuIdentity { MobileName = mobile };
    }

    /// <summary>
    /// Name evidence wins over memory: firmware can give iGPUs several GB of carved-out "dedicated" memory.
    /// </summary>
    public static GpuKind ClassifyKind(GpuIdentity id, long? dedicatedVramBytes)
    {
        const long OneGb = 1024L * 1024 * 1024;
        if (id.SoftwareName) return GpuKind.Software;
        if (id.IntegratedName) return GpuKind.Integrated;
        if (id.Family.Length > 0 || id.Vendor == GpuVendor.Nvidia) return GpuKind.Discrete;
        return dedicatedVramBytes switch
        {
            null or 0 => GpuKind.Unknown,
            >= 2 * OneGb => GpuKind.Discrete,
            < OneGb => GpuKind.Integrated,
            _ => GpuKind.Unknown,
        };
    }

    public static (FormFactor Value, string Evidence) ResolveFormFactor(GpuIdentity id, FormFactor system)
    {
        if (id.MobileName) return (FormFactor.Laptop, "name");
        return system == FormFactor.Unknown ? (FormFactor.Unknown, "none") : (system, "chassis");
    }
}
