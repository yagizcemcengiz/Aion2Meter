using System.Buffers.Binary;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class CombatLayoutMarkerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData(6, 1)] [InlineData(6, 2)] [InlineData(6, 3)]
    [InlineData(0x26, 1)] [InlineData(0x26, 2)]
    public void ValidatedMarkersPreserveAggregateAndOptionalComponents(byte category, byte marker)
    {
        ulong[] components = category == 0x26 ? [7, 0, 9] : [];
        var c = Candidate(Wire(category, marker, 400, components));
        Assert.Equal("Supported", c.Status);
        Assert.Equal(new byte[] { marker, 0, 0 }, c.UnknownRegions["UnknownThreeBytes"]);
        Assert.Equal(new byte[] { marker, 0 }, c.TerminalBytes);
        var e = new DamageEventProjector().Project(SupportedCombatRecord.From(c));
        Assert.Equal(400ul, e.Amount); Assert.Equal(400ul - (ulong)components.Sum(x => (long)x), e.DerivedBaseAmount);
        Assert.Equal(components, e.OptionalComponents);
        Assert.Equal(400m, DamageEventAccountingAudit.Analyze([e]).ValidatedTotalAmount);
    }

    [Theory]
    [InlineData(6, 2, 1ul, 128ul, 127ul, 0u)]
    [InlineData(6, 3, 16384ul, 1ul, 128ul, uint.MaxValue)]
    [InlineData(0x26, 2, ulong.MaxValue, ulong.MaxValue, 65536ul, 0xFEDCBA98u)]
    public void CursorFollowsCanonicalWidthsWithoutEntityOrRawCodeHints(byte category, byte marker,
        ulong target, ulong source, ulong amount, uint code)
    {
        ulong[] components = category == 0x26 ? [128, 255] : [];
        var c = Candidate(Wire(category, marker, amount, components, target, source, code));
        Assert.Equal("Supported", c.Status); Assert.Equal(target, c.TargetIdCandidate); Assert.Equal(source, c.ActorIdCandidate);
        Assert.Equal(code, c.RawSkillCodeCandidate); Assert.Equal(amount, c.AggregateAmount); Assert.Equal(components, c.OptionalComponents);
    }

    [Theory]
    [InlineData(6, 0)] [InlineData(6, 4)] [InlineData(6, 5)] [InlineData(6, 255)]
    [InlineData(0x26, 3)] [InlineData(0x26, 4)] [InlineData(0x36, 1)] [InlineData(0x36, 2)]
    public void UnvalidatedCategoryMarkerPairsDoNotCrossAcceptedBoundary(byte category, byte marker)
    {
        var c = Candidate(Wire(category, marker, 400, category == 6 ? [] : [7]));
        Assert.Equal("Unsupported", c.Status); Assert.Null(c.DerivedBaseAmount);
        Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(c));
    }

    [Theory]
    [InlineData(6, 2, 1)] [InlineData(6, 3, 2)] [InlineData(0x26, 2, 3)]
    public void TerminalMustMatchTheEarlierMarker(byte category, byte marker, byte terminal)
    {
        var c = Candidate(Wire(category, marker, 400, category == 6 ? [] : [7], terminal: terminal));
        Assert.Equal("Unsupported", c.Status); Assert.Equal(400ul, c.AggregateAmount); Assert.Null(c.DerivedBaseAmount);
        Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(c));
    }

    [Theory]
    [InlineData("MarkerPadding")] [InlineData("Reserved")] [InlineData("Zero")]
    [InlineData("TerminalPadding")] [InlineData("Suffix")]
    public void MatchingMarkerDoesNotBypassOtherGuards(string defect)
    {
        var c = Candidate(Wire(0x26, 2, 400, [7], defect: defect));
        Assert.NotEqual("Supported", c.Status); Assert.Null(c.DerivedBaseAmount);
        Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(c));
    }

    [Theory]
    [InlineData("ZeroCount")] [InlineData("ExcessiveCount")] [InlineData("TruncatedTail")]
    [InlineData("NoncanonicalCount")] [InlineData("NoncanonicalValue")]
    [InlineData("SumOverflow")] [InlineData("SumExceedsAmount")]
    public void NewCountedVariantRetainsListSafety(string defect)
    {
        var c = Candidate(Wire(0x26, 2, 400, [7], defect: defect));
        Assert.Equal("Malformed", c.Status); Assert.Equal(400ul, c.AggregateAmount); Assert.Null(c.DerivedBaseAmount);
        Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(c));
    }

    [Theory]
    [InlineData(6, 2)] [InlineData(6, 3)] [InlineData(0x26, 2)]
    public void DamageDecodingRequiresNoPartyState(byte category, byte marker)
    {
        var combat = Wire(category, marker, 400, category == 6 ? [] : [7]);
        // Opaque roster-like context has no validated membership semantics and must not gate combat.
        var opaqueContext = ReplayProtocolDecoderTests.Frame([0x0d, 0x92, 0x22, 0x11, 0x44]);
        var bare = Candidate(combat); var withContext = Candidate([.. opaqueContext, .. combat]);
        var a = new DamageEventProjector().Project(SupportedCombatRecord.From(bare));
        var b = new DamageEventProjector().Project(SupportedCombatRecord.From(withContext));
        Assert.Equal(a.SourceEntityId, b.SourceEntityId); Assert.Equal(a.TargetEntityId, b.TargetEntityId);
        Assert.Equal(a.RawSkillCode, b.RawSkillCode); Assert.Equal(a.Amount, b.Amount);
        Assert.Equal(a.DerivedBaseAmount, b.DerivedBaseAmount); Assert.Equal(a.OptionalComponents, b.OptionalComponents);
    }

    private static RawCombatCandidate Candidate(byte[] bytes)
    {
        var chunk = new StreamChunk(0, bytes, Start, 1);
        var stream = new ReassembledStream(TrafficDirection.ServerToClient, 100, [chunk], [], [], 0, 0, bytes.Length, false);
        return Assert.Single(new ReplayProtocolDecoder().Decode("synthetic", [stream], Start).CombatCandidates);
    }

    private static byte[] Wire(byte category, byte marker, ulong amount, ulong[] components,
        ulong target = 100, ulong source = 200, uint code = 123456, byte? terminal = null, string? defect = null)
    {
        var body = new List<byte> { 4, 0x38 };
        body.AddRange(ReplayProtocolDecoderTests.Varint(target)); body.Add(category); body.Add(0);
        body.AddRange(ReplayProtocolDecoderTests.Varint(source));
        var rawCode = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(rawCode, code); body.AddRange(rawCode);
        body.AddRange(new byte[] { 1, 2, 8, (byte)(defect == "Reserved" ? 1 : 0), 2, 9, 8, 7, 6,
            marker, (byte)(defect == "MarkerPadding" ? 1 : 0), 0, (byte)(defect == "Zero" ? 1 : 0) });
        body.AddRange(ReplayProtocolDecoderTests.Varint(300)); body.AddRange(ReplayProtocolDecoderTests.Varint(amount));
        if (category != 6)
        {
            if (defect == "TruncatedTail") return ReplayProtocolDecoderTests.Frame([.. body, 2, 0x80]);
            if (defect == "ZeroCount") body.Add(0);
            else if (defect == "ExcessiveCount") body.AddRange(ReplayProtocolDecoderTests.Varint(257));
            else if (defect == "NoncanonicalCount") body.AddRange(new byte[] { 0x81, 0 });
            else if (defect == "NoncanonicalValue") body.AddRange(new byte[] { 1, 0x87, 0 });
            else
            {
                if (defect == "SumOverflow") components = [ulong.MaxValue, 1];
                if (defect == "SumExceedsAmount") components = [amount + 1];
                body.AddRange(ReplayProtocolDecoderTests.Varint((ulong)components.Length));
                foreach (var value in components) body.AddRange(ReplayProtocolDecoderTests.Varint(value));
            }
        }
        body.Add(terminal ?? marker); body.Add((byte)(defect == "TerminalPadding" ? 1 : 0));
        if (defect == "Suffix") body.Add(0);
        return ReplayProtocolDecoderTests.Frame(body.ToArray());
    }
}
