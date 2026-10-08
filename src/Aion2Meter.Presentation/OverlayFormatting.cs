using System.Globalization;

namespace Aion2Meter.Presentation;

public static class CompactNumber
{
    public static string Format(decimal value, bool rate = false)
    {
        var scaled = value; var unit = 0;
        string[] suffixes = ["", "K", "M", "B", "T"];
        while (Math.Abs(scaled) >= 1000 && unit < suffixes.Length - 1) { scaled /= 1000; unit++; }
        if (unit == 0) return scaled.ToString(rate ? "0.0" : "0", CultureInfo.InvariantCulture);
        var precision = Math.Abs(scaled) < 10 ? 2 : Math.Abs(scaled) < 100 ? 1 : 0;
        if (Math.Abs(Math.Round(scaled, precision)) >= 1000 && unit < suffixes.Length - 1)
        { scaled /= 1000; unit++; precision = 2; }
        return scaled.ToString("F" + precision, CultureInfo.InvariantCulture) + suffixes[unit];
    }
}

public readonly record struct LatencyQuality(int Bars, string Color)
{
    public const string Inactive = "#65758B";
    public static LatencyQuality From(double? milliseconds) => milliseconds is not { } ms || !double.IsFinite(ms) || ms < 0
        ? new(0, Inactive) : ms <= 70 ? new(4, "#71D6A6") : ms <= 120 ? new(3, "#E7C86D")
            : ms <= 180 ? new(2, "#EF8B89") : new(1, "#EF8B89");
}
