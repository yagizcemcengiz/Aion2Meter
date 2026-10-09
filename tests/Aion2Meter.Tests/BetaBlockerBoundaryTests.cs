using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.ReplayProtocolDecoderTests;

namespace Aion2Meter.Tests;

public sealed class BetaBlockerBoundaryTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(6)] [InlineData(10)]
    public void AckedSplitCompressedFrameKeepsPublishedHistoryAndCompletesExactlyOnce(int split)
    {
        var h = Fresh(); h.Frame(Hit(10)); h.Tick();
        var bytes = Pack([.. Enumerable.Range(1,80).SelectMany(n=>Hit((uint)n)), .. Hit(786,category:0x36)]);
        h.Frame(bytes[..split]); var waiting = h.Tick();
        Assert.Equal(10m,waiting.TotalDamage); Assert.Equal(1,h.Feed.PublishedCount);
        Assert.Equal(split,h.Pipeline.RetainedPayloadBytes); Assert.Equal(CurrentPlayerBindingStatus.Resolved,waiting.BindingStatus);
        var pending = h.Pipeline.Snapshot().Single().CheckpointStates!.Single(p=>p.Direction=="ServerToClient");
        Assert.Equal("Waiting",pending.State); Assert.Equal(split,pending.UnpublishedBytes); Assert.Null(pending.IrreversibleInvariant);
        h.Frame(bytes[split..]); var after = h.Tick();
        Assert.Equal(3250m,after.TotalDamage); Assert.Equal(81,h.Feed.PublishedCount);
        Assert.Equal(1,after.UnsupportedCandidates); Assert.Equal(0,h.Pipeline.RetainedPayloadBytes);
        Assert.Contains(h.Pipeline.Snapshot().Single().CheckpointStates!,p=>p.ResolvedWaiting && p.State=="Complete");
        for (var i=0;i<10;i++) { h.Ack(); Assert.Equal(3250m,h.Tick().TotalDamage); }
        Assert.Equal(81,h.Feed.PublishedCount);
        Assert.Contains(h.Meter.Diagnostics.Snapshot(h.Now).Transitions,t=>t.CheckpointStates?.Any(p=>p.ResolvedWaiting)==true);
    }

    [Theory]
    [InlineData("00")] [InlineData("8000")] [InlineData("FFFFFFFFFF")]
    public void InvalidAckedPrefixAtKnownBoundaryFaultsWithoutSkippingToConvenientHit(string prefix)
    {
        var h = Fresh(); h.Frame(Hit(10)); h.Tick();
        h.Frame([.. Convert.FromHexString(prefix), .. Hit(999)]);
        Assert.Contains("UNTRUSTED",h.Tick().Status); Assert.Equal(1,h.Feed.PublishedCount);
        var fault = Assert.Single(h.Meter.Diagnostics.Snapshot(h.Now).Transitions,t=>t.Kind=="RuntimeSuspended");
        Assert.Contains(fault.CheckpointStates!,p=>p.State=="Invalid" && p.IrreversibleInvariant!.Contains("no resynchronization"));
    }

    [Fact]
    public void WholeMalformedContainerIsNotReclassifiedAsTemporaryTcpSplit()
    {
        var h = Fresh(); h.Tick(); h.Frame(Pack(Hit()[..^1]));
        Assert.Contains("UNTRUSTED",h.Tick().Status); Assert.Equal(0,h.Feed.PublishedCount);
        var fault = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t=>t.Kind=="RuntimeSuspended");
        var issue = Assert.Single(fault.CheckpointStates!,p=>p.State=="Invalid");
        Assert.Equal("ContainerWithUnparsedBytes",issue.ContainerState);
        Assert.Contains("more TCP bytes cannot extend",issue.IrreversibleInvariant);
        Assert.NotEmpty(issue.Reason);
    }

    [Fact]
    public void PrivacySuppressionHasItsOwnInvariantWithoutReclassifyingAsMalformedContainer()
    {
        var h = Fresh(); h.Tick(); h.Frame(Frame([0x90,0x91,.. System.Text.Encoding.ASCII.GetBytes("password")]));
        Assert.Contains("UNTRUSTED",h.Tick().Status);
        var fault = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t=>t.Kind=="RuntimeSuspended");
        Assert.Contains(fault.CheckpointStates!,p=>p.ContainerState is "Suppressed" or "SensitiveStream" && p.IrreversibleInvariant!.StartsWith("Privacy suppression"));
        Assert.DoesNotContain("RawBytes",h.Meter.Diagnostics.ToJson(h.Now));
    }

    [Fact]
    public void PartialCanonicalLengthVarintWaitsUntilTheActualDeclaredFrameIsComplete()
    {
        var h = Fresh(); h.Frame(Hit(10)); h.Tick();
        var unknown = Frame([0,0x36,..new byte[256]]);
        Assert.Equal(2,ApplicationFraming.Read(unknown).PrefixLength);
        h.Frame(unknown[..1]); h.Tick();
        var prefix = h.Pipeline.Snapshot().Single().CheckpointStates!.Single(p=>p.Direction=="ServerToClient");
        Assert.Equal("Partial varint",prefix.PrefixState); Assert.Equal(2,prefix.ExpectedPrefixBytes); Assert.Equal(1,prefix.AvailablePrefixBytes);
        h.Frame(unknown[1..2]); Assert.Equal(10m,h.Tick().TotalDamage);
        Assert.Equal("Waiting",h.Pipeline.Snapshot().Single().CheckpointStates!.Single(p=>p.Direction=="ServerToClient").State);
        h.Frame(unknown[2..]); Assert.Equal(10m,h.Tick().TotalDamage);
        Assert.Equal(0,h.Pipeline.RetainedPayloadBytes); Assert.Equal(1,h.Feed.PublishedCount);
    }

    [Fact]
    public void WaitingTailStaysWithinExistingBoundAndCannotSilentlyGrowForever()
    {
        var h = Fresh(limits:new(MaximumPayloadBytes:1024)); h.Tick();
        var bytes = Frame([0,0x36,..new byte[1200]]);
        h.Frame(bytes[..600]); Assert.Equal(CurrentPlayerBindingStatus.Resolved,h.Tick().BindingStatus);
        Assert.Equal(600,h.Pipeline.RetainedPayloadBytes);
        h.Frame(bytes[600..1100]); Assert.Contains("UNTRUSTED",h.Tick().Status);
        Assert.Equal(0,h.Pipeline.RetainedPayloadBytes); Assert.Equal(0,h.Feed.PublishedCount);
    }

    [Theory]
    [InlineData("",FrameReadState.Waiting)] [InlineData("80",FrameReadState.Waiting)]
    [InlineData("8080",FrameReadState.Waiting)] [InlineData("00",FrameReadState.Invalid)]
    [InlineData("8000",FrameReadState.Invalid)] [InlineData("FFFFFFFFFF",FrameReadState.Invalid)]
    [InlineData("06AA",FrameReadState.Waiting)] [InlineData("0690AA",FrameReadState.Complete)]
    public void PrefixStateIsTypedAndNeverDependsOnTheLastProtocolOpcode(string hex,FrameReadState state)
        => Assert.Equal(state,ApplicationFraming.Read(Convert.FromHexString(hex)).State);

    [Fact]
    public void MixedLongSessionWithSplitContainersUnsupportedRecordsAndRuntimeChangesHasExactBoundedAccounting()
    {
        var h = new Harness(); h.Initialize(); h.Handshake();
        h.Frame(PlayerProfileTests.Profile(29,50000,"Unseen Local",true)); h.Tick();
        ulong actor=50000; const int scenes=40, hits=80;
        for (var scene=0;scene<scenes;scene++)
        {
            var previous = actor; actor++;
            h.Frame(PlayerProfileTests.Profile(29,actor,"Unseen Local",true)); h.Tick();
            h.Frame(Hit(999,actor:(uint)previous)); Assert.Equal(scene*hits,h.Tick().TotalDamage);
            var bytes = Pack([..Enumerable.Range(0,hits).SelectMany(_=>Hit(1,actor:(uint)actor)),
                ..Frame([0,0x36,1,2,3]),..Hit(786,actor:(uint)actor,category:0x36)]);
            var split = 1+scene%10;
            h.Frame(bytes[..split]); Assert.Equal(scene*hits,h.Tick().TotalDamage);
            h.Frame(bytes[split..]); var state = h.Tick();
            Assert.Equal((scene+1)*hits,state.TotalDamage); Assert.Equal(actor,state.EntityId);
            Assert.Equal("Unseen Local",state.StableIdentity!.CharacterName);
            Assert.Equal((scene+1)*(hits+1),h.Feed.PublishedCount); // One old-actor Other, eighty Self.
            Assert.Equal(scene+1,state.UnsupportedCandidates);
            Assert.Equal(0,h.Pipeline.RetainedPayloadBytes); Assert.InRange(h.Pipeline.VerificationBytes,0,65536);
            Assert.InRange(h.Pipeline.SelectedPacketCount,0,2); Assert.Equal(0,h.Feed.RetainedIdentities);
            var oldSequence = h.ServerSequence-(uint)bytes.Length;
            h.Add(bytes,oldSequence); h.Ack(); Assert.Equal((scene+1)*hits,h.Tick().TotalDamage);
            h.Inputs.Clear(); h.Events.Clear();
        }
        Assert.True(h.Pipeline.CheckpointCount>=scenes*2);
        Assert.DoesNotContain(h.Meter.Diagnostics.Snapshot(h.Now).Transitions,t=>t.Lifecycle=="Faulted");
        Assert.Equal(3200,h.Tick().SelfCount); Assert.Equal(40,h.Tick().OtherCount);
        var path = Environment.GetEnvironmentVariable("AION_BETA_BLOCKER_STRESS");
        if (path is not null) File.WriteAllText(path,JsonSerializer.Serialize(new {Supported=3240,Self=3200,Other=40,
            Unsupported=h.Tick().UnsupportedCandidates,h.Pipeline.CheckpointCount,h.Pipeline.VerificationBytes,
            h.Pipeline.RetainedPayloadBytes,h.Feed.RetainedIdentities,FinalActor=actor},new JsonSerializerOptions{WriteIndented=true}));
    }
}
