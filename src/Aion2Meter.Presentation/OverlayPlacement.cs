namespace Aion2Meter.Presentation;

public sealed record DisplayWorkArea(string Key, int Left, int Top, int Width, int Height);

public static class OverlayPlacement
{
    /// <summary>Physical pixels survive differing monitor DPI and negative desktop coordinates.</summary>
    public static OverlayPosition Restore(OverlayPosition? saved, IReadOnlyList<DisplayWorkArea> monitors, int width, int height)
    {
        if (monitors.Count == 0 || monitors.Any(m => m.Width <= 0 || m.Height <= 0) || width <= 0 || height <= 0)
            throw new ArgumentException("Positive overlay bounds and monitor work areas required.");
        var monitor = monitors.FirstOrDefault(m => m.Key == saved?.Monitor) ??
            monitors.FirstOrDefault(m => saved is not null && saved.LeftPixels >= m.Left && saved.TopPixels >= m.Top &&
                (long)saved.LeftPixels < (long)m.Left + m.Width && (long)saved.TopPixels < (long)m.Top + m.Height) ?? monitors[0];
        var x = Math.Clamp((long)(saved?.LeftPixels ?? monitor.Left + 24), monitor.Left, (long)monitor.Left + Math.Max(0, monitor.Width - width));
        var y = Math.Clamp((long)(saved?.TopPixels ?? monitor.Top + 24), monitor.Top, (long)monitor.Top + Math.Max(0, monitor.Height - height));
        return new((int)x, (int)y, monitor.Key);
    }
}
