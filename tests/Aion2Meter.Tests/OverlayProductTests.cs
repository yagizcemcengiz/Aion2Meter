using System.Net;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.PartyMeterTests;

namespace Aion2Meter.Tests;

public sealed class OverlayProductTests
{
    private sealed class SettingsDirectory : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "settings.json");
        public SettingsDirectory() => Directory.CreateDirectory(directory);
        public void Dispose() => Directory.Delete(directory, true);
    }
    [Fact]
    public void ResetRetainsBindingRosterCountersDedupAndStartsANewEncounter()
    {
        var h = Fresh(); h.Frame(Identity()); h.Frame(Invite()); h.Frame(Join()); h.Tick();
        h.Frame(Hit(600)); h.Frame(Hit(400, 300)); var before = h.Tick();
        var events = h.Events.Count; var retained = h.Feed.RetainedIdentities;
        h.Meter.ResetCurrent(h.Now); var reset = h.Tick();
        Assert.Equal(before.EntityId, reset.EntityId); Assert.Equal(before.EpochId, reset.EpochId);
        Assert.Equal(before.SelfCount, reset.SelfCount); Assert.Equal(before.OtherCount, reset.OtherCount);
        Assert.Equal(before.PartyRoster!.ActiveMembers, reset.PartyRoster!.ActiveMembers);
        Assert.Equal(2, reset.Members!.Count); Assert.All(reset.Members, m => Assert.Equal(0, m.TotalDamage));
        Assert.Equal(0, reset.GroupTotalDamage); Assert.Equal(0, reset.EncounterElapsedSeconds); Assert.Null(reset.Dps);
        Assert.Empty(reset.RecentSelfAmounts); Assert.Equal("READY", reset.Status);
        Assert.Equal(events, h.Events.Count); Assert.Equal(retained, h.Feed.RetainedIdentities);
        h.Frame(Hit(100, 300)); h.Tick(); h.Frame(Hit(200)); var next = h.Tick();
        Assert.Equal(300, next.GroupTotalDamage); Assert.Equal(200, next.TotalDamage); Assert.Equal(1, next.EncounterSelfHits);
        Assert.Equal(100, next.Members!.Sum(m => m.ContributionPercent));
    }
    [Fact]
    public void ResetDropsFrozenLeaverButDoesNotResurrectRosterOrAcceptOldBufferedHits()
    {
        var h = Fresh(); h.Frame(Identity()); h.Frame(Invite()); h.Frame(Join()); h.Frame(Hit(400, 300)); h.Tick();
        h.Frame(Leave()); var before = h.Tick(); Assert.Equal(2, before.Members!.Count);
        var cutoff = h.Now.AddSeconds(2); h.Meter.ResetCurrent(cutoff);
        Assert.Single(h.Tick().Members!);
        h.Frame(Hit(600)); h.Frame(Hit(999, 300)); Assert.Equal(0, h.Tick().GroupTotalDamage);
        h.Now = cutoff.AddSeconds(1); h.Frame(Hit(200)); Assert.Equal(200, h.Tick().GroupTotalDamage);
    }
    [Fact]
    public void ResetBeforeBindingAfterStopAndAfterConflictCannotInventTrust()
    {
        var h = new Harness(); h.Initialize(); h.Meter.ResetCurrent(h.Now); Assert.Empty(h.Tick().Members ?? []);
        h.Handshake(); h.Bind(); h.Frame(Hit()); h.Tick(); h.Pipeline.FaultAll("test"); h.Tick();
        h.Meter.ResetCurrent(h.Now); Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Empty(h.Tick().Members!);
        h.Pipeline.Complete(); h.Meter.ResetCurrent(h.Now); Assert.Empty(h.Meter.Snapshot(h.Now).Members ?? []);
    }
    [Theory]
    [InlineData("ctrl+shift+h", "Ctrl+Shift+H", true)]
    [InlineData("Alt+F11", "Alt+F11", true)]
    [InlineData("Ctrl+F12", "", false)]
    [InlineData("Shift+H", "", false)]
    [InlineData("Ctrl+Ctrl+H", "", false)]
    [InlineData("H", "", false)]
    [InlineData("Ctrl+Foo", "", false)]
    public void HotkeyGrammarIsBoundedAndCanonical(string input, string expected, bool valid)
    {
        Assert.Equal(valid, HotkeyGesture.TryParse(input, out var parsed)); if (valid) Assert.Equal(expected, parsed.Text);
    }
    [Fact]
    public async Task OldPreferencesMigrateAndNewGeometryAndShortcutsRoundTrip()
    {
        using var temp = new SettingsDirectory(); File.WriteAllText(temp.Path, "{\"Opacity\":0.7,\"Locked\":true}");
        var store = new OverlaySettingsStore(temp.Path); var old = store.Load().Settings;
        Assert.Equal(420, old.Width); Assert.Equal("Ctrl+Shift+H", old.HideHotkey); Assert.True(old.Locked);
        var next = old with { Width = 600, Height = 450, HideHotkey = "Alt+H", ResetHotkey = "Alt+R" };
        await store.SaveAsync(next); Assert.Equal(next, store.Load().Settings);
        var safe = (next with { Width = double.NaN, Height = double.PositiveInfinity, HideHotkey = "bad" }).Validated();
        Assert.Equal(420, safe.Width); Assert.Equal(150, safe.Height); Assert.Equal("Ctrl+Shift+H", safe.HideHotkey);
    }
    [Fact]
    public void ColorsSurviveNewEpochRankingAndMetricsAndRttHasHonestFallback()
    {
        var view = new OverlayViewModel(); var row = new OverlayRow("old/1", 1, "Jeffjen", false, 100, 50, 100, 1);
        var snapshot = OverlaySnapshot.Waiting with { Rows = Array.AsReadOnly(new[] { row }) };
        view.Apply(snapshot); var accent = view.Rows[0].Accent; Assert.Equal("— ms", view.Ping);
        view.Apply(snapshot with { Rows = Array.AsReadOnly(new[] { row with { Key = "new/2", Rank = 2, Dps = 1 } }), NetworkRttMilliseconds = 123.456 });
        Assert.Equal(accent, view.Rows[0].Accent); Assert.Equal("≈ 123 ms", view.Ping);
        Assert.NotEqual(accent, PlayerAccent.For("Jeffjen", true));
        view.Apply(snapshot with { NetworkRttMilliseconds = double.NaN }); Assert.Equal("— ms", view.Ping);
    }
}

