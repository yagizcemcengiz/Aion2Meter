using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class PacketStatisticsTests
{
    [Fact]
    public void AggregatesCapturedBytesProtocolsAndTimestampRange()
    {
        var stats = new PacketStatistics();
        var time = DateTimeOffset.Parse("2026-10-07T00:15:30Z");
        stats.Add(new(time.AddSeconds(2), 54, 100, TransportProtocol: TransportProtocol.Tcp));
        stats.Add(new(time, 42, 42, TransportProtocol: TransportProtocol.Udp));
        stats.Add(new(time.AddSeconds(1), 42, 42));
        var result = stats.Snapshot();
        Assert.Equal(3, result.TotalPackets);
        Assert.Equal(138, result.TotalBytes);
        Assert.Equal(1, result.TcpCount);
        Assert.Equal(1, result.UdpCount);
        Assert.Equal(1, result.OtherCount);
        Assert.Equal(time, result.FirstTimestamp);
        Assert.Equal(time.AddSeconds(2), result.LastTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);
    }

    [Fact]
    public void CountersRemainConsistentWithConcurrentUpdates()
    {
        var stats = new PacketStatistics();
        var packet = new PacketMetadata(DateTimeOffset.UtcNow, 64, 64, TransportProtocol: TransportProtocol.Tcp);
        Parallel.For(0, 10_000, _ => stats.Add(packet));
        var result = stats.Snapshot();
        Assert.Equal(10_000, result.TotalPackets);
        Assert.Equal(640_000, result.TotalBytes);
        Assert.Equal(result.TotalPackets, result.TcpCount + result.UdpCount + result.OtherCount);
    }

    [Fact]
    public void EmptyAndResetStatisticsHaveNoTimestamps()
    {
        var stats = new PacketStatistics();
        Assert.Null(stats.Snapshot().FirstTimestamp);
        stats.Add(new(DateTimeOffset.UtcNow, 10, 10));
        stats.Reset();
        Assert.Equal(new StatisticsSnapshot(0, 0, 0, 0, 0, null, null), stats.Snapshot());
        Assert.Equal(TimeSpan.Zero, stats.Snapshot().Duration);
    }

    [Fact]
    public void RejectsNegativeCapturedLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketStatistics().Add(new(DateTimeOffset.UtcNow, -1, 0)));
    }
}
