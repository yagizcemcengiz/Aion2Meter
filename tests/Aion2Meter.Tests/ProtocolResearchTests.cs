using System.Buffers.Binary;
using System.Net;
using System.Text;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class ProtocolResearchTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
    private static readonly TcpConnectionSelection Connection = new(IPAddress.Parse("192.0.2.1"), 12345, IPAddress.Parse("198.51.100.2"), 443);
    private static readonly string[] Endpoints = ["--local", "192.0.2.1:12345", "--remote", "198.51.100.2:443"];

    private static CapturedPacket Packet(byte[] payload, double seconds = 0, uint seq = 100, TcpFlags flags = TcpFlags.Psh | TcpFlags.Ack, bool reverse = false, int tcpOptions = 0, int padding = 0)
    {
        var template = TestFiles.Packet(6, Start.AddSeconds(seconds));
        var data = new byte[54 + tcpOptions + payload.Length + padding];
        template.Data.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + tcpOptions + payload.Length));
        TestFiles.U32(data, 38, seq, false);
        TestFiles.U32(data, 42, 999, false);
        data[46] = (byte)((5 + tcpOptions / 4) << 4);
        data[47] = (byte)flags;
        payload.CopyTo(data, 54 + tcpOptions);
        if (reverse)
        {
            var ip = data.AsSpan(26, 4).ToArray(); data.AsSpan(30, 4).CopyTo(data.AsSpan(26)); ip.CopyTo(data, 30);
            var port = data.AsSpan(34, 2).ToArray(); data.AsSpan(36, 2).CopyTo(data.AsSpan(34)); port.CopyTo(data, 36);
        }
        return template with { Data = data, OriginalLength = data.Length };
    }
    private static ResearchPacket Research(byte[] payload, double seconds = 0, uint seq = 100, long index = 1, bool reverse = false, TcpFlags flags = TcpFlags.Psh | TcpFlags.Ack) =>
        new(new TcpResearchPacketReader().Read(Packet(payload, seconds, seq, flags, reverse), index)!, reverse ? TrafficDirection.ServerToClient : TrafficDirection.ClientToServer, seconds);
    private static ReassembledStream Stream(params ResearchPacket[] packets) => TcpStreamReassembler.Assemble(packets, TrafficDirection.ClientToServer);

    [Fact]
    public void TimelineSortsByTimestampThenOriginalFileIndexAndUsesMetadataOrigin()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet([3], 3), Packet([1], 1), Packet([2], 1)]);
        var capture = new ResearchAnalyzer().Read(path, Connection, Start.AddSeconds(-5));
        Assert.Equal(new long[] { 2, 3, 1 }, capture.Packets.Select(p => p.Segment.PacketIndex));
        Assert.Equal(new double[] { 6, 6, 8 }, capture.Packets.Select(p => p.RelativeSeconds));
        Assert.Equal(999u, capture.Packets[0].Segment.AcknowledgmentNumber);
    }

    [Fact]
    public void ExactFourTupleSelectsBothDirectionsAndFallbackOriginIncludesOtherTraffic()
    {
        using var files = new TestFiles();
        var other = Packet([0]) with { Data = Packet([0]).Data.ToArray() };
        other.Data[37] = 80;
        var capture = new ResearchAnalyzer().Read(files.WritePcap([other, Packet([1], 2), Packet([2], 1, reverse: true)]), Connection);
        Assert.Equal(2, capture.Packets.Count);
        Assert.Equal(TrafficDirection.ServerToClient, capture.Packets[0].Direction);
        Assert.Equal(1, capture.Packets[0].RelativeSeconds);
        Assert.Contains("earliest", capture.OriginSource);
    }

    [Theory]
    [InlineData(TcpFlags.Ack, true)]
    [InlineData(TcpFlags.Syn | TcpFlags.Ack, false)]
    [InlineData(TcpFlags.Fin | TcpFlags.Ack, false)]
    [InlineData(TcpFlags.Rst, false)]
    public void AckOnlyIsDistinctFromControlPackets(TcpFlags flags, bool expected) =>
        Assert.Equal(expected, Research([], flags: flags).Segment.IsAckOnly);

    [Fact]
    public void PayloadLengthExcludesTcpOptionsAndEthernetPadding()
    {
        var segment = new TcpResearchPacketReader().Read(Packet([0x41, 0, 0xff], tcpOptions: 12, padding: 9), 1)!;
        Assert.Equal(78, segment.CapturedFrameLength);
        Assert.Equal(3, segment.DeclaredPayloadLength);
        Assert.Equal(new byte[] { 0x41, 0, 0xff }, segment.Payload);
        Assert.False(segment.IsTruncated);
        Assert.Equal("A..", PayloadPrivacy.Ascii(segment.Payload));
        Assert.False(segment.IsAckOnly);
    }

    [Fact]
    public void TruncatedPayloadRetainsDeclaredLengthAndMarksMissingTail()
    {
        var complete = Packet([1, 2, 3, 4]);
        var truncated = complete with { Data = complete.Data[..^2] };
        var segment = new TcpResearchPacketReader().Read(truncated, 1)!;
        Assert.Equal(4, segment.DeclaredPayloadLength);
        Assert.Equal(new byte[] { 1, 2 }, segment.Payload);
        Assert.True(segment.IsTruncated);
        var stream = Stream(new ResearchPacket(segment, TrafficDirection.ClientToServer, 0));
        Assert.Equal(new ByteRange(2, 2), Assert.Single(stream.Gaps));
    }

    [Fact]
    public void IPv6ExtensionAndTcpOptionsAreNotApplicationPayload()
    {
        var packet = Packet([0x41, 0x42], tcpOptions: 4);
        var data = new byte[14 + 40 + 8 + 24 + 2];
        packet.Data.AsSpan(0, 14).CopyTo(data);
        data[12] = 0x86; data[13] = 0xdd;
        data[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(18), 34);
        data[20] = 60;
        IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(data, 22);
        IPAddress.Parse("2001:db8::2").GetAddressBytes().CopyTo(data, 38);
        data[54] = 6;
        packet.Data.AsSpan(34).CopyTo(data.AsSpan(62));
        var segment = new TcpResearchPacketReader().Read(packet with { Data = data }, 1)!;
        Assert.Equal(2, segment.DeclaredPayloadLength);
        Assert.Equal(new byte[] { 0x41, 0x42 }, segment.Payload);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), segment.SourceIp);
    }

    [Fact]
    public void FragmentsAndMalformedTcpHeadersAreReportedWithoutGuessing()
    {
        var fragment = Packet([1]); fragment.Data[20] = 0x20;
        Assert.Throws<NotSupportedException>(() => new TcpResearchPacketReader().Read(fragment, 1));
        var invalid = Packet([1]); invalid.Data[46] = 0x40;
        Assert.Throws<InvalidDataException>(() => new TcpResearchPacketReader().Read(invalid, 1));
        using var files = new TestFiles();
        var capture = new ResearchAnalyzer().Read(files.WritePcap([fragment, invalid, Packet([1])]), Connection);
        Assert.Equal(1, capture.HeaderErrors);
        Assert.Equal(1, capture.UnsupportedPackets);
        Assert.Single(capture.Packets);
    }

    [Theory]
    [InlineData(0)] [InlineData(108)] [InlineData(101)] [InlineData(113)] [InlineData(276)]
    public void ReadsSupportedNonEthernetLinkHeaders(int type)
    {
        var packet = Packet([1, 2]);
        var offset = type switch { 0 or 108 => 4, 113 => 16, 276 => 20, _ => 0 };
        var data = new byte[offset + packet.Data.Length - 14];
        if (type is 0 or 108) TestFiles.U32(data, 0, 2, type == 0);
        if (type is 113 or 276) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(type == 113 ? 14 : 0), 0x0800);
        packet.Data.AsSpan(14).CopyTo(data.AsSpan(offset));
        var result = new TcpResearchPacketReader().Read(packet with { Data = data, LinkLayerType = (uint)type }, 1)!;
        Assert.Equal(new byte[] { 1, 2 }, result.Payload);
    }

    [Fact]
    public void VlanTagChangesFrameLengthButNotPayloadLength()
    {
        var packet = Packet([1, 2]);
        var data = new byte[packet.Data.Length + 4];
        packet.Data.AsSpan(0, 12).CopyTo(data);
        data[12] = 0x81; data[13] = 0;
        packet.Data.AsSpan(12).CopyTo(data.AsSpan(16));
        var result = new TcpResearchPacketReader().Read(packet with { Data = data }, 1)!;
        Assert.Equal(60, result.CapturedFrameLength);
        Assert.Equal(2, result.DeclaredPayloadLength);
    }

    [Fact]
    public void TimeAndLengthFiltersAreInclusiveAndKeepFrameAndPayloadSeparate()
    {
        var packets = new[] { Research([1, 2, 3], 1), Research([1], 2), Research([], 3, flags: TcpFlags.Ack), Research([1, 2, 3], 4) };
        var capture = new ResearchCapture("synthetic", Start, "synthetic", packets, 0, 0);
        Assert.Equal(2, ResearchAnalyzer.Select(capture, new(1, 2)).Count);
        Assert.Equal(2, ResearchAnalyzer.Select(capture, new(FrameLengths: new HashSet<int> { 57 })).Count);
        Assert.Single(ResearchAnalyzer.Select(capture, new(1, 3, new HashSet<int> { 57 }, 3)));
        Assert.Empty(ResearchAnalyzer.Select(capture, new(PayloadLength: 57)));
        Assert.Throws<ArgumentException>(() => ResearchAnalyzer.Select(capture, new(3, 1)));
        Assert.Throws<ArgumentException>(() => ResearchAnalyzer.Select(capture, new(double.NaN)));
    }

    [Fact]
    public void SequenceIsGenericDirectionalContiguousAndIgnoresReverseInterleaving()
    {
        var packets = new[] { Research([1, 2, 3], 1, index: 1), Research([], 1.001, index: 2, reverse: true, flags: TcpFlags.Ack), Research(new byte[7], 1.002, index: 3), Research(new byte[38], 1.003, index: 4) };
        var match = Assert.Single(FrameSequenceSearch.Find(packets, [57, 61, 92], 20));
        Assert.Equal(new long[] { 1, 3, 4 }, match.Packets.Select(p => p.Segment.PacketIndex));
        Assert.Equal(new[] { 3, 7, 38 }, match.Packets.Select(p => p.Segment.Payload.Length));
        var interrupted = packets.Append(Research([], 1.0015, index: 5, flags: TcpFlags.Ack)).ToArray();
        Assert.Empty(FrameSequenceSearch.Find(interrupted, [57, 61, 92], 20));
    }

    [Fact]
    public void SequenceWindowBoundsTotalElapsedTimeAndIncludesExactBoundary()
    {
        var packets = new[] { Research([1, 2, 3], 1), Research(new byte[7], 1.012), Research(new byte[38], 1.024) };
        Assert.Empty(FrameSequenceSearch.Find(packets, [57, 61, 92], 20));
        Assert.Single(FrameSequenceSearch.Find(packets, [57, 61, 92], 24));
        Assert.Throws<ArgumentException>(() => FrameSequenceSearch.Find(packets, [], 20));
        Assert.Throws<ArgumentException>(() => FrameSequenceSearch.Find(packets, [57], double.PositiveInfinity));
    }

    [Fact]
    public void ByteDiffHasCommonPrefixSuffixAndExactVaryingRegions()
    {
        var result = ByteComparison.Compare([[1, 2, 3, 4, 5, 6], [1, 2, 9, 4, 8, 6], [1, 2, 7, 4, 5, 6]]);
        Assert.Equal(2, result.CommonPrefixLength);
        Assert.Equal(1, result.CommonSuffixLength);
        Assert.Equal(new[] { new ByteRange(2, 1), new ByteRange(4, 1) }, result.VaryingRanges);
        Assert.Equal(new[] { new ByteRange(0, 2), new ByteRange(3, 1), new ByteRange(5, 1) }, result.IdenticalRanges);
    }

    [Fact]
    public void ByteDiffHandlesUnequalAndEmptyPayloadsAndDoesNotDoubleCountPrefixSuffix()
    {
        var differentLengths = ByteComparison.Compare([[1, 2, 3], [1, 9, 2, 3]]);
        Assert.Equal(1, differentLengths.CommonPrefixLength);
        Assert.Equal(2, differentLengths.CommonSuffixLength);
        Assert.Equal(new ByteRange(1, 3), Assert.Single(differentLengths.VaryingRanges));
        Assert.Equal(0, ByteComparison.Compare([[], [1]]).CommonPrefixLength);
        var same = ByteComparison.Compare([[1, 2], [1, 2]]);
        Assert.Equal(2, same.CommonPrefixLength);
        Assert.Equal(0, same.CommonSuffixLength);
        Assert.Empty(same.VaryingRanges);
    }

    [Fact]
    public void OutOfOrderAndRetransmissionAreReassembledDeterministically()
    {
        var stream = Stream(Research([4, 5, 6], 1, 103, 1), Research([], 1.5, 106, 2, flags: TcpFlags.Ack), Research([1, 2, 3], 2, 100, 3), Research([4, 5, 6], 3, 103, 4));
        Assert.Equal(100u, stream.BaseSequence);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, stream.Chunks.SelectMany(c => c.Bytes));
        Assert.Equal(1, stream.DuplicateSegments);
        Assert.Equal(3, stream.OverlapBytes);
        Assert.Empty(stream.Gaps);
        Assert.Equal(3, stream.Chunks[0].PacketIndex);
        Assert.Equal(Start.AddSeconds(2), stream.Chunks[0].TimestampUtc);
    }

    [Fact]
    public void PartialOverlapAddsOnlyNewBytesAndConflictsKeepEarliestTimestampIndex()
    {
        var first = Research([1, 2, 3], 0, 100, 1);
        var stream = Stream(Research([9, 9, 4, 5], 1, 101, 3), Research([8, 8, 8], 0, 100, 2), first);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, stream.Chunks.SelectMany(c => c.Bytes));
        Assert.Equal(new ByteRange(0, 3), Assert.Single(stream.Conflicts));
        Assert.Equal(5, stream.OverlapBytes);
        Assert.Equal(0, stream.DuplicateSegments);
    }

    [Fact]
    public void GapsAreNeverCollapsedIntoContiguousData()
    {
        var stream = Stream(Research([1, 2], seq: 100), Research([3, 4], 1, 105));
        Assert.Equal(7, stream.DeclaredSpan);
        Assert.Equal(new ByteRange(2, 3), Assert.Single(stream.Gaps));
        Assert.Equal(5, stream.Chunks[1].Offset);
        Assert.Empty(StreamPatternSearch.Find(stream, [2, 3]));
    }

    [Fact]
    public void SequenceWrapAndSynSequenceConsumptionAreHandled()
    {
        var stream = Stream(Research([1, 2], 0, uint.MaxValue - 1, flags: TcpFlags.Syn | TcpFlags.Ack), Research([3, 4], 1, 1));
        Assert.Equal(uint.MaxValue, stream.BaseSequence);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, stream.Chunks.SelectMany(c => c.Bytes));
        Assert.Empty(stream.Gaps);
        Assert.True(stream.SynObserved);
    }

    [Fact]
    public void ConnectionReuseAndAmbiguousSequenceSpanAreRejectedForStreamAssembly()
    {
        Assert.Throws<InvalidDataException>(() => Stream(Research([], flags: TcpFlags.Syn), Research([], 1, 200, flags: TcpFlags.Syn)));
        Assert.Throws<InvalidDataException>(() => Stream(Research([1], seq: 0), Research([2], 1, int.MaxValue)));
        Assert.Empty(Stream(Research([], flags: TcpFlags.Ack)).Chunks);
    }

    [Fact]
    public void StreamPatternSearchCrossesSegmentsAndFindsOverlappingOccurrences()
    {
        var stream = Stream(Research([1, 1], seq: 100), Research([1, 1], 1, 102));
        Assert.Equal(new long[] { 0, 1, 2 }, StreamPatternSearch.Find(stream, [1, 1]));
    }

    [Fact]
    public void PrivacyDetectsCredentialsBeyondPreviewAndAcrossPacketBoundaries()
    {
        Assert.True(PayloadPrivacy.IsSensitive(Encoding.ASCII.GetBytes(new string('x', 128) + "Authorization: Bearer synthetic-secret")));
        var part1 = Encoding.ASCII.GetBytes("Autho");
        var part2 = Encoding.ASCII.GetBytes("rization: synthetic-private");
        Assert.True(PayloadPrivacy.IsSensitive(Stream(Research(part1), Research(part2, 1, 105))));
        Assert.False(PayloadPrivacy.IsSensitive(Stream(Research(part1), Research(part2, 1, 106))));
    }

    [Fact]
    public void CliSuppressesWholeDirectionEvenWhenSecretFallsOutsideDisplayWindow()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet(Encoding.ASCII.GetBytes("Authorization: synthetic-private"), 0), Packet(Encoding.ASCII.GetBytes("PRIVATE-CONTENT"), 1, 132)]);
        var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run([path, .. Endpoints, "--payload", "--from", "1", "--to", "1", "--max-payload-bytes", "2"], output, new StringWriter()));
        Assert.Contains("suppressed", output.ToString());
        Assert.DoesNotContain("PRIVATE-CONTENT", output.ToString());
        Assert.DoesNotContain("hex=", output.ToString());
    }

    [Fact]
    public void CliPayloadOnlySkipsAckAndHonorsBoundsAndMetadataTimeOrigin()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet([], flags: TcpFlags.Ack), Packet([0x41, 0x42, 0x43], 1), Packet([0x44], 2)]);
        var metadata = SessionMetadataTests.Metadata() with { StartedUtc = Start.AddSeconds(-10) };
        File.WriteAllText(Path.ChangeExtension(path, ".json"), SessionMetadataStore.Serialize(metadata));
        var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run([path, .. Endpoints, "--payload", "--max-payload-bytes", "2", "--max-packets", "1"], output, new StringWriter()));
        Assert.Contains("t=11.0000000s", output.ToString());
        Assert.Contains("hex=4142 ascii=AB shown=2/3", output.ToString());
        Assert.DoesNotContain("#1 UTC", output.ToString());
        Assert.Contains("rows omitted=1", output.ToString());
        Assert.Equal(2, Directory.GetFiles(files.DirectoryPath).Length);
    }

    [Fact]
    public void CliCrossCaptureComparisonIncludesAllMatchesAndMissingIdleSequence()
    {
        using var files = new TestFiles();
        var idle = files.WritePcap([Packet([], flags: TcpFlags.Ack)]);
        var first = files.WritePcap([Packet([1, 2, 3], 1), Packet(new byte[7], 1.001, 103)]);
        var second = files.WritePcap([Packet([1, 4, 3], 1), Packet(new byte[7], 1.001, 103), Packet([1, 5, 3], 2, 110), Packet(new byte[7], 2.001, 113)]);
        var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["compare", idle, first, second, .. Endpoints, "--sequence", "57,61"], output, new StringWriter()));
        Assert.Contains("matches=0", output.ToString());
        Assert.Contains("matches=2", output.ToString());
        Assert.Contains("frame=57, samples=3", output.ToString());
        Assert.Contains("common prefix length=1 hex=01", output.ToString());
        Assert.Contains("common suffix length=1 hex=03", output.ToString());
        Assert.Contains("varying offsets: 1..1", output.ToString());
        Assert.Equal(3, Directory.GetFiles(files.DirectoryPath).Length);
    }

    [Theory]
    [InlineData("--from", "NaN")]
    [InlineData("--to", "-1")]
    [InlineData("--max-payload-bytes", "4097")]
    [InlineData("--max-packets", "0")]
    [InlineData("--sequence", "57,,92")]
    [InlineData("--stream-pattern", "xyz")]
    [InlineData("--stream-offset", "0")]
    [InlineData("--direction", "unknown")]
    public void CliRejectsInvalidOptionsBeforeReadingCapture(string option, string value) =>
        Assert.Equal(2, ResearchCli.Run(["missing.pcap", .. Endpoints, option, value], new StringWriter(), new StringWriter()));

    [Fact]
    public void CliDoesNotManufactureSequenceByCombiningLengthFilters()
    {
        Assert.Equal(2, ResearchCli.Run(["missing.pcap", .. Endpoints, "--sequence", "57,61,92", "--frame-lengths", "57,61,92"], new StringWriter(), new StringWriter()));
        Assert.Equal(1, ResearchCli.Run(["missing.pcap", .. Endpoints], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void CompleteRetransmissionFillsEarlierTruncatedTail()
    {
        var original = Research([1, 2, 3, 4]);
        var truncated = original with { Segment = original.Segment with { Payload = [1, 2], IsTruncated = true } };
        var retransmission = Research([1, 2, 3, 4], 1, index: 2);
        var stream = Stream(truncated, retransmission);
        Assert.Empty(stream.Gaps);
        Assert.Equal(2, stream.OverlapBytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, stream.Chunks.SelectMany(c => c.Bytes));
        Assert.Equal(2, stream.Chunks[^1].PacketIndex);
    }

    [Fact]
    public void StreamAndComparisonNeverExposeDetectedAuthBytes()
    {
        using var files = new TestFiles();
        var first = files.WritePcap([Packet(Encoding.ASCII.GetBytes("password=synthetic-private"))]);
        var second = files.WritePcap([Packet(Encoding.ASCII.GetBytes("password=synthetic-private"))]);
        var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["compare", first, second, .. Endpoints, "--sequence", "80", "--streams", "--payload"], output, new StringWriter()));
        Assert.Contains("Byte comparison suppressed", output.ToString());
        Assert.Contains("Stream preview/pattern suppressed", output.ToString());
        Assert.DoesNotContain("synthetic-private", output.ToString());
        Assert.DoesNotContain("hex=", output.ToString());
    }

    [Fact]
    public void TimelineRemainsAvailableAcrossConnectionEpochsWhileBytesAreSuppressed()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet([], flags: TcpFlags.Syn), Packet([1], 0.1, 101), Packet([], 1, 200, TcpFlags.Syn), Packet([2], 1.1, 201)]);
        var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run([path, .. Endpoints, "--timeline", "--payload"], output, new StringWriter()));
        Assert.Contains("#4 UTC", output.ToString());
        Assert.DoesNotContain("hex=", output.ToString());
        Assert.Equal(1, ResearchCli.Run([path, .. Endpoints, "--streams"], new StringWriter(), new StringWriter()));
        Assert.Equal(0, ResearchCli.Run([path, .. Endpoints, "--streams", "--from", "1", "--to", "2"], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void SplitAuthMarkerOutsideTimeWindowStillSuppressesPreview()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet(Encoding.ASCII.GetBytes("Autho")), Packet(Encoding.ASCII.GetBytes("rization"), 1, 105), Packet(Encoding.ASCII.GetBytes("binary-preview"), 2, 113)]);
        var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run([path, .. Endpoints, "--payload", "--from", "2"], output, new StringWriter()));
        Assert.Contains("suppressed", output.ToString());
        Assert.DoesNotContain("binary-preview", output.ToString());
        Assert.DoesNotContain("hex=", output.ToString());
    }
}
