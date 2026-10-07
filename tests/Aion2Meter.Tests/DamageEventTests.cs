using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class DamageEventTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private readonly DamageEventProjector projector = new();

    [Fact]
    public void OneAcceptedRecordProjectsOneExactAggregate()
    {
        var r = Supported(); var e = projector.Project(r);
        Assert.Equal(r.RawRecord.TimestampUtc, e.Timestamp); Assert.Equal(r.SourceEntityId, e.SourceEntityId);
        Assert.Equal(r.TargetEntityId, e.TargetEntityId); Assert.Equal(r.RawSkillCode, e.RawSkillCode);
        Assert.Equal(r.AggregateAmount, e.Amount); Assert.Equal(r.DerivedBaseAmount, e.DerivedBaseAmount);
        Assert.Equal(r.OptionalComponents, e.OptionalComponents);
    }

    [Fact]
    public void EmptyProjectionIsValid() => Assert.Empty(projector.ProjectMany([]));

    [Fact]
    public void BatchPreservesInputOrderAndMultipleRecords()
    {
        var rows = new[] { Supported(3, 30), Supported(1, 10), Supported(2, 20) };
        var es = projector.ProjectMany(rows);
        Assert.Equal(new ulong[] { 30,10,20 }, es.Select(e => e.Amount));
        Assert.Equal(new[] { 3,1,2 }, es.Select(e => e.Provenance.RecordId));
    }

    [Fact]
    public void RepeatedCodeAtDifferentProvenanceProducesIndependentEvents()
    {
        var es = projector.ProjectMany([Supported(1, 40), Supported(2, 50)]);
        Assert.Equal(es[0].RawSkillCode, es[1].RawSkillCode); Assert.NotEqual(es[0].Identity, es[1].Identity);
        var audit = DamageEventAccountingAudit.Analyze(es);
        Assert.Equal(2, audit.ValidatedUniqueEventCount); Assert.Equal(90m, audit.ValidatedTotalAmount);
        Assert.Equal(2, Assert.Single(audit.RawCodeTotals).EventCount);
    }

    [Fact]
    public void SameValuesTimestampFlagsAndTokenWithDifferentProvenanceAreTwoEvents()
    {
        var a = Supported(); var b = a with { RawRecord = a.RawRecord with { RecordId = 2, StreamOffset = 120, OuterFrameOffset = 120 } };
        var es = projector.ProjectMany([a,b]);
        Assert.Equal(es[0].Timestamp, es[1].Timestamp); Assert.Equal(es[0].Amount, es[1].Amount);
        Assert.NotEqual(es[0].Identity, es[1].Identity);
        var audit = DamageEventAccountingAudit.Analyze(es);
        Assert.Equal(2, audit.UniqueProvenanceCount); Assert.Equal(2, audit.ValidatedUniqueEventCount);
        Assert.Equal(200m, audit.ValidatedTotalAmount); Assert.Empty(audit.DuplicateProvenance);
    }

    [Theory]
    [InlineData(100ul, 80ul, 10ul, 10ul)]
    [InlineData(343ul, 337ul, 6ul, 0ul)]
    [InlineData(ulong.MaxValue, ulong.MaxValue - 2, 1ul, 1ul)]
    public void AmountAlreadyIncludesComponents(ulong amount, ulong baseAmount, ulong a, ulong b)
    {
        var e = projector.Project(Supported(amount: amount, components: [a,b]));
        Assert.Equal(amount, e.Amount); Assert.Equal(baseAmount, e.DerivedBaseAmount);
        Assert.Equal(new[] { a,b }, e.OptionalComponents);
        Assert.Equal((decimal)amount, DamageEventAccountingAudit.Analyze([e]).ValidatedTotalAmount);
    }

    [Fact]
    public void FourComponentsDoNotBecomeExtraAmount()
    {
        var e = projector.Project(Supported(amount: 811, components: [16,16,16,16]));
        Assert.Equal(811ul, e.Amount); Assert.Equal(747ul, e.DerivedBaseAmount);
        Assert.Equal(811m, DamageEventAccountingAudit.Analyze([e]).ValidatedTotalAmount);
    }

    [Fact]
    public void ComponentOrderAndZeroValuesArePreserved()
    {
        var e = projector.Project(Supported(components: [3,0,2,1]));
        Assert.Equal(new ulong[] { 3,0,2,1 }, e.OptionalComponents);
    }

    [Fact]
    public void NoComponentRecordRemainsValid()
    {
        var e = projector.Project(Supported(amount: 0));
        Assert.Equal(0ul, e.Amount); Assert.Equal(0ul, e.DerivedBaseAmount); Assert.Empty(e.OptionalComponents);
    }

    [Theory]
    [InlineData(0ul, 0, 0)] [InlineData(3ul, 4, 2)] [InlineData(2ul, 8, 2)]
    [InlineData(ulong.MaxValue, 255, 255)]
    public void RawFlagsAreCopiedWithoutSemanticArithmetic(ulong type, byte modifier, byte direction)
    {
        var r = Supported() with { TypeCandidate = type, ModifierCandidate = modifier, DirectionCandidate = direction };
        var e = projector.Project(r);
        Assert.Equal(type, e.TypeRaw); Assert.Equal(modifier, e.ModifierRaw); Assert.Equal(direction, e.DirectionRaw);
        Assert.Equal(100ul, e.Amount);
    }

    [Theory]
    [InlineData(0ul, ulong.MaxValue)] [InlineData(ulong.MaxValue, 0ul)] [InlineData(42ul, 42ul)]
    public void StructuralSourceTargetValuesHaveNoOwnershipFilter(ulong source, ulong target)
    {
        var e = projector.Project(Supported() with { SourceEntityId = source, TargetEntityId = target });
        Assert.Equal(source, e.SourceEntityId); Assert.Equal(target, e.TargetEntityId);
    }

    [Theory]
    [InlineData(0u)] [InlineData(17010020u)] [InlineData(17010240u)] [InlineData(uint.MaxValue)]
    public void ExactOpaqueCodeIsPreserved(uint code) => Assert.Equal(code, projector.Project(Supported() with { RawSkillCode = code }).RawSkillCode);

    [Fact]
    public void SimilarExactCodesRemainTwoDiagnosticBuckets()
    {
        var a = Supported() with { RawSkillCode = 17010020 }; var b = Supported(2) with { RawSkillCode = 17010240 };
        var totals = DamageEventAccountingAudit.Analyze(projector.ProjectMany([a,b])).RawCodeTotals;
        Assert.Equal(new uint[] { 17010020,17010240 }, totals.Select(t => t.RawSkillCode));
        Assert.All(totals, t => { Assert.Equal(1, t.EventCount); Assert.Equal(100m, t.TotalAmount); });
    }

    [Fact]
    public void TimestampUsesArrivalWhileCompletionIsPreservedSeparately()
    {
        var r = Supported(); var raw = r.RawRecord with { TimestampUtc = Start.AddTicks(1234567), CompletionUtc = Start.AddSeconds(4), CompletionPacketIndex = 99 };
        var e = projector.Project(r with { RawRecord = raw });
        Assert.Equal(raw.TimestampUtc, e.Timestamp); Assert.Equal(raw.CompletionUtc, e.Provenance.CompletionTimestamp);
        Assert.Equal(99, e.Provenance.CompletionPacketIndex); Assert.NotEqual(e.Timestamp, e.Provenance.CompletionTimestamp);
    }

    [Fact]
    public void AllSourceLocationsAndNestedParentsArePreserved()
    {
        var r = Supported(); var raw = r.RawRecord with { StreamOffset = 700, OuterFrameOffset = 680, OuterFrameId = 8,
            PacketIndex = 3, CompletionPacketIndex = 9, ContainerPath = [new(8, 50), new(11, 7)] };
        var p = projector.Project(r with { RawRecord = raw }).Provenance;
        Assert.Equal(raw.SourceCapture, p.CaptureScope); Assert.Equal(raw.RecordId, p.RecordId);
        Assert.Equal(raw.Direction, p.Direction); Assert.Equal(raw.StreamOffset, p.StreamOffset);
        Assert.Equal(raw.OuterFrameId, p.OuterFrameId); Assert.Equal(raw.OuterFrameOffset, p.OuterFrameOffset);
        Assert.Equal(raw.PacketIndex, p.PacketIndex); Assert.Equal(raw.PrefixLength, p.PrefixLength);
        Assert.Equal(raw.FrameLength, p.FrameLength); Assert.Equal(raw.OpcodeCandidate, p.RecordTag);
        Assert.Equal(new[] { 8,11 }, p.ContainerPath.Select(x => x.ContainerRecordId));
        Assert.Equal(new[] { 50,7 }, p.ContainerPath.Select(x => x.InnerOffset));
    }

    [Fact]
    public void ProjectionDefensivelyCopiesMutableInputCollections()
    {
        ulong[] components = [7,3]; ContainerLocation[] path = [new(4, 5)]; var r = Supported();
        var e = projector.Project(r with { OptionalComponents = components, RawRecord = r.RawRecord with { ContainerPath = path } });
        var identity = e.Identity; components[0] = 99; path[0] = new(99,99); r.RawRecord.RawBytes[0] = 0;
        Assert.Equal(new ulong[] { 7,3 }, e.OptionalComponents); Assert.Equal(4, e.Provenance.ContainerPath[0].ContainerRecordId);
        Assert.Equal(identity, e.Identity);
        Assert.Throws<NotSupportedException>(() => ((IList<ulong>)e.OptionalComponents)[0] = 99);
        Assert.Throws<NotSupportedException>(() => ((IList<DamageContainerLocation>)e.Provenance.ContainerPath)[0] = new(99,99));
    }

    [Fact]
    public void PublicEventContractHasNoSettersOrUnreadySemanticFields()
    {
        Assert.All(typeof(DamageEvent).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.All(typeof(DamageEventProvenance).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.Equal(new[] { "Amount","DerivedBaseAmount","DirectionRaw","Identity","ModifierRaw","OptionalComponents","Provenance","RawSkillCode","SourceEntityId","TargetEntityId","Timestamp","TypeRaw" },
            typeof(DamageEvent).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RepeatedProjectionOfSameProvenanceHasStableIdentity()
    {
        var r = Supported(); var a = projector.Project(r); var b = projector.Project(r with { OptionalComponents = r.OptionalComponents.ToArray() });
        Assert.Equal(a.Identity, b.Identity); Assert.StartsWith("damage-record-v1:", a.Identity);
    }

    [Theory]
    [InlineData("capture-b")] [InlineData("CAPTURE-A")] [InlineData("capture-a|1")]
    public void ExactCaptureScopeIsPartOfIdentity(string scope)
    {
        var r = Supported(); Assert.NotEqual(projector.Project(r).Identity, projector.Project(r with { RawRecord = r.RawRecord with { SourceCapture = scope } }).Identity);
    }

    [Theory]
    [InlineData("RecordId")] [InlineData("StreamOffset")] [InlineData("OuterFrameId")] [InlineData("OuterFrameOffset")]
    [InlineData("PacketIndex")] [InlineData("CompletionPacketIndex")] [InlineData("Timestamp")]
    [InlineData("CompletionTimestamp")] [InlineData("InnerOffset")] [InlineData("ParentId")] [InlineData("NestedDepth")]
    public void DifferentProvenanceLocationProducesDifferentIdentity(string field)
    {
        var r = Supported(); var raw = r.RawRecord;
        var different = field switch
        {
            "RecordId" => raw with { RecordId = 2 }, "StreamOffset" => raw with { StreamOffset = 123 },
            "OuterFrameId" => raw with { OuterFrameId = 2 }, "OuterFrameOffset" => raw with { OuterFrameOffset = 123 },
            "PacketIndex" => raw with { PacketIndex = 99 }, "CompletionPacketIndex" => raw with { CompletionPacketIndex = 99 },
            "Timestamp" => raw with { TimestampUtc = raw.TimestampUtc.AddTicks(1) },
            "CompletionTimestamp" => raw with { CompletionUtc = raw.CompletionUtc.AddTicks(1) },
            "InnerOffset" => raw with { ContainerPath = [new(1,3)] }, "ParentId" => raw with { ContainerPath = [new(3,1)] },
            "NestedDepth" => raw with { ContainerPath = [new(1,0),new(2,0)] }, _ => throw new ArgumentException(field)
        };
        Assert.NotEqual(projector.Project(r).Identity, projector.Project(r with { RawRecord = different }).Identity);
    }

    [Fact]
    public void TokenAndEventValuesDoNotDetermineIdentity()
    {
        var r = Supported(); var changed = r with { UnknownAfterSkill = 9, RawSkillCode = 987654, AggregateAmount = 200 };
        Assert.Equal(projector.Project(r).Identity, projector.Project(changed).Identity);
        Assert.DoesNotContain("CorrelationToken", string.Join(',', typeof(DamageEvent).GetProperties().Select(p => p.Name)));
    }

    [Fact]
    public void StableIdentityIsIndependentOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); var a = projector.Project(Supported()).Identity;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); Assert.Equal(a, projector.Project(Supported()).Identity);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void DuplicateProjectionOccurrencesAreRetainedUntilExplicitAudit()
    {
        var r = Supported(); var es = projector.ProjectMany([r,r]); Assert.Equal(2, es.Count); Assert.Equal(es[0].Identity, es[1].Identity);
        var a = DamageEventAccountingAudit.Analyze(es);
        Assert.Equal(2, a.InputEventCount); Assert.Equal(1, a.UniqueProvenanceCount); Assert.Equal(1, a.ValidatedUniqueEventCount);
        Assert.Equal(1, a.DuplicateInputCount); Assert.Equal(100m, a.ValidatedTotalAmount); Assert.Equal(0, a.RejectedProvenanceCount);
        var d = Assert.Single(a.DuplicateProvenance); Assert.False(d.HasConflictingContent);
        Assert.Equal(new[] { 0,1 }, d.Occurrences.Select(o => o.InputIndex)); Assert.Same(es[0], d.Occurrences[0].Event);
        Assert.Equal(100ul, es[0].Amount);
    }

    [Fact]
    public void AuditOfEmptyInputHasNoDiagnosticsOrTotals()
    {
        var a = DamageEventAccountingAudit.Analyze([]);
        Assert.Equal(0, a.InputEventCount); Assert.Equal(0, a.ValidatedUniqueEventCount); Assert.Equal(0m, a.ValidatedTotalAmount);
        Assert.Empty(a.DuplicateProvenance); Assert.Empty(a.RawCodeTotals);
    }

    [Fact]
    public void UniqueBatchCountsEveryAggregateExactlyOnce()
    {
        var a = DamageEventAccountingAudit.Analyze(projector.ProjectMany([Supported(1, 40),Supported(2, 60, [10]),Supported(3, 80)]));
        Assert.Equal(3, a.ValidatedUniqueEventCount); Assert.Equal(180m, a.ValidatedTotalAmount); Assert.Equal(0, a.DuplicateInputCount);
    }

    [Fact]
    public void SeveralDuplicateOccurrencesKeepAllEvidenceAndCountOne()
    {
        var es = projector.ProjectMany([Supported(),Supported(2, 40)]);
        var a = DamageEventAccountingAudit.Analyze([es[0],es[1],es[0],es[0],es[1]]);
        Assert.Equal(3, a.DuplicateInputCount); Assert.Equal(2, a.ValidatedUniqueEventCount); Assert.Equal(140m, a.ValidatedTotalAmount);
        Assert.Equal(2, a.DuplicateProvenance.Count); Assert.Equal(3, a.DuplicateProvenance[0].Occurrences.Count);
    }

    [Theory]
    [InlineData("Amount")] [InlineData("Source")] [InlineData("Target")] [InlineData("Code")] [InlineData("Base")]
    [InlineData("Components")] [InlineData("Type")] [InlineData("Modifier")] [InlineData("Direction")]
    public void ConflictingCopiesOfSameProvenanceRejectWholeIdentity(string field)
    {
        var r = Supported(); var other = field switch
        {
            "Amount" => r with { AggregateAmount = 101 }, "Source" => r with { SourceEntityId = 99 },
            "Target" => r with { TargetEntityId = 99 }, "Code" => r with { RawSkillCode = 99 },
            "Base" => r with { DerivedBaseAmount = 99 }, "Components" => r with { OptionalComponents = [1] },
            "Type" => r with { TypeCandidate = 99 }, "Modifier" => r with { ModifierCandidate = 99 },
            "Direction" => r with { DirectionCandidate = 99 }, _ => throw new ArgumentException(field)
        };
        var a = DamageEventAccountingAudit.Analyze(projector.ProjectMany([r,Supported(2, 40),other]));
        Assert.True(Assert.Single(a.DuplicateProvenance).HasConflictingContent); Assert.Equal(1, a.RejectedProvenanceCount);
        Assert.Equal(1, a.ValidatedUniqueEventCount); Assert.Equal(40m, a.ValidatedTotalAmount);
        Assert.Equal(40m, Assert.Single(a.RawCodeTotals).TotalAmount);
    }

    [Fact]
    public void UniqueTotalsDoNotOverflowUnsignedAmountRange()
    {
        var a = DamageEventAccountingAudit.Analyze(projector.ProjectMany([Supported(1, ulong.MaxValue),Supported(2, ulong.MaxValue)]));
        Assert.Equal(2m * ulong.MaxValue, a.ValidatedTotalAmount);
    }

    [Fact]
    public void StandaloneAcceptedRecordRequiresNoActionAuxiliaryIdentityOrSkillMetadata()
    {
        var decoded = DecodeBytes(ReplayProtocolDecoderTests.Combat(100, []));
        Assert.Single(decoded.Records); Assert.Empty(decoded.Containers);
        var e = projector.Project(SupportedCombatRecord.From(Assert.Single(decoded.CombatCandidates)));
        Assert.Equal(100ul, e.Amount);
    }

    [Theory]
    [InlineData(4)] [InlineData(0)] [InlineData(255)]
    public void UnsupportedCategoryCannotCrossAcceptedBoundary(byte category)
    {
        var c = Assert.Single(DecodeBytes(ReplayProtocolDecoderTests.Combat(100, [], category: category)).CombatCandidates);
        Assert.NotEqual("Supported", c.Status); Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(c));
    }

    [Fact]
    public void PartiallyDecodedAmountDoesNotBecomeAcceptedEvent()
    {
        var c = Assert.Single(DecodeBytes(ReplayProtocolDecoderTests.Combat(100, [], trailing: [9])).CombatCandidates);
        Assert.Equal(100ul, c.AggregateAmount); Assert.NotEqual("Supported", c.Status);
        Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(c));
    }

    [Theory]
    [InlineData(6)] [InlineData(38)]
    public void BothAcceptedCategoryBranchesCanProject(byte category)
    {
        var r = Supported(components: category == 38 ? [10] : []);
        Assert.Equal((ulong)category, r.CategoryOrSwitch); Assert.Equal(100ul, projector.Project(r).Amount);
    }

    [Theory]
    [InlineData("category")] [InlineData("outbound")] [InlineData("tag")]
    public void ProjectorRejectsInvalidTypedEnvelope(string variant)
    {
        var r = Supported(); r = variant switch
        {
            "category" => r with { CategoryOrSwitch = 4 },
            "outbound" => r with { RawRecord = r.RawRecord with { Direction = TrafficDirection.ClientToServer } },
            "tag" => r with { RawRecord = r.RawRecord with { OpcodeCandidate = "9999" } }, _ => r
        };
        Assert.Throws<ArgumentException>(() => projector.Project(r));
    }

    [Fact]
    public void HistoricalObservationMismatchIsNotAnAccountingOverride()
    {
        var es = projector.ProjectMany([Supported(1, 573),Supported(2, 363)]);
        Assert.Equal(new ulong[] { 573,363 }, es.Select(e => e.Amount)); Assert.DoesNotContain(es, e => e.Amount == 373);
        Assert.Equal(936m, DamageEventAccountingAudit.Analyze(es).ValidatedTotalAmount);
    }

    [Fact]
    public void CliProjectsCompressedRecordsAndKeepsUnsupportedSeparate()
    {
        using var files = new TestFiles();
        var payload = ReplayProtocolDecoderTests.Pack([.. ReplayProtocolDecoderTests.Combat(100, [10,10]),
            .. ReplayProtocolDecoderTests.Combat(100, [], category: 4), .. ReplayProtocolDecoderTests.Combat(100, [])]);
        var path = files.WritePcap([Packet(payload)]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["damage-events",path,"--local","198.51.100.2:443","--remote","192.0.2.1:12345","--json"], output, error));
        Assert.Equal("", error.ToString()); using var json = JsonDocument.Parse(output.ToString()); var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("Summary").GetProperty("AcceptedSupportedRecordCount").GetInt32());
        Assert.Equal(2, root.GetProperty("Summary").GetProperty("ProjectedDamageEventCount").GetInt32());
        Assert.Equal(1, root.GetProperty("Summary").GetProperty("UnsupportedCandidateCount").GetInt32());
        Assert.Single(root.GetProperty("UnsupportedCandidates").EnumerateArray());
        var events = root.GetProperty("DamageEvents").EnumerateArray().ToArray(); Assert.Equal(2, events.Length);
        Assert.NotEqual(events[0].GetProperty("Identity").GetString(), events[1].GetProperty("Identity").GetString());
        Assert.Equal(100ul, events[0].GetProperty("Amount").GetUInt64());
        Assert.Equal(80ul, events[0].GetProperty("DerivedBaseAmount").GetUInt64());
        Assert.Equal(1, events[0].GetProperty("Provenance").GetProperty("ContainerPath").GetArrayLength());
        Assert.Equal(200m, root.GetProperty("AccountingAudit").GetProperty("ValidatedTotalAmount").GetDecimal());
        Assert.Empty(root.GetProperty("AccountingAudit").GetProperty("DuplicateProvenance").EnumerateArray());
    }

    [Fact]
    public void CliSummaryStillReportsCountsForEmptyCapture()
    {
        using var files = new TestFiles(); var path = files.WritePcap([Packet(ReplayProtocolDecoderTests.Frame([0x99,0x99]))]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["damage-events",path,"--local","198.51.100.2:443","--remote","192.0.2.1:12345","--summary"], output, error));
        using var json = JsonDocument.Parse(output.ToString()); Assert.Equal(0, json.RootElement.GetProperty("Summary").GetProperty("ProjectedDamageEventCount").GetInt32());
    }

    [Fact]
    public void CliHelpDocumentsDamageEventsCommand()
    {
        using var output = new StringWriter(); Assert.Equal(0, ResearchCli.Run(["--help"], output)); Assert.Contains("damage-events", output.ToString());
    }

    [Fact]
    public void NullInputsAreRejectedExplicitly()
    {
        Assert.Throws<ArgumentNullException>(() => projector.Project(null!));
        Assert.Throws<ArgumentNullException>(() => projector.ProjectMany(null!));
        Assert.Throws<ArgumentNullException>(() => DamageEventAccountingAudit.Analyze(null!));
        Assert.Throws<ArgumentNullException>(() => DamageEventAccountingAudit.Analyze([null!]));
    }

    private static SupportedCombatRecord Supported(int id = 1, ulong amount = 100, ulong[]? components = null)
    {
        var r = Assert.Single(DecodeBytes(ReplayProtocolDecoderTests.Combat(amount, components ?? [])).CombatCandidates);
        return SupportedCombatRecord.From(r with { RawRecord = r.RawRecord with { RecordId = id, OuterFrameId = id,
            StreamOffset = id * 100, OuterFrameOffset = id * 100 } });
    }
    private static ProtocolDecodeResult DecodeBytes(byte[] bytes) => new ReplayProtocolDecoder().Decode("capture-a",
        [new ReassembledStream(TrafficDirection.ServerToClient, 100, [new(0, bytes, Start, 1)], [], [], 0, 0, bytes.Length, false)], Start);
    private static CapturedPacket Packet(byte[] payload)
    {
        var template = TestFiles.Packet(6, Start); var data = new byte[54 + payload.Length]; template.Data.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), 100); data[47] = (byte)(TcpFlags.Psh | TcpFlags.Ack);
        payload.CopyTo(data, 54); return template with { Data = data, OriginalLength = data.Length };
    }
}
