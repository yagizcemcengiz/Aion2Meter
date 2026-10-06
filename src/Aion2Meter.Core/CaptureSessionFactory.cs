using System.Globalization;

namespace Aion2Meter.Core;

public static class CaptureSessionFactory
{
    public static string FileName(DateTimeOffset timestamp, int collisionIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(collisionIndex);
        var stem = "capture_" + timestamp.UtcDateTime.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        return collisionIndex == 0 ? stem + ".pcap" : stem + $"_{collisionIndex:D3}.pcap";
    }

    public static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "session";
        var safe = new string(label.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').Take(80).ToArray()).Trim('-', '_');
        return safe.Length == 0 ? "session" : safe;
    }

    public static CaptureSession Create(string directory, string adapterIdentifier, DateTimeOffset timestamp, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterIdentifier);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 10_000; index++)
        {
            var name = label is null ? FileName(timestamp, index)
                : timestamp.UtcDateTime.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + "_" + SanitizeLabel(label)
                    + (index == 0 ? "" : $"_{index:D3}") + ".pcap";
            var path = Path.Combine(directory, name);
            if (File.Exists(Path.ChangeExtension(path, ".json"))) continue;
            try
            {
                using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new CaptureSession(path, adapterIdentifier, timestamp.ToUniversalTime(), Guid.NewGuid());
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("Could not reserve a unique capture filename.");
    }
}
