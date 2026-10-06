using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class SessionMetadataTests
{
    [Theory]
    [InlineData("aion-idle", "aion-idle")]
    [InlineData(" ../aion:idle/ ", "aion-idle")]
    [InlineData("", "session")]
    [InlineData("...", "session")]
    [InlineData("CON", "CON")]
    public void SanitizesWindowsFilenameLabels(string label, string expected)
    {
        Assert.Equal(expected, CaptureSessionFactory.SanitizeLabel(label));
    }

    [Fact]
    public void LimitsLabelLengthAndPreservesCaptureAndMetadataDuringCollisions()
    {
        using var files = new TestFiles();
        Assert.Equal(80, CaptureSessionFactory.SanitizeLabel(new string('a', 500)).Length);
        var time = DateTimeOffset.Parse("2026-10-07T03:15:30+03:00");
        File.WriteAllText(Path.Combine(files.DirectoryPath, "2026-10-07_001530_aion-idle.json"), "private notes");
        var first = CaptureSessionFactory.Create(files.DirectoryPath, "adapter", time, "aion-idle");
        var second = CaptureSessionFactory.Create(files.DirectoryPath, "adapter", time, "aion-idle");
        Assert.Equal("2026-10-07_001530_aion-idle_001.pcap", Path.GetFileName(first.FilePath));
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.NotEqual(Guid.Empty, first.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal("private notes", File.ReadAllText(Path.Combine(files.DirectoryPath, "2026-10-07_001530_aion-idle.json")));
    }

    [Fact]
    public void JsonRoundTripPreservesSessionContextWithoutPacketPayload()
    {
        var metadata = Metadata().Finish(DateTimeOffset.UnixEpoch.AddSeconds(5), new(3, 123, 1, 1, 1, null, null), 2, 1, "Completed");
        var json = SessionMetadataStore.Serialize(metadata);
        var read = SessionMetadataStore.Deserialize(json);
        Assert.Equal(metadata.SessionId, read.SessionId);
        Assert.Equal("aion-idle", read.SessionLabel);
        Assert.Equal(CaptureMode.SelectedProcessTraffic, read.CaptureMode);
        Assert.Equal(1234, read.SelectedPid);
        Assert.Equal("Browser", read.SelectedProcessName);
        Assert.Equal("manual note", read.UserNotes);
        Assert.Equal(metadata.SelectedAdapter.Identifier, read.SelectedAdapter.Identifier);
        Assert.Equal(ProcessDiscoveryTests.Endpoint(), Assert.Single(read.ConnectionsAtStart));
        Assert.Equal(123, read.TotalBytes);
        Assert.Equal(3, read.TotalPackets);
        Assert.Equal(1, read.TcpPackets);
        Assert.Equal(1, read.UdpPackets);
        Assert.Equal(1, read.OtherPackets);
        Assert.Equal(TimeSpan.FromSeconds(5), read.Duration);
        Assert.Equal(2, read.QueueDroppedPackets);
        Assert.Equal(1, read.MetadataErrors);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentials", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AtomicMetadataWriteUpdatesCompletedSessionAndLeavesNoTempFile()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "session.json");
        await SessionMetadataStore.WriteAsync(path, Metadata());
        Assert.Null(SessionMetadataStore.Deserialize(File.ReadAllText(path)).EndedUtc);
        await SessionMetadataStore.WriteAsync(path, Metadata().Finish(DateTimeOffset.UnixEpoch.AddSeconds(2), new(0, 0, 0, 0, 0, null, null), 0, 0, "Completed"));
        Assert.Equal("Completed", SessionMetadataStore.Deserialize(File.ReadAllText(path)).Status);
        Assert.False(File.Exists(path + ".tmp"));
    }

    internal static SessionMetadata Metadata() => SessionMetadata.Create(new("unused.pcap", "synthetic", DateTimeOffset.UnixEpoch, Guid.NewGuid()),
        CaptureFilterTests.Adapter(), new(CaptureMode.SelectedProcessTraffic, "aion-idle", 1234, "Browser", [ProcessDiscoveryTests.Endpoint()], "manual note"), "ip and tcp");
}
