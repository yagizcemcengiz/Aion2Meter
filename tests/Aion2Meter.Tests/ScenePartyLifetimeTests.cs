using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;
using static Aion2Meter.Tests.PartyMeterTests;

namespace Aion2Meter.Tests;

public sealed class ScenePartyLifetimeTests
{
    internal static byte[] Refresh() => Frame([0x33, 0x36, .. Varint(200), 5, .. Encoding.UTF8.GetBytes("Local")]);
    internal static byte[] Control(byte tag = 0x2F) => Frame([tag, 0x92, 1, 2, 3, 0]);
    private static void Member(Harness h, uint id, string name, uint code = 25)
    { h.Frame(Invite(id, name)); h.Frame(Join(id, name)); h.Frame(PlayerProfileTests.Profile(code, id, name)); }
    private static Harness Three()
    {
        var h = Fresh(); h.Tick(); Member(h, 300, "Alpha"); Member(h, 301, "Beta", 14);
        Assert.Equal(3, h.Tick().Members!.Count); return h;
    }
    private static LiveMeterMemberSnapshot Alpha(LiveMeterSnapshot m) => Assert.Single(m.Members!, r => r.CharacterName == "Alpha");

    [Fact]
    public void EnterAndExitRetainThreeMembersButRetireRuntimeAndStableClassSurvives()
    {
        var h = Three(); var before = h.Tick(); var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(before));
        var rows = vm.Rows.ToArray(); var key = Alpha(before).MembershipKey;
        h.Frame(Refresh()); var entered = h.Tick(); Assert.Equal(3, entered.Members!.Count);
        Assert.Empty(entered.PartyRoster!.ActiveMembers); Assert.All(entered.PartyRoster.Memberships!, m => Assert.Null(m.CurrentRuntimeEntityId));
        Assert.Equal(PlayerClass.Sorcerer, Alpha(entered).Class); Assert.Equal(PlayerClass.Ranger, entered.Members.Single(m => m.CharacterName == "Beta").Class);
        h.Frame(Hit(999, 300)); h.Frame(Hit(999, 999)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Control()); h.Frame(Refresh()); var exited = h.Tick(); Assert.Equal(3, exited.Members!.Count);
        Assert.Equal(key, Alpha(exited).MembershipKey); Assert.Null(Alpha(exited).EntityId);
        vm.Apply(OverlaySnapshot.FromMeter(exited)); Assert.All(rows, r => Assert.Contains(vm.Rows, x => ReferenceEquals(x, r)));
        Assert.Single(exited.Members, m => m.IsSelf); Assert.DoesNotContain(exited.Members, m => m.EntityId == 999);
    }

    [Fact]
    public void NewIdRequiresStableClaimPlusFreshProfileKeepsSameRowAndOnlyFutureHitsCount()
    {
        var h = Three(); h.Frame(Hit(100, 300)); var before = h.Tick();
        var key = Alpha(before).MembershipKey; var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(before));
        var row = vm.Rows.Single(r => r.DisplayName == "Alpha");
        h.Frame(Control()); h.Frame(Hit(999, 300)); h.Tick();
        h.Frame(PlayerProfileTests.Profile(25, 400, "Alpha")); h.Frame(Hit(999, 400));
        Assert.Null(Alpha(h.Tick()).EntityId); // A matching name alone is not a stable identity relation.
        h.Frame(Invite(400, "Alpha")); h.Frame(Join(400, "Alpha"));
        h.Frame(Hit(200, 400)); h.Frame(Hit(999, 300)); h.Frame(Hit(999, 777)); var after = h.Tick();
        Assert.Equal(400UL, Alpha(after).EntityId); Assert.Equal(key, Alpha(after).MembershipKey);
        Assert.Equal(300m, Alpha(after).TotalDamage); Assert.Equal(2, Alpha(after).Hits);
        Assert.Equal(3, after.Members!.Count); Assert.Equal(PlayerClass.Sorcerer, Alpha(after).Class);
        vm.Apply(OverlaySnapshot.FromMeter(after)); Assert.Same(row, vm.Rows.Single(r => r.DisplayName == "Alpha"));
        Assert.DoesNotContain(after.Members, m => m.EntityId is 300 or 777);
    }

    [Fact]
    public void ProfileAfterUpdatedClaimRebindsWithoutCountingEarlierNewActorEvents()
    {
        var h = Three(); h.Frame(Control()); h.Tick();
        h.Frame(Invite(400, "Alpha")); h.Frame(Join(400, "Alpha")); h.Frame(Hit(999, 400));
        Assert.Null(Alpha(h.Tick()).EntityId); h.Frame(PlayerProfileTests.Profile(25, 400, "Alpha"));
        h.Frame(Hit(80, 400)); Assert.Equal(80m, Alpha(h.Tick()).TotalDamage);
    }

    [Fact]
    public void FirstByteBeforeFreshValidityAndRetiredIdAreNeverEligible()
    {
        var resolver = new PartyRosterResolver("test"); var from = DateTimeOffset.UnixEpoch;
        resolver.Observe(PlayerProfileTests.Record(Invite(), "0892", ordinal: 1), 200, "Local", from);
        resolver.Observe(PlayerProfileTests.Record(Join(), "0D92", ordinal: 2), 200, "Local", from);
        resolver.Observe(PlayerProfileTests.Record(Identity(), ordinal: 3), 200, "Local", from);
        resolver.RetireRuntime(new("test", "3336", 0, 0, 4, from.AddSeconds(4), "hash"), "scene");
        var retired = Assert.Single(resolver.Snapshot().RecentIntervals); Assert.Equal(from.AddSeconds(4), retired.ValidUntil);
        Assert.Null(resolver.Eligible(300, from.AddSeconds(4), from.AddSeconds(4)));
        resolver.Observe(PlayerProfileTests.Record(Invite(400), "0892", ordinal: 5), 200, "Local", from);
        resolver.Observe(PlayerProfileTests.Record(Join(400), "0D92", ordinal: 6), 200, "Local", from);
        resolver.Observe(PlayerProfileTests.Record(Identity(400), ordinal: 7), 200, "Local", from);
        var actor = Assert.Single(resolver.Snapshot().ActiveMembers); Assert.Equal(from.AddSeconds(7), actor.ValidFrom);
        Assert.Null(resolver.Eligible(400, from.AddSeconds(6), from.AddSeconds(8)));
        Assert.NotNull(resolver.Eligible(400, from.AddSeconds(7), from.AddSeconds(8)));
        Assert.Null(resolver.Eligible(300, from.AddSeconds(8), from.AddSeconds(8)));
    }

    [Theory]
    [InlineData(0x21)] [InlineData(0x2F)]
    public void UnknownControlWithdrawsRuntimeButCannotClaimPartyTermination(int tag)
    {
        var h = Three(); h.Frame(Control((byte)tag)); h.Frame(Hit(999, 300)); var m = h.Tick();
        Assert.Equal(3, m.Members!.Count); Assert.Empty(m.PartyRoster!.ActiveMembers); Assert.Equal(0m, m.GroupTotalDamage);
        h.Frame(Identity(300, "Alpha"));
        if (tag == 0x21)
        {
            h.Frame(Hit(999, 300)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
            h.Frame(Invite(300, "Alpha")); h.Frame(Join(300, "Alpha"));
        }
        h.Frame(Hit(50, 300)); Assert.Equal(50m, Alpha(h.Tick()).TotalDamage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualSelfOnlyLeaveAndDisbandStillRemoveMembershipAndBlockDelayedStatus(bool disband)
    {
        var h = Three(); h.Frame(Refresh()); h.Tick(); h.Frame(disband ? Disband() : Leave());
        h.Frame(LivePartyStatusTests.Status(300)); h.Frame(Identity(300, "Alpha")); h.Frame(Hit(999, 300));
        var ended = h.Tick(); Assert.Empty(ended.PartyRoster!.Memberships!); Assert.Single(ended.Members!);
        Assert.Equal(0m, ended.GroupTotalDamage);
        Member(h, 400, "Alpha"); h.Frame(Hit(60, 400)); Assert.Equal(60m, Alpha(h.Tick()).TotalDamage);
    }

    [Fact]
    public void ResetOnlyClearsCombatAndUnknownOtherDoesNotExtendEncounter()
    {
        var h = Three(); h.Frame(Hit(80, 300)); h.Frame(Hit(100)); var before = h.Tick();
        h.Now = h.Now.AddSeconds(10); h.Frame(Hit(999, 777)); var after = h.Tick();
        Assert.Equal(before.GroupTotalDamage, after.GroupTotalDamage); Assert.Equal(before.EncounterElapsedSeconds, after.EncounterElapsedSeconds);
        h.Meter.ResetCurrent(h.Now); var reset = h.Tick(); Assert.Equal(3, reset.Members!.Count);
        Assert.Equal(2, reset.PartyRoster!.ActiveMembers.Count); Assert.Equal(0m, reset.GroupTotalDamage);
    }

    [Fact]
    public void RepeatedEnterExitAndActorRebindStayBoundedWithoutDuplicateSelfOrMembers()
    {
        var h = Three(); var key = Alpha(h.Tick()).MembershipKey;
        for (var i = 0; i < 300; i++)
        {
            h.Frame(Refresh()); h.Tick(); h.Frame(Control()); h.Tick();
            h.Frame(Identity(300, "Alpha")); h.Frame(Identity(301, "Beta")); h.Frame(Hit(1, 300));
            var m = h.Tick(); Assert.Equal(3, m.Members!.Count); Assert.Single(m.Members, r => r.IsSelf);
            Assert.Equal(key, Alpha(m).MembershipKey); Assert.Equal(i + 1, Alpha(m).TotalDamage);
            Assert.InRange(m.PartyRoster!.RecentIntervals.Count, 0, 16); Assert.InRange(m.PartyRoster.Proofs!.IndependentIdentities, 0, 2);
            Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        }
        Assert.Equal(LiveDiagnosticBuffer.Capacity, h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Count);
    }

    [Fact]
    public void SameNameDifferentUuidCannotInheritStableClassOrCombatRow()
    {
        var h = Three(); var before = Alpha(h.Tick()); h.Frame(Control()); h.Tick();
        var join = Join(400, "Alpha"); var frame = Aion2Meter.Replay.Research.ApplicationFraming.Read(join);
        join[frame.PrefixLength + 2 + 11] = (byte)'9'; // Syntactically valid different UUID, unmatched invite.
        h.Frame(Invite(400, "Alpha")); h.Frame(join); h.Frame(Identity(400, "Alpha")); h.Frame(Hit(999, 400));
        Assert.Equal(0m, h.Tick().GroupTotalDamage); Assert.DoesNotContain(h.Tick().Members!, m => m.EntityId == 400);
        Assert.NotNull(before.ClassEvidence);
    }

    [Fact]
    public void FreshTcpRetainsPartyPresentationWithoutRuntimeAuthorityAndExportsSuspension()
    {
        var h = Three(); h.Handshake(9000, 19000); h.Bind(); h.Frame(Identity(300, "Alpha")); h.Frame(Hit(999, 300));
        Assert.Equal(3, h.Tick().Members!.Count); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        Assert.All(h.Tick().Members!.Where(m=>!m.IsSelf), m=>Assert.Null(m.EntityId));
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers);
        Assert.Contains(h.Meter.Diagnostics.Snapshot(h.Now).Transitions, t => t.Kind == "EpochEnded" && t.MembersAfter == 2 && t.After.All(m => m.RuntimeId is null));
    }

    private static byte[] FullRoster()
    {
        static byte[] Entry(uint id, string name, bool first)
        {
            var join = Join(id, name); var prefix = Aion2Meter.Replay.Research.ApplicationFraming.Read(join).PrefixLength;
            var member = join[(prefix + 2)..];
            if (!first) return member[..^1];
            member[1] = 1; member[47] = 1;
            member[^22] = 0x3F; return member;
        }
        return Frame([0, 0x92, 8, .. new byte[24], 5, .. new byte[120], 2,
            .. Entry(200, "Local", true), .. Entry(300, "Alpha", false), 5]);
    }

    [Fact]
    public void RealFullReplacementExcludingBetaTerminatesBetaEvenAfterSceneRetirement()
    {
        var h = Three(); h.Frame(Control()); h.Tick(); h.Frame(FullRoster());
        var replaced = h.Tick(); Assert.Equal(2, replaced.Members!.Count);
        Assert.DoesNotContain(replaced.PartyRoster!.Memberships!, m => m.CharacterName == "Beta");
        h.Frame(Identity(301, "Beta")); h.Frame(Hit(999, 301)); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Identity(300, "Alpha")); h.Frame(Hit(40, 300)); Assert.Equal(40m, Alpha(h.Tick()).TotalDamage);
    }

    [Fact]
    public void NewLiveJoinDoesNotDeleteFarMemberFromEarlierAuthoritativeRoster()
    {
        var h = Fresh(); h.Frame(FullRoster()); Assert.Null(Alpha(h.Tick()).EntityId);
        Member(h, 301, "Beta", 14); var joined = h.Tick(); Assert.Equal(3, joined.Members!.Count);
        Assert.Null(Alpha(joined).EntityId); h.Frame(Identity(300, "Alpha")); h.Frame(Hit(30, 300));
        Assert.Equal(30m, Alpha(h.Tick()).TotalDamage); Assert.Equal(3, h.Tick().Members!.Count);
    }

    [Fact]
    public void DelayedProfileOrClaimWhoseFirstBytePredatesSceneCannotRestoreRuntime()
    {
        var roster = new PartyRosterResolver("test"); var from = DateTimeOffset.UnixEpoch;
        roster.Observe(PlayerProfileTests.Record(Invite(), "0892", ordinal: 1), 200, "Local", from);
        roster.Observe(PlayerProfileTests.Record(Join(), "0D92", ordinal: 2), 200, "Local", from);
        roster.Observe(PlayerProfileTests.Record(Identity(), ordinal: 3), 200, "Local", from);
        roster.RetireRuntime(new("test", "3336", 0, 0, 4, from.AddSeconds(4), "hash"), "scene");
        roster.Observe(PlayerProfileTests.Record(Identity(), ordinal: 5) with { TimestampUtc = from.AddSeconds(3) }, 200, "Local", from);
        Assert.Empty(roster.Snapshot().ActiveMembers);
        roster.Observe(PlayerProfileTests.Record(Invite(400), "0892", ordinal: 6), 200, "Local", from);
        roster.Observe(PlayerProfileTests.Record(Join(400), "0D92", ordinal: 7) with { TimestampUtc = from.AddSeconds(3) }, 200, "Local", from);
        roster.Observe(PlayerProfileTests.Record(Identity(400), ordinal: 8), 200, "Local", from);
        Assert.Empty(roster.Snapshot().ActiveMembers); Assert.Equal(300UL, Assert.Single(roster.Snapshot().Memberships!).RosterEntityIdCandidate);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RebindingAtRosterCapacityReplacesClaimWithoutExhaustingOrDuplicatingIt(bool scene)
    {
        var h = Three(); Member(h, 302, "Gamma"); Member(h, 303, "Delta"); Member(h, 304, "Epsilon");
        var key = Alpha(h.Tick()).MembershipKey; Assert.Equal(5, h.Tick().PartyRoster!.Memberships!.Count);
        if (scene) { h.Frame(Refresh()); h.Tick(); }
        Member(h, 400, "Alpha"); h.Frame(Hit(70, 400)); var m = h.Tick();
        Assert.Equal(6, m.Members!.Count); Assert.Equal(5, m.PartyRoster!.Memberships!.Count);
        Assert.Equal(key, Alpha(m).MembershipKey); Assert.Equal(400UL, Alpha(m).EntityId); Assert.Equal(70m, Alpha(m).TotalDamage);
    }
}
