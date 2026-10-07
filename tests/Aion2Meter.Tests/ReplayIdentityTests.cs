using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class ReplayIdentityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData(1ul)] [InlineData(127ul)] [InlineData(128ul)] [InlineData(16384ul)] [InlineData(ulong.MaxValue)]
    public void BoundedIdentityHandlesVariableWidthIdsAndPreservesUnknownFields(ulong id)
    {
        var raw = Identity(id, "ExactName", trailing: [9,8,7]);
        var observed = Assert.Single(IdentityRecordDecoder.Decode(raw).Identities);
        Assert.Equal(id, observed.EntityId); Assert.Equal("ExactName", observed.Name);
        Assert.Equal(0x01020304u, observed.UnknownMask); Assert.Equal((byte)7, observed.PresenceByte);
        Assert.Equal(new byte[] { 9,8,7 }, observed.UnresolvedRemainder); Assert.Same(raw, observed.RawRecord);
        Assert.Equal(Start, observed.Timestamp); Assert.Equal("capture-a", observed.CaptureId);
    }

    [Theory]
    [InlineData("Ноа")] [InlineData("Çaçaron")] [InlineData("Name with space")] [InlineData("é\u0301")]
    public void ExactUtf8IsNeitherAsciiConvertedTrimmedNorNormalized(string name)
    {
        var observed = Assert.Single(IdentityRecordDecoder.Decode(Identity(42, name)).Identities);
        Assert.Equal(name, observed.Name);
    }

    [Theory]
    [InlineData("C3")] [InlineData("C328")] [InlineData("EDA080")] [InlineData("F4908080")]
    public void InvalidUtf8DoesNotBecomeAName(string hex)
    {
        var result = IdentityRecordDecoder.Decode(NameBytes(10, Convert.FromHexString(hex)));
        Assert.Empty(result.Identities); Assert.Single(result.Issues);
        Assert.NotEmpty(result.Issues[0].RawRecord.RawBytes);
    }

    [Fact]
    public void DeclaredByteLengthCannotReadPastRecord()
    {
        var result = IdentityRecordDecoder.Decode(NameBytes(10, Encoding.UTF8.GetBytes("abc"), declaredLength: 50));
        Assert.Empty(result.Identities); Assert.Contains("Truncated", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void TruncatedFrameIsNotResynchronizedOrGivenAName()
    {
        var raw = Identity(42, "A name");
        var result = IdentityRecordDecoder.Decode(raw with { RawBytes = raw.RawBytes[..^1] });
        Assert.Empty(result.Identities); Assert.Single(result.Issues);
    }

    [Fact]
    public void TruncatedEnvelopeRetainsRawIssue()
    {
        var raw = Record([0x45,0x36,0x80]);
        var result = IdentityRecordDecoder.Decode(raw);
        Assert.Empty(result.Identities); Assert.Equal(raw, Assert.Single(result.Issues).RawRecord);
    }

    [Fact]
    public void UnverifiedPresenceDoesNotPromoteAReferenceOnlyNameBranch()
    {
        var result = IdentityRecordDecoder.Decode(NameBytes(10, Encoding.UTF8.GetBytes("ShouldNotBeNamed"), presence: 3));
        var observation = Assert.Single(result.Identities);
        Assert.Null(observation.Name); Assert.Equal(ResearchConfidence.Unresolved, observation.Confidence);
        Assert.Equal(EvidenceClassification.E, observation.Classification); Assert.NotEmpty(observation.UnresolvedRemainder);
        Assert.Single(result.Issues);
    }

    [Fact]
    public void InnerProvenanceAndRawRemainderAreRetained()
    {
        var raw = Identity(42, "Name", trailing: [1,2]) with { ContainerPath = [new(3, 25), new(4, 7)], StreamOffset = 200, OuterFrameId = 9, OuterFrameOffset = 200 };
        var observed = Assert.Single(IdentityRecordDecoder.Decode(raw).Identities);
        Assert.Equal(new[] { 3,4 }, observed.Provenance.ContainerPath.Select(p => p.ContainerRecordId));
        Assert.Equal(200, observed.Provenance.StreamOffset); Assert.Equal(9, observed.Provenance.OuterFrameId);
        Assert.Equal(new byte[] { 1,2 }, observed.UnresolvedRemainder);
    }

    [Fact]
    public void OutboundIdentityTagAndSuppressedBytesDoNotProduceIdentity()
    {
        var raw = Identity(42, "Name");
        Assert.Empty(IdentityRecordDecoder.Decode(raw with { Direction = TrafficDirection.ClientToServer }).Identities);
        Assert.Empty(IdentityRecordDecoder.Decode(raw with { DecodeStatus = "Suppressed", RawBytes = [] }).Identities);
    }

    [Fact]
    public void DuplicateNamesKeepBothObservationIds()
    {
        var first = Observe(Identity(42, "Name", id: 1)); var second = Observe(Identity(42, "Name", id: 2));
        var directory = new ReplayIdentityDirectory("capture-a", [first,second]);
        Assert.Equal(2, directory.GetObservations(42).Count);
        var resolution = directory.GetRetrospectiveSameCaptureNames(42);
        Assert.Equal(NameResolutionState.Resolved, resolution.State); Assert.Equal("Name", resolution.Name);
        Assert.Equal(new[] { 1,2 }, resolution.ObservationIds);
    }

    [Fact]
    public void ConflictingNamesAreNotOverwrittenEvenByLatestObservation()
    {
        var a = Observe(Identity(42, "Name", id: 1)); var b = Observe(Identity(42, "name", id: 2, seconds: 1));
        var directory = new ReplayIdentityDirectory("capture-a", [a,b]);
        var result = directory.GetLatestPrecedingName(42, Start.AddSeconds(2));
        Assert.Equal(NameResolutionState.Conflict, result.State); Assert.Null(result.Name);
        Assert.Equal(new[] { "Name","name" }, result.Names); Assert.Equal(Start.AddSeconds(1), result.LatestObservationTimestamp);
    }

    [Fact]
    public void PrecedingIncludesEqualTimestampButNeverLaterObservation()
    {
        var directory = new ReplayIdentityDirectory("capture-a", [Observe(Identity(42, "Name", seconds: 1))]);
        Assert.Equal(NameResolutionState.Unknown, directory.GetLatestPrecedingName(42, Start).State);
        Assert.Equal("Name", directory.GetLatestPrecedingName(42, Start.AddSeconds(1)).Name);
        var retro = directory.GetRetrospectiveSameCaptureNames(42);
        Assert.Equal(NameLookupMode.RetrospectiveSameCapture, retro.Mode); Assert.Equal("Name", retro.Name);
    }

    [Fact]
    public void FutureConflictDoesNotRewriteHistoricalPrecedingResolution()
    {
        var directory = new ReplayIdentityDirectory("capture-a", [Observe(Identity(42, "Earlier", id: 1)), Observe(Identity(42, "Later", id: 2, seconds: 5))]);
        Assert.Equal("Earlier", directory.GetLatestPrecedingName(42, Start.AddSeconds(1)).Name);
        Assert.Equal(NameResolutionState.Conflict, directory.GetRetrospectiveSameCaptureNames(42).State);
    }

    [Fact]
    public void SameNumericIdInAnotherCaptureCannotEnterDirectory()
    {
        var a = Observe(Identity(42, "First")); var b = Observe(Identity(42, "Second") with { SourceCapture = "capture-b" });
        Assert.Throws<ArgumentException>(() => new ReplayIdentityDirectory("capture-a", [a,b]));
        Assert.Equal("First", new ReplayIdentityDirectory("capture-a", [a]).GetRetrospectiveSameCaptureNames(42).Name);
        Assert.Equal("Second", new ReplayIdentityDirectory("capture-b", [b]).GetRetrospectiveSameCaptureNames(42).Name);
    }

    [Fact]
    public void UnknownEntityIsAValidExplicitResolution()
    {
        var directory = new ReplayIdentityDirectory("capture-a", []);
        Assert.Empty(directory.GetObservations(999));
        Assert.Equal(NameResolutionState.Unknown, directory.GetLatestPrecedingName(999, Start).State);
        Assert.Equal(NameResolutionState.Unknown, directory.GetRetrospectiveSameCaptureNames(999).State);
    }

    [Fact]
    public void RelationshipPositionsAndAuxiliaryFieldsStaySeparate()
    {
        var raw = Related(16484, 42, 55, "ContextX");
        var result = IdentityRecordDecoder.Decode(raw);
        Assert.Null(Assert.Single(result.Identities).Name); Assert.Empty(result.Contexts);
        Assert.Equal(2, result.Relationships.Count);
        var anchor = Assert.Single(result.Relationships, o => o.EvidenceType == RelationshipEvidenceType.FfAnchorRelatedId);
        var suffix = Assert.Single(result.Relationships, o => o.EvidenceType == RelationshipEvidenceType.SuffixRelatedId);
        Assert.Equal(42ul, anchor.RelatedEntityIdCandidate); Assert.Equal(55ul, suffix.RelatedEntityIdCandidate);
        Assert.Equal(16484ul, suffix.HeaderEntityId); Assert.Equal(0x11223344u, suffix.AuxiliaryValue);
        Assert.Equal((ushort)1313, suffix.ContextWorldCandidate); Assert.Equal("ContextX", suffix.ContextLabel);
        Assert.Equal(new byte[] { 1,2,3,4,5,6,7,8 }, anchor.UnknownAnchorBytes);
        Assert.Equal(new byte[] { 9 }, suffix.UnresolvedRemainder); Assert.Same(raw, suffix.RawRecord);
    }

    [Fact]
    public void SelfIdAnchorIsPreservedAlongsideDifferentSuffixRelatedId()
    {
        var result = IdentityRecordDecoder.Decode(Related(16484, 16484, 42, "ContextX"));
        Assert.True(result.Relationships[0].IsSelfId); Assert.False(result.Relationships[1].IsSelfId);
        Assert.Equal(2, result.Relationships.Count); Assert.Empty(result.Issues);
    }

    [Fact]
    public void InvalidRelationshipSuffixDoesNotRemoveHeaderOrValidAnchor()
    {
        var result = IdentityRecordDecoder.Decode(Related(16484, 42, 55, "ContextX", badPadding: true));
        Assert.Single(result.Identities); Assert.Single(result.Relationships);
        Assert.Contains("suffix", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void TruncatedFfAnchorDoesNotReadPastRecord()
    {
        var result = IdentityRecordDecoder.Decode(Record([0x41,0x36, .. V(16484), 0x5f, .. Enumerable.Repeat((byte)255,8)]));
        Assert.Single(result.Identities); Assert.Empty(result.Relationships); Assert.Single(result.Issues);
    }

    [Fact]
    public void Unknown4136KindStaysUnresolved()
    {
        var result = IdentityRecordDecoder.Decode(Record([0x41,0x36, .. V(16484), 0x77, 0]));
        Assert.Empty(result.Identities); Assert.Empty(result.Relationships); Assert.Single(result.Issues);
    }

    [Fact]
    public void ContextTextNeverBecomesNameOfTheSameEntity()
    {
        var result = Analyze([Identity(42, "CharacterA", id: 1), Context(42, "ContextX", id: 2), Combat(42, 99, 17010240, id: 3)]);
        Assert.Equal("CharacterA", Assert.Single(result.CombatCorrelations).SourceRetrospectiveName.Name);
        Assert.Equal("ContextX", Assert.Single(result.TextContextObservations).ContextLabel);
        Assert.Single(result.Graph.NameEdges); Assert.Single(result.Graph.ContextLabelEdges);
        Assert.DoesNotContain(result.IdentityObservations, o => o.Name == "ContextX");
    }

    [Fact]
    public void ContextOnlyEntityRemainsUnnamedInCombat()
    {
        var result = Analyze([Context(42, "ReadableText"), Combat(42, 99, 17010240, id: 2)]);
        Assert.Empty(result.IdentityObservations);
        Assert.Equal(NameResolutionState.Unknown, Assert.Single(result.CombatCorrelations).SourceRetrospectiveName.State);
    }

    [Fact]
    public void ContextPreservesExactUtf8AndTrailingBytes()
    {
        var result = IdentityRecordDecoder.Decode(Context(42, "GÖKTÜRK"));
        var context = Assert.Single(result.Contexts);
        Assert.Equal("GÖKTÜRK", context.ContextLabel); Assert.Equal(new byte[] { 9 }, context.UnresolvedRemainder);
        Assert.Empty(result.Identities);
    }

    [Fact]
    public void ContextPaddingAndDeclaredLengthAreBounded()
    {
        var invalid = Record([0x33,0x8a, .. V(42), 1,0,0,0, 1,0, 0x21,5, 30, (byte)'A']);
        var result = IdentityRecordDecoder.Decode(invalid);
        Assert.Empty(result.Contexts); Assert.Single(result.Issues);
    }

    [Fact]
    public void KnownSourceAndTargetNamesAreEnrichedWithExactObservationIds()
    {
        var result = Analyze([Identity(42, "Source", id: 1), Identity(99, "Target", id: 2), Combat(42, 99, 17010240, id: 3, seconds: 1)]);
        var correlation = Assert.Single(result.CombatCorrelations);
        Assert.Equal("Source", correlation.SourcePrecedingName.Name); Assert.Equal("Target", correlation.TargetPrecedingName.Name);
        Assert.Equal(new[] { 1 }, correlation.SourceIdentityObservationIds); Assert.Equal(new[] { 2 }, correlation.TargetIdentityObservationIds);
        Assert.Equal(1, result.Summary.SourceRowsWithNames); Assert.Equal(1, result.Summary.TargetRowsWithNames);
    }

    [Fact]
    public void LaterIdentityIsClearlyRetrospectiveAndUnknownEntitiesStayUnknown()
    {
        var result = Analyze([Combat(42, 99, 17010240, id: 1), Identity(42, "Later", id: 2, seconds: 2)]);
        var c = Assert.Single(result.CombatCorrelations);
        Assert.Equal(NameResolutionState.Unknown, c.SourcePrecedingName.State);
        Assert.Equal(NameLookupMode.PrecedingOnly, c.SourcePrecedingName.Mode);
        Assert.Equal("Later", c.SourceRetrospectiveName.Name); Assert.Equal(NameLookupMode.RetrospectiveSameCapture, c.SourceRetrospectiveName.Mode);
        Assert.Null(c.TargetPrecedingName.Name); Assert.Null(c.TargetRetrospectiveName.Name);
        Assert.Equal(1, result.Summary.RetrospectiveOnlySourceNameRows); Assert.Equal(1, result.Summary.UnknownTargetRows);
    }

    [Fact]
    public void CompletelyUnknownSourceAndTargetDoNotPreventCombatDecoding()
    {
        var result = Analyze([Combat(42, 99, 17010240)]);
        Assert.Single(result.SupportedCombatRecords); Assert.Single(result.Skills);
        Assert.Equal(1, result.Summary.UnknownSourceRows); Assert.Equal(1, result.Summary.UnknownTargetRows);
    }

    [Fact]
    public void EntityNamesAreIndependentOfTheirSourceAndTargetRoles()
    {
        var result = Analyze([Identity(42, "SameEntity", id: 1), Combat(42, 99, 17010240, id: 2), Combat(99, 42, 17730001, id: 3), Combat(42, 42, 17010020, id: 4)]);
        Assert.Equal("SameEntity", result.CombatCorrelations[0].SourcePrecedingName.Name);
        Assert.Equal("SameEntity", result.CombatCorrelations[1].TargetPrecedingName.Name);
        Assert.Equal("SameEntity", result.CombatCorrelations[2].SourcePrecedingName.Name);
        Assert.Equal("SameEntity", result.CombatCorrelations[2].TargetPrecedingName.Name);
    }

    [Fact]
    public void CloseRawCodesAndUint32MaximumAreNeverAliasedOrNormalized()
    {
        uint[] codes = [17010020,17010240,17010340,17730001,uint.MaxValue];
        var result = Analyze(codes.Select((code,index) => Combat(42, 99, code, id: index+1)).ToArray());
        Assert.Equal(codes, result.SupportedCombatRecords.Select(c => c.RawSkillCode));
        Assert.Equal(codes.Order(), result.Skills.Select(s => s.RawSkillCode));
        Assert.All(result.CombatCorrelations, c => Assert.Null(c.SkillMetadata));
    }

    [Fact]
    public void UnsupportedCombatCannotBePromoted()
    {
        var raw = Record([4,0x38, .. V(99), 4]);
        var candidate = CombatCandidateDecoder.Decode(raw);
        Assert.Throws<ArgumentException>(() => SupportedCombatRecord.From(candidate));
        var result = Analyze([raw]); Assert.Empty(result.SupportedCombatRecords); Assert.Empty(result.CombatCorrelations);
    }

    [Fact]
    public void SupportedProjectionRetainsAccountingUnknownBytesAndProvenance()
    {
        var raw = Combat(16384, ulong.MaxValue, 17010240) with { ContainerPath = [new(10, 32)] };
        var original = CombatCandidateDecoder.Decode(raw); var promoted = SupportedCombatRecord.From(original);
        Assert.Equal(original.AggregateAmount, promoted.AggregateAmount); Assert.Equal(original.DerivedBaseAmount, promoted.DerivedBaseAmount);
        Assert.Equal(original.OptionalComponents, promoted.OptionalComponents); Assert.Same(original.UnknownRegions, promoted.UnknownRegions);
        Assert.Equal(original.TerminalBytes, promoted.TerminalBytes); Assert.Same(raw, promoted.RawRecord);
        Assert.Equal(ulong.MaxValue, promoted.TargetEntityId); Assert.Equal(16384ul, promoted.SourceEntityId);
    }

    [Fact]
    public void GraphReportsDuplicateNamesConflictsAndNeutralRelationships()
    {
        var result = Analyze([Identity(42, "First", id: 1), Identity(42, "First", id: 2), Identity(42, "Second", id: 3), Related(16484, 16484, 42, "Context", id: 4), Combat(42, 99, 17010240, id: 5)]);
        Assert.Single(result.Graph.Duplicates); Assert.Single(result.Graph.Conflicts);
        Assert.Equal(3, result.Graph.NameEdges.Count); Assert.Equal(2, result.Graph.RelatedIdCandidateEdges.Count);
        Assert.Equal(NameResolutionState.Conflict, Assert.Single(result.CombatCorrelations).SourceRetrospectiveName.State);
        Assert.Equal(1, result.Summary.SourceConflictRows);
    }

    [Fact]
    public void ReplayAnalyzerRejectsMixedCaptureRecords()
    {
        var raw = Identity(42, "Other") with { SourceCapture = "capture-b" };
        Assert.Throws<ArgumentException>(() => Analyze([raw]));
    }

    [Theory]
    [InlineData(0u)] [InlineData(17010240u)] [InlineData(uint.MaxValue)]
    public void EmptyMetadataProviderAlwaysReturnsNoMetadata(uint rawCode)
    {
        Assert.False(new EmptySkillMetadataProvider().TryGetSkillMetadata(rawCode, out var metadata)); Assert.Null(metadata);
    }

    [Fact]
    public void ProviderCannotSubstituteAParentOrNormalizedCode()
    {
        var raw = Combat(42, 99, 17010240);
        var decoded = new ProtocolDecodeResult([raw], [CombatCandidateDecoder.Decode(raw)], []);
        Assert.Throws<InvalidDataException>(() => new ReplayIdentityAnalyzer().Analyze("capture-a", decoded, new WrongCodeProvider()));
    }

    [Theory]
    [InlineData("identities")] [InlineData("id-graph")] [InlineData("skills")]
    public void JsonCommandsReplayCompressedSyntheticRecords(string command)
    {
        using var files = new TestFiles();
        var body = Identity(42, "SyntheticName").RawBytes.Concat(Context(42, "ContextLabel").RawBytes).Concat(Combat(42, 99, 17010240).RawBytes).ToArray();
        var payload = ReplayProtocolDecoderTests.Pack(body); var path = files.WritePcap([Packet(payload)]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, ResearchCli.Run([command, path, "--local", "198.51.100.2:443", "--remote", "192.0.2.1:12345", "--json"], output, error));
        Assert.Equal("", error.ToString()); using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement; var summary = root.GetProperty("Summary");
        Assert.Equal(1, summary.GetProperty("NameObservations4536").GetInt32());
        Assert.Equal(1, summary.GetProperty("TextContextObservations338A").GetInt32());
        Assert.Equal(1, summary.GetProperty("SourceRowsWithNames").GetInt32());
        if (command != "id-graph")
        {
            var combat = root.GetProperty("SupportedCombatRecords")[0];
            Assert.Equal(17010240u, combat.GetProperty("RawSkillCode").GetUInt32());
            Assert.False(combat.TryGetProperty("RawSkillCodeCandidate", out _));
            Assert.Equal(1, combat.GetProperty("Provenance").GetProperty("ContainerPath").GetArrayLength());
        }
        else Assert.Equal(1, root.GetProperty("Graph").GetProperty("ContextLabelEdges").GetArrayLength());
    }

    private sealed class WrongCodeProvider : ISkillMetadataProvider
    {
        public bool TryGetSkillMetadata(uint rawSkillCode, out SkillMetadata? metadata)
        { metadata = new(17010020); return true; }
    }
    private static IdentityObservation Observe(RawProtocolRecord raw) => Assert.Single(IdentityRecordDecoder.Decode(raw).Identities);
    private static byte[] V(ulong n) => ReplayProtocolDecoderTests.Varint(n);
    private static RawProtocolRecord Record(byte[] body, int id = 1, double seconds = 0)
    {
        var bytes = ReplayProtocolDecoderTests.Frame(body); var framing = ApplicationFraming.Read(bytes);
        return new(id, "capture-a", TrafficDirection.ServerToClient, id*100, id, id*100, Start.AddSeconds(seconds), id, id,
            Start.AddSeconds(seconds), framing.PrefixLength, bytes.Length, bytes, Convert.ToHexString(body.AsSpan(0,2)), [], "Unknown", []);
    }
    private static RawProtocolRecord Identity(ulong entity, string name, int id = 1, double seconds = 0, byte[]? trailing = null) =>
        NameBytes(entity, Encoding.UTF8.GetBytes(name), id, seconds, trailing: trailing);
    private static RawProtocolRecord NameBytes(ulong entity, byte[] name, int id = 1, double seconds = 0, byte? declaredLength = null, byte presence = 7, byte[]? trailing = null) =>
        Record([0x45,0x36, .. V(entity), 4,3,2,1, presence, declaredLength ?? checked((byte)name.Length), .. name, .. trailing ?? []], id, seconds);
    private static RawProtocolRecord Context(ulong entity, string label, int id = 1)
    {
        var text = Encoding.UTF8.GetBytes(label);
        return Record([0x33,0x8a, .. V(entity), 0x44,0x33,0x22,0x11, 0,0, 0x21,5, (byte)text.Length, .. text, 9], id);
    }
    private static RawProtocolRecord Related(ulong header, ulong anchor, uint suffix, string label, int id = 1, bool badPadding = false)
    {
        var parent = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(parent, suffix); var text = Encoding.UTF8.GetBytes(label);
        return Record([0x41,0x36, .. V(header), 0x5f, 0,0, .. Enumerable.Repeat((byte)255,8), 1,2,3,4,5,6,7,8, .. V(anchor),
            7,2,6, .. parent, 0x44,0x33,0x22,0x11, badPadding ? (byte)1 : (byte)0,0, 0x21,5, (byte)text.Length, .. text, 9], id);
    }
    private static RawProtocolRecord Combat(ulong source, ulong target, uint code, int id = 1, double seconds = 0)
    {
        var skill = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(skill, code);
        // A synthetic supported aggregate with a single component; no sample payload is used.
        return Record([4,0x38, .. V(target), 0x26,0, .. V(source), .. skill, 1,2, 0,0,2, 9,8,7,6, 1,0,0, 0,1, .. V(400), 1,7, 1,0], id, seconds);
    }
    private static IdentityResearchResult Analyze(RawProtocolRecord[] records) =>
        new ReplayIdentityAnalyzer().Analyze("capture-a", new(records, records.Where(r => r.OpcodeCandidate == "0438").Select(CombatCandidateDecoder.Decode).ToArray(), []));
    private static CapturedPacket Packet(byte[] payload)
    {
        var template = TestFiles.Packet(6, Start); var data = new byte[54+payload.Length]; template.Data.CopyTo(data,0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40+payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), 100); data[47] = (byte)(TcpFlags.Psh | TcpFlags.Ack);
        payload.CopyTo(data,54); return template with { Data = data, OriginalLength = data.Length };
    }
}
