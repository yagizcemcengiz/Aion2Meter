using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;
using static Aion2Meter.Tests.PlayerProfileTests;

namespace Aion2Meter.Tests;

public sealed class SceneIdentityContinuityTests
{
    private static byte[] Local(ulong id = 200, string name = "Local", uint code = 29,
        byte faction = 1) => Profile(code, id, name, true, faction: faction);
    private static Harness Start(bool checkpoints = true)
    { var h = new Harness(checkpoints: checkpoints); h.Initialize(); h.Handshake(); return h; }
    private static Harness Bound(bool checkpoints = true)
    { var h = Start(checkpoints); h.Frame(Local()); Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus); return h; }

    [Theory]
    [InlineData("Abyss")] [InlineData("town")] [InlineData("dungeon")]
    [InlineData("green-quest instance")] [InlineData("unseen future scene")]
    public void FreshSavedSceneUsesStructuralProfileWithoutMapInputOr1536(string scenario)
    {
        Assert.False(string.IsNullOrEmpty(scenario)); // Test label only; never passed to production.
        var h = Start(); h.Frame(Hit(900)); Assert.Null(h.Tick().EntityId);
        h.Frame(Local()); var resolved = h.Tick();
        Assert.Equal(200UL, resolved.EntityId); Assert.Equal("Local", resolved.CharacterName);
        Assert.Equal(PlayerClass.Cleric, resolved.StableIdentity!.Class);
        Assert.Equal(0m, resolved.TotalDamage); Assert.Equal(1, resolved.UnknownCount);
        h.Frame(Hit(100)); Assert.Equal(100m, h.Tick().TotalDamage);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, h.Events[0].Association.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FixedProfileBefore1536HasDeterministicAuthorityAndPreservesConfirmation(bool checkpoints)
    {
        var h = Bound(checkpoints); var before = h.Tick();
        h.Frame(Frame([0x15, 0x36, .. BitConverter.GetBytes(200U), 5, .. Encoding.UTF8.GetBytes("Local")]));
        h.Frame(Local()); h.Frame(Hit(100)); var after = h.Tick();
        Assert.Equal(before.IdentityValidFrom, after.IdentityValidFrom);
        Assert.Equal(100m, after.TotalDamage); Assert.Equal(PlayerClass.Cleric, after.StableIdentity!.Class);
    }

    [Theory]
    [InlineData("world -> subzone")] [InlineData("world -> town")]
    [InlineData("world -> Abyss")] [InlineData("Abyss -> world")]
    [InlineData("world -> dungeon")] [InlineData("dungeon -> world")]
    [InlineData("teleport")] [InlineData("channel change")]
    public void SceneLabelsDoNotControlRuntimeReplacementOrRetiredActorEligibility(string scenario)
    {
        Assert.False(string.IsNullOrEmpty(scenario));
        var h = Bound(); var first = h.Tick(); h.Frame(Hit(100)); h.Tick();
        h.Frame(Hit(800, actor: 201)); h.Tick(); // New actor has no proof yet: diagnostic Other.
        h.Frame(Local(201)); var rebound = h.Tick();
        Assert.Equal(first.StableIdentity, rebound.StableIdentity); Assert.Equal(201UL, rebound.EntityId);
        Assert.True(rebound.IdentityValidFrom > first.IdentityValidFrom); Assert.Equal(100m, rebound.TotalDamage);
        h.Frame(Hit(900, actor: 200)); h.Frame(Hit(50, actor: 201)); var now = h.Tick();
        Assert.Equal(150m, now.TotalDamage); Assert.Equal(2, now.EncounterSelfHits); Assert.Equal(2, now.OtherCount);
        Assert.Single(now.Members!); Assert.Equal(PlayerClass.Cleric, now.Members![0].Class);
        Assert.Equal(DamageEventPlayerAssociationStatus.Other, h.Events[1].Association.Status);
        Assert.Equal(200UL, h.Events[0].Association.BindingEntityId); // Previously published provenance stays old.
        Assert.Equal(201UL, h.Events[^1].Association.BindingEntityId);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void SharedPacketOrContainerOrdersOldHitRebindAndNewHitWithoutBackfill(bool checkpoints, bool compressed)
    {
        var h = Bound(checkpoints);
        byte[] batch = [.. Hit(100), .. Hit(999, actor: 201), .. Local(201), .. Hit(50, actor: 201), .. Hit(888)];
        h.Frame(compressed ? Pack(Pack(batch)) : batch);
        Assert.Equal(150m, h.Tick().TotalDamage); Assert.Equal(4, h.Events.Count);
        Assert.Equal(new[] { DamageEventPlayerAssociationStatus.Self, DamageEventPlayerAssociationStatus.Other,
            DamageEventPlayerAssociationStatus.Self, DamageEventPlayerAssociationStatus.Other },
            h.Events.Select(e => e.Association.Status));
        h.Frame(Hit(10, actor: 201)); Assert.Equal(160m, h.Tick().TotalDamage);
        var adapter = h.Adapter();
        var prior = Assert.Single(adapter.Binding.PreviousBindings);
        var lateOld = adapter.Events.Single(e => e.Amount == 888);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown,
            new ReplayDamageEventBindingAssociator().Associate(lateOld, prior, adapter).Status);
    }

    [Fact]
    public void SplitUnackedProfileCannotBindUntilCompleteAndPeerAcknowledged()
    {
        var h = Start(); var profile = Local();
        h.Frame(profile[..8]); Assert.Null(h.Tick().EntityId);
        h.Frame(profile[8..], acknowledge: false); Assert.Null(h.Tick().EntityId);
        h.Ack(); Assert.Equal(200UL, h.Tick().EntityId);
        h.Frame(Hit()); Assert.Equal(400m, h.Tick().TotalDamage);
    }

    [Fact]
    public void ConnectionHandoffRetainsStableClassWithoutAnActorOrDamageAndResumesAfterFreshProof()
    {
        var h = Bound(); h.Frame(Hit(100)); var original = h.Tick();
        h.Add([], flags: TcpFlags.Rst | TcpFlags.Ack);
        h.Frame([.. Local(300), .. Hit(999, actor: 300)], port: 25002); // Midstream candidate cannot attest framing.
        var waiting = h.Tick(); Assert.Equal(original.StableIdentity, waiting.StableIdentity);
        Assert.Null(waiting.EntityId); Assert.Equal(0m, waiting.TotalDamage); Assert.Null(waiting.Dps);
        var row = Assert.Single(OverlaySnapshot.FromMeter(waiting).Rows);
        Assert.Equal("Local", row.DisplayName); Assert.Equal(PlayerClass.Cleric, row.Class); Assert.Equal(0m, row.TotalDamage);
        h.Handshake(2000, 9000, 25002); h.Frame(Local(300), port: 25002); h.Frame(Hit(50, actor: 300), port: 25002);
        var next = h.Tick(); Assert.Equal(300UL, next.EntityId); Assert.Equal(50m, next.TotalDamage);
        Assert.NotEqual(original.EpochId, next.EpochId); Assert.Equal(original.StableIdentity, next.StableIdentity);
        Assert.Equal(2, h.Events.Count); // No replay of ignored midstream damage.
    }

    [Fact]
    public void RepeatedEntryExitRefreshesKeepCheckpointEvidenceAndAccountingBounded()
    {
        var h = Bound();
        for (uint id = 201; id < 261; id++)
        {
            h.Frame(Local(id)); h.Frame(Hit(1, actor: id)); var s = h.Tick();
            Assert.Equal(id - 200m, s.TotalDamage); Assert.Equal((ulong)id, s.EntityId);
            Assert.Equal(PlayerClass.Cleric, s.StableIdentity!.Class);
            Assert.Equal(1, h.Pipeline.BindingEvidenceCount); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
            Assert.Equal(0, h.Feed.RetainedIdentities);
        }
        Assert.Equal(60, h.Feed.PublishedCount);
    }

    [Fact]
    public void SameAssignmentRefreshPreservesOriginalValidityAndOpaqueBytesAreNotStableIdentity()
    {
        var h = Bound(); var first = h.Tick();
        var refresh = Local(); var at = ApplicationFraming.Read(refresh).PrefixLength + 2 + Varint(200).Length;
        refresh[at] = 0xDE; refresh[at + 1] = 0x93;
        h.Frame(refresh); h.Frame(Hit(100)); var now = h.Tick();
        Assert.Equal(first.IdentityValidFrom, now.IdentityValidFrom); Assert.Equal(first.StableIdentity, now.StableIdentity);
        Assert.Equal(100m, now.TotalDamage);
    }

    [Fact]
    public void FirstAttackerRemoteProfileAndMatchingNameCannotBootstrapSelf()
    {
        var h = Start(); h.Frame(Profile(29, 300, "Local")); h.Frame(Hit(999, actor: 300));
        var s = h.Tick(); Assert.Null(s.EntityId); Assert.Null(s.StableIdentity); Assert.Equal(0m, s.TotalDamage);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, Assert.Single(h.Events).Association.Status);
    }

    [Theory]
    [InlineData("name")] [InlineData("class")] [InlineData("server")] [InlineData("faction")]
    public void ConflictingStableFactsNeverChooseAWinner(string field)
    {
        var h = Start(); var second = Local(201, field == "name" ? "Other" : "Local", field == "class" ? 5U : 29U,
            field == "faction" ? (byte)2 : (byte)1);
        if (field == "server")
        {
            var at = ApplicationFraming.Read(second).PrefixLength + 2 + Varint(201).Length + 6 + 5;
            second[at] = 2;
        }
        h.Frame([.. Local(), .. second]); var s = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Conflict, s.BindingStatus); Assert.Null(s.EntityId);
        Assert.Null(s.StableIdentity); Assert.Equal(0m, s.TotalDamage);
    }

    [Theory]
    [InlineData("marker")] [InlineData("class")] [InlineData("server")] [InlineData("zero-id")]
    [InlineData("truncated")]
    public void UnvalidatedProfileDoesNotGainSingleRecordBinding(string mutation)
    {
        var h = Start(); var bytes = mutation == "zero-id" ? Local(0) : Local();
        var at = ApplicationFraming.Read(bytes).PrefixLength + 2 + Varint(mutation == "zero-id" ? 0UL : 200UL).Length;
        if (mutation == "marker") bytes[at + 4] = 0x17;
        if (mutation == "class") bytes[at + 6 + 5 + 2] = 45;
        if (mutation == "server") bytes[at + 6 + 5] = 0;
        if (mutation == "truncated") bytes = Frame([0x33, 0x36, .. Varint(200), 0, 0, 0, 0, 0x37, 20, 65]);
        h.Frame(bytes); h.Frame(Hit()); var s = h.Tick();
        Assert.NotEqual(CurrentPlayerBindingStatus.Resolved, s.BindingStatus); Assert.Null(s.EntityId); Assert.Equal(0m, s.TotalDamage);
    }

    [Fact]
    public void UnknownInitializationWithKnownProfileCanRetainStableIdentityWithoutNumericAuthority()
    {
        var h = Start(); h.Frame([.. Local(), .. Frame([0x33, 0x36, .. Varint(201), 5, .. Encoding.UTF8.GetBytes("Local")])]);
        h.Frame(Hit()); var s = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, s.BindingStatus); Assert.NotNull(s.StableIdentity); Assert.Null(s.EntityId);
        Assert.Equal(0m, s.TotalDamage); Assert.Single(OverlaySnapshot.FromMeter(s).Rows);
    }

    [Fact]
    public void DiagnosticCountsCandidatesAndDecisionsAreBoundedJsonSafeAndContainNoRawPayload()
    {
        var h = Start(); h.Bind(); h.Tick(); h.Frame(Local()); h.Tick();
        var json = h.Meter.Diagnostics.ToJson(h.Now); using var parsed = JsonDocument.Parse(json);
        var transition = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t => t.RecordTag == "3336");
        Assert.Equal(1, transition.Initialization1536Count); Assert.Equal(2, transition.Initialization3336Count);
        Assert.Equal("Resolved", transition.RuntimeBindingState); Assert.Equal("Local", transition.StableSelfName);
        Assert.Equal("Cleric", transition.StableSelfClass);
        var candidates = transition.InitializationCandidates!;
        Assert.InRange(candidates.Count, 1, 8);
        Assert.Contains(candidates, c => c.LayoutFingerprint!.StartsWith("3336/37") && c.RuntimeIdCandidate == 200 && c.Decision!.Contains("fixed 3336/37"));
        Assert.DoesNotContain("RawRecordBase64", json); Assert.DoesNotContain("NumericBytes", json); Assert.DoesNotContain("RawBytes", json);
        Assert.Contains("192.0.2.5:24001", json); Assert.Contains("198.51.100.7:13328", json);
        for (var i = 0; i < 270; i++) { h.Frame(Local()); h.Tick(); }
        Assert.Equal(LiveDiagnosticBuffer.Capacity, h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Count);
    }

    [Fact]
    public void Unsupported36RemainsUnsupportedAfterDirectProfileRebind()
    {
        var h = Bound(); h.Frame(Local(201)); h.Frame(Hit(786, actor: 201, components: [15], category: 0x36));
        Assert.Equal(0m, h.Tick().TotalDamage); Assert.Empty(h.Events); Assert.Equal(1, h.Tick().UnsupportedCandidates);
    }

    [Fact]
    public void DirectProfileDoesNotIgnoreAnUnambiguousContradicting1536()
    {
        var h = Start();
        h.Frame(Frame([0x15, 0x36, .. BitConverter.GetBytes(201U), 5, .. Encoding.UTF8.GetBytes("Local")]));
        h.Frame(Local()); Assert.Equal(CurrentPlayerBindingStatus.Conflict, h.Tick().BindingStatus);
        Assert.Null(h.Tick().EntityId); h.Frame(Hit()); Assert.Equal(0m, h.Tick().TotalDamage);
    }

    [Fact]
    public void RuntimeHistoryBoundNeverSelectsAnArbitraryWinner()
    {
        var h = Bound(checkpoints: false);
        for (uint id = 201; id < 219; id++) h.Frame(Local(id));
        Assert.NotEqual(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
        Assert.Null(h.Tick().EntityId); Assert.Equal(0m, h.Tick().TotalDamage);
    }

    [Fact]
    public void LiveSmokeJsonExplicitlyProjectsStableIdentityWithoutNetworkingReflection()
    {
        var h = Bound();
        var json = LiveSmokeCli.SnapshotJson(h.Pipeline, h.Pipeline.Snapshot(), h.Now);
        using var parsed = JsonDocument.Parse(json);
        var stable = parsed.RootElement.GetProperty("Epochs")[0].GetProperty("StableIdentity");
        Assert.Equal("Local", stable.GetProperty("CharacterName").GetString());
        Assert.Equal("Cleric", stable.GetProperty("Class").GetString());
        Assert.Contains("192.0.2.5", json);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnsupportedInitializationSuspendsActorThenLateFixedProofResumesWithoutBackfill(bool checkpoints)
    {
        var h = Bound(checkpoints); h.Frame(Hit(100)); h.Tick();
        h.Frame(Frame([0x33, 0x36, .. Varint(201), 5, .. Encoding.UTF8.GetBytes("Local")]));
        var waiting = h.Tick(); Assert.Null(waiting.EntityId); Assert.NotNull(waiting.StableIdentity);
        Assert.True(h.Pipeline.Snapshot().Single().AwaitingActor);
        Assert.Single(OverlaySnapshot.FromMeter(waiting).Rows); Assert.Equal(0m, waiting.TotalDamage);
        h.Frame(Hit(999)); h.Tick(); h.Frame(Hit(888, actor: 201)); h.Tick();
        Assert.Equal(2, h.Tick().UnknownCount);
        Assert.Equal(3, h.Pipeline.Snapshot().Single().AcceptedEvents); // Counts do not grow on recomputation.
        h.Frame(Local(201)); h.Frame(Hit(50, actor: 201)); var resolved = h.Tick();
        Assert.Equal(201UL, resolved.EntityId); Assert.Equal(150m, resolved.TotalDamage);
        Assert.Equal(2, resolved.EncounterSelfHits); Assert.Equal(2, resolved.UnknownCount);
        Assert.Equal(new[] { DamageEventPlayerAssociationStatus.Self, DamageEventPlayerAssociationStatus.Unknown,
            DamageEventPlayerAssociationStatus.Unknown, DamageEventPlayerAssociationStatus.Self }, h.Events.Select(e=>e.Association.Status));
        Assert.Equal(PlayerClass.Cleric, resolved.Members![0].Class);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SamePacketUnsupportedBoundaryQuarantinesHitsBeforeLateConfirmation(bool compressed)
    {
        var h = Bound();
        byte[] batch = [.. Hit(100), .. Frame([0x33, 0x36, .. Varint(201), 5, .. Encoding.UTF8.GetBytes("Local")]),
            .. Hit(999), .. Hit(888, actor:201), .. Local(201), .. Hit(50, actor:201)];
        h.Frame(compressed ? Pack(Pack(batch)) : batch);
        Assert.Equal(150m, h.Tick().TotalDamage); Assert.Equal(2, h.Tick().UnknownCount);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, h.Events[0].Association.Status);
        Assert.All(h.Events.Skip(1).Take(2), e=>Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, e.Association.Status));
    }

    [Fact]
    public void FreshStableProfileWithUnmodeledSequenceCanRecoverOnLaterStructuralProof()
    {
        var h = Start();
        h.Frame([.. Local(), .. Frame([0x33, 0x36, .. Varint(201), 5, .. Encoding.UTF8.GetBytes("Local")])]);
        h.Frame(Hit(888, actor:201)); Assert.Null(h.Tick().EntityId);
        Assert.Single(OverlaySnapshot.FromMeter(h.Tick()).Rows);
        h.Frame(Local(201)); h.Frame(Hit(50, actor:201)); Assert.Equal(50m, h.Tick().TotalDamage);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, h.Events[0].Association.Status);
    }

    [Fact]
    public void DifferentBackendAndCanonicalActorWidthNeedIndependentFreshEvidenceWithoutAnIpWhitelist()
    {
        var h = Bound(); var previous = h.Tick(); h.Frame(Hit(100)); h.Tick();
        h.Add([], flags: TcpFlags.Rst | TcpFlags.Ack);
        h.RemoteAddress = System.Net.IPAddress.Parse("203.0.113.88");
        h.Handshake(9000, 19000, 25002);
        var pending = h.Tick(); Assert.Equal(previous.StableIdentity, pending.StableIdentity); Assert.Null(pending.EntityId);
        h.Frame(Local(50000), port:25002); h.Frame(Hit(50,actor:50000), port:25002);
        var after = h.Tick(); Assert.Equal(50000UL, after.EntityId); Assert.Equal(50m, after.TotalDamage);
        Assert.Equal(previous.StableIdentity, after.StableIdentity); Assert.NotEqual(previous.EpochId, after.EpochId);
        Assert.Contains("203.0.113.88:13328", h.Meter.Diagnostics.ToJson(h.Now));
        Assert.Equal(200UL, h.Events[0].Association.BindingEntityId);
    }

    [Fact]
    public void CompetingResolvedFlowCannotReplaceSelectedStableIdentity()
    {
        var h = Bound(); var original = h.Tick(); var oldServer = h.ServerSequence; var oldClient = h.ClientSequence;
        h.Handshake(9000, 19000, 25002); h.Frame(Local(300,"Different"), port:25002);
        Assert.Contains("AMBIGUOUS", h.Tick().Status);
        h.Add([], flags:TcpFlags.Rst|TcpFlags.Ack, port:25002);
        Assert.Equal(original.StableIdentity, h.Tick().StableIdentity);
        h.Add([], oldServer, flags:TcpFlags.Rst|TcpFlags.Ack, ack:oldClient);
        h.Frame(Hit(999,actor:400), port:26000);
        Assert.Equal("Local", h.Tick().StableIdentity!.CharacterName); Assert.Null(h.Tick().EntityId);
    }

    [Fact]
    public void CallerClonedRuntimeWindowCannotGrantHistoricalAuthority()
    {
        var h = Bound(); h.Frame([.. Hit(100), .. Local(201), .. Hit(50,actor:201)]); h.Tick();
        var adapter = h.Adapter(); var old = Assert.Single(adapter.Binding.PreviousBindings);
        var hit = adapter.Events.Single(e=>e.Amount==100);
        var associator = new ReplayDamageEventBindingAssociator();
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, associator.Associate(hit,old,adapter).Status);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, associator.Associate(hit,old.CurrentOnly(),adapter).Status);
    }
}
