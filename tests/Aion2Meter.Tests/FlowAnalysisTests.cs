using System.Net;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class FlowAnalysisTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.UnixEpoch.AddSeconds(1000);
    private static PacketMetadata Packet(int size = 50, int seconds = 0) => new(Time.AddSeconds(seconds), size, 900,
        IpVersion: 4, SourceIp: IPAddress.Parse("192.0.2.1"), DestinationIp: IPAddress.Parse("198.51.100.2"),
        TransportProtocol: TransportProtocol.Tcp, SourcePort: 49152, DestinationPort: 443, IpProtocolNumber: 6);

    [Fact]
    public void GroupsByDirectionalTupleAndProtocol()
    {
        var stats = new FlowStatistics();
        var packet = Packet();
        stats.Add(packet);
        stats.Add(packet with { DestinationPort = 444 });
        stats.Add(packet with { SourceIp = packet.DestinationIp, DestinationIp = packet.SourceIp, SourcePort = 443, DestinationPort = 49152 });
        stats.Add(packet with { TransportProtocol = TransportProtocol.Udp, IpProtocolNumber = 17 });
        Assert.Equal(4, stats.Snapshot().Count);
    }

    [Fact]
    public void AggregatesPacketCountCapturedBytesAndUnorderedTimestamps()
    {
        var stats = new FlowStatistics();
        stats.Add(Packet(50, 5));
        stats.Add(Packet(70, 1));
        stats.Add(Packet(50, 3));
        var flow = Assert.Single(stats.Snapshot());
        Assert.Equal(3, flow.Packets);
        Assert.Equal(170, flow.Bytes);
        Assert.Equal(Time.AddSeconds(1), flow.FirstSeen);
        Assert.Equal(Time.AddSeconds(5), flow.LastSeen);
        Assert.Equal(TimeSpan.FromSeconds(4), flow.Duration);
        Assert.Equal(50, flow.MinimumCapturedLength);
        Assert.Equal(70, flow.MaximumCapturedLength);
        Assert.Equal(170.0 / 3, flow.AverageCapturedLength);
        Assert.Equal(new PacketSizeFrequency(50, 2), flow.CommonSizes()[0]);
        Assert.Equal(new PacketSizeFrequency(70, 1), flow.CommonSizes()[1]);
    }

    [Fact]
    public void TopFlowsSortByBytesAndLimitResults()
    {
        var stats = new FlowStatistics();
        stats.Add(Packet(20));
        stats.Add(Packet(100) with { DestinationPort = 80 });
        stats.Add(Packet(70) with { DestinationPort = 53 });
        var top = FlowStatistics.Top(stats.Snapshot(), 2);
        Assert.Equal(new long[] { 100, 70 }, top.Select(f => f.Bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowStatistics.Top(stats.Snapshot(), 0));
    }

    [Fact]
    public void HandlesIpv6MissingPortsAndUngroupablePacketsWithoutGuessing()
    {
        var stats = new FlowStatistics();
        stats.Add(Packet() with { IpVersion = 6, SourceIp = IPAddress.IPv6Loopback, DestinationIp = IPAddress.Parse("2001:db8::1"), SourcePort = null, DestinationPort = null });
        stats.Add(new(Time, 0, 0));
        var flow = Assert.Single(stats.Snapshot());
        Assert.Equal("::1", flow.Key.SourceIp);
        Assert.Null(flow.Key.SourcePort);
        Assert.Equal(1, stats.UngroupedPackets);
    }

    [Fact]
    public void OfflineReplayAggregatesFlowsWithoutNpcapAndDefaultSummaryDoesNotKeepFlows()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([TestFiles.Packet(6, Time), TestFiles.Packet(6, Time.AddSeconds(2)), TestFiles.IPv6Udp(Time)]);
        var analyzer = new ReplayAnalyzer(new PcapPacketSource(), new PacketMetadataReader());
        Assert.Empty(analyzer.Analyze(path).Flows);
        var result = analyzer.Analyze(path, includeFlows: true);
        Assert.Equal(2, result.Flows.Count);
        Assert.Equal(result.Statistics.TotalBytes, result.Flows.Sum(f => f.Bytes));
        Assert.Equal(2, result.Flows.Single(f => f.Key.Protocol == TransportProtocol.Tcp).Packets);
    }

    [Fact]
    public void ComparisonFindsOnlyCommonFlowsDeltasAndRemoteChanges()
    {
        var a = Result(Packet(50), Packet(20) with { DestinationIp = IPAddress.Parse("198.51.100.3") });
        var b = Result(Packet(100), Packet(80) with { DestinationIp = IPAddress.Parse("198.51.100.4") }, Packet(100));
        var comparison = CaptureComparison.Compare(a, b, SessionMetadataTests.Metadata(), SessionMetadataTests.Metadata());
        Assert.Equal("198.51.100.3", Assert.Single(comparison.OnlyA).Key.DestinationIp);
        Assert.Equal("198.51.100.4", Assert.Single(comparison.OnlyB).Key.DestinationIp);
        var common = Assert.Single(comparison.Common);
        Assert.Equal(1, common.PacketDelta);
        Assert.Equal(150, common.ByteDelta);
        Assert.Equal(1, comparison.PacketDelta);
        Assert.Equal(210, comparison.ByteDelta);
        Assert.True(comparison.RemoteEndpointsIdentified);
        Assert.Equal("198.51.100.4", Assert.Single(comparison.NewEndpoints).Ip);
        Assert.Equal("198.51.100.3", Assert.Single(comparison.LostEndpoints).Ip);
    }

    [Fact]
    public void ComparisonWithoutMetadataDoesNotClaimRemoteDirection()
    {
        var comparison = CaptureComparison.Compare(Result(), Result(Packet()));
        Assert.False(comparison.RemoteEndpointsIdentified);
        Assert.Equal(2, comparison.NewEndpoints.Count);
        Assert.Empty(comparison.LostEndpoints);
    }

    [Fact]
    public void EmptyCapturesHaveNoFlowsSizesOrComparisonChanges()
    {
        var result = CaptureComparison.Compare(Result(), Result());
        Assert.Empty(new FlowStatistics().Snapshot());
        Assert.Empty(result.OnlyA);
        Assert.Empty(result.OnlyB);
        Assert.Empty(result.Common);
        Assert.Empty(result.NewEndpoints);
        Assert.Empty(result.LostEndpoints);
        Assert.Equal(0, result.PacketDelta);
        Assert.Equal(0, result.ByteDelta);
    }

    [Fact]
    public void RemoteComparisonTreatsIncomingAndOutgoingPeerAsSameEndpoint()
    {
        var packet = Packet();
        var reversed = packet with { SourceIp = packet.DestinationIp, DestinationIp = packet.SourceIp, SourcePort = 443, DestinationPort = 49152 };
        var result = CaptureComparison.Compare(Result(packet), Result(reversed), SessionMetadataTests.Metadata(), SessionMetadataTests.Metadata());
        Assert.Empty(result.NewEndpoints);
        Assert.Empty(result.LostEndpoints);
    }

    [Fact]
    public void RemoteComparisonNormalizesIpv6AdapterScopeIds()
    {
        var metadata = SessionMetadataTests.Metadata() with
        { SelectedAdapter = new("v6", "v6", "v6", ["fe80::1%7"]) };
        var packet = Packet() with { SourceIp = IPAddress.Parse("fe80::1"), DestinationIp = IPAddress.Parse("fe80::2") };
        var comparison = CaptureComparison.Compare(Result(), Result(packet), metadata, metadata);
        Assert.True(comparison.RemoteEndpointsIdentified);
        Assert.Equal("fe80::2", Assert.Single(comparison.NewEndpoints).Ip);
    }

    [Theory]
    [InlineData("--top-flows", "0")]
    [InlineData("--top-flows", "10001")]
    [InlineData("--top-flows")]
    [InlineData("--flows", "--flows")]
    [InlineData("--sizes", "unexpected")]
    public void CliRejectsMalformedFlowArguments(params string[] options) => Assert.Equal(2, Program.Main(["unused.pcap", .. options]));

    [Fact]
    public async Task CliSupportsCombinedOptionsComparisonAndMalformedOptionalMetadata()
    {
        using var files = new TestFiles();
        var a = files.WritePcap([TestFiles.Packet(6, Time)]);
        var b = files.WritePcap([TestFiles.Packet(6, Time), TestFiles.Packet(17, Time)]);
        Assert.Equal(0, Program.Main([a, "--flows", "--top-flows", "20", "--sizes", "--dump", "1"]));
        await SessionMetadataStore.WriteAsync(Path.ChangeExtension(a, ".json"), SessionMetadataTests.Metadata());
        await SessionMetadataStore.WriteAsync(Path.ChangeExtension(b, ".json"), SessionMetadataTests.Metadata());
        Assert.Equal(0, Program.Main(["compare", a, b]));
        File.WriteAllText(Path.ChangeExtension(b, ".json"), "{invalid");
        Assert.Equal(0, Program.Main(["compare", a, b]));
        Assert.Equal(2, Program.Main(["compare", a]));
    }

    private static ReplayResult Result(params PacketMetadata[] packets)
    {
        var stats = new PacketStatistics();
        var flows = new FlowStatistics();
        foreach (var packet in packets) { stats.Add(packet); flows.Add(packet); }
        return new(stats.Snapshot(), [], 0) { Flows = flows.Snapshot() };
    }
}
