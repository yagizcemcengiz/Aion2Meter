using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class LiveCombatMeterTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.5"), Remote = IPAddress.Parse("198.51.100.7");
    private sealed class Harness(TimeSpan? timeout = null, LivePipelineLimits? limits = null, int eventLimit = 100_000)
    {
        public LiveCombatFeed Feed { get; } = new(eventLimit);
        public LiveCombatMeter Meter { get; private set; } = null!;
        public LivePacketPipeline Pipeline { get; private set; } = null!;
        public List<LiveCombatEvent> Events { get; } = [];
        public List<SourcePacket> Inputs { get; } = [];
        public uint ServerSequence = 901, ClientSequence = 101;
        public long Index;
        public DateTimeOffset Now = Start;
        public TimeSpan Step = TimeSpan.FromMilliseconds(10);
        public void Initialize()
        {
            Meter = new(Feed, timeout); Pipeline = new([Local], limits: limits, combatFeed: Feed);
            Feed.Published += Events.Add;
        }
        public void Add(byte[] bytes, uint? sequence = null, bool server = true,
            TcpFlags flags = TcpFlags.Psh | TcpFlags.Ack, uint? ack = null, ushort port = 24001)
        {
            Now = Now.Add(Step);
            var packet = Wire(bytes, sequence ?? (server ? ServerSequence : ClientSequence),
                server, flags, ack ?? (server ? ClientSequence : ServerSequence), port, Now);
            var input = new SourcePacket("meter-source", "synthetic", ++Index, packet);
            Inputs.Add(input); Pipeline.Ingest(input);
        }
        public void Handshake(uint client = 100, uint server = 900, ushort port = 24001)
        {
            ClientSequence = client + 1; ServerSequence = server + 1;
            Add([], client, false, TcpFlags.Syn, 0, port);
            Add([], server, true, TcpFlags.Syn | TcpFlags.Ack, client + 1, port);
            Add([], client + 1, false, TcpFlags.Ack, server + 1, port);
        }
        public void Ack() => Add([], server: false, flags: TcpFlags.Ack);
        public void Frame(byte[] bytes, bool acknowledge = true, ushort port = 24001)
        { Add(bytes, port: port); ServerSequence += (uint)bytes.Length; if (acknowledge) Add([], server: false, flags: TcpFlags.Ack, port: port); }
        public void Bind(string name = "Local", ulong id = 200, ushort port = 24001)
        { Frame(Init(false, name, id), port: port); Frame(Init(true, name, id), port: port); }
        public LiveMeterSnapshot Tick()
        { Pipeline.Snapshot(); return Meter.Snapshot(Now); }
        public ReplayDamageEventEpochAdapter Adapter()
        {
            var scope = Pipeline.Snapshot().Last(e => e.Lifecycle == "Active").EpochId;
            var reader = new TcpResearchPacketReader(); var connection = new TcpConnectionSelection(Local, 24001, Remote, 13328);
            var packets = Inputs.Select(i => reader.Read(i.Packet, i.PacketIndex)!).Select(s => new ResearchPacket(s, connection.Direction(s)!.Value, (s.TimestampUtc - Start).TotalSeconds)).ToArray();
            return ReplayDamageEventEpochAdapter.Create(new(scope, packets[0].Segment.TimestampUtc, "test", packets, 0, 0), connection);
        }
    }

    private static Harness Fresh(TimeSpan? timeout = null, LivePipelineLimits? limits = null, int eventLimit = 100_000)
    { var h = new Harness(timeout, limits, eventLimit); h.Initialize(); h.Handshake(); h.Bind(); return h; }
    private static byte[] Hit(uint amount = 400, uint actor = 200, ulong[]? components = null, byte? category = null) =>
        ReplayProtocolDecoderTests.Combat(amount, components ?? [], actor: actor, category: category ?? (components?.Length > 0 ? (byte)0x26 : (byte)0x06));

    [Fact]
    public void AcceptedSelfPublicationIsOnceAcrossRepeatedSnapshotsAndCountsAggregateOnly()
    {
        var h = Fresh(); h.Frame(Hit(500, components: [5, 5]));
        for (var i = 0; i < 5; i++) { h.Ack(); Assert.Equal(500m, h.Tick().TotalDamage); }
        var recomputes = h.Pipeline.RecomputeCount;
        h.Tick(); Assert.Equal(recomputes, h.Pipeline.RecomputeCount); // clean ticks use cached decode
        var published = Assert.Single(h.Events);
        Assert.Equal(DamageEventPlayerAssociationStatus.Self, published.Association.Status);
        Assert.Equal(1, published.PublicationSequence); Assert.Equal(500ul, published.DamageEvent.Amount);
        Assert.Equal(new ulong[] { 5, 5 }, published.DamageEvent.OptionalComponents);
        Assert.Equal(1, h.Feed.PublishedCount); Assert.Equal(1, h.Tick().EncounterSelfHits);
    }

    [Fact]
    public void PublicationWaitsForPeerAckWithoutArtificialTimerDelay()
    {
        var h = Fresh(); h.Frame(Hit(), acknowledge: false);
        Assert.Equal(0m, h.Tick().TotalDamage); Assert.Empty(h.Events);
        h.Ack(); Assert.Equal(400m, h.Tick().TotalDamage); Assert.Single(h.Events);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SplitOrReorderedRecordPublishesOnlyOnceWhenComplete(bool reorder)
    {
        var h = Fresh(); h.Tick(); var bytes = Hit(); var sequence = h.ServerSequence;
        if (reorder) h.Add(bytes[7..], sequence + 7); else h.Add(bytes[..7], sequence);
        h.ServerSequence += (uint)bytes.Length; h.Ack();
        var pending = h.Tick(); Assert.Empty(h.Events); Assert.Equal(CurrentPlayerBindingStatus.Resolved, pending.BindingStatus);
        if (reorder) h.Add(bytes[..7], sequence); else h.Add(bytes[7..], sequence + 7);
        h.Ack(); Assert.Equal(400m, h.Tick().TotalDamage); Assert.Single(h.Events);
        Assert.Equal(h.Inputs[^2].Packet.TimestampUtc, h.Events[0].DamageEvent.Provenance.CompletionTimestamp);
        h.Tick(); Assert.Single(h.Events);
    }

    [Fact]
    public void RetransmissionDuplicatePacketAndPartialOverlapNeverRepeatAccounting()
    {
        var h = Fresh(); var bytes = Hit(); var sequence = h.ServerSequence;
        h.Frame(bytes); h.Tick();
        h.Add(bytes, sequence); h.Add(bytes[4..], sequence + 4); h.Ack();
        var state = h.Tick(); Assert.Equal(400m, state.TotalDamage); Assert.Single(h.Events);
        Assert.True(state.DuplicateSegments >= 2); Assert.True(state.OverlapBytes > 0);
    }

    [Fact]
    public void SameAmountAtDistinctStreamLocationsCountsTwice()
    {
        var h = Fresh(); h.Frame(Hit()); h.Frame(Hit()); var meter = h.Tick();
        Assert.Equal(800m, meter.TotalDamage); Assert.Equal(2, meter.EncounterSelfHits);
        Assert.Equal(2, h.Events.Select(e => e.PublicationIdentity).Distinct().Count());
    }

    [Fact]
    public void GrowingOutboundHistoryChangesReplayOrdinalsButNotLivePublicationIdentity()
    {
        var h = Fresh(); h.Frame(ReplayProtocolDecoderTests.Pack(Hit())); h.Tick();
        var original = Assert.Single(h.Events).DamageEvent.Identity;
        var outbound = ReplayProtocolDecoderTests.Frame([0x57, 0x01, 0]);
        h.Add(outbound, server: false); h.ClientSequence += (uint)outbound.Length;
        h.Add([], flags: TcpFlags.Ack); h.Tick();
        Assert.NotEqual(original, Assert.Single(h.Adapter().Events).Identity);
        Assert.Single(h.Events); Assert.Equal(400m, h.Tick().TotalDamage);
        h.Frame(Hit()); Assert.Equal(800m, h.Tick().TotalDamage); Assert.Equal(2, h.Events.Count);
    }

    [Fact]
    public void NestedCompressedDistinctRecordsRetainSharedProvenanceAndPublishIndependently()
    {
        var h = Fresh(); h.Frame(ReplayProtocolDecoderTests.Pack(ReplayProtocolDecoderTests.Pack([.. Hit(), .. Hit()])));
        Assert.Equal(800m, h.Tick().TotalDamage); Assert.Equal(2, h.Events.Count);
        Assert.All(h.Events, e => Assert.Equal(2, e.DamageEvent.Provenance.ContainerPath.Count));
    }

    [Fact]
    public void ConflictBeforeAckPublishesNothingAndPermanentlyFaultsEpoch()
    {
        var h = Fresh(); var bytes = Hit(); var sequence = h.ServerSequence;
        h.Frame(bytes, false); var changed = bytes.ToArray(); changed[^3] ^= 1;
        h.Add(changed, sequence); h.Ack(); var state = h.Tick();
        Assert.Empty(h.Events); Assert.Equal(0m, state.TotalDamage); Assert.Contains("UNTRUSTED", state.Status);
        h.Frame(Hit(700)); Assert.Equal(0m, h.Tick().TotalDamage); Assert.Empty(h.Events);
        Assert.Equal(0, h.Pipeline.SelectedPacketCount);
    }

    [Fact]
    public void ConflictAfterPublicationInvalidatesTotalsWithoutPublishingCorrectionsOrReusingScope()
    {
        var h = Fresh(); var bytes = Hit(); var sequence = h.ServerSequence;
        h.Frame(bytes); Assert.Equal(400m, h.Tick().TotalDamage);
        var changed = bytes.ToArray(); changed[^3] ^= 1; h.Add(changed, sequence);
        var fault = h.Tick(); Assert.Equal(0m, fault.TotalDamage); Assert.Null(fault.Dps); Assert.Contains("UNTRUSTED", fault.Status);
        Assert.Single(h.Events); Assert.Equal(1, h.Feed.PublishedCount); Assert.Equal(0, h.Feed.RetainedIdentities);
        h.Handshake(1000, 9000); h.Bind(); h.Frame(Hit(700));
        Assert.Equal(700m, h.Tick().TotalDamage); Assert.Equal(2, h.Events.Count);
    }

    [Fact]
    public void GapAfterPublishedPrefixHoldsNewRecordsAndDoesNotWithdrawEarlierEvents()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick();
        var bytes = Hit(600); var sequence = h.ServerSequence;
        h.Add(bytes[5..], sequence + 5); h.ServerSequence += (uint)bytes.Length; h.Ack();
        Assert.Equal(400m, h.Tick().TotalDamage); Assert.Single(h.Events);
        h.Add(bytes[..5], sequence); h.Ack(); Assert.Equal(1000m, h.Tick().TotalDamage); Assert.Equal(2, h.Events.Count);
    }

    [Fact]
    public void IdenticalFrameLocationAndIsnsInFreshEpochCannotBeSuppressedByOldDedupState()
    {
        var h = Fresh(); h.Frame(Hit()); var old = h.Tick();
        h.Handshake(); var waiting = h.Tick();
        Assert.NotEqual(old.EpochId, waiting.EpochId); Assert.Equal(0m, waiting.TotalDamage); Assert.Null(waiting.EntityId);
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, waiting.BindingStatus); Assert.Equal(0, h.Feed.RetainedIdentities);
        h.Bind(); h.Frame(Hit()); var current = h.Tick();
        Assert.Equal(400m, current.TotalDamage); Assert.Equal(1, current.SelfCount); Assert.Equal(2, h.Feed.PublishedCount);
        Assert.NotEqual(h.Events[0].EpochId, h.Events[1].EpochId);
        Assert.Equal(h.Events[0].DamageEvent.Provenance.StreamOffset, h.Events[1].DamageEvent.Provenance.StreamOffset);
    }

    [Fact]
    public void OtherAndUnknownAreDiagnosticOnlyAndCannotStartOrExtendSelfEncounter()
    {
        var h = new Harness(TimeSpan.FromSeconds(1)); h.Initialize(); h.Handshake();
        h.Frame(Hit(900)); Assert.Equal(0m, h.Tick().TotalDamage); Assert.Equal(1, h.Tick().UnknownCount);
        h.Bind(); h.Frame(Hit(800, actor: 201)); var idle = h.Tick();
        Assert.Equal(0m, idle.TotalDamage); Assert.Equal(1, idle.OtherCount);
        h.Frame(Hit()); h.Tick(); h.Now = h.Now.AddSeconds(2);
        h.Frame(Hit(800, actor: 201)); var closed = h.Tick();
        Assert.Equal(400m, closed.TotalDamage); Assert.Equal(2, closed.OtherCount); Assert.Contains("IDLE", closed.Status);
    }

    [Fact]
    public void FirstHitHasNoRateAndSubsequentHitUsesFirstToLastCompletionTime()
    {
        var h = Fresh(); h.Frame(Hit()); var first = h.Tick();
        Assert.Equal("IN COMBAT", first.Status); Assert.Null(first.Dps); Assert.Equal(0, first.EncounterElapsedSeconds);
        var t = h.Events[0].DamageEvent.Provenance.CompletionTimestamp;
        h.Now = t.AddSeconds(2).AddMilliseconds(-10); h.Frame(Hit(600)); var second = h.Tick();
        Assert.Equal(2d, second.EncounterElapsedSeconds); Assert.Equal(500m, second.Dps);
        h.Now = h.Now.AddSeconds(4); var still = h.Tick();
        Assert.Equal(second.Dps, still.Dps); Assert.Equal(second.EncounterElapsedSeconds, still.EncounterElapsedSeconds);
    }

    [Fact]
    public void InactivityBoundaryClosesAndNextSelfHitStartsFreshEncounterButKeepsEpochCounts()
    {
        var h = Fresh(TimeSpan.FromSeconds(2)); h.Frame(Hit()); h.Tick();
        var last = h.Events[0].DamageEvent.Provenance.CompletionTimestamp;
        Assert.Equal("IN COMBAT", h.Meter.Snapshot(last.AddSeconds(2).AddTicks(-1)).Status);
        Assert.Contains("IDLE", h.Meter.Snapshot(last.AddSeconds(2)).Status);
        h.Now = last.AddSeconds(3); h.Frame(Hit(600)); var next = h.Tick();
        Assert.Equal(600m, next.TotalDamage); Assert.Equal(1, next.EncounterSelfHits); Assert.Equal(2, next.SelfCount); Assert.Null(next.Dps);
    }

    [Fact]
    public void SameCompletionTimestampDoesNotInventDpsDuration()
    {
        var h = Fresh(); h.Frame([.. Hit(), .. Hit()]); var meter = h.Tick();
        Assert.Equal(800m, meter.TotalDamage); Assert.Null(meter.Dps); Assert.Equal(0, meter.EncounterElapsedSeconds);
    }

    [Fact]
    public void SubMillisecondElapsedHasNoMisleadingBurstRate()
    {
        var h = Fresh(); h.Frame(Hit(), acknowledge: false); h.Step = TimeSpan.FromTicks(1);
        h.Frame(Hit(), acknowledge: false); h.Ack(); var state = h.Tick();
        Assert.Equal(800m, state.TotalDamage); Assert.InRange(state.EncounterElapsedSeconds, 0.00000001, 0.00099999); Assert.Null(state.Dps);
    }

    [Fact]
    public void LaterIncompleteFrameCannotRevokePublishedPrefixOrBindingAndStopDoesNotFlushIt()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick(); h.Frame(Hit(800)[..^1]);
        Assert.Equal(400m, h.Tick().TotalDamage); Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
        h.Pipeline.Complete(); Assert.Single(h.Events); Assert.Equal(400m, h.Meter.Snapshot(h.Now).TotalDamage);
    }

    [Fact]
    public async Task ReplayPacketSourcePublishesTheSameEventsAsTheSyntheticLiveSource()
    {
        var h = Fresh(); h.Frame(ReplayProtocolDecoderTests.Pack([.. Hit(500), .. Hit(600, actor: 201)])); h.Tick();
        using var files = new TestFiles(); var path = files.WritePcap(h.Inputs.Select(i => i.Packet).ToArray());
        var feed = new LiveCombatFeed(); var meter = new LiveCombatMeter(feed); var pipeline = new LivePacketPipeline([Local], combatFeed: feed);
        await foreach (var packet in new ReplayPacketSource(path).ReadAsync()) pipeline.Ingest(packet);
        pipeline.Snapshot(); var state = meter.Snapshot(h.Now);
        Assert.Equal(h.Tick().TotalDamage, state.TotalDamage); Assert.Equal(h.Tick().SelfCount, state.SelfCount);
        Assert.Equal(h.Tick().OtherCount, state.OtherCount); Assert.Equal(h.Feed.PublishedCount, feed.PublishedCount);
    }

    [Fact]
    public void ClosedSocketReconnectOnNewPortNeedsIndependentIdentityAndDoesNotCarryOldEncounter()
    {
        var h = Fresh(); h.Frame(Hit()); var old = h.Tick(); h.Add([], flags: TcpFlags.Rst | TcpFlags.Ack);
        h.Handshake(1000, 9000, 25002); var waiting = h.Tick();
        Assert.NotEqual(old.EpochId, waiting.EpochId); Assert.Null(waiting.EntityId); Assert.Equal(0m, waiting.TotalDamage);
        h.Bind("NewLocal", 300, 25002); h.Frame(Hit(600, actor: 300), port: 25002);
        var current = h.Tick(); Assert.Equal("NewLocal", current.CharacterName); Assert.Equal(600m, current.TotalDamage);
        Assert.Equal(1, current.SelfCount); Assert.Equal(2, h.Feed.PublishedCount);
    }

    [Fact]
    public void MultipleResolvedActiveEpochsCannotBeSummedOrArbitrarilyChosenAsPersonalMeter()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick();
        h.Handshake(1000, 9000, 25002); h.Bind("OtherLocal", 300, 25002); h.Frame(Hit(600, actor: 300), port: 25002);
        var state = h.Tick(); Assert.Contains("AMBIGUOUS", state.Status); Assert.Equal(0m, state.TotalDamage); Assert.Null(state.Dps);
    }

    [Fact]
    public void ClosedEpochHistoryAndRetainedIdentitiesStayBoundedAcrossManyReconnects()
    {
        var h = Fresh();
        for (var i = 0; i < 25; i++)
        {
            h.Frame(Hit()); h.Tick(); h.Add([], flags: TcpFlags.Rst | TcpFlags.Ack);
            Assert.Equal(0, h.Feed.RetainedIdentities); Assert.Equal(0, h.Pipeline.SelectedPacketCount);
            h.Handshake(); h.Bind(); h.Tick();
        }
        Assert.Equal(25, h.Feed.PublishedCount); Assert.InRange(h.Pipeline.Snapshot().Count, 1, 9);
    }

    [Fact]
    public void Unsupported36AndUnknownNonCombatRecordsNeverStartDamageEncounter()
    {
        var h = Fresh(); h.Frame(Hit(900, components: [15], category: 0x36));
        h.Frame(ReplayProtocolDecoderTests.Frame([0x04, 0x39, 5, 6, 7]));
        var meter = h.Tick(); Assert.Equal(0m, meter.TotalDamage); Assert.Empty(h.Events);
        Assert.Equal(1, meter.UnsupportedCandidates); Assert.Equal("READY", meter.Status); Assert.Contains("0x36 pending", meter.Coverage);
    }

    [Fact]
    public void BindingContradictionInvalidatesEntireMeterAndCannotRecoverByMajorityVote()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick(); h.Bind("Remote", 201);
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0m, h.Tick().TotalDamage); Assert.Single(h.Events);
        h.Frame(Hit()); Assert.Equal(0m, h.Tick().TotalDamage);
    }

    [Fact]
    public void ResourceBoundStopsPublicationAndReleasesRawAndDedupMemory()
    {
        var h = Fresh(eventLimit: 1); h.Frame(Hit()); h.Tick(); h.Frame(Hit());
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Single(h.Events);
        Assert.Equal(0, h.Feed.RetainedIdentities); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
    }

    [Fact]
    public void SelectedPacketBoundInvalidatesCommittedMeterInsteadOfSilentlyRollingIdentity()
    {
        var h = Fresh(limits: new(MaximumPackets: 10)); h.Frame(Hit()); h.Tick();
        h.Frame(Hit()); var state = h.Tick(); Assert.Contains("UNTRUSTED", state.Status); Assert.Equal(0m, state.TotalDamage);
    }

    [Fact]
    public void BackwardCaptureTimeFailsClosedRatherThanChangingEarlierProvenance()
    {
        var h = Fresh(); h.Frame(Hit()); h.Tick();
        var packet = Wire([], h.ServerSequence, true, TcpFlags.Ack, h.ClientSequence, 24001, Start);
        Assert.Throws<InvalidDataException>(() => h.Pipeline.Ingest(new("meter-source", "synthetic", ++h.Index, packet)));
        Assert.Contains("UNTRUSTED", h.Tick().Status); Assert.Equal(0m, h.Tick().TotalDamage);
    }

    [Fact]
    public void MidstreamCannotGuessSelfIdentityOrDps()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit());
        var state = h.Tick(); Assert.Equal(CurrentPlayerBindingStatus.Unknown, state.BindingStatus);
        Assert.Equal(0m, state.TotalDamage); Assert.Contains("Waiting for fresh", state.Status); Assert.Empty(h.Events);
    }

    [Fact]
    public void JsonUsesSameMeterAndSafeEndpointProjectionAndContainsPerformanceMetrics()
    {
        var h = Fresh(); h.Frame(Hit()); var state = h.Tick();
        using var json = JsonDocument.Parse(LiveMeterJson.Serialize(h.Pipeline, h.Feed, h.Pipeline.Snapshot(), state, h.Now));
        Assert.Equal(400m, json.RootElement.GetProperty("Meter").GetProperty("TotalDamage").GetDecimal());
        Assert.Equal(Local.ToString(), json.RootElement.GetProperty("Diagnostics").GetProperty("Epochs")[0].GetProperty("Connection").GetProperty("LocalIp").GetString());
        var metrics = json.RootElement.GetProperty("Performance"); Assert.Equal(1, metrics.GetProperty("PublishedEvents").GetInt64());
        Assert.True(metrics.GetProperty("SelectedPackets").GetInt32() > 0); Assert.True(metrics.GetProperty("RetainedPayloadBytes").GetInt64() > 0);
        Assert.True(metrics.GetProperty("DiagnosticTickMilliseconds").GetDouble() >= 0);
    }

    [Fact]
    public void InteractiveDashboardUpdatesOnlyOwnedLinesAndRestoresCursor()
    {
        var h = Fresh(); h.Frame(Hit()); var state = h.Tick(); using var output = new StringWriter();
        using (var dashboard = new LiveMeterDashboard(output, true)) { dashboard.Render(state, "metrics"); dashboard.Render(state, "metrics"); }
        var text = output.ToString(); Assert.StartsWith("\u001b[?25l", text); Assert.Contains("\u001b[13A", text);
        Assert.EndsWith("\u001b[?25h", text); Assert.DoesNotContain("\u001b[2J", text); Assert.Contains("Damage    : 400", text);
    }

    [Fact]
    public void RedirectedDashboardIsPlainLineOrientedTextAndRetainsCoverage()
    {
        var h = Fresh(); h.Frame(Hit()); using var output = new StringWriter();
        using (var dashboard = new LiveMeterDashboard(output, false)) { dashboard.Render(h.Tick(), "metrics"); dashboard.Render(h.Tick(), "metrics"); }
        Assert.DoesNotContain('\u001b', output.ToString()); Assert.Equal(2, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("PARTIAL", output.ToString()); Assert.Contains("0x36 pending", output.ToString());
    }

    [Fact]
    public void DashboardRestoresAnOriginallyHiddenCursorAndSanitizesControlCharacters()
    {
        var h = Fresh(); var state = h.Tick() with { CharacterName = "Name\u001b[2J\n", Warnings = ["reason\r\n\u001b"] };
        using var output = new StringWriter();
        using (var dashboard = new LiveMeterDashboard(output, true, true, cursorInitiallyVisible: false)) dashboard.Render(state, "metrics");
        Assert.EndsWith("\u001b[?25l", output.ToString()); Assert.DoesNotContain("\u001b[?25h", output.ToString());
        Assert.DoesNotContain("\u001b[2J", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationFinalizesEncounterSourceAndRestoresInteractiveCursor()
    {
        var h = Fresh(); h.Frame(Hit()); using var output = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var source = new WaitingSource(h.Inputs); var feed = new LiveCombatFeed(); var meter = new LiveCombatMeter(feed);
        var pipeline = new LivePacketPipeline([Local], combatFeed: feed);
        using (var dashboard = new LiveMeterDashboard(output, true))
        {
            var run = LiveDiagnosticRunner.RunAsync(source, pipeline, _ => dashboard.Render(meter.Snapshot(h.Now), "metrics"), TimeSpan.FromMilliseconds(100), cancellation.Token);
            await source.Waiting.Task; pipeline.Snapshot(); cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.True(source.Disposed); Assert.EndsWith("\u001b[?25h", output.ToString());
        Assert.Equal("STOPPED", meter.Snapshot(h.Now).Status); Assert.Equal(400m, meter.Snapshot(h.Now).TotalDamage);
        Assert.Equal(0, pipeline.SelectedPacketCount); Assert.Equal(0, feed.RetainedIdentities);
    }

    [Theory]
    [InlineData("--idle-seconds", "0")] [InlineData("--idle-seconds", "3601")]
    [InlineData("--interface", "0")] [InlineData("--port", "65536")]
    public void MeterCliRejectsInvalidOptionsWithoutNativeCapture(string option, string value)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, ResearchCli.Run(["live-meter", option, value], output, error)); Assert.Contains("live-meter", error.ToString());
    }

    [Fact]
    public void MeterAndSmokeHelpRemainAvailableWithoutNativeCapture()
    {
        using var output = new StringWriter(); Assert.Equal(0, ResearchCli.Run(["live-meter", "--help"], output));
        Assert.Contains("--idle-seconds", output.ToString()); Assert.Contains("--json", output.ToString());
        output.GetStringBuilder().Clear(); Assert.Equal(0, ResearchCli.Run(["live-smoke", "--help"], output));
        Assert.Contains("diagnostic snapshots", output.ToString());
    }

    private sealed class WaitingSource(IReadOnlyList<SourcePacket> inputs) : IPacketSource
    {
        public string SourceId => "meter-source";
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public async IAsyncEnumerable<SourcePacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                foreach (var input in inputs) yield return input;
                Waiting.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { Disposed = true; }
        }
    }

    private static byte[] Init(bool confirm, string text, ulong id)
    {
        var name = Encoding.UTF8.GetBytes(text); var little = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(little, (uint)id);
        return ReplayProtocolDecoderTests.Frame(confirm
            ? [0x33, 0x36, .. ReplayProtocolDecoderTests.Varint(id), (byte)name.Length, .. name]
            : [0x15, 0x36, .. little, (byte)name.Length, .. name]);
    }
    private static CapturedPacket Wire(byte[] bytes, uint sequence, bool server, TcpFlags flags, uint ack, ushort port, DateTimeOffset timestamp)
    {
        var data = new byte[54 + bytes.Length]; TestFiles.Packet(6, timestamp).Data.CopyTo(data, 0);
        (server ? Remote : Local).GetAddressBytes().CopyTo(data, 26); (server ? Local : Remote).GetAddressBytes().CopyTo(data, 30);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + bytes.Length));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(34), server ? (ushort)13328 : port);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(36), server ? port : (ushort)13328);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), sequence); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(42), ack);
        data[47] = (byte)flags; bytes.CopyTo(data, 54); return new(timestamp, data.Length, 1, data);
    }
}
