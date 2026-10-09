using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.PlayerProfileTests;

namespace Aion2Meter.Tests;

public sealed class BetaBlockerProfileDiagnosticTests
{
    [Theory]
    [InlineData("world -> world zone")] [InlineData("world -> town")] [InlineData("world -> Abyss")]
    [InlineData("Abyss -> world")] [InlineData("world -> dungeon")] [InlineData("dungeon -> world")]
    [InlineData("teleport")] [InlineData("channel change")] [InlineData("backend handoff")]
    [InlineData("unseen synthetic scene")]
    public void SameStableSelfRowSurvivesPendingActorAndValidatedRebind(string description)
    {
        Assert.NotEmpty(description); // Labels are never inputs to production.
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Profile(29,50000,"Unseen Local",true)); h.Tick();
        var view = new Aion2Meter.Presentation.OverlayViewModel(); view.Apply(Aion2Meter.Presentation.OverlaySnapshot.FromMeter(h.Tick()));
        var row = Assert.Single(view.Rows);
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack); view.Apply(Aion2Meter.Presentation.OverlaySnapshot.FromMeter(h.Tick()));
        Assert.Same(row,Assert.Single(view.Rows)); Assert.Equal("0",row.Damage); Assert.Equal("—",row.Dps);
        h.Handshake(5000,15000,25002); h.Frame(Hit(900,actor:60000),port:25002);
        view.Apply(Aion2Meter.Presentation.OverlaySnapshot.FromMeter(h.Tick())); Assert.Same(row,Assert.Single(view.Rows));
        h.Frame(Profile(29,60000,"Unseen Local",true),port:25002); Assert.Equal(0m,h.Tick().TotalDamage);
        h.Frame(Hit(50,actor:60000),port:25002); h.Frame(Hit(999,actor:50000),port:25002);
        view.Apply(Aion2Meter.Presentation.OverlaySnapshot.FromMeter(h.Tick())); Assert.Same(row,Assert.Single(view.Rows));
        Assert.Equal("50",row.Damage); Assert.Equal(PlayerClass.Cleric,row.Class);
    }

    [Theory]
    [InlineData(0xBF)] [InlineData(0x17)] [InlineData(0x07)]
    public void UnvalidatedSceneVariantRetainsStablePresentationButCannotBindFromNameAndLeadingId(byte marker)
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Profile(29,50000,"Unseen Local",true)); h.Tick();
        h.Add([],flags:TcpFlags.Rst|TcpFlags.Ack); h.Handshake(5000,15000,25002);
        h.Frame(Profile(29,60000,"Unseen Local",true,marker),port:25002);
        h.Frame(Hit(999,actor:60000),port:25002); var waiting = h.Tick();
        Assert.Null(waiting.EntityId); Assert.Equal("Unseen Local",waiting.CharacterName); Assert.Equal(PlayerClass.Cleric,waiting.StableIdentity!.Class);
        Assert.Equal(0m,waiting.TotalDamage);
        var init = h.Meter.Diagnostics.Snapshot(h.Now).Transitions.Last(t=>t.RecordTag=="3336");
        Assert.Equal(marker,init.ProfileLayout!.Marker); Assert.Equal("Unsupported profile",init.ProfileLayout.LayoutType);
        Assert.Contains($"0x{marker:X2}",init.ProfileLayout.Decision);
        Assert.Equal(50000UL,init.PriorSelfRuntimeId); Assert.Null(init.ProfileLayout.ClassCode);
        Assert.Contains(init.InitializationCandidates!,c=>c.RuntimeIdCandidate==60000 && c.NameCandidate=="Unseen Local" && c.Decision!.Contains("Unsupported branch marker"));
        h.Frame(Profile(29,60000,"Unseen Local",true),port:25002); var rebound = h.Tick();
        Assert.Equal(60000UL,rebound.EntityId); Assert.Equal(0m,rebound.TotalDamage);
        h.Frame(Hit(50,actor:60000),port:25002); h.Frame(Hit(900,actor:50000),port:25002);
        Assert.Equal(50m,h.Tick().TotalDamage); Assert.Equal(1,h.Tick().SelfCount);
    }

    [Theory]
    [InlineData("class")] [InlineData("faction")] [InlineData("server")] [InlineData("truncated")]
    public void FixedProfileDiagnosticReportsTheFirstRejectedBoundaryWithoutInventingANewLayout(string kind)
    {
        var bytes = Profile(kind=="class"?37U:29U,50000,"Unseen Local",true,faction:kind=="faction"?(byte)3:(byte)1);
        var r = Record(bytes,"3336"); var at = r.PrefixLength+2+ReplayProtocolDecoderTests.Varint(50000).Length+6+System.Text.Encoding.UTF8.GetByteCount("Unseen Local");
        if (kind=="server") { bytes[at]=bytes[at+1]=0; }
        if (kind=="truncated") r = r with { RawBytes=bytes[..^20] };
        var inspected = PlayerProfileDecoder.Inspect(r);
        Assert.Contains(kind=="truncated"?"framing":kind,inspected.Decision,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RawBytes",System.Text.Json.JsonSerializer.Serialize(inspected));
    }
}
