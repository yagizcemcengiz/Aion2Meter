using System.Buffers.Binary;
using System.Text;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.PlayerProfileTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class CrossServerSelfProfileTests
{
    // Independently generated wire input. No public implementation/test code or fixture bytes copied.
    private static byte[] Local(ulong id, string name = "Synthetic Local", byte marker = 0x3F,
        ushort server = 1313, uint code = 29, byte faction = 1)
    {
        var b = Profile(code, id, name, true, marker, faction);
        var at = ApplicationFraming.Read(b).PrefixLength + 2 + Varint(id).Length + 6 + Encoding.UTF8.GetByteCount(name);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), server);
        return b;
    }
    private static Harness Start(bool checkpoints = true)
    { var h = new Harness(checkpoints: checkpoints); h.Initialize(); h.Handshake(); return h; }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactLogicalLiveHandoffRetainsRowAndCountsOnlyFutureValidatedActor(bool checkpoints)
    {
        var h = Start(checkpoints); h.Frame(Local(13143,"Yaaz",0x37)); h.Frame(Hit(100,actor:13143));
        var first = h.Tick(); var vm = new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(first));
        var row = Assert.Single(vm.Rows); Assert.Equal(PlayerClass.Cleric,row.Class);
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack); h.RemoteAddress = System.Net.IPAddress.Parse("203.0.113.73");
        h.Handshake(5000,15000,25002); var waiting = h.Tick();
        Assert.Null(waiting.EntityId); Assert.Equal("Yaaz",waiting.CharacterName);
        vm.Apply(OverlaySnapshot.FromMeter(waiting)); Assert.Same(row,Assert.Single(vm.Rows));
        // As in the live export, the new 1536 has multiple unrelated hypotheses, not a
        // unique actor/name declaration. It supplies no authority and does not veto 3F proof.
        var declaration=Frame([0x15,0x36,..BitConverter.GetBytes(900U),5,..Encoding.UTF8.GetBytes("Token"),
            ..BitConverter.GetBytes(901U),5,..Encoding.UTF8.GetBytes("Other")]);
        Assert.True(LocalInitializationExtractor.Extract(Record(declaration,"1536")).Count>1);
        h.Frame(declaration,port:25002);
        h.Frame(Hit(900,actor:14126),port:25002); h.Tick();
        h.Frame(Local(14126,"Yaaz"),port:25002); var bound = h.Tick();
        Assert.Equal(14126UL,bound.EntityId); Assert.Equal(CurrentPlayerBindingStatus.Resolved,bound.BindingStatus);
        Assert.True(first.StableIdentity!.SameProfileFacts(bound.StableIdentity!)); Assert.Equal(0m,bound.TotalDamage);
        h.Frame(Hit(999,actor:13143),port:25002); h.Frame(Hit(50,actor:14126),port:25002);
        var after = h.Tick(); Assert.Equal(50m,after.TotalDamage); Assert.Equal(1,after.EncounterSelfHits);
        vm.Apply(OverlaySnapshot.FromMeter(after)); Assert.Same(row,Assert.Single(vm.Rows)); Assert.Equal("50",row.Damage);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown,h.Events[1].Association.Status);
        Assert.Equal(DamageEventPlayerAssociationStatus.Other,h.Events[2].Association.Status);
        Assert.Equal(13143UL,h.Events[0].Association.BindingEntityId); // Old published provenance is immutable.
        var diagnostic = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t=>t.RecordTag=="3336");
        Assert.Equal("3336/3F",diagnostic.ProfileLayout!.LayoutType); Assert.Equal((byte)0x3F,diagnostic.ProfileLayout.Marker);
        Assert.Equal((ushort)1313,diagnostic.ProfileLayout.ServerField); Assert.Equal(29U,diagnostic.ProfileLayout.ClassCode);
        Assert.Contains(diagnostic.InitializationCandidates!,c=>c.Decision!.Contains("fixed 3336/3F"));
    }

    [Theory]
    [InlineData("world -> Abyss")] [InlineData("Abyss -> world")] [InlineData("world -> dungeon")]
    [InlineData("dungeon -> world")] [InlineData("town")] [InlineData("green quest")]
    [InlineData("channel change")] [InlineData("teleport")] [InlineData("backend handoff")]
    [InlineData("never observed future map")]
    public void AllSceneDescriptionsUseTheSameLateProfileAndRuntimeLifetime(string description)
    {
        Assert.NotEmpty(description); // Neither name nor map label enters the production trust decision.
        var h = Start(); h.Frame(Local(50000,marker:0x37)); h.Tick();
        h.Frame([..Hit(100,actor:50000),..Hit(900,actor:60000),..Local(60000),..Hit(50,actor:60000),..Hit(999,actor:50000)]);
        var after = h.Tick(); Assert.Equal(150m,after.TotalDamage); Assert.Equal(60000UL,after.EntityId);
        var adapter = h.Adapter(); var old = Assert.Single(adapter.Binding.PreviousBindings);
        Assert.Equal(adapter.Binding.ValidFrom,old.ValidUntil); Assert.NotNull(old.RetirementEvidence);
        Assert.Equal(DamageEventPlayerAssociationStatus.Other,h.Events[1].Association.Status);
        Assert.Equal(DamageEventPlayerAssociationStatus.Other,h.Events[^1].Association.Status);
        Assert.Equal(60000UL,h.Events[2].Association.BindingEntityId);
    }

    [Theory]
    [InlineData("NeverSeen Person Ω",1)] [InlineData("가상의 플레이어",2)] [InlineData("NewName_194",1)]
    public void FreshNonDefaultSceneCanBootstrapOnlyFromACompleteLocalCrossServerProfile(string name,byte faction)
    {
        var h = Start(); h.Frame(Hit(777,actor:50000)); h.Tick();
        h.Frame(Profile(29,50000,name)); Assert.Null(h.Tick().EntityId); // Remote 4536 cannot bootstrap.
        h.Frame(Local(50000,name,faction:faction)); var bound = h.Tick();
        Assert.Equal(name,bound.CharacterName); Assert.Equal(50000UL,bound.EntityId);
        Assert.Equal(PlayerClass.Cleric,bound.StableIdentity!.Class); Assert.Equal(0m,bound.TotalDamage);
        h.Frame(Hit(50,actor:50000)); Assert.Equal(50m,h.Tick().TotalDamage);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown,h.Events[0].Association.Status);
    }

    [Theory]
    [InlineData("zero-id")] [InlineData("wide-id")] [InlineData("noncanonical-id")]
    [InlineData("server")] [InlineData("class")] [InlineData("faction")] [InlineData("utf8")]
    [InlineData("name-length")] [InlineData("short-class")]
    public void Invalid3FNeverFallsBackToTextPairAuthority(string mutation)
    {
        ulong id = mutation=="zero-id"?0UL:mutation=="wide-id"?(ulong)uint.MaxValue+1:50000UL;
        var bytes = Local(id,server:mutation=="server"?(ushort)0:(ushort)1313,
            code:mutation=="class"?37U:29U,faction:mutation=="faction"?(byte)3:(byte)1);
        var prefix = ApplicationFraming.Read(bytes).PrefixLength; var nameAt = prefix+2+Varint(id).Length+5;
        if(mutation=="utf8")bytes[nameAt+1]=0xFF;
        if(mutation=="name-length")bytes[nameAt]=0;
        if(mutation=="short-class")bytes=Frame(bytes.AsSpan(prefix, nameAt-prefix+1+Encoding.UTF8.GetByteCount("Synthetic Local")+2+3).ToArray());
        if(mutation=="noncanonical-id")bytes=Frame([0x33,0x36,0xD0,0x86,0x83,0x00,0,0,0,0,0x3F,1,65,1,0,29,0,0,0,1]);
        Assert.Null(PlayerProfileDecoder.Decode(Record(bytes,"3336")));
        var h=Start(); h.Frame(Frame([0x15,0x36,..BitConverter.GetBytes((uint)id),15,..Encoding.UTF8.GetBytes("Synthetic Local")]));
        h.Frame(bytes); h.Frame(Hit(999,actor:50000)); Assert.Null(h.Tick().EntityId); Assert.Equal(0m,h.Tick().TotalDamage);
    }

    [Fact]
    public void PartialProfileAndUnackedCompleteProfileCannotAuthorizeUntilCompletion()
    {
        var h=Start(); var bytes=Local(50000); h.Frame(bytes[..12]); Assert.Null(h.Tick().EntityId);
        h.Frame(bytes[12..],acknowledge:false); Assert.Null(h.Tick().EntityId);
        h.Ack(); Assert.Equal(50000UL,h.Tick().EntityId); h.Frame(Hit(50,actor:50000)); Assert.Equal(50m,h.Tick().TotalDamage);
    }

    [Theory]
    [InlineData("name")] [InlineData("server")] [InlineData("class")] [InlineData("faction")]
    public void ConflictingStableFactsAcrossFreshCrossServerScopeCannotHijackSelf(string field)
    {
        var h=Start(); h.Frame(Local(50000,marker:0x37)); h.Tick(); h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack);
        h.Handshake(5000,15000,25002);
        h.Frame(Local(60000,field=="name"?"Different":"Synthetic Local",server:field=="server"?(ushort)1422:(ushort)1313,
            code:field=="class"?5U:29U,faction:field=="faction"?(byte)2:(byte)1),port:25002);
        h.Frame(Hit(999,actor:60000),port:25002);
        Assert.Null(h.Tick().EntityId); Assert.Equal(0m,h.Tick().TotalDamage);
        Assert.Contains(h.Meter.Diagnostics.Snapshot(h.Now).Transitions,t=>t.Reason.Contains("contradicts"));
    }

    [Fact]
    public void Repeated37To3FTo37And3FTo3FKeepFutureAccountingAndStableFacts()
    {
        var h=Start(); h.Frame(Local(50000,marker:0x37)); var stable=h.Tick().StableIdentity!;
        byte[] markers=[0x3F,0x3F,0x37,0x3F,0x37]; uint actor=50000;
        foreach(var marker in markers)
        {
            var old=actor; actor++; h.Frame(Hit(900,actor:actor)); h.Tick(); h.Frame(Local(actor,marker:marker));
            Assert.True(stable.SameProfileFacts(h.Tick().StableIdentity!)); h.Frame(Hit(10,actor:actor)); h.Frame(Hit(999,actor:old)); h.Tick();
        }
        Assert.Equal(50m,h.Tick().TotalDamage); Assert.Equal(5,h.Tick().EncounterSelfHits);
        Assert.Equal(0,h.Pipeline.RetainedPayloadBytes); Assert.Equal(1,h.Pipeline.BindingEvidenceCount);
    }

    [Fact]
    public void DuplicateNamesWithDifferentServerOrFactionConflictWithinTheSameEpoch()
    {
        var h=Start(); h.Frame([..Local(50000),..Local(60000,server:1422)]);
        Assert.Equal(CurrentPlayerBindingStatus.Conflict,h.Tick().BindingStatus); Assert.Null(h.Tick().EntityId);
    }

    [Fact]
    public void ProfileAuthorityCannotBeEstablishedByFirstDamageOrAnUnrelatedOther()
    {
        var h=Start(); h.Frame(Profile(29,50000,"Synthetic Local")); h.Frame(Hit(999,actor:50000));
        Assert.Null(h.Tick().EntityId); h.Frame(Local(60000)); h.Frame(Hit(999,actor:50000)); h.Frame(Hit(50,actor:60000));
        Assert.Equal(50m,h.Tick().TotalDamage); Assert.Equal(60000UL,h.Tick().EntityId);
    }

    [Fact]
    public void Combat36StaysUnsupportedAfterCrossServerRebind()
    {
        var h=Start(); h.Frame(Local(50000)); h.Frame(Hit(786,actor:50000,components:[15],category:0x36));
        Assert.Empty(h.Events); Assert.Equal(0m,h.Tick().TotalDamage); Assert.Equal(1,h.Tick().UnsupportedCandidates);
    }

    [Fact]
    public void CrossServerHeaderIsNotRemotePartyOrMembershipAuthority()
    {
        var r=Record(Local(50000),"3336"); var profile=Assert.IsType<PlayerClassEvidence>(PlayerProfileDecoder.Decode(r));
        Assert.Equal(PlayerClassSource.Character3336,profile.Source);
        var directory=new PlayerProfileDirectory("test"); directory.Observe(r);
        Assert.Null(directory.Get(50000,"Synthetic Local",false));
        var roster=new PartyRosterResolver("test"); roster.Observe(r,200,"Local",DateTimeOffset.UnixEpoch);
        Assert.Empty(roster.Snapshot().ActiveMembers); Assert.Empty(roster.Snapshot().Memberships!);
    }

    [Theory]
    [InlineData("Arbitrary Partner A","NeverCaptured B")]
    [InlineData("DuplicateName","DuplicateName")]
    public void PartyStableRowsSurviveLocal3FAndNeedIndependentCurrentMemberClaims(string a,string b)
    {
        var h=Start(); h.Frame(Local(50000,marker:0x37)); h.Tick();
        void Member(uint id,string name,string seed,uint code)
        {
            var uuid=Encoding.ASCII.GetBytes(new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0,16)).ToString("D"));
            var token=System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("token/"+seed))[..8];
            byte[] text=[(byte)Encoding.UTF8.GetByteCount(name),..Encoding.UTF8.GetBytes(name)];
            h.Frame(Frame([0x08,0x92,1,0,36,..uuid,..token,..Varint(id),..new byte[12],..text,..new byte[8]]));
            h.Frame(Frame([0x0D,0x92,0,2,..BitConverter.GetBytes(id),1,0,1,0,36,..uuid,..token,..text,..new byte[78]]));
            h.Frame(Profile(code,id,name));
        }
        Member(130001,a,"new-key-A",25); Member(130002,b,"new-key-B",14);
        var before=h.Tick(); var vm=new OverlayViewModel(); vm.Apply(OverlaySnapshot.FromMeter(before)); var rows=vm.Rows.ToArray();
        h.Frame(Local(60000)); vm.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        Assert.Equal(3,vm.Rows.Count); Assert.All(rows,row=>Assert.Contains(row,vm.Rows));
        h.Frame(Profile(25,140099,a)); h.Frame(Hit(999,actor:140099)); Assert.Equal(0m,h.Tick().GroupTotalDamage);
        Member(140001,a,"new-key-A",25); Member(140002,b,"new-key-B",14);
        h.Frame(Hit(50,actor:140001)); h.Frame(Hit(60,actor:140002)); h.Frame(Hit(999,actor:130001));
        var after=h.Tick(); Assert.Equal(110m,after.GroupTotalDamage); vm.Apply(OverlaySnapshot.FromMeter(after));
        Assert.All(rows,row=>Assert.Contains(row,vm.Rows));
        Assert.Equal(new ulong?[]{140001,140002},after.Members!.Where(m=>!m.IsSelf).Select(m=>m.EntityId).Order());
        Assert.Equal(before.StablePartyIdentities!.Select(p=>p.StableKey).Order(),after.StablePartyIdentities!.Select(p=>p.StableKey).Order());
    }

    [Fact]
    public void AmbiguousProfileArrivalCannotChooseALastCandidate()
    {
        var h=Start(checkpoints:false); h.Frame([..Local(50000),..Local(60000)]);
        var reader=new TcpResearchPacketReader();
        var connection=new TcpConnectionSelection(System.Net.IPAddress.Parse("192.0.2.5"),24001,System.Net.IPAddress.Parse("198.51.100.7"),13328);
        var packets=h.Inputs.Select(i=>reader.Read(i.Packet,i.PacketIndex)!).Select(s=>new ResearchPacket(s,connection.Direction(s)!.Value,0)).ToArray();
        var capture=new ResearchCapture("ambiguous",packets[0].Segment.TimestampUtc,"test",packets,0,0);
        var records=SharedProtocolPipeline.Decode(capture).Decoded.Records.ToArray();
        records[1]=records[1] with { StreamOffset=records[0].StreamOffset };
        var rejected=new ReplayCurrentPlayerBindingResolver().Resolve(capture,connection,records);
        Assert.Equal(CurrentPlayerBindingStatus.Unknown,rejected.Status); Assert.Null(rejected.EntityId);
        Assert.Contains(rejected.Diagnostics,d=>d.Contains("ambiguous",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedCrossServerRefreshRetiresRuntimeWithoutErasingValidatedClass()
    {
        var h=Start(); h.Frame(Local(50000,marker:0x37)); var original=h.Tick();
        h.Frame(Local(60000,code:37)); h.Frame(Hit(999,actor:60000)); var waiting=h.Tick();
        Assert.Null(waiting.EntityId); Assert.Equal(original.StableIdentity,waiting.StableIdentity);
        Assert.Equal(PlayerClass.Cleric,waiting.StableIdentity!.Class); Assert.Equal(0m,waiting.TotalDamage);
        h.Frame(Local(60000,code:30)); Assert.Equal(PlayerClass.Cleric,h.Tick().StableIdentity!.Class);
        h.Frame(Hit(50,actor:60000)); Assert.Equal(50m,h.Tick().TotalDamage);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown,h.Events[0].Association.Status);
    }

    [Fact]
    public void CrossServerLayoutDoesNotBypassMidstreamStartupTransportProof()
    {
        var h=new Harness(); h.Initialize(); h.Frame(Local(50000)); h.Frame(Hit(999,actor:50000));
        Assert.Null(h.Tick().EntityId); Assert.Empty(h.Events); Assert.Equal(0,h.Pipeline.RetainedPayloadBytes);
    }
}
