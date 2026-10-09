using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using System.Diagnostics;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class LiveCheckpointTests
{
    [Fact]
    public void StablePrefixRetiresInitializationAndPublicationIdentityButKeepsAttestedBinding()
    {
        var h = Fresh(); h.Frame(Hit(500, components: [5, 5])); var state = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, state.BindingStatus); Assert.Equal(200UL, state.EntityId);
        Assert.Equal(500m, state.TotalDamage); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        Assert.Equal(2, h.Pipeline.SelectedPacketCount); Assert.Equal(2, h.Pipeline.BindingEvidenceCount);
        Assert.Equal(24, h.Pipeline.BindingEvidenceBytes); Assert.Equal(0, h.Feed.RetainedIdentities);
        var identity = Assert.Single(h.Events).DamageEvent.Identity;
        for (var i = 0; i < 10; i++) { h.Ack(); Assert.Equal(500m, h.Tick().TotalDamage); }
        Assert.Equal(identity, Assert.Single(h.Events).DamageEvent.Identity); Assert.Equal(1, h.Feed.PublishedCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SplitFrameOrCompressedContainerTailSurvivesPrefixRetirement(bool compressed)
    {
        var h = Fresh(); h.Tick(); var first = Hit(100); var tail = compressed ? ReplayProtocolDecoderTests.Pack(Hit(200)) : Hit(200);
        var sequence = h.ServerSequence;
        h.Frame([.. first, .. tail[..7]]); Assert.Equal(100m, h.Tick().TotalDamage);
        Assert.Equal(7, h.Pipeline.RetainedPayloadBytes); var checkpoints = h.Pipeline.CheckpointCount;
        h.Ack(); h.Tick(); Assert.Equal(checkpoints, h.Pipeline.CheckpointCount); Assert.Equal(7, h.Pipeline.RetainedPayloadBytes);
        h.Add(tail[7..], sequence + (uint)first.Length + 7); h.ServerSequence += (uint)(tail.Length - 7); h.Ack();
        Assert.Equal(300m, h.Tick().TotalDamage); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        Assert.Equal(2, h.Events.Count); Assert.Equal(compressed ? 1 : 0, h.Events[1].DamageEvent.Provenance.ContainerPath.Count);
    }

    [Fact]
    public void UnacknowledgedCompleteFrameRemainsRawUntilPeerCommitsIt()
    {
        var h = Fresh(); h.Tick(); var before = h.Pipeline.CheckpointCount;
        var bytes = Hit(); h.Frame(bytes, false); h.Tick();
        Assert.Equal(bytes.Length, h.Pipeline.RetainedPayloadBytes); Assert.Equal(before, h.Pipeline.CheckpointCount);
        Assert.Equal(0, h.Feed.PublishedCount); h.Ack(); Assert.Equal(400m, h.Tick().TotalDamage);
        Assert.Equal(before + 1, h.Pipeline.CheckpointCount);
    }

    [Fact]
    public void RetiredOverlapPlusNewFrameIsTrimmedWithoutRecountOrLostProvenance()
    {
        var h = Fresh(); var first = Hit(100); var sequence = h.ServerSequence; h.Frame(first); h.Tick();
        var second = Hit(100); h.Add([.. first[^8..], .. second], sequence + (uint)first.Length - 8);
        h.ServerSequence += (uint)second.Length; h.Ack();
        Assert.Equal(200m, h.Tick().TotalDamage); Assert.Equal(2, h.Feed.PublishedCount);
        Assert.NotEqual(h.Events[0].PublicationIdentity, h.Events[1].PublicationIdentity);
        Assert.Equal(h.Events[0].DamageEvent.Provenance.StreamOffset + first.Length, h.Events[1].DamageEvent.Provenance.StreamOffset);
    }

    [Fact]
    public void ConflictWithinRetiredAuditWindowInvalidatesCommittedEncounter()
    {
        var h = Fresh(); var sequence = h.ServerSequence; var bytes = Hit(); h.Frame(bytes); h.Tick();
        bytes[^1] ^= 1; h.Add(bytes, sequence);
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0m, h.Tick().TotalDamage);
        Assert.Equal(1, h.Feed.PublishedCount); Assert.Equal(0, h.Pipeline.VerificationBytes);
    }

    [Fact]
    public void GapAtNewCursorCannotBeDecodedAsIndependentFrame()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick(); var sequence = h.ServerSequence; var bytes = Hit(600);
        h.Add(bytes[1..], sequence + 1); h.ServerSequence += (uint)bytes.Length; h.Ack();
        Assert.Equal(400m, h.Tick().TotalDamage); Assert.Equal(bytes.Length - 1, h.Pipeline.RetainedPayloadBytes);
        Assert.True(h.Tick().Gaps > 0);
        h.Add(bytes[..1], sequence); h.Ack(); Assert.Equal(1000m, h.Tick().TotalDamage);
    }

    [Fact]
    public void UnknownThenResolvedOtherAndSelfUseTheSameSharedAssociationRulesAcrossCheckpoints()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Hit()); h.Tick();
        Assert.Equal(1, h.Tick().UnknownCount); h.Bind(); h.Tick();
        h.Frame(Hit(600, actor: 201)); h.Tick(); h.Frame(Hit(200)); var current = h.Tick();
        Assert.Equal(200m, current.TotalDamage); Assert.Equal(1, current.SelfCount);
        Assert.Equal(1, current.OtherCount); Assert.Equal(1, current.UnknownCount); Assert.Equal(0, h.Feed.RetainedIdentities);
    }

    [Fact]
    public void InactivityAndReconnectKeepExistingEncounterRulesAfterManyCheckpoints()
    {
        var h = Fresh(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 20; i++) { h.Frame(Hit(100)); h.Tick(); }
        Assert.Equal(2000m, h.Tick().TotalDamage); h.Now = h.Now.AddSeconds(2);
        h.Frame(Hit(700, actor: 201)); Assert.Contains("IDLE", h.Tick().Status);
        h.Frame(Hit(300)); Assert.Equal(300m, h.Tick().TotalDamage); Assert.Equal(21, h.Tick().SelfCount);
        var old = h.Tick().EpochId; h.Handshake(); var waiting = h.Tick();
        Assert.NotEqual(old, waiting.EpochId); Assert.Null(waiting.EntityId); Assert.Equal(0m, waiting.TotalDamage);
        Assert.Equal(0, h.Pipeline.VerificationBytes); Assert.Equal(0, h.Pipeline.BindingEvidenceCount);
        h.Bind(); h.Frame(Hit(100)); Assert.Equal(100m, h.Tick().TotalDamage); Assert.Equal(1, h.Tick().SelfCount);
    }

    [Fact]
    public void FreshIsnsNearWrapAndLaterCheckpointSequenceWrapRemainIndependentOfLifetimeOffset()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(uint.MaxValue - 5, uint.MaxValue - 10); h.Bind(); h.Tick();
        for (var i = 0; i < 100; i++) { h.Frame(Hit(1)); h.Tick(); }
        Assert.Equal(100m, h.Tick().TotalDamage); Assert.Equal(100, h.Feed.PublishedCount);
        Assert.Equal(0, h.Feed.RetainedIdentities); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
    }

    [Fact]
    public void LifetimePast64MiBRetiresSafelyButUnverifiableVeryOldRetransmissionFailsClosed()
    {
        var h = Fresh(); var oldSequence = h.ServerSequence; var old = Hit(); h.Frame(old); h.Tick();
        var filler = ReplayProtocolDecoderTests.Frame(new byte[48 * 1024]);
        for (var i = 0; i < 1400; i++) { h.Frame(filler); h.Tick(); h.Inputs.Clear(); h.Events.Clear(); }
        Assert.True(h.Pipeline.RetiredPayloadBytes > 64 * 1024 * 1024);
        Assert.Equal(400m, h.Tick().TotalDamage); Assert.Equal(65536, h.Pipeline.VerificationBytes);
        h.Frame(Hit()); Assert.Equal(800m, h.Tick().TotalDamage);
        h.Add(old, oldSequence); Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0m, h.Tick().TotalDamage);
        Assert.Equal(2, h.Feed.PublishedCount);
    }

    [Fact]
    public void PendingTailAndBatchPublicationLimitsStillFailClosed()
    {
        var h = Fresh(limits: new(MaximumPackets: 12), eventLimit: 1); h.Tick();
        h.Frame([.. Hit(), .. Hit()]); Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Empty(h.Events);
        var tail = Fresh(limits: new(MaximumPackets: 12)); tail.Tick(); var bytes = Hit(); tail.Frame(bytes[..1]); tail.Tick();
        for (var i = 0; i < 20; i++) tail.Add(bytes[..1], tail.ServerSequence - 1);
        Assert.Contains("UNTRUSTED", tail.Tick().Status); Assert.Equal(0, tail.Pipeline.RetainedPayloadBytes);
    }

    [Fact]
    public void InitializationRefreshCannotSilentlyReuseCheckpointBinding()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick(); h.Bind();
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0m, h.Tick().TotalDamage);
        Assert.Equal(1, h.Feed.PublishedCount);
    }

    [Fact]
    public void Unsupported36StaysUnsupportedAcrossCheckpointedTraffic()
    {
        var h = Fresh(); h.Tick();
        for (var i = 0; i < 20; i++) { h.Frame(Hit(786, components: [15], category: 0x36)); h.Tick(); }
        Assert.Equal(20, h.Tick().UnsupportedCandidates); Assert.Equal(0m, h.Tick().TotalDamage);
        Assert.Equal(0, h.Feed.PublishedCount); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
    }

    [Fact]
    public void MalformedCompletedContainerCannotBecomeACommittedCheckpoint()
    {
        var h = Fresh(); h.Tick(); var before = h.Pipeline.CheckpointCount;
        var container = ReplayProtocolDecoderTests.Pack(Hit()[..^1]); h.Frame(container); h.Tick();
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(before, h.Pipeline.CheckpointCount);
        Assert.Equal(0, h.Feed.PublishedCount);
    }

    [Fact]
    public void HalfCloseEvidenceStaysBoundedAndAcknowledgedClosureReleasesCheckpointContext()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick();
        for (var i = 0; i < 50; i++)
        {
            h.Add([], flags: TcpFlags.Fin | TcpFlags.Ack); h.Tick();
            Assert.InRange(h.Pipeline.SelectedPacketCount, 1, 4);
            Assert.Equal("HalfClosed", h.Pipeline.Snapshot().Last().Lifecycle);
        }
        h.Add([], server: false, flags: TcpFlags.Fin | TcpFlags.Ack, ack: h.ServerSequence + 1);
        h.Add([], sequence: h.ServerSequence + 1, flags: TcpFlags.Ack, ack: h.ClientSequence + 1);
        Assert.Equal("Closed", h.Pipeline.Snapshot().Last().Lifecycle);
        Assert.StartsWith("AWAITING ACTOR", h.Tick().Status); Assert.Null(h.Tick().EntityId);
        Assert.Equal(0m, h.Tick().TotalDamage); Assert.Equal("Local", h.Tick().CharacterName);
        Assert.Equal(0, h.Pipeline.VerificationBytes);
        Assert.Equal(0, h.Pipeline.BindingEvidenceCount); Assert.Equal(0, h.Pipeline.SelectedPacketCount);
    }

    [Fact]
    public void CheckpointedMixedTrafficMatchesFiniteReplayAmountsAssociationsAndPhysicalProvenance()
    {
        var h = Fresh(); h.Tick();
        for (var i = 0; i < 40; i++)
        {
            var data = ReplayProtocolDecoderTests.Pack(ReplayProtocolDecoderTests.Pack(
                [.. Hit((uint)(300 + i), components: [6, 7]), .. Hit((uint)(100 + i), actor: 201)]));
            var sequence = h.ServerSequence; h.Add(data[..9]); h.ServerSequence += 9; h.Ack(); h.Tick();
            h.Add(data[9..], sequence + 9); h.ServerSequence += (uint)(data.Length - 9); h.Ack(); h.Tick();
            var outgoing = ReplayProtocolDecoderTests.Frame([0x57, 1, 0]);
            h.Add(outgoing, server: false); h.ClientSequence += (uint)outgoing.Length;
            h.Add([], flags: TcpFlags.Ack); h.Tick();
        }
        var replay = h.Adapter(); var audit = new ReplayDamageEventBindingAssociator().Analyze(replay.Events, replay.Binding, replay);
        Assert.Equal(replay.Events.Count, h.Events.Count); Assert.Equal(audit.SelfCount, h.Tick().SelfCount);
        Assert.Equal(audit.OtherCount, h.Tick().OtherCount);
        Assert.Equal(replay.Events.Where((_, i) => audit.Associations[i].Status == DamageEventPlayerAssociationStatus.Self).Sum(e => (decimal)e.Amount), h.Tick().TotalDamage);
        for (var i = 0; i < replay.Events.Count; i++)
        {
            var actual = h.Events[i]; var expected = replay.Events[i];
            Assert.Equal(expected.Amount, actual.DamageEvent.Amount); Assert.Equal(expected.OptionalComponents, actual.DamageEvent.OptionalComponents);
            Assert.Equal(audit.Associations[i].Status, actual.Association.Status);
            var a = actual.DamageEvent.Provenance; var e = expected.Provenance;
            Assert.Equal(e.StreamOffset, a.StreamOffset); Assert.Equal(e.OuterFrameOffset, a.OuterFrameOffset);
            Assert.Equal(e.PacketIndex, a.PacketIndex); Assert.Equal(e.CompletionPacketIndex, a.CompletionPacketIndex);
            Assert.Equal(e.Timestamp, a.Timestamp); Assert.Equal(e.CompletionTimestamp, a.CompletionTimestamp);
            Assert.Equal(e.ContainerPath.Select(p => p.InnerOffset), a.ContainerPath.Select(p => p.InnerOffset));
        }
    }

    [Fact]
    public void SharedPrivacyScanRetainsRawBoundaryContextAcrossCheckpoints()
    {
        var h = Fresh(); h.Tick();
        // Frame prefix byte 0x6e is 'n'; the next frame's suffix makes a sensitive string only when
        // the retired raw suffix and the new frame are scanned together.
        h.Frame(ReplayProtocolDecoderTests.Frame([0, 0, .. System.Text.Encoding.ASCII.GetBytes("authorizatio")])); h.Tick();
        var body = new byte[106]; body[0] = (byte)'x'; body[1] = 0;
        h.Frame(ReplayProtocolDecoderTests.Frame(body));
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0, h.Feed.PublishedCount);
    }

    [Fact]
    public void EarlierHigherAckSurvivesLaterRegressingAckAndCommitsAnEventuallyCompletedTail()
    {
        var h = Fresh(); h.Tick(); var sequence = h.ServerSequence; var first = Hit(100); var second = Hit(200);
        h.Frame([.. first, .. second[..7]], false); h.ServerSequence += (uint)(second.Length - 7);
        h.Ack(); h.Add([], server: false, flags: TcpFlags.Ack, ack: sequence + (uint)first.Length + 7);
        Assert.Equal(100m, h.Tick().TotalDamage); Assert.Equal(7, h.Pipeline.RetainedPayloadBytes);
        h.Add(second[7..], sequence + (uint)first.Length + 7);
        Assert.Equal(300m, h.Tick().TotalDamage); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
    }

    [Fact]
    public void ConflictingUncommittedTailAfterCheckpointPoisonsEpochBeforePublication()
    {
        var h = Fresh(); h.Frame(Hit(100)); h.Tick(); var sequence = h.ServerSequence; var bytes = Hit(200);
        h.Frame(bytes, false); var changed = bytes.ToArray(); changed[^1] ^= 1;
        h.Add(changed, sequence); h.Ack(); Assert.Contains("UNTRUSTED", h.Tick().Status);
        Assert.Equal(0m, h.Tick().TotalDamage); Assert.Equal(1, h.Feed.PublishedCount);
        Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); Assert.Equal(0, h.Pipeline.VerificationBytes);
    }

    [Fact]
    public void LongSessionExceedsFormerIdentityLimitWithBoundedStateAndExactAccounting()
    {
        var h = Fresh(); h.Tick(); var rows = new List<object>(); var times = new List<double>();
        var payload = Enumerable.Range(0, 100).SelectMany(_ => Hit(1)).ToArray();
        for (var batch = 1; batch <= 1200; batch++)
        {
            var start = Stopwatch.GetTimestamp(); h.Frame(payload); var state = h.Tick();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Assert.Equal(batch * 100m, state.TotalDamage); Assert.Equal(batch * 100L, h.Feed.PublishedCount);
            Assert.InRange(h.Pipeline.SelectedPacketCount, 0, 2); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
            Assert.Equal(0, h.Feed.RetainedIdentities); Assert.Equal(2, h.Pipeline.BindingEvidenceCount);
            Assert.InRange(h.Pipeline.VerificationBytes, 0, 65536); h.Inputs.Clear(); h.Events.Clear();
            if (batch is 100 or 300 or 600 or 1200)
            {
                rows.Add(new { Batch = batch, Events = h.Feed.PublishedCount, Packets = h.Pipeline.SelectedPacketCount,
                    Payload = h.Pipeline.RetainedPayloadBytes, Identities = h.Feed.RetainedIdentities,
                    MedianTickMs = times.Order().ElementAt(times.Count / 2), Memory = GC.GetTotalMemory(true),
                    Checkpoints = h.Pipeline.CheckpointCount, Verification = h.Pipeline.VerificationBytes });
                times.Clear();
            }
        }
        var path = Environment.GetEnvironmentVariable("AION_CHECKPOINT_SOAK");
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void SyntheticSessionMeasuresRetentionAndAccounting()
    {
        var h = LiveCombatMeterTests.Fresh(checkpoints: Environment.GetEnvironmentVariable("AION_CHECKPOINT_LEGACY") != "1");
        var rows = new List<object>();
        var payload = Enumerable.Range(0, 100).SelectMany(_ => LiveCombatMeterTests.Hit(1)).ToArray();
        for (var batch = 1; batch <= 60; batch++)
        {
            h.Frame(payload); var state = h.Tick();
            Assert.Equal(batch * 100m, state.TotalDamage);
            h.Inputs.Clear(); h.Events.Clear();
            if (batch is 1 or 10 or 30 or 60)
                rows.Add(new { Batch = batch, Events = h.Feed.PublishedCount, Packets = h.Pipeline.SelectedPacketCount,
                    Payload = h.Pipeline.RetainedPayloadBytes, Identities = h.Feed.RetainedIdentities,
                    TickMs = h.Pipeline.LastRecomputeMilliseconds, Memory = GC.GetTotalMemory(true),
                    Checkpoints = h.Pipeline.CheckpointCount, Verification = h.Pipeline.VerificationBytes,
                    BindingEvidence = h.Pipeline.BindingEvidenceCount, BindingBytes = h.Pipeline.BindingEvidenceBytes });
        }
        var path = Environment.GetEnvironmentVariable("AION_CHECKPOINT_MEASUREMENT");
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