public sealed class PassiveTcpRttTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly TcpConnectionSelection Flow = new(IPAddress.Parse("192.0.2.1"), 23456, IPAddress.Parse("198.51.100.2"), 13328);
    private static TcpSegment Segment(uint seq, uint ack, double ms, bool inbound = false, int length = 10, TcpFlags flags = TcpFlags.Ack) =>
        new(1, Start.AddMilliseconds(ms), inbound ? Flow.RemoteIp : Flow.LocalIp, inbound ? Flow.RemotePort : Flow.LocalPort,
            inbound ? Flow.LocalIp : Flow.RemoteIp, inbound ? Flow.LocalPort : Flow.RemotePort, seq, ack, flags, 54 + length, length, new byte[length], false);
    private static PassiveTcpRtt Ready()
    {
        var rtt = new PassiveTcpRtt(); rtt.Select("one", Flow);
        for (uint i = 0; i < 3; i++) { rtt.Observe(Segment(100 + i * 10, 0, i * 200)); rtt.Observe(Segment(900, 110 + i * 10, i * 200 + 100, true, 0)); }
        return rtt;
    }
    [Fact]
    public void NeedsThreeSamplesSmoothsAndExpiresWithoutTraffic()
    {
        var rtt = Ready(); Assert.Equal(100, rtt.Milliseconds(Start.AddMilliseconds(500)));
        rtt.Observe(Segment(130, 0, 600)); rtt.Observe(Segment(900, 140, 800, true, 0));
        Assert.Equal(112.5, rtt.Milliseconds(Start.AddMilliseconds(800)));
        Assert.Null(rtt.Milliseconds(Start.AddSeconds(11))); Assert.Equal(0, rtt.PendingCount);
    }
    [Fact]
    public void RetransmissionCumulativeAckDuplicateAckAndUnknownAckCannotCreateSamples()
    {
        var rtt = new PassiveTcpRtt(); rtt.Select("one", Flow);
        rtt.Observe(Segment(100, 0, 0)); rtt.Observe(Segment(110, 0, 10)); rtt.Observe(Segment(100, 0, 20));
        rtt.Observe(Segment(900, 120, 100, true, 0)); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(100)));
        for (var i = 0; i < 10; i++) rtt.Observe(Segment(900, 999, 110 + i, true, 0));
        Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(150))); Assert.Equal(0, rtt.PendingCount);
        rtt.Observe(Segment(120, 0, 200)); rtt.Observe(Segment(130, 0, 210));
        rtt.Observe(Segment(900, 140, 300, true, 0));
        rtt.Observe(Segment(900, 140, 310, true, 0)); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(310)));
    }
    [Fact]
    public void BoundsAndEpochChangesWrongFlowsTruncationAndBackwardTimeAreSafe()
    {
        var rtt = Ready(); rtt.Select("two", Flow); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(500)));
        for (uint i = 0; i < 10000; i++) rtt.Observe(Segment(i * 10, 0, 600 + i));
        Assert.InRange(rtt.PendingCount, 0, 128);
        rtt.Observe(Segment(900, 100000, 11000, true, 0) with { SourcePort = 80 }); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(11000)));
        rtt.Observe(Segment(100000, 0, 11001) with { IsTruncated = true }); Assert.Equal(0, rtt.PendingCount);
        rtt = Ready(); rtt.Observe(Segment(130, 0, 0)); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(500)));
    }
    [Fact]
    public void WraparoundAndFinAreHandledWithoutInventingLongLatency()
    {
        var rtt = new PassiveTcpRtt(); rtt.Select("one", Flow);
        for (uint i = 0; i < 3; i++) { rtt.Observe(Segment(unchecked(uint.MaxValue - 4 + i * 10), 0, i * 200)); rtt.Observe(Segment(900, unchecked(5 + i * 10), i * 200 + 100, true, 0)); }
        Assert.Equal(100, rtt.Milliseconds(Start.AddMilliseconds(500)));
        rtt.Observe(Segment(26, 0, 600, flags: TcpFlags.Fin)); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(600)));
    }
    [Fact]
    public void IPv6EndpointsAndMalformedFramesDoNotAffectMeterOrInventSamples()
    {
        var local = IPAddress.Parse("2001:db8::1"); var remote = IPAddress.Parse("2001:db8::2");
        var rtt = new PassiveTcpRtt(); rtt.Select("ipv6", new(local, Flow.LocalPort, remote, Flow.RemotePort));
        for (uint i = 0; i < 3; i++)
        {
            rtt.Observe(Segment(100 + i * 10, 0, i * 200) with { SourceIp = local, DestinationIp = remote });
            rtt.Observe(Segment(900, 110 + i * 10, i * 200 + 100, true, 0) with { SourceIp = remote, DestinationIp = local });
        }
        Assert.Equal(100, rtt.Milliseconds(Start.AddMilliseconds(500)));
        rtt.Observe(new SourcePacket("test", "test", 1, new CapturedPacket(Start, 1, 1, [0])));
        Assert.Equal(100, rtt.Milliseconds(Start.AddMilliseconds(500)));
        rtt.Select(null, null); Assert.Null(rtt.Milliseconds(Start.AddMilliseconds(500)));
    }
}
