using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;
using static Aion2Meter.Tests.PartyMeterTests;

namespace Aion2Meter.Tests;

public sealed class LivePartyStatusTests
{
    internal static byte[] Status(ulong id = 300, ulong current = 500, ulong maximum = 1000, byte end = 1) =>
        Frame([0x1B, 0x92, .. Varint(id), .. Varint(current), .. Varint(maximum), .. new byte[24], end]);
    private static LiveMeterMemberSnapshot Remote(LiveMeterSnapshot m) => Assert.Single(m.Members!, p => !p.IsSelf);

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void EarlyStatusBeforeRosterShowsUnresolvedRowAndLateIdentityReusesItWithoutBackfill(bool checkpoints)
    {
        var h = Fresh(checkpoints: checkpoints); h.Frame(Status()); var early = h.Tick();
        var row = Remote(early); Assert.Null(row.EntityId); Assert.Null(row.Dps); Assert.Equal(0m, row.TotalDamage);
        Assert.Equal("Party member (identity pending)", row.CharacterName);
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(early)); var objectBefore = vm.Rows.Single(r => !r.IsSelf);
        h.Frame(Hit(999, 300)); h.Tick(); h.Frame(Identity()); h.Frame(Hit(100, 300)); h.Frame(Hit(999, 777));
        var final = h.Tick(); vm.Apply(OverlaySnapshot.FromMeter(final));
        Assert.Same(objectBefore, vm.Rows.Single(r => !r.IsSelf)); Assert.Equal(row.MembershipKey, Remote(final).MembershipKey);
        Assert.Equal(300UL, Remote(final).EntityId); Assert.Equal("Remote", Remote(final).CharacterName);
        Assert.Equal(100m, final.GroupTotalDamage); Assert.Equal(1, Remote(final).Hits); Assert.Equal(3, final.OtherCount);
        Assert.DoesNotContain(final.Members!, m => m.EntityId == 777);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FarLiveAcceptedJoinIsNamedBeforeProfileWithInviteOrIndependentStatus(bool invite)
    {
        var h = Fresh();
        if (invite) h.Frame(Invite());
        h.Frame(Join());
        if (!invite) { Assert.Single(h.Tick().Members!); h.Frame(Status()); }
        var early = Remote(h.Tick()); Assert.Equal("Remote", early.CharacterName); Assert.Null(early.EntityId);
        h.Frame(Hit(777, 300)); h.Frame(Identity()); h.Frame(Hit(200, 300));
        var late = Remote(h.Tick()); Assert.Equal(early.MembershipKey, late.MembershipKey); Assert.Equal(200m, late.TotalDamage);
    }

