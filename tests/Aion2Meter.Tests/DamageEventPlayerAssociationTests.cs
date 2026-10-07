using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class DamageEventPlayerAssociationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2025-03-04T00:00:00Z");
    private static readonly TcpConnectionSelection Connection = new(IPAddress.Parse("192.0.2.5"), 24001, IPAddress.Parse("198.51.100.7"), 13328);
    private readonly ReplayDamageEventBindingAssociator associator = new();
    private static byte[] Combat(ulong source = 9) => ReplayProtocolDecoderTests.Combat(100, [], actor: source);

    [Theory]
    [InlineData(9ul, DamageEventPlayerAssociationStatus.Self)]
    [InlineData(100ul, DamageEventPlayerAssociationStatus.Other)]
    [InlineData(0ul, DamageEventPlayerAssociationStatus.Other)]
    [InlineData(ulong.MaxValue, DamageEventPlayerAssociationStatus.Other)]
    public void EligibleAssociationUsesOnlyDirectSource(ulong source, DamageEventPlayerAssociationStatus expected)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(source), 10));
        var e = Assert.Single(epoch.Events); var identity = e.Identity; var provenance = e.Provenance;
        Assert.Equal(expected, associator.Associate(e, epoch.Binding, epoch).Status);
        Assert.Equal(identity, e.Identity); Assert.Same(provenance, e.Provenance); Assert.Equal(100ul, e.Amount);
    }

    [Fact]
    public void BatchRetainsOrderOccurrencesAndDiagnosesAllDuplicateOccurrences()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10), (Combat(100), 11));
        DamageEvent[] input = [epoch.Events[1], epoch.Events[0], epoch.Events[0]];
        var a = associator.Analyze(input, epoch.Binding, epoch); var b = associator.Analyze(input, epoch.Binding, epoch);
        Assert.Equal(3, a.InputEventCount); Assert.Equal(2, a.SelfCount); Assert.Equal(1, a.OtherCount); Assert.Equal(0, a.UnknownCount);
        Assert.Equal(input.Select(e => e.Identity), a.Associations.Select(e => e.EventIdentity));
        Assert.Equal(new[] { 0, 1, 2 }, a.Associations.Select(e => e.InputIndex));
        Assert.Equal(new[] { false, true, true }, a.Associations.Select(e => e.DuplicateInput));
        Assert.Equal(a.Associations, b.Associations); Assert.Contains("Duplicate input", a.Associations[1].Diagnostic);
        var accounting = DamageEventAccountingAudit.Analyze(input);
        Assert.Equal(2, accounting.ValidatedUniqueEventCount); Assert.Equal(1, accounting.DuplicateInputCount);
        Assert.Throws<NotSupportedException>(() => ((IList<DamageEventPlayerAssociation>)a.Associations).Clear());
    }

    [Theory]
    [InlineData(CurrentPlayerBindingStatus.Unknown)] [InlineData(CurrentPlayerBindingStatus.Conflict)]
    public void NonResolvedBindingNeverYieldsSelfOrOther(CurrentPlayerBindingStatus status)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10), (Combat(100), 11));
        var binding = new CurrentPlayerBinding(status, epoch.Scope, null, null, Start.AddSeconds(4), null, null,
            epoch.Binding.EvidenceCoverageEnd, [], ["synthetic"]);
        var result = associator.Analyze(epoch.Events, binding, epoch);
        Assert.Equal(2, result.UnknownCount); Assert.Equal(0, result.SelfCount); Assert.Equal(0, result.OtherCount);
        Assert.All(result.Associations, a => Assert.Equal("Binding" + status, a.Diagnostic));
    }

    [Theory]
    [InlineData("SourceCapture")] [InlineData("SessionId")] [InlineData("LocalEndpoint")]
    [InlineData("RemoteEndpoint")] [InlineData("ClientIsn")] [InlineData("ServerIsn")]
    [InlineData("ClientSynTimestamp")] [InlineData("ClientSynPacketIndex")]
    public void EveryScopeComponentSeparatesBindings(string part)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10)); var s = epoch.Scope!;
        var other = part switch
        {
            "SourceCapture" => s with { SourceCapture = "other" }, "SessionId" => s with { SessionId = "other" },
            "LocalEndpoint" => s with { LocalEndpoint = "192.0.2.5:24002" }, "RemoteEndpoint" => s with { RemoteEndpoint = "198.51.100.8:13328" },
            "ClientIsn" => s with { ClientIsn = 200 }, "ServerIsn" => s with { ServerIsn = 1000 },
            "ClientSynTimestamp" => s with { ClientSynTimestamp = Start.AddTicks(1) },
            "ClientSynPacketIndex" => s with { ClientSynPacketIndex = 2 }, _ => throw new ArgumentException(part)
        };
        Assert.Equal("ScopeMismatch", associator.Associate(epoch.Events[0], Copy(epoch.Binding, scope: other), epoch).Diagnostic);
    }

    [Fact]
    public void SameEndpointsNamesAndEntityAcrossIndependentIsnsDoNotShareBinding()
    {
        var a = Epoch((Confirm(), 8), (Combat(), 10));
        var capture = Capture((Confirm(), 8), (Combat(), 10));
        var packets = capture.Packets.Select(p => p with { Segment = p.Direction == TrafficDirection.ClientToServer
            ? p.Segment with { SequenceNumber = p.Segment.SequenceNumber + 100, AcknowledgmentNumber = p.Segment.AcknowledgmentNumber == 0 ? 0 : p.Segment.AcknowledgmentNumber + 100 }
            : p.Segment with { SequenceNumber = p.Segment.SequenceNumber + 100, AcknowledgmentNumber = p.Segment.AcknowledgmentNumber + 100 } }).ToArray();
        var b = ReplayDamageEventEpochAdapter.Create(capture with { Packets = packets }, Connection, "session");
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, b.Binding.Status); Assert.Equal(a.Binding.EntityId, b.Binding.EntityId);
        Assert.Equal(a.Binding.CharacterName, b.Binding.CharacterName); Assert.NotEqual(a.Scope, b.Scope);
        Assert.Equal("ScopeMismatch", associator.Associate(b.Events[0], a.Binding, b).Diagnostic);
        Assert.Equal(a.Events[0].Identity, b.Events[0].Identity);
        Assert.Equal("UnsupportedEventProvenance", associator.Associate(b.Events[0], a.Binding, a).Diagnostic);
    }

    [Fact]
    public void MissingBridgeAndWrongSelectedEndpointsFailClosed()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10));
        Assert.Equal("MissingOrAmbiguousEpoch", associator.Associate(epoch.Events[0], epoch.Binding, null).Diagnostic);
        var bad = ReplayDamageEventEpochAdapter.Create(Capture((Confirm(), 8), (Combat(), 10)), Connection with { LocalPort = 24002 });
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, bad.Binding.Status);
        Assert.Equal("MissingOrAmbiguousEpoch", associator.Associate(epoch.Events[0], epoch.Binding, bad).Diagnostic);
    }

    [Fact]
    public void MultipleEpochOriginsAreUnmodeled()
    {
        var c = Capture((Confirm(), 8), (Combat(), 10));
        var second = c.Packets[0] with { Segment = c.Packets[0].Segment with { PacketIndex = 100, TimestampUtc = Start.AddSeconds(11), SequenceNumber = 500 } };
        var epoch = ReplayDamageEventEpochAdapter.Create(c with { Packets = [.. c.Packets, second] }, Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, epoch.Binding.Status);
        var eventEpoch = Epoch((Confirm(), 8), (Combat(), 10));
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, associator.Associate(eventEpoch.Events[0], eventEpoch.Binding, epoch).Status);
    }

    [Fact]
    public void PathAloneDoesNotEstablishEventMembership()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10)); var e = epoch.Events[0]; var p = e.Provenance;
        var fake = new DamageEventProvenance(p.CaptureScope, p.RecordId + 1, p.Direction, p.StreamOffset,
            p.OuterFrameId, p.OuterFrameOffset, p.Timestamp, p.PacketIndex, p.CompletionPacketIndex,
            p.CompletionTimestamp, p.PrefixLength, p.FrameLength, p.RecordTag, p.ContainerPath);
        Assert.Equal("UnsupportedEventProvenance", associator.Associate(Changed(e, provenance: fake), epoch.Binding, epoch).Diagnostic);
    }

    [Theory]
    [InlineData(7, "BeforeConfirmation")]
    [InlineData(8, "EligibleDirectSourceEqualsBinding")]
    [InlineData(9, "EligibleDirectSourceEqualsBinding")]
    public void ArrivalStartBoundaryIsInclusiveWithProvenanceOrder(int seconds, string diagnostic)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), seconds));
        Assert.Equal(diagnostic, associator.Associate(epoch.Events[0], epoch.Binding, epoch).Diagnostic);
    }

    [Fact]
    public void CandidateObservedFromDoesNotRetroactivelyAuthorizeEqualSource()
    {
        var epoch = Epoch((Combat(), 5), (Confirm(), 8), (Combat(), 10));
        Assert.Equal(Start.AddSeconds(4), epoch.Binding.CandidateObservedFrom);
        Assert.Equal("BeforeConfirmation", associator.Associate(epoch.Events[0], epoch.Binding, epoch).Diagnostic);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(epoch.Events[1], epoch.Binding, epoch).Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SamePacketStreamAndSharedContainerBeforeAfterConfirmationAreOrdered(bool compressed)
    {
        byte[] inner = [.. Combat(), .. Confirm(), .. Combat()];
        var epoch = Epoch((compressed ? ReplayProtocolDecoderTests.Pack(inner) : inner, 8));
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, epoch.Binding.Status); Assert.Equal(2, epoch.Events.Count);
        var a = associator.Analyze(epoch.Events, epoch.Binding, epoch);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, a.Associations[0].Status);
        Assert.Equal("BeforeOrAmbiguousConfirmationOrder", a.Associations[0].Diagnostic);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, a.Associations[1].Status);
        Assert.All(epoch.Events, e => Assert.Equal(epoch.Binding.ValidFrom, e.Timestamp));
    }

    [Fact]
    public void NestedSiblingContainersRetainOrderWithoutRecordIdHeuristic()
    {
        var before = ReplayProtocolDecoderTests.Pack(Combat()); var confirm = ReplayProtocolDecoderTests.Pack(Confirm());
        var after = ReplayProtocolDecoderTests.Pack(Combat());
        var epoch = Epoch((ReplayProtocolDecoderTests.Pack([.. before, .. confirm, .. after]), 8));
        var a = associator.Analyze(epoch.Events, epoch.Binding, epoch);
        Assert.Equal(1, a.UnknownCount); Assert.Equal(1, a.SelfCount); Assert.All(epoch.Events, e => Assert.Equal(2, e.Provenance.ContainerPath.Count));
    }

    [Fact]
    public void SplitSharedContainerUsesCompletionForBindingButArrivalForEvent()
    {
        var bytes = ReplayProtocolDecoderTests.Pack([.. Confirm(), .. Combat()]);
        var c = Capture((bytes[..4], 8), (bytes[4..], 10), (Combat(), 11));
        var epoch = ReplayDamageEventEpochAdapter.Create(c, Connection, "session");
        Assert.Equal(Start.AddSeconds(10), epoch.Binding.ValidFrom);
        Assert.Equal(Start.AddSeconds(8), epoch.Events[0].Timestamp);
        Assert.Equal(Start.AddSeconds(10), epoch.Events[0].Provenance.CompletionTimestamp);
        Assert.Equal("BeforeConfirmation", associator.Associate(epoch.Events[0], epoch.Binding, epoch).Diagnostic);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(epoch.Events[1], epoch.Binding, epoch).Status);
    }

    [Fact]
    public void FirstArrivalPacketBeforeConfirmationCompletionPacketIsUnknownEvenAtSameTimestamp()
    {
        var bytes = ReplayProtocolDecoderTests.Pack([.. Confirm(), .. Combat()]);
        var epoch = Epoch((bytes[..4], 8), (bytes[4..], 8), (Combat(), 8));
        Assert.Equal(Start.AddSeconds(8), epoch.Binding.ValidFrom);
        Assert.Equal("BeforeOrAmbiguousConfirmationOrder", associator.Associate(epoch.Events[0], epoch.Binding, epoch).Diagnostic);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(epoch.Events[1], epoch.Binding, epoch).Status);
    }

    [Theory]
    [InlineData(-1, DamageEventPlayerAssociationStatus.Unknown)]
    [InlineData(0, DamageEventPlayerAssociationStatus.Self)]
    [InlineData(1, DamageEventPlayerAssociationStatus.Self)]
    public void OneTimestampStepAroundStart(long ticks, DamageEventPlayerAssociationStatus expected)
    {
        var c = Capture((Confirm(), 8), (Combat(), 8));
        var last = c.Packets[^1];
        var epoch = ReplayDamageEventEpochAdapter.Create(c with { Packets = [.. c.Packets.SkipLast(1), last with { Segment = last.Segment with { TimestampUtc = Start.AddSeconds(8).AddTicks(ticks) } }] }, Connection);
        Assert.Equal(expected, associator.Associate(epoch.Events[0], epoch.Binding, epoch).Status);
    }

    [Theory]
    [InlineData(-1, "AfterCoverage")] [InlineData(0, "EligibleDirectSourceEqualsBinding")]
    public void CoverageEndIsInclusive(long ticks, string diagnostic)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10));
        var b = Copy(epoch.Binding, coverage: Start.AddSeconds(10).AddTicks(ticks));
        Assert.Equal(diagnostic, associator.Associate(epoch.Events[0], b, epoch).Diagnostic);
    }

    [Fact]
    public void RecordCompletionAfterCoverageIsUnknownEvenWhenArrivalFits()
    {
        var combat = Combat(); var epoch = Epoch((Confirm(), 8), (combat[..4], 9), (combat[4..], 10));
        Assert.Equal(Start.AddSeconds(9), epoch.Events[0].Timestamp);
        Assert.Equal("AfterCoverage", associator.Associate(epoch.Events[0], Copy(epoch.Binding, coverage: Start.AddSeconds(9)), epoch).Diagnostic);
    }

    [Theory]
    [InlineData(-1, "AfterValidUntil")] [InlineData(0, "EligibleDirectSourceEqualsBinding")]
    public void KnownValidUntilIsInclusive(long ticks, string diagnostic)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10), (Combat(), 11));
        var b = Copy(epoch.Binding, until: Start.AddSeconds(10).AddTicks(ticks));
        Assert.Equal(diagnostic, associator.Associate(epoch.Events[0], b, epoch).Diagnostic);
        Assert.Equal("AfterValidUntil", associator.Associate(epoch.Events[1], b, epoch).Diagnostic);
    }

    [Fact]
    public void ObservedResetCannotBeRemovedBySuppliedOpenBinding()
    {
        var c = Capture((Confirm(), 8), (Combat(), 10), (Combat(), 11));
        var p = c.Packets[1];
        var reset = p with { Segment = p.Segment with { PacketIndex = 100, TimestampUtc = Start.AddSeconds(10), Flags = TcpFlags.Rst,
            SequenceNumber = c.Packets[^1].Segment.SequenceNumber, Payload = [], DeclaredPayloadLength = 0 } };
        var epoch = ReplayDamageEventEpochAdapter.Create(c with { Packets = [.. c.Packets, reset] }, Connection);
        Assert.Equal(Start.AddSeconds(10), epoch.Binding.ValidUntil);
        var open = new CurrentPlayerBinding(CurrentPlayerBindingStatus.Resolved, epoch.Scope, epoch.Binding.EntityId, epoch.Binding.CharacterName,
            epoch.Binding.CandidateObservedFrom, epoch.Binding.ValidFrom, null, epoch.Binding.EvidenceCoverageEnd, epoch.Binding.Evidence, []);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(epoch.Events[0], open, epoch).Status);
        Assert.Equal("AfterValidUntil", associator.Associate(epoch.Events[1], open, epoch).Diagnostic);
    }

    [Theory]
    [InlineData("missing")] [InlineData("expanded")] [InlineData("before-start")]
    public void UnsupportedCoverageFailsClosed(string kind)
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10));
        var coverage = kind switch { "expanded" => Start.AddSeconds(11), "before-start" => Start.AddSeconds(7), _ => (DateTimeOffset?)null };
        var b = new CurrentPlayerBinding(CurrentPlayerBindingStatus.Resolved, epoch.Scope, 9, "Alice", Start.AddSeconds(4), epoch.Binding.ValidFrom, null, coverage, epoch.Binding.Evidence, []);
        Assert.Equal("UnsupportedCoverage", associator.Associate(epoch.Events[0], b, epoch).Diagnostic);
    }

    [Fact]
    public void MissingConfirmationOrChangedAuthorityTimeFailsClosed()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10));
        var missing = new CurrentPlayerBinding(CurrentPlayerBindingStatus.Resolved, epoch.Scope, 9, "Alice", null, Start.AddSeconds(8), null, Start.AddSeconds(10), [], []);
        Assert.Equal("UnsupportedConfirmationProvenance", associator.Associate(epoch.Events[0], missing, epoch).Diagnostic);
        var earlier = new CurrentPlayerBinding(CurrentPlayerBindingStatus.Resolved, epoch.Scope, 9, "Alice", null, Start.AddSeconds(4), null, Start.AddSeconds(10), epoch.Binding.Evidence, []);
        Assert.Equal("UnsupportedConfirmationProvenance", associator.Associate(epoch.Events[0], earlier, epoch).Diagnostic);
    }

    [Fact]
    public void AssociationIgnoresDamageSkillTargetFlagsComponentsAndNameSpelling()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10), (ReplayProtocolDecoderTests.Combat(ulong.MaxValue, [0, 7, 99], target: ulong.MaxValue, actor: 9), 11));
        Assert.Equal(2, associator.Analyze(epoch.Events, epoch.Binding, epoch).SelfCount);
        Assert.Equal(ulong.MaxValue, epoch.Events[1].Amount); Assert.Equal(ulong.MaxValue - 106, epoch.Events[1].DerivedBaseAmount);
        Assert.Equal(ulong.MaxValue, epoch.Events[1].TargetEntityId); Assert.Equal(new ulong[] { 0, 7, 99 }, epoch.Events[1].OptionalComponents);
        var named = ReplayDamageEventEpochAdapter.Create(Capture([(Confirm("名字 Another"), 8), (Combat(), 10)], name: "名字 Another"), Connection);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(named.Events[0], named.Binding, named).Status);
        // The existing binding constructor currently requires a name. Association adds no name lookup.
        Assert.Throws<ArgumentException>(() => new CurrentPlayerBinding(CurrentPlayerBindingStatus.Resolved, epoch.Scope, 9, null, null, Start.AddSeconds(8), null, Start.AddSeconds(10), [], []));
    }

    [Fact]
    public void ConflictingDuplicateAmountsStayOccurrencesForSeparateAccountingRejection()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10)); var e = epoch.Events[0]; var different = Changed(e, amount: 200);
        var association = associator.Analyze([e, different], epoch.Binding, epoch);
        Assert.Equal(1, association.SelfCount); Assert.Equal(1, association.UnknownCount);
        Assert.All(association.Associations, a => Assert.True(a.DuplicateInput));
        var accounting = DamageEventAccountingAudit.Analyze([e, different]);
        Assert.Equal(1, accounting.RejectedProvenanceCount); Assert.Equal(0, accounting.ValidatedUniqueEventCount);
    }

    [Fact]
    public void DetachedClonesCannotClaimAnEpochEvenWithIdenticalIdentityAndContent()
    {
        var epoch = Epoch((Confirm(), 8), (Combat(), 10)); var e = epoch.Events[0];
        var clone = new DamageEvent(e.SourceEntityId, e.TargetEntityId, e.RawSkillCode, e.Amount, e.DerivedBaseAmount,
            e.OptionalComponents, e.TypeRaw, e.ModifierRaw, e.DirectionRaw, e.Provenance);
        Assert.Equal(e.Identity, clone.Identity);
        Assert.Equal("UnsupportedEventProvenance", associator.Associate(clone, epoch.Binding, epoch).Diagnostic);
        Assert.Equal(2, associator.Analyze([e, e], epoch.Binding, epoch).SelfCount);
    }

    [Theory]
    [InlineData(0u)] [InlineData(17010020u)] [InlineData(17010240u)] [InlineData(uint.MaxValue)]
    public void RawCodeNeverInfluencesAssociation(uint code)
    {
        var bytes = Combat();
        var skillOffset = ReplayProtocolDecoderTests.Varint(100).Length + 2 + ReplayProtocolDecoderTests.Varint(9).Length + 3;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(skillOffset, 4), code);
        var epoch = Epoch((Confirm(), 8), (bytes, 10));
        Assert.Equal(code, epoch.Events[0].RawSkillCode);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(epoch.Events[0], epoch.Binding, epoch).Status);
    }

    [Fact]
    public void CompletionAfterKnownClosureFailsEvenWhenArrivalFits()
    {
        var bytes = Combat(); var epoch = Epoch((Confirm(), 8), (bytes[..4], 9), (bytes[4..], 10));
        Assert.Equal("AfterValidUntil", associator.Associate(epoch.Events[0], Copy(epoch.Binding, until: Start.AddSeconds(9)), epoch).Diagnostic);
    }

    [Fact]
    public void SameTimestampEventPacketAfterObservedResetStaysUnknown()
    {
        var c = Capture((Confirm(), 8), (Combat(), 10), (Combat(), 10));
        var eventAfter = c.Packets[^1] with { Segment = c.Packets[^1].Segment with { PacketIndex = 101 } };
        var reset = c.Packets[1] with { Segment = c.Packets[1].Segment with { PacketIndex = 100, TimestampUtc = Start.AddSeconds(10),
            Flags = TcpFlags.Rst, Payload = [], DeclaredPayloadLength = 0 } };
        var epoch = ReplayDamageEventEpochAdapter.Create(c with { Packets = [.. c.Packets.SkipLast(1), reset, eventAfter] }, Connection);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(epoch.Events[0], epoch.Binding, epoch).Status);
        Assert.Equal("AfterObservedClosureOrder", associator.Associate(epoch.Events[1], epoch.Binding, epoch).Diagnostic);
    }

    [Fact]
    public void IdenticalTransportRetransmissionProjectsOneEventBeforeAssociation()
    {
        var c = Capture((Confirm(), 8), (Combat(), 10)); var original = c.Packets[^1];
        var duplicate = original with { Segment = original.Segment with { PacketIndex = 100, TimestampUtc = Start.AddSeconds(11) } };
        var epoch = ReplayDamageEventEpochAdapter.Create(c with { Packets = [.. c.Packets, duplicate] }, Connection);
        Assert.Single(epoch.Events); Assert.Equal(1, associator.Analyze(epoch.Events, epoch.Binding, epoch).SelfCount);
        Assert.Equal(1, DamageEventAccountingAudit.Analyze(epoch.Events).ValidatedUniqueEventCount);
    }

    [Fact]
    public void MidstreamDoesNotInferSelfFromEqualSource()
    {
        var c = Capture((Confirm(), 8), (Combat(), 10));
        var epoch = ReplayDamageEventEpochAdapter.Create(c with { Packets = c.Packets.Where(p => !p.Segment.Flags.HasFlag(TcpFlags.Syn)).ToArray() }, Connection);
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, epoch.Binding.Status); Assert.Single(epoch.Events);
        Assert.Equal(1, associator.Analyze(epoch.Events, epoch.Binding, epoch).UnknownCount);
    }

    [Fact]
    public void EmptyAndNullInputsAreExplicit()
    {
        var epoch = Epoch((Confirm(), 8)); Assert.Equal(0, associator.Analyze([], epoch.Binding, epoch).InputEventCount);
        Assert.Throws<ArgumentNullException>(() => associator.Analyze(null!, epoch.Binding, epoch));
        Assert.Throws<ArgumentNullException>(() => associator.Analyze([null!], epoch.Binding, epoch));
        Assert.Throws<ArgumentNullException>(() => associator.Associate(null!, epoch.Binding, epoch));
        Assert.Throws<ArgumentNullException>(() => associator.Associate(new DamageEventProjector().Project(SupportedCombatRecord.From(
            new ReplayProtocolDecoder().Decode("x", [new(TrafficDirection.ServerToClient, 1, [new(0, Combat(), Start, 1)], [], [], 0, 0, Combat().Length, false)], Start).CombatCandidates[0])), null!, null));
    }

    [Fact]
    public void CliJsonAndTextKeepAllStatusesAndAreDeterministic()
    {
        var c = Capture((Combat(), 5), (Confirm(), 8), (Combat(), 10), (Combat(100), 11));
        using var files = new TestFiles(); var path = files.WritePcap(c.Packets.Select(Wire).ToArray());
        string Run(bool json)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            var args = new List<string> { "self-association", path, "--local", "192.0.2.5:24001", "--remote", "198.51.100.7:13328" };
            if (json) args.Add("--json");
            Assert.Equal(0, ResearchCli.Run(args.ToArray(), output, error)); Assert.Empty(error.ToString()); return output.ToString();
        }
        var json = Run(true); Assert.Equal(json, Run(true)); using var j = JsonDocument.Parse(json);
        var summary = j.RootElement.GetProperty("Summary"); Assert.Equal(3, summary.GetProperty("InputEventCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("SelfCount").GetInt32()); Assert.Equal(1, summary.GetProperty("OtherCount").GetInt32()); Assert.Equal(1, summary.GetProperty("UnknownCount").GetInt32());
        Assert.Equal(new[] { "Unknown", "Self", "Other" }, j.RootElement.GetProperty("Associations").EnumerateArray().Select(a => a.GetProperty("Status").GetString()));
        var text = Run(false); Assert.Contains("OtherCount=1", text); Assert.Contains("BeforeConfirmation", text);
        Assert.DoesNotContain("ValidatedTotalAmount", json); Assert.DoesNotContain("Self Total Damage", text);
    }

    [Fact]
    public void CliNoSelectedConnectionReturnsUnknownWithoutScanningAllTraffic()
    {
        using var files = new TestFiles(); var path = files.WritePcap([TestFiles.Packet(6, Start)]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, ResearchCli.Run(["self-association", path, "--json"], output, error));
        using var j = JsonDocument.Parse(output.ToString());
        Assert.Equal("Unknown", j.RootElement.GetProperty("Binding").GetProperty("Status").GetString());
        Assert.Equal(0, j.RootElement.GetProperty("Summary").GetProperty("InputEventCount").GetInt32());
        Assert.Empty(error.ToString());
    }

    private static CurrentPlayerBinding Copy(CurrentPlayerBinding b, ReplayConnectionScope? scope = null,
        DateTimeOffset? coverage = null, DateTimeOffset? until = null) => new(b.Status, scope ?? b.Scope,
        b.EntityId, b.CharacterName, b.CandidateObservedFrom, b.ValidFrom, until ?? b.ValidUntil,
        coverage ?? b.EvidenceCoverageEnd, b.Evidence, b.Diagnostics);
    private static DamageEvent Changed(DamageEvent e, ulong amount = 0, DamageEventProvenance? provenance = null) =>
        new(e.SourceEntityId, ulong.MaxValue, uint.MaxValue, amount, 0, [0, 7, 99], ulong.MaxValue, 255, 255, provenance ?? e.Provenance);
    private static ReplayDamageEventEpochAdapter Epoch(params (byte[] Bytes, int Seconds)[] inbound) =>
        ReplayDamageEventEpochAdapter.Create(Capture(inbound), Connection, "session");
    private static byte[] Confirm(string name = "Alice")
    {
        var text = Encoding.UTF8.GetBytes(name); return ReplayProtocolDecoderTests.Frame([0x33, 0x36, 9, (byte)text.Length, .. text]);
    }
    private static ResearchCapture Capture((byte[] Bytes, int Seconds)[] inbound, string name = "Alice")
    {
        var packets = new List<ResearchPacket>();
        void Add(TrafficDirection d, uint sequence, uint ack, TcpFlags flags, byte[] payload, int seconds)
        {
            var server = d == TrafficDirection.ServerToClient;
            var s = new TcpSegment(packets.Count + 1, Start.AddSeconds(seconds), server ? Connection.RemoteIp : Connection.LocalIp,
                server ? Connection.RemotePort : Connection.LocalPort, server ? Connection.LocalIp : Connection.RemoteIp,
                server ? Connection.LocalPort : Connection.RemotePort, sequence, ack, flags, 54 + payload.Length, payload.Length, payload, false);
            packets.Add(new(s, d, seconds));
        }
        Add(TrafficDirection.ClientToServer, 100, 0, TcpFlags.Syn, [], 0);
        Add(TrafficDirection.ServerToClient, 900, 101, TcpFlags.Syn | TcpFlags.Ack, [], 1);
        Add(TrafficDirection.ClientToServer, 101, 901, TcpFlags.Ack, [], 2);
        var text = Encoding.UTF8.GetBytes(name); var first = ReplayProtocolDecoderTests.Frame([0x15, 0x36, 9, 0, 0, 0, (byte)text.Length, .. text]);
        Add(TrafficDirection.ServerToClient, 901, 101, TcpFlags.Psh | TcpFlags.Ack, first, 4);
        var sequence = 901u + (uint)first.Length;
        foreach (var (bytes, seconds) in inbound)
        {
            Add(TrafficDirection.ServerToClient, sequence, 101, TcpFlags.Psh | TcpFlags.Ack, bytes, seconds); sequence += (uint)bytes.Length;
        }
        return new("synthetic-association", Start, "synthetic", packets.ToArray(), 0, 0);
    }
    private static ResearchCapture Capture((byte[] Bytes, int Seconds) a, (byte[] Bytes, int Seconds) b) => Capture([a, b]);
    private static ResearchCapture Capture((byte[] Bytes, int Seconds) a, (byte[] Bytes, int Seconds) b, (byte[] Bytes, int Seconds) c) => Capture([a, b, c]);
    private static ResearchCapture Capture((byte[] Bytes, int Seconds) a, (byte[] Bytes, int Seconds) b, (byte[] Bytes, int Seconds) c, (byte[] Bytes, int Seconds) d) => Capture([a, b, c, d]);
    private static CapturedPacket Wire(ResearchPacket p)
    {
        var s = p.Segment; var data = new byte[54 + s.Payload.Length]; TestFiles.Packet(6, s.TimestampUtc).Data.CopyTo(data, 0);
        s.SourceIp.GetAddressBytes().CopyTo(data, 26); s.DestinationIp.GetAddressBytes().CopyTo(data, 30);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + s.Payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(34), s.SourcePort); BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(36), s.DestinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), s.SequenceNumber); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(42), s.AcknowledgmentNumber);
        data[47] = (byte)s.Flags; s.Payload.CopyTo(data, 54); return new(s.TimestampUtc, data.Length, 1, data);
    }
}
