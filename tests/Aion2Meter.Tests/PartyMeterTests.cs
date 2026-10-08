using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class PartyMeterTests
{
    private static byte[] Uuid(string name) => Encoding.ASCII.GetBytes(new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16)).ToString("D"));
    private static byte[] Text(string name) => [checked((byte)Encoding.UTF8.GetByteCount(name)), .. Encoding.UTF8.GetBytes(name)];
    internal static byte[] Identity(uint id = 300, string name = "Remote") => Frame([0x45, 0x36, .. Varint(id), 0, 0, 0, 0, 7, .. Text(name)]);
    private static byte[] Member(uint id, string name) => [0, 2, .. BitConverter.GetBytes(id), 1, 0, 1, 0, 36, .. Uuid(name), .. new byte[8], .. Text(name), .. new byte[78]];
    internal static byte[] Invite(uint id = 300, string name = "Remote") => Frame([0x08, 0x92, 1, 0, 36, .. Uuid(name), .. new byte[8], .. Varint(id), .. new byte[12], .. Text(name), .. new byte[8]]);
    internal static byte[] Join(uint id = 300, string name = "Remote") => Frame([0x0D, 0x92, .. Member(id, name)]);
    internal static byte[] Leave(bool optional = true, uint selfId = 200, string selfName = "Local") =>
        Frame([0, 0x92, optional ? (byte)8 : (byte)0, .. new byte[24],
            .. optional ? new byte[] { 5 }.Concat(new byte[120]).ToArray() : Array.Empty<byte>(),
            1, .. Member(selfId, selfName), 0]);
    internal static byte[] Disband() => Frame([0x13, 0x92, 0, 0]);
    private static void AddMember(Harness h, uint id = 300, string name = "Remote")
    { h.Frame(Identity(id, name)); h.Frame(Invite(id, name)); h.Frame(Join(id, name)); }
    private static LiveMeterMemberSnapshot Remote(LiveMeterSnapshot s) => Assert.Single(s.Members!, m => !m.IsSelf);

    [Fact]
    public void SoloAndInviteOnlyNeverExposeRandomOther()
    {
        var h = Fresh(); h.Frame(Identity()); h.Frame(Invite()); h.Frame(Hit(500, 300));
        var m = h.Tick(); Assert.Single(m.Members!); Assert.Equal(0m, m.GroupTotalDamage); Assert.Equal(1, m.OtherCount);
        Assert.Empty(m.PartyRoster!.ActiveMembers);
    }

    [Fact]
    public void JoinMembershipWaitsForIndependentActorAndRejectsConflictingName()
    {
        var h = Fresh(); h.Frame(Invite()); h.Frame(Invite()); h.Frame(Join()); Assert.Equal(2, h.Tick().Members!.Count); Assert.Null(Remote(h.Tick()).EntityId);
        h.Frame(Identity()); h.Frame(Join(name: "Wrong")); Assert.Single(h.Tick().Members!);
        h.Frame(Join()); Assert.Equal(2, h.Tick().Members!.Count);
    }

    [Fact]
    public void WholeBatchIncludesOnlyTwoMembershipIntervalsAndFreezesDamage()
    {
        var h = Fresh(checkpoints: false); h.Frame(Hit(999, 300)); AddMember(h); h.Frame(Hit(100, 300));
        h.Frame(Leave()); h.Frame(Hit(999, 300)); h.Frame(Invite()); h.Frame(Join()); h.Frame(Hit(200, 300));
        h.Frame(Disband()); h.Frame(Hit(999, 300));
        var m = h.Tick(); Assert.Equal(300m, Remote(m).TotalDamage); Assert.Equal(2, Remote(m).Hits);
        Assert.False(Remote(m).ActivePartyMember); Assert.Empty(m.PartyRoster!.ActiveMembers);
        Assert.Equal(2, m.PartyRoster.RecentIntervals.Count);
        Assert.All(m.PartyRoster.RecentIntervals, i => Assert.True(i.ValidUntil > i.ValidFrom));
        Assert.Equal(5, m.OtherCount); Assert.Equal(300m, m.GroupTotalDamage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BothSelfOnlyRefreshFormsTerminateWithCompleteConsumption(bool optional)
    {
        var h = Fresh(); AddMember(h); h.Tick(); h.Frame(Leave(optional)); var m = h.Tick();
        Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.Single(m.PartyRoster.RecentIntervals);
        Assert.Single(m.Members!); Assert.Null(m.PartyRoster.Diagnostic);
    }

    [Fact]
    public void LeavePreservesCurrentDamageButNextEncounterHasNoStaleRow()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(400, 300)); h.Tick();
        h.Frame(Leave()); h.Frame(Hit(999, 300)); var frozen = h.Tick(); Assert.Equal(400m, Remote(frozen).TotalDamage);
        h.Now = h.Now.AddSeconds(31); h.Frame(Hit(100)); var next = h.Tick();
        Assert.Single(next.Members!); Assert.Equal(100m, next.GroupTotalDamage); Assert.Equal(1, next.EncounterSelfHits);
    }

    [Fact]
    public void DuplicateJoinsLeavesAndDisbandsAreHarmless()
    {
        var h = Fresh(); AddMember(h); h.Frame(Join()); h.Frame(Hit(100, 300));
        h.Frame(Leave()); h.Frame(Leave()); h.Frame(Disband()); h.Frame(Disband()); var m = h.Tick();
        Assert.Equal(100m, Remote(m).TotalDamage); Assert.Single(m.PartyRoster!.RecentIntervals);
    }

    [Fact]
    public void ReconnectNeverInheritsNameRuntimeIdOrInvite()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(100, 300)); var old = h.Tick();
        h.Handshake(9000, 19000); h.Bind("Next", 201); h.Frame(Hit(999, 300));
        var m = h.Tick(); Assert.NotEqual(old.EpochId, m.EpochId); Assert.Single(m.Members!);
        Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.Equal(0m, m.GroupTotalDamage);
        h.Frame(Join()); Assert.Single(h.Tick().Members!);
        AddMember(h, 301, "Remote"); Assert.Equal(301UL, Remote(h.Tick()).EntityId);
    }

    [Fact]
    public void PartyContinuesWhileSelfIsAfkAndUsesSharedDenominator()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(1000)); h.Tick();
        var start = Assert.Single(h.Events).DamageEvent.Provenance.CompletionTimestamp;
        h.Now = start.AddSeconds(20).AddMilliseconds(-10); h.Frame(Hit(200, 300)); h.Tick();
        h.Now = start.AddSeconds(40).AddMilliseconds(-10); h.Frame(Hit(200, 300)); var m = h.Tick();
        Assert.Equal("IN COMBAT", m.Status); Assert.Equal(1000m, m.TotalDamage);
        Assert.Equal(40, m.EncounterElapsedSeconds); Assert.Equal(25m, m.Dps); Assert.Equal(10m, Remote(m).Dps);
        Assert.Equal(1400m, m.GroupTotalDamage); Assert.Equal(100m, m.Members!.Sum(p => p.ContributionPercent));
    }

    [Fact]
    public void FormerMembersDamageCannotExtendGroupClock()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(400, 300)); var before = h.Tick(); h.Frame(Disband());
        h.Now = h.Now.AddSeconds(20); h.Frame(Hit(900, 300)); var after = h.Tick();
        Assert.Equal(before.EncounterElapsedSeconds, after.EncounterElapsedSeconds);
        h.Now = h.Now.AddSeconds(11); Assert.StartsWith("IDLE", h.Tick().Status);
    }

    [Fact]
    public void ChadSixAmountsAnd267UnrelatedSourcesDoNotLeakIntoGroup()
    {
        // Owned-capture regression values; anonymized synthetic identities and transport.
        var h = Fresh(); AddMember(h);
        foreach (var amount in new uint[] { 757, 761, 786, 774, 794, 762 }) h.Frame(Hit(amount, 300));
        for (uint id = 1000; id < 1267; id++) h.Frame(Hit(9999, id));
        var m = h.Tick(); Assert.Equal(273, m.OtherCount); Assert.Equal(4634m, Remote(m).TotalDamage);
        Assert.Equal(6, Remote(m).Hits); Assert.Equal(4634m, m.GroupTotalDamage); Assert.Equal(2, m.Members!.Count);
    }

    [Fact]
    public void AggregatedAmountCountsOnceAndUnsupported36DoesNotCount()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(500, 300, [5, 5])); h.Frame(Hit(1000, 300, category: 0x36));
        var m = h.Tick(); Assert.Equal(500m, Remote(m).TotalDamage); Assert.Equal(1, Remote(m).Hits);
        Assert.True(m.UnsupportedCandidates > 0); Assert.Contains("0x36 pending", m.Coverage);
    }

    [Fact]
    public void UnsupportedRosterAndMalformedJoinWithdrawEligibility()
    {
        var h = Fresh(); AddMember(h); h.Tick();
        h.Frame(Frame([0, 0x92, 0, .. new byte[24], 2])); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        AddMember(h); h.Tick();
        // Add a trailing byte and a correct outer frame, so the party shape, not transport, rejects it.
        h.Frame(Frame([0x0D, 0x92, .. Member(300, "Remote"), 9]));
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
    }

    [Fact]
    public void ConflictingIdentityInvalidatesPartyWithoutInventingAnotherMember()
    {
        var h = Fresh(); AddMember(h); h.Tick(); h.Frame(Identity(300, "Another")); h.Frame(Invite()); h.Frame(Join());
        Assert.Single(h.Tick().Members!); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
    }

    [Fact]
    public void CheckpointsRetainCompactRosterWithoutRawHistoryOrRepeatedAccounting()
    {
        var h = Fresh(); AddMember(h); h.Tick();
        for (var i = 0; i < 50; i++) { h.Frame(Hit(10, 300)); h.Tick(); }
        var m = h.Tick(); Assert.Equal(500m, Remote(m).TotalDamage); Assert.Equal(50, Remote(m).Hits);
        Assert.True(h.Pipeline.CheckpointCount > 50); Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities);
        Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); Assert.True(h.Pipeline.SelectedPacketCount <= 2);
        Assert.Single(m.PartyRoster!.ActiveMembers);
    }

    [Fact]
    public void PartyMembershipClosedIntervalsStayBoundedUnderChurn()
    {
        var h = Fresh(); h.Frame(Identity());
        for (var i = 0; i < 25; i++) { h.Frame(Invite()); h.Frame(Join()); h.Frame(Leave()); h.Tick(); }
        Assert.Equal(16, h.Tick().PartyRoster!.RecentIntervals.Count); Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
    }

    [Fact]
    public void MultipleRowsRankByDpsReuseObjectsAndKeepMeAtAnyRank()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(100)); h.Frame(Hit(500, 300)); var view = new OverlayViewModel();
        view.Apply(OverlaySnapshot.FromMeter(h.Tick())); Assert.Equal(2, view.Rows.Count);
        Assert.False(view.Rows[0].IsSelf); Assert.True(view.Rows[1].IsSelf); var old = view.Rows.ToArray();
        h.Frame(Hit(1000)); view.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        Assert.True(view.Rows[0].IsSelf); Assert.Same(old[1], view.Rows[0]); Assert.Same(old[0], view.Rows[1]);
        for (var i = 0; i < 5; i++) view.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        Assert.Same(old[1], view.Rows[0]); Assert.Equal(1, view.Rows[0].Rank); Assert.Equal(2, view.Rows[1].Rank);
    }

    [Fact]
    public void MidstreamCannotPromotePartyWithoutFreshIdentity()
    {
        var h = new Harness(); h.Initialize(); AddMember(h); h.Frame(Hit(400, 300));
        var m = h.Tick(); Assert.Equal(CurrentPlayerBindingStatus.Unknown, m.BindingStatus); Assert.Empty(m.Members!);
        Assert.Empty(OverlaySnapshot.FromMeter(m).Rows);
        h.Handshake(); h.Bind(); AddMember(h); h.Frame(Hit(200, 300)); Assert.Equal(200m, Remote(h.Tick()).TotalDamage);
    }

    [Fact]
    public void UnicodeNameUsesByteLengthAndVariableWidthId()
    {
        var h = Fresh(); AddMember(h, 50000, "Étranger"); h.Frame(Hit(100, 50000));
        Assert.Equal("Étranger", Remote(h.Tick()).CharacterName); Assert.Equal(50000UL, Remote(h.Tick()).EntityId);
    }

    [Fact]
    public void MystoganOwnedCaptureAmountsRespectBothIntervalsAndPreserveCompleteEvidence()
    {
        var h = Fresh(); AddMember(h); h.Frame(Hit(1228, 300)); h.Frame(Leave()); h.Frame(Hit(1284, 300));
        h.Frame(Invite()); h.Frame(Join()); h.Frame(Hit(1446, 300)); h.Frame(Disband());
        h.Frame(Hit(1230, 300)); h.Frame(Hit(246, 300)); var m = h.Tick();
        Assert.Equal(2674m, Remote(m).TotalDamage); Assert.Equal(2, Remote(m).Hits); Assert.Equal(5, m.OtherCount);
        Assert.Equal(new[] { "0092", "1392" }, m.PartyRoster!.RecentIntervals.Select(i => i.TerminationEvidence!.Tag));
        Assert.All(m.PartyRoster.RecentIntervals, i =>
        {
            Assert.Equal("4536", i.IdentityEvidence!.Tag); Assert.Equal("0892", i.InviteEvidence!.Tag);
            Assert.Equal("0D92", i.Evidence.Tag); Assert.Equal(m.EpochId, i.EpochId);
        });
    }

    [Fact]
    public void PartyCapacityFailureIsClosedAndNeverAddsAWorldActor()
    {
        var h = Fresh();
        for (uint i = 300; i < 306; i++) AddMember(h, i, "Member" + i);
        var m = h.Tick(); Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.Single(m.Members!);
        Assert.Contains("capacity", m.PartyRoster.Diagnostic);
    }

    [Fact]
    public void IdentityAndInvitationStateHasExplicitBounds()
    {
        var resolver = new PartyRosterResolver("synthetic");
        for (uint i = 1000; i < 5100; i++)
        {
            var bytes = Identity(i, "Person"); var prefix = Aion2Meter.Replay.Research.ApplicationFraming.Read(bytes).PrefixLength;
            var r = new Aion2Meter.Replay.Research.RawProtocolRecord((int)i, "synthetic", TrafficDirection.ServerToClient,
                i, (int)i, i, DateTimeOffset.UnixEpoch, i, i, DateTimeOffset.UnixEpoch, prefix, bytes.Length, bytes,
                "4536", [], "Unknown", []);
            resolver.Observe(r, 200, "Local", DateTimeOffset.UnixEpoch);
        }
        Assert.Equal(4096, resolver.RetainedIdentityCount); Assert.Empty(resolver.Snapshot().ActiveMembers);
        Assert.Contains("bound", resolver.Snapshot().Diagnostic);
    }
}
