using System.Buffers.Binary;
using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class LiveInitializationRefreshTests
{
    // Synthetic records, independently constructed from our existing identity grammar.
    // Opaque bytes vary; only the already attested identity fields may continue a binding.
    private static byte[] Refresh(ulong id = 200, string name = "Local", int padding = 0, int tail = 0)
    {
        var text = Encoding.UTF8.GetBytes(name);
        return Frame([0x33, 0x36, .. Varint(id), .. new byte[padding], (byte)text.Length, .. text, .. new byte[tail]]);
    }

    private static Harness WithLayout(int padding)
    {
        var h = new Harness(); h.Initialize(); h.Handshake();
        var id = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(id, 200);
        h.Frame(Frame([0x15, 0x36, .. id, 5, .. Encoding.UTF8.GetBytes("Local")]));
        h.Frame(Refresh(padding: padding)); Assert.Equal("READY", h.Tick().Status);
        return h;
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(5, 50)] [InlineData(5, 200)]
    public void WorldDungeonWorldRefreshKeepsOriginalScopeAndAttestation(int padding, int tail)
    {
        var h = WithLayout(padding); var original = h.Pipeline.Snapshot().Single();
        var evidenceCount = h.Pipeline.BindingEvidenceCount; var evidenceBytes = h.Pipeline.BindingEvidenceBytes;
        h.Frame(Hit(100)); h.Tick(); h.Frame(Refresh(padding: padding, tail: tail));
        h.Frame(Hit(500, components: [6])); Assert.Equal(600m, h.Tick().TotalDamage);
        h.Frame(Refresh(padding: padding, tail: tail + 20)); h.Frame(Hit(200));
        var state = h.Tick(); var epoch = h.Pipeline.Snapshot().Single();
        Assert.Equal("IN COMBAT", state.Status); Assert.Equal(800m, state.TotalDamage);
        Assert.Equal(original.EpochId, state.EpochId); Assert.Equal(200UL, state.EntityId);
        Assert.Equal(original.IdentityValidFrom, epoch.IdentityValidFrom);
        Assert.Equal(evidenceCount, h.Pipeline.BindingEvidenceCount); Assert.Equal(evidenceBytes, h.Pipeline.BindingEvidenceBytes);
        Assert.Equal(3, h.Events.Count); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        Assert.Equal(OverlayState.InCombat, OverlaySnapshot.FromMeter(state).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SplitRefreshWaitsForCompleteAckedBytesWithoutRecounting(bool compressed)
    {
        var h = WithLayout(5); h.Frame(Hit(100)); h.Tick();
        var data = Refresh(padding: 5, tail: 200);
        if (compressed) data = Pack(Pack(data));
        var sequence = h.ServerSequence;
        h.Frame(data[..7]); Assert.Equal(100m, h.Tick().TotalDamage);
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
        h.Frame(data[7..], acknowledge: false); Assert.Equal(100m, h.Tick().TotalDamage);
        Assert.True(h.Pipeline.RetainedPayloadBytes > 0);
        h.Ack(); Assert.Equal(100m, h.Tick().TotalDamage); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        h.Add(data, sequence); h.Ack(); Assert.Equal(100m, h.Tick().TotalDamage);
        h.Frame(Hit(200)); Assert.Equal(300m, h.Tick().TotalDamage); Assert.Equal(2, h.Events.Count);
    }

    [Fact]
    public void NestedRefreshAndDamageInOneCheckpointKeepSharedProvenanceAndAmounts()
    {
        var h = WithLayout(5);
        h.Frame(Pack(Pack([.. Refresh(padding: 5, tail: 100), .. Hit(786, components: [15]), .. Hit(999, actor: 300)])));
        var state = h.Tick(); Assert.Equal(786m, state.TotalDamage); Assert.Equal(1, state.OtherCount);
        Assert.Equal(2, h.Events.Count); Assert.All(h.Events, e => Assert.Equal(2, e.DamageEvent.Provenance.ContainerPath.Count));
        Assert.Single(state.Members!); Assert.Equal(786m, state.GroupTotalDamage);
    }

    [Fact]
    public void RepeatedRefreshesKeepEvidenceVerificationAndPublicationMemoryBounded()
    {
        var h = WithLayout(5); var count = h.Pipeline.BindingEvidenceCount; var bytes = h.Pipeline.BindingEvidenceBytes;
        for (var i = 0; i < 300; i++)
        {
            h.Frame([.. Refresh(padding: 5, tail: 200), .. Hit(1)]);
            Assert.Equal(i + 1, h.Tick().TotalDamage);
            Assert.Equal(count, h.Pipeline.BindingEvidenceCount); Assert.Equal(bytes, h.Pipeline.BindingEvidenceBytes);
            Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); Assert.Equal(0, h.Feed.RetainedIdentities);
            Assert.InRange(h.Pipeline.SelectedPacketCount, 1, 2);
            Assert.InRange(h.Pipeline.VerificationBytes, 0, 128 * 1024);
            h.Inputs.Clear(); h.Events.Clear();
        }
        Assert.Equal(300, h.Feed.PublishedCount);
    }

    [Theory]
    [InlineData(201, "Local")] [InlineData(200, "Other")]
    public void ChangedIdentityCannotContinueAnAttestedEpoch(uint id, string name)
    {
        var h = WithLayout(5); h.Frame(Hit()); h.Tick(); h.Frame(Refresh(id, name, 5));
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0m, h.Tick().TotalDamage);
        Assert.Equal(OverlayState.Unavailable, OverlaySnapshot.FromMeter(h.Tick()).State);
        h.Frame(Hit()); Assert.Single(h.Events);
    }

    [Fact]
    public void SameNameAndIdAtUnattestedOffsetDoesNotRecoverByScanning()
    {
        var h = WithLayout(5); h.Frame(Refresh(padding: 6));
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Null(h.Tick().EntityId);
    }

    [Fact]
    public void DuplicateMatchingIdentityFieldsAreAmbiguousAndFailClosed()
    {
        var h = WithLayout(5);
        h.Frame(Frame([0x33, 0x36, .. Varint(200), .. new byte[5], 5, .. Encoding.UTF8.GetBytes("Local"),
            .. Varint(200), .. new byte[5], 5, .. Encoding.UTF8.GetBytes("Local")]));
        Assert.Contains("UNTRUSTED", h.Tick().Status);
    }

    [Fact]
    public void CompleteNewInitializationPairStillRequiresANewEpoch()
    {
        var h = WithLayout(5); h.Bind(); Assert.Contains("UNTRUSTED", h.Tick().Status);
        h.Handshake(1000, 9000); h.Bind(); Assert.Equal("READY", h.Tick().Status);
    }

    [Fact]
    public void MalformedRefreshContainerCannotKeepBindingTrusted()
    {
        var h = WithLayout(5); h.Frame(Pack(Refresh(padding: 5)[..^1]));
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Empty(h.Events);
    }

    [Fact]
    public void NoncanonicalIdentityNumberCannotContinueBinding()
    {
        var h = WithLayout(5);
        h.Frame(Frame([0x33, 0x36, 0xC8, 0x81, 0, .. new byte[5], 5, .. Encoding.UTF8.GetBytes("Local")]));
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Empty(h.Events);
    }

    [Fact]
    public void OutboundLookalikeCannotChangeSelfBinding()
    {
        var h = WithLayout(5); var bytes = Refresh(201, "Other", 5);
        h.Add(bytes, server: false); h.ClientSequence += (uint)bytes.Length;
        h.Add([], flags: TcpFlags.Ack); h.Frame(Hit());
        Assert.Equal(200UL, h.Tick().EntityId); Assert.Equal(400m, h.Tick().TotalDamage);
    }

    [Fact]
    public void InitialResolverStillRejectsUnmodeledMultipleConfirmations()
    {
        var h = Fresh(); h.Frame(Refresh());
        Assert.Null(h.Tick().EntityId); Assert.Equal(CurrentPlayerBindingStatus.Unknown, h.Tick().BindingStatus);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void RetiredRefreshConflictStillInvalidatesTransport()
    {
        var h = WithLayout(5); var sequence = h.ServerSequence; var data = Refresh(padding: 5, tail: 40);
        h.Frame(data); h.Tick(); data[^1] ^= 1; h.Add(data, sequence);
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0, h.Pipeline.VerificationBytes);
    }

    [Fact]
    public void RefreshCannotBootstrapMissingFreshInitialization()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Refresh(padding: 5)); h.Frame(Hit());
        Assert.Null(h.Tick().EntityId); Assert.Equal(0m, h.Tick().TotalDamage); Assert.Equal(1, h.Tick().UnknownCount);
        Assert.Equal(OverlayState.Waiting, OverlaySnapshot.FromMeter(h.Tick()).State);
    }

    [Fact]
    public void ManualResetAndMembershipSurviveRefreshButActorRequiresFreshIdentity()
    {
        var h = WithLayout(5); AddParty(h); h.Frame(Hit(100)); h.Frame(Hit(200, actor: 300)); h.Tick();
        h.Meter.ResetCurrent(h.Now); h.Frame(Refresh(padding: 5, tail: 100));
        h.Frame(Hit(999, actor: 300)); h.Frame(PartyMeterTests.Identity());
        h.Frame(Hit(50)); h.Frame(Hit(70, actor: 300)); h.Frame(Hit(999, actor: 301));
        var state = h.Tick(); Assert.Equal(50m, state.TotalDamage); Assert.Equal(120m, state.GroupTotalDamage);
        Assert.Equal(2, state.Members!.Count); Assert.Single(state.PartyRoster!.ActiveMembers);
        Assert.Equal(2, state.SelfCount); Assert.Equal(4, state.OtherCount);
        h.Frame(Hit(1000, components: [15], category: 0x36)); Assert.Equal(120m, h.Tick().GroupTotalDamage);
        Assert.Equal(1, h.Tick().UnsupportedCandidates);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OverlappingFreshFlowRequiresIndependentBindingAndOldClosure(bool bindBeforeClose)
    {
        var h = WithLayout(5); AddParty(h); h.Frame(Hit(100)); var old = h.Tick();
        var oldServer = h.ServerSequence; var oldClient = h.ClientSequence;
        h.Handshake(1000, 9000, 25002); Assert.Equal(old.EpochId, h.Tick().EpochId);
        if (bindBeforeClose)
        {
            h.Bind("Next", 400, 25002); Assert.Contains("AMBIGUOUS", h.Tick().Status);
        }
        h.Add([], oldServer, flags: TcpFlags.Rst | TcpFlags.Ack, ack: oldClient);
        if (!bindBeforeClose)
        {
            Assert.Null(h.Tick().EntityId); Assert.Contains("Waiting", h.Tick().Status);
            h.Bind("Next", 400, 25002);
        }
        h.Frame(Hit(600, actor: 400), port: 25002); var next = h.Tick();
        Assert.Equal(400UL, next.EntityId); Assert.NotEqual(old.EpochId, next.EpochId);
        Assert.Equal(600m, next.TotalDamage); Assert.Single(next.Members!); Assert.Empty(next.PartyRoster!.ActiveMembers);
        h.Add(Hit(999), oldServer, ack: oldClient); h.Tick(); Assert.Equal(600m, h.Tick().TotalDamage);
        Assert.Equal(2, h.Events.Count); // Old late bytes cannot publish in the new scope.
        h.Frame(PartyMeterTests.Identity(), port: 25002); h.Frame(PartyMeterTests.Invite(), port: 25002);
        h.Frame(PartyMeterTests.Join(), port: 25002); h.Frame(Hit(50, actor: 300), port: 25002);
        Assert.Equal(650m, h.Tick().GroupTotalDamage); Assert.Equal(2, h.Tick().Members!.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NonProtocolOrMalformedCompetitorCannotStealAttestedAuthority(bool malformed)
    {
        var h = WithLayout(5); h.Frame(Hit(100)); var old = h.Tick();
        h.Handshake(1000, 9000, 25002);
        h.Frame(malformed ? Pack(Refresh()[..^1]) : Frame([0x57, 1, 0, 0]), port: 25002);
        var state = h.Tick(); Assert.Equal(old.EpochId, state.EpochId);
        Assert.Equal(200UL, state.EntityId); Assert.Equal(100m, state.TotalDamage);
        Assert.Equal("IN COMBAT", state.Status);
    }

    private static void AddParty(Harness h)
    { h.Frame(PartyMeterTests.Identity()); h.Frame(PartyMeterTests.Invite()); h.Frame(PartyMeterTests.Join()); }
}
