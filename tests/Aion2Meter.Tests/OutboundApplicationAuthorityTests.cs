using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class OutboundApplicationAuthorityTests
{
    private static void Outbound(Harness h, byte[] bytes, bool ack = true)
    {
        h.Add(bytes, server: false); h.ClientSequence += (uint)bytes.Length;
        if (ack) h.Add([], flags: TcpFlags.Ack);
    }
    private static byte[] SizedFrame(int length)
    {
        for (var width = 1; width <= 5; width++)
        {
            var bytes = Frame([0x90,0x91,..new byte[length-width-2]]);
            if (bytes.Length == length) return bytes;
        }
        throw new ArgumentException("No canonical frame at requested size.");
    }
    private static byte[] Profile(uint id, byte marker = 0x3F)
    {
        var bytes = PlayerProfileTests.Profile(29,id,"Yaaz",true,marker);
        var serverAt = ApplicationFraming.Read(bytes).PrefixLength+2+Varint(id).Length+6+4;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(serverAt),1313);
        return bytes;
    }
    private static ProtocolDecodeResult Decode(byte[] bytes, TrafficDirection direction, bool product,
        ProtocolDecodeLimits? limits = null, long offset = 0)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var stream = new ReassembledStream(direction,100,[new(offset,bytes,now,1)],[],[],0,0,offset+bytes.Length,false);
        return new ReplayProtocolDecoder(limits,product).Decode("synthetic",[stream],now);
    }

    [Theory]
    [InlineData(0U)] [InlineData(2097153U)] [InlineData(uint.MaxValue)]
    public void ExactLogicalLiveFailureSeparatesOutboundBodyFromHealthy3FAuthority(uint declared)
    {
        // No raw wire bytes exist in the diagnostic export. These independent fixtures
        // reproduce its size, offsets, direction and failing predicate, not its unknown body.
        var h = new Harness(); h.Initialize(); h.Handshake();
        h.Frame(Profile(3288,0x37)); h.Frame(Hit(10,actor:3288)); h.Tick();
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack); h.Handshake(5000,15000);
        h.Frame(Profile(14281)); Assert.Equal(14281UL,h.Tick().EntityId);
        Outbound(h,SizedFrame(24354)); h.Tick();
        var candidate = Container([0,0,0,0],declared); Assert.Equal(11,candidate.Length);
        var old = Decode(candidate,TrafficDirection.ClientToServer,false,offset:24540);
        var failed = Assert.Single(old.Records); Assert.Equal("FailedContainer",failed.DecodeStatus);
        Assert.Equal(24540,failed.OuterFrameOffset); Assert.Contains("outside bound",Assert.Single(failed.DecodeWarnings));
        Outbound(h,[..SizedFrame(186),..candidate,..SizedFrame(95)]);
        h.Frame(Hit(20,actor:14281)); var after = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Resolved,after.BindingStatus); Assert.Equal(14281UL,after.EntityId);
        Assert.Equal(20m,after.TotalDamage); Assert.Equal("Yaaz",after.CharacterName);
        var state = Assert.Single(h.Pipeline.Snapshot().Single(e=>e.Lifecycle=="Active").CheckpointStates!,s=>s.Action=="IgnoreNonAuthoritative");
        Assert.Equal(24354,state.CheckpointOffset); Assert.Equal(24540,state.FrameOffset); Assert.Equal(292,state.UnpublishedBytes);
        Assert.Equal(11,state.ExpectedFrameBytes); Assert.Equal(11,state.AvailableFrameBytes);
        Assert.Equal(1,state.ExpectedPrefixBytes); Assert.False(state.ApplicationAuthoritative);
        Assert.Equal(declared,state.DeclaredDecompressedSize); Assert.Equal("OutboundFFFFUnclassified",state.CompressionDiscriminator);
        Assert.Equal(64,state.OuterFrameSha256!.Length); Assert.Equal(0,h.Pipeline.RetainedPayloadBytes);
        h.Frame(Hit(30,actor:14281)); h.Tick(); h.Ack(); Assert.Equal(50m,h.Tick().TotalDamage);
        Assert.Equal(3,h.Feed.PublishedCount); Assert.Equal(3,h.Events.Select(e=>e.DamageEvent.Identity).Distinct().Count());
        Assert.DoesNotContain("RawBytes",h.Meter.Diagnostics.ToJson(h.Now));
    }

    [Theory]
    [InlineData("plain")] [InlineData("valid-container")] [InlineData("invalid-container")]
    [InlineData("partial-container-header")] [InlineData("combat")] [InlineData("profile")]
    public void OutboundApplicationBodiesCannotCreateEventsOrReplaceSelf(string kind)
    {
        var h = Fresh(); h.Tick();
        var bytes = kind switch {
            "plain"=>SizedFrame(11), "valid-container"=>Pack(Hit(999)),
            "invalid-container"=>Container([0xFF,0xFF,0xFF,0xFF],7),
            "partial-container-header"=>Frame([255,255,1]), "combat"=>Hit(999), "profile"=>Profile(14281),
            _=>throw new ArgumentException(kind)
        };
        var decoded = Decode(bytes,TrafficDirection.ClientToServer,true);
        Assert.Equal("NonAuthoritativeFrame",Assert.Single(decoded.Records).DecodeStatus);
        Assert.Empty(decoded.CombatCandidates); Assert.Empty(decoded.Containers);
        Outbound(h,bytes); Assert.Equal(200UL,h.Tick().EntityId); Assert.Equal(0m,h.Tick().TotalDamage);
        h.Frame(Hit(50)); Assert.Equal(50m,h.Tick().TotalDamage); Assert.Single(h.Events);
        Assert.Equal(0,h.Pipeline.RetainedPayloadBytes);
    }

    [Theory]
    [InlineData("plain")] [InlineData("compressed")]
    public void InboundValidFramesStillPublishExactlyOnce(string kind)
    {
        var h=Fresh(); h.Tick(); var hit=Hit(70);
        h.Frame(kind=="compressed"?Pack(hit):hit); Assert.Equal(70m,h.Tick().TotalDamage);
        for(var i=0;i<5;i++){h.Ack(); Assert.Equal(70m,h.Tick().TotalDamage);}
        Assert.Single(h.Events);
    }

    [Theory]
    [InlineData("zero-size")] [InlineData("oversize")] [InlineData("partial-header")]
    [InlineData("invalid-lz4")] [InlineData("inner-framing")]
    public void InboundMalformedContainersStillFaultClosed(string failure)
    {
        var h=Fresh(); h.Tick();
        var bytes=failure switch {
            "zero-size"=>Container([0],0), "oversize"=>Container([0],2097153),
            "partial-header"=>Frame([255,255,1]), "invalid-lz4"=>Container([255],10),
            "inner-framing"=>Pack(Hit()[..^1]), _=>throw new ArgumentException(failure)
        };
        h.Frame(bytes); Assert.Contains("UNTRUSTED",h.Tick().Status); Assert.Empty(h.Events);
        var state=Assert.Single(h.Pipeline.Snapshot().Single().CheckpointStates!,s=>s.State=="Invalid");
        Assert.True(state.ApplicationAuthoritative); Assert.Equal("Fault",state.Action);
        Assert.Equal("InboundFFFF",state.CompressionDiscriminator); Assert.NotNull(state.OuterFrameSha256);
        if(failure=="zero-size")Assert.Equal(0U,state.DeclaredDecompressedSize);
        if(failure=="oversize")Assert.Equal(2097153U,state.DeclaredDecompressedSize);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PartialOuterFrameWaitsInEitherDirectionWithoutSkipping(bool outbound)
    {
        var h=Fresh(); h.Tick(); var frame=outbound?Container([0,0,0,0],uint.MaxValue):Pack(Hit(50));
        if(outbound)Outbound(h,frame[..4]); else h.Frame(frame[..4]);
        Assert.Equal(0m,h.Tick().TotalDamage);
        Assert.Contains(h.Pipeline.Snapshot().Single().CheckpointStates!,s=>s.State=="Waiting");
        if(outbound){Outbound(h,frame[4..]); h.Frame(Hit(50));} else h.Frame(frame[4..]);
        Assert.Equal(50m,h.Tick().TotalDamage); Assert.Single(h.Events); Assert.Equal(0,h.Pipeline.RetainedPayloadBytes);
    }

    [Theory]
    [InlineData("00")] [InlineData("8000")] [InlineData("FFFFFFFFFF")]
    public void InvalidOutboundCanonicalFramingStillFaultsWithoutResynchronization(string prefix)
    {
        var h=Fresh(); h.Tick(); Outbound(h,[..Convert.FromHexString(prefix),..SizedFrame(11)]);
        Assert.Contains("UNTRUSTED",h.Tick().Status); Assert.Empty(h.Events);
        Assert.Contains(h.Pipeline.Snapshot().Single().CheckpointStates!,s=>s.Direction=="ClientToServer" && s.State=="Invalid");
    }

    [Theory]
    [InlineData("gap")] [InlineData("conflict")]
    public void OutboundTransportIntegrityRemainsRequired(string problem)
    {
        var h=Fresh(); h.Tick(); var bytes=SizedFrame(11); var sequence=h.ClientSequence;
        if(problem=="gap")
        {
            h.Add(bytes[1..],sequence+1,server:false); h.ClientSequence+=(uint)bytes.Length;
            h.Add([],flags:TcpFlags.Ack); h.Frame(Hit(50));
            Assert.Equal(50m,h.Tick().TotalDamage); Assert.True(h.Pipeline.Snapshot().Single().Gaps>0);
            Assert.Equal(bytes.Length-1,h.Pipeline.RetainedPayloadBytes);
            h.Add(bytes[..1],sequence,server:false); h.Add([],flags:TcpFlags.Ack); h.Tick();
            Assert.Equal(0,h.Pipeline.RetainedPayloadBytes); Assert.Equal(50m,h.Tick().TotalDamage);
        }
        else
        {
            Outbound(h,bytes); h.Tick(); bytes[^1]^=1; h.Add(bytes,sequence,server:false);
            Assert.Contains("UNTRUSTED",h.Tick().Status); Assert.Equal(0m,h.Tick().TotalDamage);
        }
    }

    [Fact]
    public void OutboundPrivacyRemainsFailClosed()
    {
        var h=Fresh(); h.Tick(); Outbound(h,Frame([0x90,0x91,..Encoding.ASCII.GetBytes("password")]));
        Assert.Contains("UNTRUSTED",h.Tick().Status); Assert.Empty(h.Events);
        Assert.DoesNotContain("password",h.Meter.Diagnostics.ToJson(h.Now),StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("FFFE")] [InlineData("FEFF")] [InlineData("90FFFF00")]
    public void WrongOrCoincidentalMarkersRemainOrdinaryInboundUnknowns(string body)
    {
        var result=Decode(Frame(Convert.FromHexString(body)),TrafficDirection.ServerToClient,true);
        Assert.Equal("Unknown",Assert.Single(result.Records).DecodeStatus); Assert.Empty(result.Containers); Assert.Empty(result.CombatCandidates);
    }

    [Fact]
    public void ExactConfiguredDecompressionMaximumIsInclusiveAndNextByteRejected()
    {
        var inner=SizedFrame(64); var limits=new ProtocolDecodeLimits { MaximumDecompressedSize=64 };
        Assert.True(Assert.Single(Decode(Pack(inner),TrafficDirection.ServerToClient,true,limits).Containers).ValidDecompression);
        var rejected=Decode(Pack(SizedFrame(65)),TrafficDirection.ServerToClient,true,limits);
        Assert.Equal("FailedContainer",Assert.Single(rejected.Records).DecodeStatus);
    }

    [Fact]
    public void DefaultTwoMiBDecompressionMaximumIsInclusive()
    {
        var inner=Enumerable.Range(0,512).SelectMany(_=>SizedFrame(4096)).ToArray();
        Assert.Equal(ProtocolDecodeLimits.DefaultMaximumDecompressedSize,inner.Length);
        var result=Decode(Pack(inner),TrafficDirection.ServerToClient,true);
        Assert.Equal(inner.Length,Assert.Single(result.Containers).DecompressedBytes);
        Assert.Equal(512,result.Records.Count(r=>r.DecodeStatus=="Unknown"));
        var above=Decode(Container([0],(uint)inner.Length+1),TrafficDirection.ServerToClient,true);
        Assert.Equal("FailedContainer",Assert.Single(above.Records).DecodeStatus);
    }

    [Fact]
    public void InterleavedSoakKeepsCheckpointMemoryBoundedAndRingAt256()
    {
        var h=Fresh(); h.Tick(); var outbound=Container([0,0,0,0],uint.MaxValue);
        for(var batch=1;batch<=1000;batch++)
        {
            Outbound(h,[..outbound,..Hit(999)]); h.Frame(Pack([..Hit(1),..Hit(2)]));
            Assert.Equal(batch*3m,h.Tick().TotalDamage); Assert.Equal(batch*2,h.Feed.PublishedCount);
            Assert.Equal(0,h.Pipeline.RetainedPayloadBytes); Assert.Equal(0,h.Feed.RetainedIdentities);
            Assert.InRange(h.Pipeline.VerificationBytes,0,2*65536); h.Inputs.Clear(); h.Events.Clear();
        }
        Assert.InRange(h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Count,1,256);
        var path=Environment.GetEnvironmentVariable("AION_OUTBOUND_SOAK");
        if(path is not null)File.WriteAllText(path,JsonSerializer.Serialize(new{Events=h.Feed.PublishedCount,
            Damage=h.Tick().TotalDamage,h.Pipeline.CheckpointCount,h.Pipeline.VerificationBytes,h.Pipeline.RetainedPayloadBytes,
            Ring=h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Count}));
    }
}
