using Aion2Meter.Capture;
using Aion2Meter.Replay;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class NpcapFactAttribute : FactAttribute
{
    public NpcapFactAttribute()
    {
        try { _ = Pcap.Version; }
        catch (Exception) { Skip = "Npcap is unavailable; offline native writer integration test skipped."; }
    }
}

public sealed class NativeWriterTests
{
    [NpcapFact]
    [Trait("Category", "NpcapIntegration")]
    public void SharpPcapWriterProducesReplayCompatibleFileWithOriginalLength()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.DirectoryPath, "writer.pcap");
        var packet = TestFiles.Packet(6, DateTimeOffset.UnixEpoch.AddSeconds(1234).AddTicks(1234560), 100);
        using (var writer = new CaptureFileWriterDevice(path))
        {
            writer.Open(new DeviceConfiguration { LinkLayerType = LinkLayers.Ethernet, Snaplen = 262_144 });
            var header = new PcapHeader(1234, 123456, 100, (uint)packet.Data.Length);
            writer.Write(packet.Data, ref header);
        }
        var result = new ReplayAnalyzer(new PcapPacketSource(), new PacketMetadataReader()).Analyze(path, 1);
        Assert.Equal(1, result.Statistics.TcpCount);
        Assert.Equal(54, result.Statistics.TotalBytes);
        Assert.Equal(100, Assert.Single(result.Dump).OriginalLength);
        Assert.Equal(packet.TimestampUtc, result.Statistics.FirstTimestamp);
    }
}
