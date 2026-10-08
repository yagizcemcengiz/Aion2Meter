using System.Collections.Specialized;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;

namespace Aion2Meter.Tests;

public sealed class OverlayTests
{
    private static readonly IPAddress LocalAddress = IPAddress.Parse("192.0.2.5");
    [Fact]
    public void UnknownMidstreamHasNoRowOrInventedMetrics()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit());
        var snapshot = OverlaySnapshot.FromMeter(h.Tick());
        Assert.Equal(OverlayState.Waiting, snapshot.State); Assert.Empty(snapshot.Rows);
        Assert.Equal(0, snapshot.ElapsedSeconds); Assert.Contains("next fresh world connection", snapshot.Hint);
        Assert.DoesNotContain("TCP", snapshot.Status + snapshot.Hint);
    }

    [Fact]
    public void ResolvedReadyUsesAuthoritativeNameWithZeroDamageAndUndefinedRate()
    {
        var snapshot = OverlaySnapshot.FromMeter(Fresh().Tick()); var row = Assert.Single(snapshot.Rows);
        Assert.Equal(OverlayState.Ready, snapshot.State); Assert.Equal("Local", row.DisplayName);
        Assert.True(row.IsSelf); Assert.Equal(0m, row.TotalDamage); Assert.Null(row.Dps);
        Assert.Equal(100m, row.ContributionPercent); Assert.Equal(1, row.Rank);
        Assert.Throws<NotSupportedException>(() => ((IList<OverlayRow>)snapshot.Rows).Clear());
    }

    [Fact]
    public void CombatProjectsExistingAggregateDpsDurationAndSoloContributionExactly()
    {
        var h = Fresh(); h.Frame(Hit(500, components: [5, 5])); h.Tick();
        h.Now = h.Events[0].DamageEvent.Provenance.CompletionTimestamp.AddSeconds(2).AddMilliseconds(-10);
        h.Frame(Hit(600)); var meter = h.Tick(); var ui = OverlaySnapshot.FromMeter(meter); var row = Assert.Single(ui.Rows);
        Assert.Equal(OverlayState.InCombat, ui.State); Assert.Equal(1100m, row.TotalDamage);
        Assert.Equal(meter.TotalDamage, row.TotalDamage); Assert.Equal(meter.Dps, row.Dps);
        Assert.Equal(550m, row.Dps); Assert.Equal(2d, ui.ElapsedSeconds);
        Assert.Equal(meter.EncounterSelfHits, row.SelfEvents); Assert.Equal(100m, row.ContributionPercent);
    }

    [Fact]
    public void HundredsOfDecodedOtherActorsNeverCreateRowsOrChangeSelfMetrics()
    {
        var h = new Harness(); h.Initialize(); h.Handshake(); h.Frame(Hit(999)); h.Tick(); h.Bind();
        for (uint actor = 1000; actor < 1300; actor++) h.Frame(Hit(999, actor));
        var meter = h.Tick(); Assert.Equal(300, meter.OtherCount); Assert.Equal(1, meter.UnknownCount);
        var ready = Assert.Single(OverlaySnapshot.FromMeter(meter).Rows); Assert.Equal(0m, ready.TotalDamage);
        h.Frame(Hit(400)); var self = Assert.Single(OverlaySnapshot.FromMeter(h.Tick()).Rows);
        Assert.True(self.IsSelf); Assert.Equal("Local", self.DisplayName); Assert.Equal(400m, self.TotalDamage);
    }

    [Fact]
    public void TimeoutPreservesLastEncounterAndNextHitResetsDisplayedEncounterOnly()
    {
        var h = Fresh(); h.Frame(Hit()); var combat = OverlaySnapshot.FromMeter(h.Tick());
        h.Now = h.Now.AddSeconds(31); var idle = OverlaySnapshot.FromMeter(h.Tick());
        Assert.Equal(OverlayState.Idle, idle.State); Assert.Equal(combat.Rows, idle.Rows);
        h.Frame(Hit(700)); var next = OverlaySnapshot.FromMeter(h.Tick());
        Assert.Equal(OverlayState.InCombat, next.State); Assert.Equal(700m, Assert.Single(next.Rows).TotalDamage);
        Assert.Equal(1, next.Rows[0].SelfEvents); Assert.Null(next.Rows[0].Dps);
        Assert.Equal(combat.Rows[0].Key, next.Rows[0].Key);
    }

    [Fact]
    public void FreshReconnectClearsStaleRowsAndUsesNewScopeEvenForSameEntity()
    {
        var h = Fresh(); h.Frame(Hit()); var view = new OverlayViewModel();
        view.Apply(OverlaySnapshot.FromMeter(h.Tick())); var old = Assert.Single(view.Rows);
        h.Handshake(2000, 8000); view.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        Assert.Empty(view.Rows); Assert.Contains("Waiting", view.Status);
        h.Bind("Next character"); view.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        var current = Assert.Single(view.Rows); Assert.NotSame(old, current); Assert.NotEqual(old.Key, current.Key);
        Assert.Equal("Next character", current.DisplayName); Assert.Equal("0", current.Damage); Assert.Equal("—", current.Dps);
    }

    [Fact]
    public void TransportConflictRemovesPreviouslyTrustedDamageAndIdentity()
    {
        var h = Fresh(); var bytes = Hit(); var sequence = h.ServerSequence;
        h.Frame(bytes); var view = new OverlayViewModel(); view.Apply(OverlaySnapshot.FromMeter(h.Tick()));
        Assert.Single(view.Rows); bytes[^3] ^= 1; h.Add(bytes, sequence);
        var fault = OverlaySnapshot.FromMeter(h.Tick()); view.Apply(fault);
        Assert.Equal(OverlayState.Unavailable, fault.State); Assert.Empty(view.Rows); Assert.Equal("Data unavailable", view.Status);
    }

    [Theory]
    [InlineData("AMBIGUOUS EPOCHS - Self meter paused", CurrentPlayerBindingStatus.Resolved)]
    [InlineData("CONFLICT - Self meter paused", CurrentPlayerBindingStatus.Conflict)]
    [InlineData("UNTRUSTED - reconnect required", CurrentPlayerBindingStatus.Resolved)]
    public void UntrustedStatesNeverExposeStaleMetrics(string status, CurrentPlayerBindingStatus binding)
    {
        var h = Fresh(); h.Frame(Hit()); var meter = h.Tick() with { Status = status, BindingStatus = binding };
        var snapshot = OverlaySnapshot.FromMeter(meter);
        Assert.Equal(OverlayState.Unavailable, snapshot.State); Assert.Empty(snapshot.Rows); Assert.Equal(0, snapshot.ElapsedSeconds);
    }

    [Fact]
    public void StopAndUnsupportedCategoryCannotDisplayUnsupportedDamage()
    {
        var h = Fresh(); h.Frame(Hit(900, components: [15], category: 0x36)); var meter = h.Tick();
        Assert.Equal(1, meter.UnsupportedCandidates); Assert.Empty(h.Events);
        var ready = OverlaySnapshot.FromMeter(meter); Assert.Equal(0m, Assert.Single(ready.Rows).TotalDamage);
        Assert.Equal("Coverage: Partial", ready.Coverage); Assert.Contains("0x36 pending", ready.CoverageDetails);
        h.Pipeline.Complete(); var stopped = OverlaySnapshot.FromMeter(h.Meter.Snapshot(h.Now));
        Assert.Equal(OverlayState.Stopped, stopped.State); Assert.Empty(stopped.Rows);
    }

    [Fact]
    public void OrdinaryRefreshKeepsRowObjectAndDoesNotNotifyUnchangedProperties()
    {
        var snapshot = OverlaySnapshot.FromMeter(Fresh().Tick()); var view = new OverlayViewModel(); view.Apply(snapshot);
        var row = Assert.Single(view.Rows); var changes = 0; var rowChanges = 0; var collectionChanges = 0;
        view.PropertyChanged += (_, _) => changes++; row.PropertyChanged += (_, _) => rowChanges++;
        ((INotifyCollectionChanged)view.Rows).CollectionChanged += (_, _) => collectionChanges++;
        for (var i = 0; i < 100; i++) view.Apply(snapshot with { Rows = Array.AsReadOnly(snapshot.Rows.ToArray()) });
        Assert.Same(row, view.Rows[0]); Assert.Equal(0, changes + rowChanges + collectionChanges);
        view.Apply(snapshot with { ElapsedSeconds = 2.3, Rows = Array.AsReadOnly(new[] { snapshot.Rows[0] with { TotalDamage = 12345, Dps = 678.9m } }) });
        Assert.Same(row, view.Rows[0]); Assert.Equal(0, collectionChanges);
        Assert.Equal("12,345", row.Damage); Assert.Equal("678.9", row.Dps); Assert.Equal("100%", row.Contribution);
        Assert.Equal("00:02.3", view.Elapsed);
    }

    [Fact]
    public void RowTemplateSupportsRankingAndNonSelfBadgeWithoutAddingPartyResolution()
    {
        var view = new OverlayViewModel();
        var a = new OverlayRow("a", 1, "A", true, 100, 50, 60, 1);
        var b = new OverlayRow("b", 2, "B", false, 70, 35, 40, 1);
        var presentationOnly = OverlaySnapshot.Waiting with { Rows = Array.AsReadOnly(new[] { a, b }) };
        view.Apply(presentationOnly); var oldB = view.Rows[1]; Assert.False(oldB.IsSelf);
        view.Apply(presentationOnly with { Rows = Array.AsReadOnly(new[] { b with { Rank = 1 }, a with { Rank = 2 } }) });
        Assert.Same(oldB, view.Rows[0]); Assert.Equal(1, oldB.Rank); Assert.Equal(40d, oldB.ContributionValue);
        // Only the production FromMeter projector decides eligible actors; this exercises reusable row ordering.
    }

    [Fact]
    public void AdapterPersistenceUsesIdentifierAndUnavailableSelectionHasNoArbitraryFallback()
    {
        NetworkAdapter Make(string id, bool ip = true) => new(id, "Same friendly name", "", ip ? [LocalAddress] : [], [], null, "Up");
        var first = Make("stable-a"); var remembered = Make("stable-b");
        Assert.Same(remembered, OverlaySettings.SelectAdapter([first, remembered], "stable-b"));
        Assert.Same(remembered, OverlaySettings.SelectAdapter([remembered, first], "stable-b"));
        Assert.Null(OverlaySettings.SelectAdapter([first], "stable-b"));
        Assert.Null(OverlaySettings.SelectAdapter([first], null));
        Assert.Null(OverlaySettings.SelectAdapter([Make("no-address", false)], "no-address"));
        Assert.Null(OverlaySettings.SelectAdapter([Make("rpcap://remote")], "rpcap://remote"));
    }

    [Fact]
    public async Task SettingsRoundTripStoresPreferencesOnlyAndConcurrentWritesLeaveValidJson()
    {
        using var temp = new SettingsDirectory(); var store = new OverlaySettingsStore(temp.Path);
        var expected = new OverlaySettings("stable-id", true, 0.7, 1.25, new(-1400, 30, "Left"));
        await store.SaveAsync(expected); Assert.Equal(expected, store.Load().Settings); Assert.Null(store.Load().Warning);
        using var json = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal(new[] { "AdapterIdentifier", "Locked", "Opacity", "Scale", "Position" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("Entity", File.ReadAllText(temp.Path)); Assert.DoesNotContain("Character", File.ReadAllText(temp.Path));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.SaveAsync(expected with { Opacity = 0.5 + i * 0.05 })));
        Assert.Null(store.Load().Warning); Assert.Single(Directory.GetFiles(temp.Directory));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"Scale\":9999,\"Opacity\":-20}")]
    public void InvalidSettingsCannotCreateInvisibleOrUnusableWindow(string json)
    {
        using var temp = new SettingsDirectory(); File.WriteAllText(temp.Path, json);
        var loaded = new OverlaySettingsStore(temp.Path).Load();
        Assert.InRange(loaded.Settings.Opacity, .5, 1); Assert.InRange(loaded.Settings.Scale, .75, 1.5);
    }

    [Fact]
    public void MissingOversizedAndNonfiniteSettingsRecoverSafely()
    {
        using var temp = new SettingsDirectory(); var store = new OverlaySettingsStore(temp.Path);
        Assert.Equal(new OverlaySettings(), store.Load().Settings); Assert.Null(store.Load().Warning);
        File.WriteAllText(temp.Path, new string(' ', 16385)); Assert.NotNull(store.Load().Warning);
        Assert.Equal(new OverlaySettings(), store.Load().Settings);
        var invalid = new OverlaySettings(new string('x', 513), Opacity: double.NaN, Scale: double.PositiveInfinity).Validated();
        Assert.Null(invalid.AdapterIdentifier); Assert.Equal(.92, invalid.Opacity); Assert.Equal(1, invalid.Scale);
    }

    [Theory]
    [InlineData(-1500, 40, "Left", -1500, 40, "Left")]
    [InlineData(-9999, -9999, "Left", -1920, 0, "Left")]
    [InlineData(9999, 9999, "Primary", 1500, 940, "Primary")]
    [InlineData(-5000, 100, "Removed", 0, 100, "Primary")]
    [InlineData(-1500, 40, null, -1500, 40, "Left")]
    [InlineData(int.MaxValue, int.MinValue, "Primary", 1500, 0, "Primary")]
    public void PlacementHandlesNegativeCoordinatesMonitorRemovalAndOffscreenBounds(int x, int y, string? monitor, int expectedX, int expectedY, string expectedMonitor)
    {
        var actual = OverlayPlacement.Restore(new(x, y, monitor), [new("Primary", 0, 0, 1920, 1080), new("Left", -1920, 0, 1920, 1080)], 420, 140);
        Assert.Equal(new OverlayPosition(expectedX, expectedY, expectedMonitor), actual);
    }

    [Fact]
    public void OversizedOverlayKeepsHeaderReachableAndInvalidGeometryIsRejected()
    {
        DisplayWorkArea[] monitors = [new("Small", -400, 20, 300, 200)];
        Assert.Equal(new OverlayPosition(-400, 20, "Small"), OverlayPlacement.Restore(new(9999, 9999, "Small"), monitors, 630, 300));
        Assert.Throws<ArgumentException>(() => OverlayPlacement.Restore(null, [], 420, 140));
        Assert.Throws<ArgumentException>(() => OverlayPlacement.Restore(null, monitors, 0, 140));
    }

    [Fact]
    public async Task PacketBurstRunsInBackgroundWithoutPerPacketPresentationAndStopDisposesSource()
    {
        var h = Fresh(); for (var i = 0; i < 500; i++) h.Frame(Hit(1));
        var source = new GatedSource(h.Inputs);
        var callerContext = SynchronizationContext.Current;
        LiveOverlaySession created;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            created = new LiveOverlaySession(source, [LocalAddress], interval: TimeSpan.FromHours(1), clock: () => h.Now);
        }
        finally { SynchronizationContext.SetSynchronizationContext(callerContext); }
        await using var session = created;
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(source.ReadContext); Assert.Equal(h.Inputs.Count, source.ReadCount);
        Assert.Equal(0, session.SnapshotCount); Assert.Same(OverlaySnapshot.Waiting, session.Latest);
        await Task.WhenAll(session.StopAsync(), session.StopAsync(), session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(source.Disposed); Assert.False(session.IsRunning); Assert.Null(session.Error);
        Assert.Equal(1, session.SnapshotCount); Assert.Equal(OverlayState.Stopped, session.Latest.State);
        await session.StopAsync();
    }

    [Fact]
    public async Task PeriodicSessionPublishesSelfMetricsAndSourceFailureWithdrawsStaleRows()
    {
        var h = Fresh(); h.Frame(Hit(600)); h.Tick(); var source = new GatedSource(h.Inputs);
        await using var session = new LiveOverlaySession(source, [LocalAddress], clock: () => h.Now);
        Assert.Equal(TimeSpan.FromMilliseconds(200), LiveOverlaySession.RefreshInterval);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.Latest.State != OverlayState.InCombat) await Task.Delay(10, timeout.Token);
        Assert.Equal(600m, Assert.Single(session.Latest.Rows).TotalDamage);
        source.Fail.TrySetResult();
        while (session.Error is null) await Task.Delay(10, timeout.Token);
        await session.StopAsync(); Assert.True(source.Disposed); Assert.Empty(session.Latest.Rows);
        Assert.Equal(OverlayState.Unavailable, session.Latest.State); Assert.Equal("test source failure", session.Error);
    }

    private sealed class GatedSource(IReadOnlyList<SourcePacket> packets) : IPacketSource
    {
        public string SourceId => "overlay-test";
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public SynchronizationContext? ReadContext;
        public int ReadCount;
        public async IAsyncEnumerable<SourcePacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadContext = SynchronizationContext.Current;
            try
            {
                foreach (var packet in packets) { cancellationToken.ThrowIfCancellationRequested(); ReadCount++; yield return packet; }
                Waiting.TrySetResult(); await Fail.Task.WaitAsync(cancellationToken);
                throw new IOException("test source failure");
            }
            finally { Disposed = true; }
        }
    }

    private sealed class SettingsDirectory : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Aion2Meter-overlay-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "overlay-settings.json");
        public SettingsDirectory() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
