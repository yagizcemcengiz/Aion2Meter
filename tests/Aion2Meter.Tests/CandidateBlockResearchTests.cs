using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class CandidateBlockResearchTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly string[] Endpoints = ["--local", "192.0.2.1:12345", "--remote", "198.51.100.2:443"];
    private static ReassembledStream Stream(StreamChunk[] chunks, ByteRange[]? conflicts = null) =>
        new(TrafficDirection.ServerToClient, 100, chunks, [], conflicts ?? [], 0, 0, chunks.Length == 0 ? 0 : chunks[^1].End, false);
    private static byte[] Block(int length, byte tag = 0xa1, byte tail = 0)
    {
        var bytes = new byte[length];
        var width = length + 3 < 128 ? 1 : 2;
        var value = length + 4 - width;
        bytes[0] = width == 1 ? (byte)value : (byte)((value & 127) | 128);
        if (width == 2) bytes[1] = (byte)(value >> 7);
        bytes[width] = tag; bytes[width + 1] = 0xb2; bytes[^1] = tail;
        return bytes;
    }
    private static CandidateBlock Candidate(byte[] bytes, long offset = 0, TrafficDirection direction = TrafficDirection.ServerToClient) =>
        new(1, direction, Start, 0, offset, 1, 1, Start, bytes[0], 1, bytes);

    [Fact]
    public void ExtractsCoalescedBlocksAndBlockSpanningTransportChunks()
    {
        var bytes = Block(35).Concat(Block(7)).ToArray();
        var result = CandidateBlockExtractor.Extract(Stream([new(900, bytes[..2], Start, 1), new(902, bytes[2..], Start.AddMilliseconds(20), 2)]), Start);
        Assert.Empty(result.Issues);
        Assert.Equal(42, result.CoveredBytes);
        Assert.Equal(new[] { 35, 7 }, result.Blocks.Select(b => b.Length));
        Assert.Equal(900, result.Blocks[0].StreamOffset);
        Assert.Equal(1, result.Blocks[0].PacketIndex);
        Assert.Equal(2, result.Blocks[0].CompletionPacketIndex);
        Assert.Equal(Start.AddMilliseconds(20), result.Blocks[0].CompletionUtc);
        Assert.Equal(935, result.Blocks[1].StreamOffset);
        Assert.Equal(0.02, result.Blocks[1].RelativeSeconds);
        Assert.Equal(38u, result.Blocks[0].PrefixValue);
        Assert.Equal(1, result.Blocks[0].PrefixLength);
    }

    [Theory]
    [InlineData(7)] [InlineData(124)] [InlineData(199)] [InlineData(892)]
    public void FramingAccountsForVariablePrefixWidth(int length)
    {
        var result = CandidateBlockExtractor.Extract(Stream([new(0, Block(length), Start, 1)]), Start);
        var block = Assert.Single(result.Blocks);
        Assert.Equal(length, block.Length);
        Assert.Equal((long)block.PrefixValue + block.PrefixLength - 4, length);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("00")] [InlineData("8000")] [InlineData("80")] [InlineData("FFFFFFFF1F")] [InlineData("FFFFFFFFFF")]
    [InlineData("26AABB")]
    public void MalformedOrIncompleteRunStopsWithoutResynchronization(string hex)
    {
        var bytes = Convert.FromHexString(hex).Concat(Block(7)).ToArray();
        var result = CandidateBlockExtractor.Extract(Stream([new(0, bytes, Start, 1)]), Start);
        Assert.Empty(result.Blocks);
        Assert.Single(result.Issues);
        Assert.Equal(bytes.Length, result.Issues[0].UnparsedBytes);
    }

    [Fact]
    public void GapDoesNotJoinAnIncompleteBlockAndNextRunIsExplicit()
    {
        var result = CandidateBlockExtractor.Extract(Stream([new(0, Block(35)[..8], Start, 1), new(35, Block(7), Start, 2)]), Start);
        Assert.Single(result.Issues);
        Assert.Equal(2, result.ContiguousRuns);
        Assert.Equal(35, Assert.Single(result.Blocks).StreamOffset);
        Assert.Equal(7, result.CoveredBytes);
    }

    [Fact]
    public void ConflictingOverlapStopsBeforeAmbiguousCandidate()
    {
        var result = CandidateBlockExtractor.Extract(Stream([new(0, Block(7).Concat(Block(35)).ToArray(), Start, 1)], [new(10, 1)]), Start);
        Assert.Equal(7, Assert.Single(result.Blocks).Length);
        Assert.Contains("Conflicting", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void GroupingSeparatesDirectionLengthAndBodyPrefix()
    {
        var families = CandidateBlockFamilies.Group([
            new("a", Candidate(Block(35))), new("b", Candidate(Block(35, tail: 6))),
            new("c", Candidate(Block(33))), new("d", Candidate(Block(35, tag: 0xa2))),
            new("e", Candidate(Block(35), direction: TrafficDirection.ClientToServer))]);
        Assert.Equal(4, families.Count);
        Assert.Equal(2, Assert.Single(families, f => f.Samples.Count == 2).Samples.Count);
    }

    [Fact]
    public void SignatureAndComparisonAreOrderIndependentAndIncludeEveryVariableOffset()
    {
        var a = Block(35); var b = a.ToArray(); b[14] = 1; b[29] = 2; b[32] = 6;
        BlockSample[] samples = [new("a", Candidate(a)), new("b", Candidate(b))];
        var family = Assert.Single(CandidateBlockFamilies.Group(samples));
        Assert.Equal(family.Signature, Assert.Single(CandidateBlockFamilies.Group(samples.Reverse().ToArray())).Signature);
        Assert.Equal(new long[] { 14, 29, 32 }, family.Comparison.VaryingRanges.Select(r => r.Offset));
        Assert.Equal(14, family.Comparison.CommonPrefixLength);
        Assert.Equal(2, family.Comparison.CommonSuffixLength);
        Assert.Equal("??", family.Signature.Split(' ')[32]);
    }

    [Theory]
    [InlineData(6u, "uint8", "06")]
    [InlineData(337u, "uint16 LE", "5101")]
    [InlineData(337u, "uint16 BE", "0151")]
    [InlineData(0x01020304u, "uint32 LE", "04030201")]
    [InlineData(0x01020304u, "uint32 BE", "01020304")]
    public void NumericHypothesesFindOnlyDirectRepresentations(uint value, string representation, string hex)
    {
        var bytes = new byte[] { 0xff }.Concat(Convert.FromHexString(hex)).Concat(new byte[] { 0xff }).ToArray();
        var match = Assert.Single(NumericHypothesisSearch.Find(bytes, value), m => m.Representation == representation);
        Assert.Equal(1, match.Offset); Assert.Equal(hex, match.Hex);
    }

    [Fact]
    public void NumericSearchDoesNotTruncateIntegersOrInventTransforms()
    {
        Assert.Empty(NumericHypothesisSearch.Find([0x51], 337));
        Assert.Empty(NumericHypothesisSearch.Find([0xd1, 0x02], 337));
        Assert.Empty(NumericHypothesisSearch.Find([0xff, 0xff], uint.MaxValue));
        Assert.Equal(new[] { 0, 1 }, NumericHypothesisSearch.Find([6, 6], 6).Where(m => m.Representation == "uint8").Select(m => m.Offset));
    }

    private static CapturedPacket Packet(byte[] payload, uint sequence = 100, double seconds = 0)
    {
        var template = TestFiles.Packet(6, Start.AddSeconds(seconds));
        var data = new byte[54 + payload.Length]; template.Data.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + payload.Length));
        TestFiles.U32(data, 38, sequence, false);
        data[47] = (byte)(TcpFlags.Psh | TcpFlags.Ack);
        var address = data.AsSpan(26, 4).ToArray(); data.AsSpan(30, 4).CopyTo(data.AsSpan(26)); address.CopyTo(data, 30);
        var port = data.AsSpan(34, 2).ToArray(); data.AsSpan(36, 2).CopyTo(data.AsSpan(34)); port.CopyTo(data, 36);
        payload.CopyTo(data, 54);
        return template with { Data = data, OriginalLength = data.Length };
    }

    [Fact]
    public void CliFiltersAfterFramingAndReportsAllMatchesEvenWhenRowsAreLimited()
    {
        using var files = new TestFiles();
        var a = Block(35); a[32] = 6;
        var path = files.WritePcap([Packet(Block(7)), Packet(a.Concat(a).ToArray(), 107, 2)]);
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = ResearchCli.Run(["blocks", path, .. Endpoints, "--block-length", "35", "--from", "1", "--to", "3", "--max-blocks", "1", "--hypothesis-value", "6", "--byte-offsets", "--json"], output, error);
        Assert.Equal(0, code); Assert.Equal("", error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, json.RootElement.GetProperty("SelectedBlockCount").GetInt32());
        var capture = json.RootElement.GetProperty("Captures")[0];
        Assert.Equal(1, capture.GetProperty("OmittedBlocks").GetInt32());
        Assert.Equal(7, capture.GetProperty("Blocks")[0].GetProperty("StreamOffset").GetInt32());
        Assert.Equal(35, capture.GetProperty("Blocks")[0].GetProperty("ByteOffsets").GetArrayLength());
        Assert.True(capture.GetProperty("NumericHypotheses")[0].GetProperty("MatchCount").GetInt32() >= 2);
        Assert.Equal(1, json.RootElement.GetProperty("Families")[0].GetProperty("Offset32").GetArrayLength());
        Assert.Equal(1, json.RootElement.GetProperty("Families")[0].GetProperty("OmittedOffset32Rows").GetInt32());
    }

    [Fact]
    public void SensitiveMaterialOutsideDisplayWindowSuppressesWholeDirection()
    {
        using var files = new TestFiles();
        var token = Block(35); Encoding.ASCII.GetBytes("Authorization: Bearer secret").CopyTo(token, 3);
        var path = files.WritePcap([Packet(Block(7)), Packet(token, 107, 10)]);
        using var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["blocks", path, .. Endpoints, "--to", "1", "--hypothesis-value", "585", "--json"], output));
        Assert.DoesNotContain("Bearer", output.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, json.RootElement.GetProperty("SelectedBlockCount").GetInt32());
        Assert.True(json.RootElement.GetProperty("Captures")[0].GetProperty("Streams")[0].GetProperty("Suppressed").GetBoolean());
        Assert.Equal("no match in extracted blocks; incomplete/suppressed evidence", json.RootElement.GetProperty("Captures")[0].GetProperty("NumericHypotheses")[0].GetProperty("Status").GetString());
    }

    [Fact]
    public void ActionGroupsReportMultipleBlocksAndSameFamilyRepetition()
    {
        using var files = new TestFiles();
        // Synthetic outbound sequence; no real gameplay framing or payload fixture.
        var inbound = Packet(Block(7).Concat(Block(7)).ToArray(), 100, 1.1);
        var outbound = Packet([1, 2, 3], 100, 1);
        var bytes = outbound.Data.ToArray();
        var ip = bytes.AsSpan(26, 4).ToArray(); bytes.AsSpan(30, 4).CopyTo(bytes.AsSpan(26)); ip.CopyTo(bytes, 30);
        var port = bytes.AsSpan(34, 2).ToArray(); bytes.AsSpan(36, 2).CopyTo(bytes.AsSpan(34)); port.CopyTo(bytes, 36);
        outbound = outbound with { Data = bytes };
        var path = files.WritePcap([outbound, inbound]);
        using var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["blocks", path, .. Endpoints, "--sequence", "57", "--action-window-seconds", "1", "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        var action = json.RootElement.GetProperty("Captures")[0].GetProperty("ActionGroups")[0];
        Assert.Equal(2, action.GetProperty("RelatedBlockCount").GetInt32());
        Assert.Equal(2, action.GetProperty("Repetitions")[0].GetProperty("Count").GetInt32());
        Assert.Equal(100, action.GetProperty("Blocks")[0].GetProperty("DeltaMilliseconds").GetDouble(), 6);
    }

    [Fact]
    public void NumericAbsenceIsExplicitAndSingletonIsNotCrossSampleValidation()
    {
        using var files = new TestFiles();
        var path = files.WritePcap([Packet(Block(7))]);
        using var output = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["blocks", path, .. Endpoints, "--hypothesis-value", "585", "--summary", "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("Families")[0].GetProperty("Singleton").GetBoolean());
        Assert.Equal("not directly represented in selected candidate blocks", json.RootElement.GetProperty("Captures")[0].GetProperty("NumericHypotheses")[0].GetProperty("Status").GetString());
        Assert.Contains("UNVALIDATED", json.RootElement.GetProperty("Framing").GetString());
    }

    [Theory]
    [InlineData("80")] [InlineData("FFFFFFFF0F")]
    public void IsolatedPartialOrOverLimitPrefixDoesNotAllocateClaimedBody(string hex)
    {
        var result = CandidateBlockExtractor.Extract(Stream([new(0, Convert.FromHexString(hex), Start, 1)]), Start);
        Assert.Empty(result.Blocks); Assert.Single(result.Issues);
    }

    [Theory]
    [InlineData("--hypothesis-value", "4294967296")]
    [InlineData("--hypothesis-value", "-1")]
    [InlineData("--from", "NaN")]
    [InlineData("--body-prefix", "ZZ")]
    [InlineData("--block-length", "0")]
    [InlineData("--action-window-seconds", "3")]
    public void CliRejectsInvalidOptions(string option, string value)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, ResearchCli.Run(["blocks", "unused.pcap", .. Endpoints, option, value], output, error));
        Assert.NotEqual("", error.ToString());
    }
}
