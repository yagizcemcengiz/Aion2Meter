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

    public static CaptureSession Create(string directory, string adapterIdentifier, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterIdentifier);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 10_000; index++)
        {
            var path = Path.Combine(directory, FileName(timestamp, index));
            try
            {
                using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return new CaptureSession(path, adapterIdentifier, timestamp.ToUniversalTime());
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("Could not reserve a unique capture filename.");
    }
}
