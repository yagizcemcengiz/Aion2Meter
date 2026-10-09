using System.Security.Cryptography;
using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class GenericPartyIdentityContinuityTests
{
    private static byte[] Text(string value) => [(byte)Encoding.UTF8.GetByteCount(value), .. Encoding.UTF8.GetBytes(value)];
    private static byte[] Uuid(string seed) => Encoding.ASCII.GetBytes(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0,16)).ToString("D"));
    private static byte[] Token(string seed) => SHA256.HashData(Encoding.UTF8.GetBytes("token/"+seed))[..8];
    private static byte[] Invite(uint id, string name, string seed) => Frame([0x08,0x92,1,0,36,..Uuid(seed),..Token(seed),..Varint(id),..new byte[12],..Text(name),..new byte[8]]);
    private static byte[] Join(uint id, string name, string seed) => Frame([0x0D,0x92,0,2,..BitConverter.GetBytes(id),1,0,1,0,36,..Uuid(seed),..Token(seed),..Text(name),..new byte[78]]);
    private static Harness Bound()
    {
        var h = new Harness(); h.Initialize(); h.Handshake();
        h.Frame(PlayerProfileTests.Profile(29,200,"Local",true)); h.Tick(); return h;
    }
    private static void Member(Harness h, uint id, string name, string seed, uint code = 25, ushort port = 24001)
    {
        h.Frame(Invite(id,name,seed),port:port); h.Frame(Join(id,name,seed),port:port);
        h.Frame(PlayerProfileTests.Profile(code,id,name),port:port);
    }
    private static LiveMeterMemberSnapshot Row(LiveMeterSnapshot m, string name) => Assert.Single(m.Members!,r=>!r.IsSelf&&r.CharacterName==name);

    [Theory]
    [InlineData("Unseen-Ω-47","Fresh-漢字-8")]
    [InlineData("Synthetic Person A","Synthetic Person B")]
    [InlineData("NeverCaptured_912","NotInCorpus_733")]
    public void ArbitraryValidatedPartyNamesRemainDuringPendingActorAndSameRowsResume(string a, string b)
    {
        var h = Bound(); Member(h,130001,a,"independent-A"); Member(h,130002,b,"independent-B",14);
        var initial = h.Tick(); var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(initial));
        var oldRows = vm.Rows.Where(r=>!r.IsSelf).ToDictionary(r=>r.DisplayName);
        Assert.Equal(2, initial.StablePartyIdentities!.Count);
        Assert.NotEqual(initial.StablePartyIdentities[0].MemberUuid, initial.StablePartyIdentities[1].MemberUuid);
        Assert.NotEqual(initial.StablePartyIdentities[0].OpaqueToken, initial.StablePartyIdentities[1].OpaqueToken);
        h.Frame(Frame([0x33,0x36,..Varint(201),5,..Encoding.UTF8.GetBytes("Local")]));
        var waiting = h.Tick(); Assert.Null(waiting.EntityId); Assert.Equal(3,waiting.Members!.Count);
        Assert.All(waiting.Members,m=>{Assert.Null(m.EntityId); Assert.Equal(0m,m.TotalDamage); Assert.Null(m.Dps);});
        vm.Apply(OverlaySnapshot.FromMeter(waiting));
        Assert.Same(oldRows[a],vm.Rows.Single(r=>r.DisplayName==a)); Assert.Same(oldRows[b],vm.Rows.Single(r=>r.DisplayName==b));
        h.Frame(Hit(900,actor:130001)); h.Frame(Hit(800,actor:130003)); h.Tick();
        h.Frame(PlayerProfileTests.Profile(29,201,"Local",true)); h.Tick();
        Member(h,140001,a,"independent-A"); Member(h,140002,b,"independent-B",14); h.Tick();
        h.Frame(Hit(50,actor:140001)); h.Frame(Hit(60,actor:140002)); h.Frame(Hit(999,actor:130001));
        var after = h.Tick(); Assert.Equal(110m,after.GroupTotalDamage); Assert.Equal(140001UL,Row(after,a).EntityId);
        vm.Apply(OverlaySnapshot.FromMeter(after));
        Assert.Same(oldRows[a],vm.Rows.Single(r=>r.DisplayName==a)); Assert.Same(oldRows[b],vm.Rows.Single(r=>r.DisplayName==b));
        Assert.Equal(50m,Row(after,a).TotalDamage); Assert.Equal(PlayerClass.Sorcerer,Row(after,a).Class);
    }

    [Fact]
    public void TransportCloseBeforeNewCandidateRetainsAllStableRowsWithoutCombatEligibility()
    {
        var h = Bound(); Member(h,130001,"UnknownPlayer-A","close-A"); Member(h,130002,"UnknownPlayer-B","close-B",14);
        h.Frame(Hit(50,actor:130001));
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        var rows = vm.Rows.Where(r=>!r.IsSelf).ToDictionary(r=>r.DisplayName);
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack);
        var waiting = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Unknown,waiting.BindingStatus);
        Assert.Null(waiting.EntityId); Assert.Equal(0m,waiting.GroupTotalDamage);
        Assert.Empty(waiting.PartyRoster!.ActiveMembers);
        Assert.Equal(3,waiting.Members!.Count);
        Assert.All(waiting.Members,m=>{ Assert.Null(m.EntityId); Assert.Equal(0m,m.TotalDamage); Assert.Null(m.Dps); });
        vm.Apply(OverlaySnapshot.FromMeter(waiting));
        Assert.All(rows,p=>Assert.Same(p.Value,vm.Rows.Single(r=>r.DisplayName==p.Key)));
    }

    [Fact]
    public void BackendHandoffRetainsOnlyStableMembershipThenRequiresCurrentScopeMembershipAndProfile()
    {
        var h = Bound(); Member(h,130001,"Unseen-Remote","persistent-uuid"); var first = h.Tick();
        var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(first)); var row = vm.Rows.Single(r=>!r.IsSelf);
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack);
        h.RemoteAddress = System.Net.IPAddress.Parse("203.0.113.91"); h.Handshake(5000,15000,25002);
        vm.Apply(OverlaySnapshot.FromMeter(h.Tick())); Assert.Same(row,vm.Rows.Single(r=>!r.IsSelf));
        Assert.Equal("0",row.Damage); Assert.Equal("—",row.Dps);
        h.Frame(PlayerProfileTests.Profile(29,201,"Local",true),port:25002);
        h.Frame(PlayerProfileTests.Profile(25,140001,"Unseen-Remote"),port:25002);
        h.Frame(Hit(999,actor:140001),port:25002); Assert.Equal(0m,h.Tick().GroupTotalDamage);
        Assert.Null(Row(h.Tick(),"Unseen-Remote").EntityId); // A name/profile alone cannot migrate membership.
        h.Frame(Invite(140001,"Unseen-Remote","persistent-uuid"),port:25002);
        h.Frame(Join(140001,"Unseen-Remote","persistent-uuid"),port:25002);
        h.Frame(Hit(70,actor:140001),port:25002); var bound = h.Tick(); Assert.Equal(70m,bound.GroupTotalDamage);
        vm.Apply(OverlaySnapshot.FromMeter(bound)); Assert.Same(row,vm.Rows.Single(r=>!r.IsSelf));
        Assert.Equal(140001UL,Row(bound,"Unseen-Remote").EntityId);
        Assert.Equal(2,bound.OtherCount); // Earlier uncertain Other was never backfilled.
    }

    [Fact]
    public void DuplicateNamesWithDifferentUuidTokenCannotBeHijackedByAConvenientProfile()
    {
        var h = Bound(); Member(h,130001,"DuplicateName","uuid-first"); Member(h,130002,"DuplicateName","uuid-second",14);
        var before = h.Tick(); Assert.Equal(3,before.Members!.Count);
        Assert.Equal(2,before.StablePartyIdentities!.Select(m=>m.StableKey).Distinct().Count());
        h.Frame(PlayerProfileTests.Profile(29,201,"Local",true)); h.Tick();
        h.Frame(PlayerProfileTests.Profile(25,140099,"DuplicateName")); h.Frame(Hit(999,actor:140099));
        var pending = h.Tick(); Assert.Equal(0m,pending.GroupTotalDamage);
        Assert.All(pending.Members!.Where(m=>!m.IsSelf),m=>Assert.Null(m.EntityId));
        Member(h,140001,"DuplicateName","uuid-first"); h.Frame(Hit(50,actor:140001));
        var rebound = h.Tick(); Assert.Equal(50m,rebound.GroupTotalDamage); Assert.Equal(3,rebound.Members!.Count);
        Assert.Single(rebound.Members,m=>!m.IsSelf&&m.EntityId is not null);
        Assert.Single(rebound.Members,m=>!m.IsSelf&&m.EntityId is null);
    }

    [Fact]
    public void AuthoritativeTerminationInNewScopeRemovesCarriedPresentation()
    {
        var h = Bound(); Member(h,130001,"Unseen-Remote","membership-one"); h.Tick();
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack); h.Handshake(5000,15000,25002);
        h.Frame(PlayerProfileTests.Profile(29,201,"Local",true),port:25002); Assert.Equal(2,h.Tick().Members!.Count);
        h.Frame(PartyMeterTests.Disband(),port:25002); Assert.Single(h.Tick().Members!); Assert.Empty(h.Tick().StablePartyIdentities!);
    }

    [Fact]
    public void FreshSavedSceneDiscoversUnknownMemberFromValidatedStatusAndIndependentProfile()
    {
        var h = Bound(); h.Frame(LivePartyStatusTests.Status(130001)); h.Frame(PlayerProfileTests.Profile(25,130001,"CompletelyNewPerson"));
        h.Frame(Hit(30,actor:130001)); h.Frame(Hit(999,actor:130002));
        var current = h.Tick(); Assert.Equal(30m,current.GroupTotalDamage); Assert.Equal(2,current.Members!.Count);
        Assert.Equal("CompletelyNewPerson",Assert.Single(current.StablePartyIdentities!).CharacterName);
        Assert.Equal("1B92",current.StablePartyIdentities![0].MembershipEvidence.Tag);
        Assert.Equal(130001UL,Row(current,"CompletelyNewPerson").EntityId);
    }
}