    [Fact]
    public void StatusAloneDoesNotInventNameOrBindAnotherIdWithSameName()
    {
        var h = Fresh(); h.Frame(Status()); h.Frame(Identity(301)); h.Frame(Hit(999, 301));
        Assert.Null(Remote(h.Tick()).EntityId); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Invite()); h.Frame(Join()); h.Frame(Identity(300, "Wrong")); h.Frame(Hit(999, 300));
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Equal(0m, h.Tick().GroupTotalDamage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TerminationSuppressesDelayedStatusUntilExplicitRejoin(bool disband)
    {
        var h = Fresh(); h.Frame(Status()); h.Frame(Identity()); h.Tick();
        h.Frame(disband ? Disband() : Leave()); h.Frame(Status()); h.Frame(Identity()); h.Frame(Hit(999, 300));
        Assert.Single(h.Tick().Members!); Assert.Equal(0m, h.Tick().GroupTotalDamage);
        h.Frame(Invite()); h.Frame(Join()); h.Frame(Hit(200, 300)); Assert.Equal(200m, Remote(h.Tick()).TotalDamage);
    }

    [Fact]
    public void EarlyStatusCanWaitForFreshBindingButDoesNotCrossEpoch()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Status()); h.Frame(Identity());
        Assert.Empty(h.Tick().Members!); h.Bind(); Assert.Equal(2, h.Tick().Members!.Count);
        h.Handshake(9000, 19000); h.Bind("Next", 201); h.Frame(Hit(999, 300));
        Assert.Single(h.Tick().Members!); Assert.Equal(0m, h.Tick().GroupTotalDamage);
    }

    [Theory]
    [InlineData("zero")] [InlineData("maximum")] [InlineData("over")] [InlineData("end")]
    [InlineData("truncated")] [InlineData("extra")] [InlineData("noncanonical")]
    public void UnvalidatedStatusFormsNeverAuthorizeParty(string mutation)
    {
        var h = Fresh(); h.Frame(Identity());
        var bytes = mutation switch
        {
            "zero" => Status(0), "maximum" => Status(maximum: 0), "over" => Status(current: 1001),
            "end" => Status(end: 2), "truncated" => Frame([0x1B, 0x92, 1, 1, 1, .. new byte[24]]),
            "extra" => Frame([0x1B, 0x92, 1, 1, 1, .. new byte[26]]),
            _ => Frame([0x1B, 0x92, 0xAC, 0x82, 0, 1, 1, .. new byte[25]])
        };
        h.Frame(bytes); h.Frame(Hit(999, 300)); Assert.Single(h.Tick().Members!); Assert.Equal(0m, h.Tick().GroupTotalDamage);
    }

    [Fact]
    public void OutboundStatusAndSelfStatusDoNotGrantRemoteMembership()
    {
        var h = Fresh(); var b = Status(); h.Add(b, server: false); h.ClientSequence += (uint)b.Length;
        h.Add([], flags: TcpFlags.Ack); h.Frame(Status(200)); h.Frame(Identity()); h.Frame(Hit(999, 300));
        Assert.Single(h.Tick().Members!); Assert.Equal(0m, h.Tick().GroupTotalDamage);
    }

    [Fact]
    public void RepeatedStatusIsCompactCheckpointSafeAndDoesNotRewriteValidity()
    {
        var h = Fresh(); h.Frame(Status()); h.Frame(Identity()); var before = h.Tick();
        for (var i = 0; i < 30; i++) { h.Frame(Status(current: (ulong)i)); h.Tick(); }
        var after = h.Tick(); Assert.Equal(1, after.PartyRoster!.Proofs!.RetainedStatuses);
        Assert.Equal(31, after.PartyRoster.Proofs.StatusRecords); Assert.Equal(8, after.PartyRoster.Proofs.RecentTransitions.Count);
        Assert.Equal(before.PartyRoster!.ActiveMembers[0].ValidFrom, after.PartyRoster.ActiveMembers[0].ValidFrom);
        Assert.Equal(0, h.Feed.RetainedPartyRecordIdentities); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        h.Meter.ResetCurrent(h.Now); Assert.Equal(2, h.Tick().Members!.Count);
    }

    [Fact]
    public void ExcessStatusMembersFailClosedRatherThanEvictingEvidence()
    {
        var h = Fresh(); for (uint id = 300; id < 306; id++) h.Frame(Status(id));
        Assert.Empty(h.Tick().PartyRoster!.ActiveMembers); Assert.Single(h.Tick().Members!);
        h.Frame(Status(300)); h.Frame(Identity()); Assert.Single(h.Tick().Members!);
    }

    [Fact]
    public void FreshLiveJoinAndStatusCanFollowSoloReplacementWithoutAnInboundInvite()
    {
        var h = Fresh(); h.Frame(Leave()); h.Tick(); h.Frame(Status()); Assert.Single(h.Tick().Members!);
        h.Frame(Join()); h.Frame(Status()); var pending = Remote(h.Tick());
        Assert.Equal("Remote", pending.CharacterName); Assert.Null(pending.EntityId);
        h.Frame(Identity()); h.Frame(Hit(200, 300)); Assert.Equal(200m, Remote(h.Tick()).TotalDamage);
        h.Frame(Disband()); h.Frame(Status()); h.Frame(Hit(999, 300));
        Assert.Equal(200m, Remote(h.Tick()).TotalDamage); Assert.False(Remote(h.Tick()).ActivePartyMember);
    }

    [Fact]
    public void LaterStatusProofDoesNotAuthorizeFrameWhoseFirstBytePrecedesIt()
    {
        var resolver = new PartyRosterResolver("test");
        resolver.Observe(PlayerProfileTests.Record(Join(), "0D92", ordinal: 1), 200, "Local", DateTimeOffset.UnixEpoch);
        resolver.Observe(PlayerProfileTests.Record(Identity(), ordinal: 2), 200, "Local", DateTimeOffset.UnixEpoch);
        resolver.Observe(PlayerProfileTests.Record(Status(), "1B92", ordinal: 3), 200, "Local", DateTimeOffset.UnixEpoch);
        var member = Assert.Single(resolver.Snapshot().ActiveMembers);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(3), member.ValidFrom);
        Assert.Null(resolver.Eligible(300, DateTimeOffset.UnixEpoch.AddSeconds(2), DateTimeOffset.UnixEpoch.AddSeconds(4)));
        Assert.NotNull(resolver.Eligible(300, member.ValidFrom, DateTimeOffset.UnixEpoch.AddSeconds(4)));
        Assert.Equal("0D92", member.Evidence.Tag); Assert.Equal("1B92", member.StatusEvidence!.Tag);

    }
}
