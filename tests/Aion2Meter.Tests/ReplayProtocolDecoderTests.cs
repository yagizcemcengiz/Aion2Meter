using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using K4os.Compression.LZ4;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class ReplayProtocolDecoderTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData("00", 0ul)] [InlineData("7F", 127ul)] [InlineData("8001", 128ul)]
    [InlineData("BE04", 574ul)] [InlineData("B802", 312ul)] [InlineData("8809", 1160ul)] [InlineData("9C05", 668ul)]
    [InlineData("808001", 16384ul)] [InlineData("FFFFFF7F", 268435455ul)]
    [InlineData("FFFFFFFF0F", uint.MaxValue)] [InlineData("FFFFFFFFFFFFFFFFFF01", ulong.MaxValue)]
    public void UnsignedValuesAndConsumedWidths(string hex, ulong expected)
    {
        var bytes = Convert.FromHexString(hex);
        var result = UnsignedVarint.Read([.. bytes, 0x12]);
        Assert.True(result.Success); Assert.Equal(expected, result.Value); Assert.Equal(bytes.Length, result.BytesConsumed);
    }

    [Theory]
    [InlineData("", "Truncated")] [InlineData("80", "Truncated")] [InlineData("8080", "Truncated")]
    [InlineData("FFFFFFFFFFFFFFFFFF02", "overflow")] [InlineData("80808080808080808080", "width")]
    public void VarintFailuresAreBounded(string hex, string error)
    {
        var result = UnsignedVarint.Read(Convert.FromHexString(hex));
        Assert.False(result.Success); Assert.Contains(error, result.Error); Assert.InRange(result.BytesConsumed, 0, 10);
    }

    [Fact]
    public void ConfigurableVarintBoundsAndCanonicalPolicy()
    {
        Assert.False(UnsignedVarint.Read([0x80, 0x80, 1], maximumWidth: 2).Success);
        Assert.False(UnsignedVarint.Read([0x80, 1], maximumValue: 127).Success);
        Assert.True(UnsignedVarint.Read([0x80, 0]).Success);
        Assert.False(UnsignedVarint.Read([0x80, 0], requireCanonical: true).Success);
        Assert.Throws<ArgumentOutOfRangeException>(() => UnsignedVarint.Read([1], 11));
    }

    [Fact]
    public void FramingPreservesMultipleFramesAndSplitChunkCompletion()
    {
        var first = Frame([0x90, 0x91, 1, 2, 3]); var second = Frame([0x90, 0x92]);
        var joined = first.Concat(second).ToArray();
        var stream = Stream([new(0, joined[..3], Start, 1), new(3, joined[3..], Start.AddSeconds(1), 2)]);
        var result = CandidateBlockExtractor.Extract(stream, Start);
        Assert.Equal(2, result.Blocks.Count); Assert.Empty(result.Issues);
        Assert.Equal(first, result.Blocks[0].Bytes); Assert.Equal(second, result.Blocks[1].Bytes);
        Assert.Equal(2, result.Blocks[0].CompletionPacketIndex); Assert.Equal(1, result.Blocks[0].PacketIndex);
    }

    [Theory]
    [InlineData("00")] [InlineData("03")] [InlineData("80")] [InlineData("FFFFFF7F")] [InlineData("8000")]
    public void InvalidFramingStopsWithoutResync(string hex)
    {
        var bytes = Convert.FromHexString(hex).Concat(Frame([0x90, 0x91])).ToArray();
        Assert.False(ApplicationFraming.Read(bytes).Success);
        var decoded = Decode(bytes);
        var issue = Assert.Single(decoded.Records);
        Assert.Equal("MalformedFraming", issue.DecodeStatus); Assert.Equal(bytes, issue.RawBytes);
    }

    [Fact]
    public void TruncatedFrameAndGapCannotInventACompleteRecord()
    {
        var bytes = Frame([0x90, 0x91, 1, 2]);
        Assert.False(ApplicationFraming.Read(bytes[..^1]).Success);
        var result = new ReplayProtocolDecoder().Decode("synthetic", [Stream([new(0, bytes[..2], Start, 1), new(bytes.Length + 2, bytes, Start, 2)])], Start);
        Assert.Single(result.Records, r => r.DecodeStatus == "MalformedFraming");
        Assert.Single(result.Records, r => r.DecodeStatus == "Unknown");
    }

    [Fact]
    public void KnownLiteralRawLz4FixtureAndProvenance()
    {
        // Independently constructed literal-only raw block: seven literal bytes, no match sequence.
        byte[] inner = [0x0a, 0x99, 0xaa, 1, 2, 3, 4];
        var result = Decode(Container([0x70, .. inner], 7));
        var audit = Assert.Single(result.Containers);
        Assert.True(audit.ValidDecompression); Assert.Equal(7, audit.FullyConsumedBytes); Assert.Equal(0, audit.TrailingUnparsedBytes);
        var raw = Assert.Single(result.Records, r => r.ContainerDepth == 1);
        Assert.Equal(inner, raw.RawBytes); Assert.Equal("99AA", raw.OpcodeCandidate);
        Assert.Equal(0, raw.InnerOffset); Assert.Equal(0, raw.StreamOffset); Assert.Equal(Start, raw.TimestampUtc);
        Assert.Equal(1, raw.ContainerPath[0].ContainerRecordId); Assert.Equal("synthetic", raw.SourceCapture);
    }

    [Theory]
    [InlineData("00", 8)] [InlineData("700A99AA01020304", 8)] [InlineData("700A99AA01020304", 6)]
    public void BadLz4OrDeclaredSizeMismatchRetainsContainer(string compressed, int size)
    {
        var bytes = Container(Convert.FromHexString(compressed), (uint)size);
        var result = Decode(bytes);
        Assert.False(Assert.Single(result.Containers).ValidDecompression);
        Assert.Equal(bytes, Assert.Single(result.Records).RawBytes);
        Assert.Equal("FailedContainer", result.Records[0].DecodeStatus);
    }

    [Fact]
    public void HeaderOutputAndDepthBoundsAreExplicitFailures()
    {
        Assert.False(Assert.Single(Decode(Frame([0xff, 0xff, 1])).Containers).ValidDecompression);
        var huge = Decode(Container([0], uint.MaxValue));
        Assert.Contains("bound", Assert.Single(huge.Containers).Error);
        var nested = Pack(Pack(Frame([0x99, 0xaa])));
        var result = Decode(nested, new() { MaximumContainerDepth = 1 });
        Assert.Equal(2, result.Containers.Count); Assert.Single(result.Containers, c => c.ValidDecompression);
        Assert.Contains("depth", Assert.Single(result.Containers, c => !c.ValidDecompression).Error);
    }

    [Fact]
    public void PerOuterAndPerCaptureBudgetsIncludeNestedAttempts()
    {
        var inner = Frame([0x99, 0xaa]); var outer = Pack(Pack(inner));
        var first = Decode(outer, new() { MaximumDecompressedBytesPerOuter = Pack(inner).Length });
        Assert.Contains("budget", Assert.Single(first.Containers, c => !c.ValidDecompression).Error);
        var second = Decode(Pack(inner).Concat(Pack(inner)).ToArray(), new() { MaximumDecompressedBytesPerCapture = inner.Length });
        Assert.Single(second.Containers, c => c.ValidDecompression); Assert.Single(second.Containers, c => !c.ValidDecompression);
    }

    [Fact]
    public void FrameCountBoundAndMalformedInnerRemainderArePreserved()
    {
        var inner = Frame([0x99, 0xaa]);
        var limited = Decode(Pack(inner.Concat(inner).ToArray()), new() { MaximumFramesPerContainer = 1 });
        Assert.Equal(inner.Length, Assert.Single(limited.Containers).TrailingUnparsedBytes);
        Assert.Equal(inner, Assert.Single(limited.Records, r => r.DecodeStatus == "MalformedInnerFraming").RawBytes);
        var malformed = Decode(Pack(inner.Concat(new byte[] { 0 }).ToArray()));
        Assert.Equal(1, Assert.Single(malformed.Containers).TrailingUnparsedBytes);
        Assert.Equal(new byte[] { 0 }, Assert.Single(malformed.Records, r => r.DecodeStatus == "MalformedInnerFraming").RawBytes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void SyntheticCategory6ComponentsAreSubtractedOnce(int count)
    {
        var components = Enumerable.Repeat(7ul, count).ToArray();
        var candidate = Assert.Single(Decode(Combat(400, components)).CombatCandidates);
        Assert.Equal("Supported", candidate.Status); Assert.Equal(400ul, candidate.AggregateAmount);
        Assert.Equal(400ul - (ulong)count * 7, candidate.DerivedBaseAmount); Assert.Equal(components, candidate.OptionalComponents);
        Assert.Equal(100ul, candidate.TargetIdCandidate); Assert.Equal(200ul, candidate.ActorIdCandidate);
        Assert.Equal(123456u, candidate.RawSkillCodeCandidate); Assert.Equal((byte)8, candidate.ModifierCandidate);
        Assert.Equal("0438", candidate.RawRecord.OpcodeCandidate);
    }

    [Fact]
    public void VariableIdentityTypePreValueAggregateAndComponentWidthsMoveCursor()
    {
        var candidate = Assert.Single(Decode(Combat(100000, [16384], target: 1, actor: 100000, preValue: 1)).CombatCandidates);
        Assert.Equal("Supported", candidate.Status); Assert.Equal(100000ul, candidate.AggregateAmount);
        Assert.Equal(83616ul, candidate.DerivedBaseAmount); Assert.Equal(1ul, candidate.TargetIdCandidate);
        Assert.Equal(100000ul, candidate.ActorIdCandidate); Assert.Equal(1ul, candidate.PreValueCandidate);
        var widest = Assert.Single(Decode(Combat(ulong.MaxValue, [1], target: ulong.MaxValue)).CombatCandidates);
        Assert.Equal(ulong.MaxValue - 1, widest.DerivedBaseAmount);
    }

    [Fact]
    public void ImpossibleAndOverflowingComponentsRetainAggregate()
    {
        var excessive = Assert.Single(Decode(Combat(5, [6])).CombatCandidates);
        Assert.Equal("Malformed", excessive.Status); Assert.Equal(5ul, excessive.AggregateAmount); Assert.Null(excessive.DerivedBaseAmount);
        var overflow = Assert.Single(Decode(Combat(ulong.MaxValue, [ulong.MaxValue, 1])).CombatCandidates);
        Assert.Contains("overflow", Assert.Single(overflow.Warnings)); Assert.Equal(ulong.MaxValue, overflow.AggregateAmount);
    }

    [Fact]
    public void UnknownCategoryLayoutAndTrailingBytesRemainUnresolved()
    {
        var category = Assert.Single(Decode(Combat(400, [], category: 4)).CombatCandidates);
        Assert.Equal("Unsupported", category.Status); Assert.Null(category.AggregateAmount);
        var trailing = Assert.Single(Decode(Combat(400, [], trailing: [9])).CombatCandidates);
        Assert.Equal("Unsupported", trailing.Status); Assert.Equal(400ul, trailing.AggregateAmount); Assert.Null(trailing.DerivedBaseAmount);
        var shortBody = Frame([0x04, 0x38, 0x64, 6, 0, 0xc8, 1]);
        Assert.Equal("Malformed", Assert.Single(Decode(shortBody).CombatCandidates).Status);
    }

    [Fact]
    public void CandidateWithinContainerHasInheritedTimestampAndSeparateOffset()
    {
        var prefix = Frame([0x99, 0xaa]);
        var decoded = Decode(Pack(prefix.Concat(Combat(400, [7])).ToArray()));
        var record = Assert.Single(decoded.CombatCandidates).RawRecord;
        Assert.Equal(prefix.Length, record.InnerOffset); Assert.Equal(0, record.OuterFrameOffset);
        Assert.Equal(0, record.StreamOffset); Assert.Equal(Start, record.TimestampUtc);
    }

    [Fact]
    public void NestedContainerPathsRetainEveryParent()
    {
        var result = Decode(Pack(Pack(Combat(400, []))));
        var record = Assert.Single(result.CombatCandidates).RawRecord;
        Assert.Equal(2, record.ContainerDepth); Assert.Equal(new[] { 1, 2 }, record.ContainerPath.Select(p => p.ContainerRecordId));
        Assert.Equal(2, result.Containers.Count(c => c.ValidDecompression));
    }

    [Fact]
    public void CredentialsInsideDecompressedBytesSuppressWholeDirection()
    {
        var text = Frame([0x99, 0xaa, .. Encoding.ASCII.GetBytes("Authorization: Bearer synthetic")]);
        var result = Decode(Combat(400, []).Concat(Pack(text)).ToArray());
        Assert.Empty(result.CombatCandidates); Assert.All(result.Records, r => { Assert.Empty(r.RawBytes); Assert.Equal("Suppressed", r.DecodeStatus); });
    }

    [Fact]
    public void ConflictingTcpBytesStillStopFraming()
    {
        var bytes = Combat(400, []);
        var stream = Stream([new(0, bytes, Start, 1)]) with { Conflicts = [new(5, 1)] };
        var result = new ReplayProtocolDecoder().Decode("synthetic", [stream], Start);
        Assert.Empty(result.CombatCandidates); Assert.Contains("Conflicting", Assert.Single(result.Records).DecodeWarnings[0]);
    }

    [Theory]
    [InlineData("--max-depth", "0")] [InlineData("--max-output-bytes", "9999999999")]
    [InlineData("--max-depth", "17")] [InlineData("--local", "not-an-endpoint")]
    public void CliRejectsInvalidSafetyOrEndpointOptions(string option, string value)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, ResearchCli.Run(["decode", "unused.pcap", option, value], output, error));
    }

    [Fact]
    public void ExcessiveComponentCountAndUnknownLayoutDoNotAllocateUntrustedLists()
    {
        var result = Assert.Single(Decode(Combat(400, Enumerable.Repeat(1ul, 257).ToArray())).CombatCandidates);
        Assert.Equal("Malformed", result.Status); Assert.Equal(400ul, result.AggregateAmount);
        Assert.Empty(result.OptionalComponents); Assert.Null(result.DerivedBaseAmount);
        var bytes = Combat(400, []);
        var prefix = ApplicationFraming.Read(bytes).PrefixLength;
        // Synthetic target has one byte, actor two; modifier-reserved is at body-relative 14.
        bytes[prefix + 14] = 1;
        var guard = Assert.Single(Decode(bytes).CombatCandidates);
        Assert.Equal("Unsupported", guard.Status); Assert.Null(guard.AggregateAmount);
    }

    [Fact]
    public void RecordBoundAbortsExplicitlyAndDecoderCannotBeReused()
    {
        var bytes = Frame([0x99, 0xaa]);
        Assert.Throws<InvalidDataException>(() => Decode(bytes.Concat(bytes).ToArray(), new() { MaximumRecordsPerCapture = 1 }));
        var decoder = new ReplayProtocolDecoder();
        decoder.Decode("synthetic", [], Start);
        Assert.Throws<InvalidOperationException>(() => decoder.Decode("synthetic", [], Start));
    }

    [Fact]
    public void JsonCliReconstructsSplitSyntheticTcpAndDecodesCompressedCandidate()
    {
        using var files = new TestFiles();
        var bytes = Pack(Combat(100000, [128]));
        var first = Packet(bytes[..5], 100, Start);
        var last = Packet(bytes[5..], 105, Start.AddSeconds(1));
        // Arrival order is reversed; sequence reconstruction must still join the original bytes.
        var path = files.WritePcap([last, first]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["decode", path, "--local", "198.51.100.2:443", "--remote", "192.0.2.1:12345", "--json"], output, error));
        Assert.Equal("", error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var summary = json.RootElement.GetProperty("Summary");
        Assert.Equal(1, summary.GetProperty("ValidDecompressions").GetInt32());
        Assert.Equal(1, summary.GetProperty("InnerCombatCandidateCount").GetInt32());
        var candidate = json.RootElement.GetProperty("CombatCandidates")[0];
        Assert.Equal(100000ul, candidate.GetProperty("AggregateAmount").GetUInt64());
        Assert.Equal(99872ul, candidate.GetProperty("DerivedBaseAmount").GetUInt64());
        Assert.Equal("ServerToClient", candidate.GetProperty("RawRecord").GetProperty("Direction").GetString());
        Assert.Equal(1, candidate.GetProperty("RawRecord").GetProperty("ContainerPath").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("Streams").GetArrayLength());
    }

    private static CapturedPacket Packet(byte[] payload, uint sequence, DateTimeOffset timestamp)
    {
        var template = TestFiles.Packet(6, timestamp);
        var data = new byte[54 + payload.Length]; template.Data.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), sequence);
        data[47] = (byte)(TcpFlags.Psh | TcpFlags.Ack); payload.CopyTo(data, 54);
        return template with { Data = data, OriginalLength = data.Length };
    }

    internal static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 127); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 128)); } while (value != 0);
        return bytes.ToArray();
    }
    internal static byte[] Frame(byte[] body) => [.. Varint((ulong)body.Length + 4), .. body];
    internal static byte[] Combat(ulong aggregate, ulong[] components, ulong target = 100, ulong actor = 200,
        ulong preValue = 300, byte? category = null, byte[]? trailing = null)
    {
        var body = new List<byte> { 4, 0x38 };
        body.AddRange(Varint(target)); body.Add(category ?? (components.Length == 0 ? (byte)6 : (byte)0x26));
        body.Add(0); body.AddRange(Varint(actor));
        var skill = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(skill, 123456); body.AddRange(skill);
        body.AddRange(new byte[] { 1, 2, 8, 0, 2, 9, 8, 7, 6, 1, 0, 0 });
        body.AddRange(Varint(0)); body.AddRange(Varint(preValue)); body.AddRange(Varint(aggregate));
        if (components.Length > 0) { body.AddRange(Varint((ulong)components.Length)); foreach (var component in components) body.AddRange(Varint(component)); }
        body.AddRange(new byte[] { 1, 0 }); body.AddRange(trailing ?? []); return Frame(body.ToArray());
    }
    internal static byte[] Container(byte[] compressed, uint size)
    {
        var header = new byte[6]; header[0] = header[1] = 0xff; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), size);
        return Frame([.. header, .. compressed]);
    }
    internal static byte[] Pack(byte[] inner)
    {
        var output = new byte[LZ4Codec.MaximumOutputSize(inner.Length)];
        var count = LZ4Codec.Encode(inner, 0, inner.Length, output, 0, output.Length);
        Assert.True(count > 0); return Container(output[..count], (uint)inner.Length);
    }
    private static ReassembledStream Stream(StreamChunk[] chunks) => new(TrafficDirection.ServerToClient, 100, chunks, [], [], 0, 0, chunks[^1].End, false);
    private static ProtocolDecodeResult Decode(byte[] bytes, ProtocolDecodeLimits? limits = null) =>
        new ReplayProtocolDecoder(limits).Decode("synthetic", [Stream([new(0, bytes, Start, 1)])], Start);
}
