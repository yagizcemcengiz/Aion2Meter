using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class CaptureSessionTests
{
    [Fact]
    public void FileNameUsesUtcAndStableFormat()
    {
        var time = new DateTimeOffset(2026, 10, 7, 3, 15, 30, TimeSpan.FromHours(3));
        Assert.Equal("capture_2026-10-07_001530.pcap", CaptureSessionFactory.FileName(time));
        Assert.Equal("capture_2026-10-07_001530_002.pcap", CaptureSessionFactory.FileName(time, 2));
    }

    [Fact]
    public void SessionCreatesDirectoryAndDoesNotOverwriteExistingCapture()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.DirectoryPath, "captures");
        var time = DateTimeOffset.Parse("2026-10-07T00:15:30Z");
        var first = CaptureSessionFactory.Create(directory, "adapter-1", time);
        File.WriteAllText(first.FilePath, "existing content");
        var second = CaptureSessionFactory.Create(directory, "adapter-1", time);
        Assert.Equal("capture_2026-10-07_001530_001.pcap", Path.GetFileName(second.FilePath));
        Assert.Equal("existing content", File.ReadAllText(first.FilePath));
        Assert.Equal(TimeSpan.Zero, second.StartedUtc.Offset);
    }

    [Fact]
    public void ConcurrentSessionsReserveDifferentFiles()
    {
        using var files = new TestFiles();
        var time = DateTimeOffset.UtcNow;
        var names = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 32, _ => names.Add(CaptureSessionFactory.Create(files.DirectoryPath, "adapter", time).FilePath));
        Assert.Equal(32, names.Distinct().Count());
    }

    [Fact]
    public void RejectsInvalidSessionParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureSessionFactory.FileName(DateTimeOffset.UtcNow, -1));
        Assert.Throws<ArgumentException>(() => CaptureSessionFactory.Create("", "adapter", DateTimeOffset.UtcNow));
    }
}
