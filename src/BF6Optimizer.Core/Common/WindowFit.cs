namespace BF6Optimizer.Core.Common;

/// <summary>A rectangle in physical pixels.</summary>
public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public int OverlapArea(PixelRect o)
    {
        int w = Math.Min(Right, o.Right) - Math.Max(X, o.X);
        int h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>Chooses the startup window rectangle. Pure, so every monitor case can be tested.</summary>
public static class WindowFit
{
    public const int MinWidthDip = 760;
    public const int MinHeightDip = 460;
    private const double WidthShare = 0.73;
    private const double HeightShare = 0.80;
    private const int IdealMinWidth = 1180;
    private const int IdealMaxWidth = 1600;
    private const int IdealMinHeight = 700;
    private const int IdealMaxHeight = 950;

    /// <summary>Default size for a monitor, centered in its work area.</summary>
    public static PixelRect Initial(PixelRect work, double scale)
    {
        double workW = work.Width / scale, workH = work.Height / scale;
        double w = Math.Min(workW, Math.Clamp(workW * WidthShare, IdealMinWidth, IdealMaxWidth));
        double h = Math.Min(workH, Math.Clamp(workH * HeightShare, IdealMinHeight, IdealMaxHeight));
        int pw = (int)(w * scale), ph = (int)(h * scale);
        return new PixelRect(work.X + (work.Width - pw) / 2, work.Y + (work.Height - ph) / 2, pw, ph);
    }

    /// <summary>
    /// Uses saved geometry only if most of it is on a monitor that still exists, clamped into that monitor.
    /// Anything else, such as a disconnected display, opens fresh on the active monitor.
    /// </summary>
    public static PixelRect Choose(PixelRect? saved, IReadOnlyList<PixelRect> workAreas, PixelRect activeWork, double activeScale)
    {
        if (saved is null || saved.Width <= 0 || saved.Height <= 0) return Initial(activeWork, activeScale);

        var host = workAreas.OrderByDescending(saved.OverlapArea).FirstOrDefault();
        if (host is null || host.OverlapArea(saved) < saved.Width * saved.Height / 2) return Initial(activeWork, activeScale);

        int w = Math.Min(saved.Width, host.Width), h = Math.Min(saved.Height, host.Height);
        return new PixelRect(
            Math.Clamp(saved.X, host.X, host.Right - w),
            Math.Clamp(saved.Y, host.Y, host.Bottom - h),
            w, h);
    }
}

public sealed class SavedWindow
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}
