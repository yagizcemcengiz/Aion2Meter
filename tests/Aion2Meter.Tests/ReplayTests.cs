using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class ReplayTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-07T00:15:30.123456Z");
    private static ReplayAnalyzer Analyzer() => new(new PcapPacketSource(), new PacketMetadataReader());

    [Fact]
    public void CalculatesReplayStatisticsAndDumpsOnlyRequestedPacketsInFileOrder()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([
            TestFiles.Packet(6, Timestamp, 100), TestFiles.Packet(17, Timestamp.AddSeconds(2)),
            TestFiles.Packet(1, Timestamp.AddSeconds(1))]);
        var result = Analyzer().Analyze(path, 2);
        Assert.Equal(3, result.Statistics.TotalPackets);
        Assert.Equal(138, result.Statistics.TotalBytes);
        Assert.Equal(1, result.Statistics.TcpCount);
        Assert.Equal(1, result.Statistics.UdpCount);
        Assert.Equal(1, result.Statistics.OtherCount);
        Assert.Equal(Timestamp, result.Statistics.FirstTimestamp);
        Assert.Equal(Timestamp.AddSeconds(2), result.Statistics.LastTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Statistics.Duration);
        Assert.Equal(2, result.Dump.Count);
        Assert.Equal(100, result.Dump[0].OriginalLength);
        Assert.Equal(Timestamp.AddSeconds(2), result.Dump[1].TimestampUtc);
        Assert.Equal(0, result.MetadataErrors);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ReadsBothByteOrdersAndTimestampResolutions(bool littleEndian, bool nanoseconds)
    {
        using var files = new TestFiles();
        var timestamp = nanoseconds ? Timestamp.AddTicks(7) : Timestamp;
        var path = files.WritePcap([TestFiles.Packet(17, timestamp)], littleEndian, nanoseconds);
        var packet = Assert.Single(new PcapPacketSource().Read(path));
        Assert.Equal(timestamp, packet.TimestampUtc);
        Assert.Equal((uint)1, packet.LinkLayerType);
    }

    [Fact]
    public void EmptyCaptureHasZeroTotalsAndNoTimestamps()
    {
        using var files = new TestFiles();
        var result = Analyzer().Analyze(files.WritePcap([]), 20);
        Assert.Equal(0, result.Statistics.TotalPackets);
        Assert.Equal(0, result.Statistics.TotalBytes);
        Assert.Equal(TimeSpan.Zero, result.Statistics.Duration);
        Assert.Null(result.Statistics.FirstTimestamp);
        Assert.Empty(result.Dump);
    }

    [Fact]
    public void MissingAndInvalidPathsAreReadableErrors()
    {
        using var files = new TestFiles();
        Assert.Throws<FileNotFoundException>(() => new PcapPacketSource().Read(Path.Combine(files.DirectoryPath, "missing.pcap")));
        Assert.Throws<ArgumentException>(() => new PcapPacketSource().Read(" "));
        Assert.Throws<ArgumentException>(() => new PcapPacketSource().Read("\0"));
        Assert.Throws<FileNotFoundException>(() => Analyzer().Analyze(files.DirectoryPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(45)]
    public void RejectsTruncatedGlobalHeaderPacketHeaderAndPacketData(int length)
    {
        using var files = new TestFiles();
        var path = files.WritePcap([TestFiles.Packet(6, Timestamp)]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(length);
        Assert.Throws<InvalidDataException>(() => Analyzer().Analyze(path));
    }

    [Theory]
    [InlineData(0, 0x0a0d0d0a)]
    [InlineData(16, 0)]
    [InlineData(16, uint.MaxValue)]
    [InlineData(28, 1_000_000)]
    [InlineData(32, 262_145)]
    [InlineData(36, 1)]
    public void RejectsUnsupportedFormatAndInvalidHeaderValues(int offset, uint value)
    {
        using var files = new TestFiles();
        var path = files.WritePcap([TestFiles.Packet(6, Timestamp)]);
        var bytes = File.ReadAllBytes(path);
        TestFiles.U32(bytes, offset, value, true);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => Analyzer().Analyze(path));
    }

    [Fact]
    public void DamagedMetadataStillCountsSavedPacketAndCapturedBytes()
    {
        using var files = new TestFiles();
        var result = Analyzer().Analyze(files.WritePcap([new CapturedPacket(Timestamp, 2, 1, [1, 2])]), 20);
        Assert.Equal(1, result.Statistics.TotalPackets);
        Assert.Equal(2, result.Statistics.TotalBytes);
        Assert.Equal(1, result.Statistics.OtherCount);
        Assert.Equal(1, result.MetadataErrors);
        Assert.Equal(Timestamp, Assert.Single(result.Dump).TimestampUtc);
    }

    [Fact]
    public void HonorsCancellationAndValidatesDumpLimits()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([TestFiles.Packet(17, Timestamp)]);
        Assert.Throws<OperationCanceledException>(() => Analyzer().Analyze(path, cancellationToken: new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Analyzer().Analyze(path, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Analyzer().Analyze(path, 10_001));
    }

    [Theory]
    [InlineData()]
    [InlineData("file", "--dump", "-1")]
    [InlineData("file", "--dump", "abc")]
    [InlineData("file", "--unknown", "20")]
    public void CommandLineRejectsInvalidArguments(params string[] args)
    {
        Assert.Equal(2, Program.Main(args));
    }

    [Fact]
    public void CommandLineReportsCorruptAndMissingFilesWithoutCrashing()
    {
        using var files = new TestFiles();
        var corrupt = Path.Combine(files.DirectoryPath, "corrupt.pcap");
        File.WriteAllBytes(corrupt, []);
        Assert.Equal(1, Program.Main([corrupt]));
        Assert.Equal(1, Program.Main([Path.Combine(files.DirectoryPath, "missing.pcap")]));
    }
}
