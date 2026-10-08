using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class LiveStartupRecoveryTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OpenMidstreamIsAutomaticallySupersededByFreshHandshakeOnSameOrNewPort(bool newPort)
    {
        var h = new Harness(); h.Initialize(); h.Bind("Missed", 123); h.Frame(Hit(actor: 123));
        var waiting = h.Tick(); var old = waiting.EpochId;
        Assert.Equal(LiveIdentityRecoveryMethod.WaitingForFreshEpoch, waiting.RecoveryMethod);
        Assert.Null(waiting.IdentityValidFrom); Assert.Empty(h.Events);
        ushort port = newPort ? (ushort)24002 : (ushort)24001;
        h.Handshake(1000, 9000, port); h.Frame(Hit(actor: 300), port: port); h.Tick();
        Assert.Equal(1, h.Tick().UnknownCount); // Fresh bytes before confirmation stay Unknown forever.
        h.Bind("Recovered", 300, port); var resolved = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, resolved.BindingStatus);
        Assert.Equal(LiveIdentityRecoveryMethod.FreshEpochAfterMidstreamStart, resolved.RecoveryMethod);
        Assert.NotEqual(old, resolved.EpochId); Assert.Equal(300UL, resolved.EntityId);
        Assert.Equal(0m, resolved.TotalDamage); Assert.NotNull(resolved.IdentityValidFrom);
        Assert.Equal(h.Now.AddMilliseconds(-10), resolved.IdentityValidFrom); // Complete 3336 arrival, before its peer ACK.
        h.Frame(Hit(600, actor: 300), port: port); h.Tick(); h.Frame(Hit(700, actor: 301), port: port);
        var state = h.Tick(); Assert.Equal(600m, state.TotalDamage);
        Assert.Equal(1, state.SelfCount); Assert.Equal(1, state.OtherCount); Assert.Equal(1, state.UnknownCount);
        Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, h.Events[0].Association.Status);
        for (var i = 0; i < 10; i++) Assert.Equal(600m, h.Tick().TotalDamage);
        Assert.Equal(3, h.Feed.PublishedCount); Assert.Equal(0, h.Feed.RetainedIdentities);
        Assert.True(h.Pipeline.CheckpointCount >= 3); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
        Assert.Equal(2, h.Pipeline.SelectedPacketCount);
        Assert.Equal("SupersededByFreshHandshake", h.Pipeline.Snapshot().Single(e => e.EpochId == old).Lifecycle);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MidstreamValidLookingAmbiguousOrMalformedBytesNeverEstablishFramingOrSelf(int kind)
    {
        var h = new Harness(); h.Initialize(); var frame = Hit();
        byte[] bytes = kind switch
        {
            0 => [.. frame, .. frame, .. frame],
            1 => ReplayProtocolDecoderTests.Frame([0x49, 0x36, .. ReplayProtocolDecoderTests.Pack([.. frame, .. frame, .. frame])]),
            _ => [0x80, 0x00, 0xff, 0xff, 0x06, 0x00, 0x36, .. frame]
        };
        h.Frame(bytes); h.Bind("Plausible", 200); var state = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, state.BindingStatus); Assert.Null(state.EntityId);
        Assert.Equal(0m, state.TotalDamage); Assert.Empty(h.Events);
        Assert.False(h.Pipeline.Snapshot().Single().ProtocolObserved);
        Assert.Equal(0, h.Pipeline.SelectedPacketCount); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
    }

    [Fact]
    public void ServerSynAckWithoutClientSynCannotTurnMidstreamIntoFreshIdentity()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit());
        h.Add([], 900, flags: TcpFlags.Syn | TcpFlags.Ack, ack: 101); h.Bind();
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, h.Tick().BindingStatus);
        Assert.Equal(LiveIdentityRecoveryMethod.WaitingForFreshEpoch, h.Tick().RecoveryMethod);
        h.Handshake(); h.Bind(); Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
    }

    [Fact]
    public void FreshSynReclaimsFullMidstreamConnectionSlotsWithoutRequiringOldFin()
    {
        var h = new Harness(limits: new(MaximumConnections: 8, MaximumPackets: 12, MaximumPayloadBytes: 1024)); h.Initialize();
        for (ushort port = 24001; port <= 24008; port++) h.Frame(Hit(), port: port);
        Assert.Equal(8, h.Pipeline.Snapshot().Count); Assert.Equal(0, h.Pipeline.SelectedPacketCount);
        h.Handshake(1000, 9000, 24009); h.Bind("New", 300, 24009); var state = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, state.BindingStatus); Assert.Equal(0, h.Pipeline.RejectedFlows);
        Assert.Single(h.Pipeline.Snapshot(), e => e.Lifecycle == "Active");
        Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapacityRecoveryNeverEvictsFreshUnresolvedOrResolvedEpochs(bool bind)
    {
        var h = new Harness(limits: new(MaximumConnections: 1)); h.Initialize(); h.Handshake();
        if (bind) h.Bind(); var before = h.Tick();
        h.Add([], 1000, server: false, flags: TcpFlags.Syn, ack: 0, port: 24002);
        var after = h.Tick(); Assert.Equal(1, h.Pipeline.RejectedFlows);
        Assert.Equal(before.EpochId, after.EpochId); Assert.Equal(before.BindingStatus, after.BindingStatus);
        Assert.Single(h.Pipeline.Snapshot());
    }

    [Fact]
    public void LaterFreshEpochNeverCarriesRecoveredIdentityOrDamage()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit()); h.Handshake(); h.Bind(); h.Frame(Hit());
        Assert.Equal(400m, h.Tick().TotalDamage); var old = h.Tick().EpochId;
        h.Handshake(1000, 9000); h.Frame(Hit()); var waiting = h.Tick();
        Assert.NotEqual(old, waiting.EpochId); Assert.Null(waiting.EntityId); Assert.Null(waiting.IdentityValidFrom);
        Assert.Equal(0m, waiting.TotalDamage); Assert.Equal(1, waiting.UnknownCount);
        h.Bind("Different", 300); h.Frame(Hit(actor: 200)); h.Tick(); h.Frame(Hit(600, actor: 300));
        Assert.Equal(600m, h.Tick().TotalDamage); Assert.Equal(1, h.Tick().OtherCount);
        Assert.Equal(1, h.Tick().UnknownCount);
    }

    [Fact]
    public void ConflictingFreshInitializationAfterMidstreamPausesSelfInsteadOfChoosingIdentity()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit()); h.Handshake();
        h.Bind("One", 200); h.Bind("Two", 201); h.Frame(Hit()); var state = h.Tick();
        Assert.Equal(CurrentPlayerBindingStatus.Conflict, state.BindingStatus);
        Assert.Null(state.EntityId); Assert.Null(state.IdentityValidFrom);
        Assert.Equal(0m, state.TotalDamage); Assert.Contains("CONFLICT", state.Status);
        Assert.All(h.Events, e => Assert.Equal(DamageEventPlayerAssociationStatus.Unknown, e.Association.Status));
    }

    [Fact]
    public void RecoveredCheckpointInvalidationClearsRecoveryValidityMetadata()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit()); h.Handshake(); h.Bind(); h.Tick();
        var seq = h.ServerSequence; var bytes = Hit(); h.Frame(bytes); h.Tick(); bytes[^1] ^= 1; h.Add(bytes, seq);
        var state = h.Tick(); Assert.Null(state.IdentityValidFrom);
        Assert.Equal(LiveIdentityRecoveryMethod.None, state.RecoveryMethod); Assert.Equal(0m, state.TotalDamage);
        Assert.Contains("UNTRUSTED", state.Status);
    }

    [Fact]
    public void JsonAndDashboardExplainWaitingAndExposeFreshRecoveryProvenance()
    {
        var h = new Harness(); h.Initialize(); h.Frame(Hit()); var state = h.Tick();
        var lines = LiveMeterDashboard.Lines(state, "metrics", false);
        Assert.Contains(lines, s => s.StartsWith("Binding   : Waiting for identity..."));
        Assert.Contains(lines, s => s.Contains("next fresh game connection")); Assert.Equal(12, lines.Length);
        using var waiting = JsonDocument.Parse(LiveDiagnosticJson.SerializeSnapshot(h.Pipeline, h.Pipeline.Snapshot(), h.Now));
        var epoch = waiting.RootElement.GetProperty("Epochs")[0];
        Assert.Equal("WaitingForFreshEpoch", epoch.GetProperty("RecoveryMethod").GetString());
        Assert.Equal(2, epoch.GetProperty("DiscardedMidstreamPackets").GetInt64());
        Assert.Equal(Hit().Length, epoch.GetProperty("DiscardedMidstreamBytes").GetInt64());
        h.Handshake(); h.Bind(); state = h.Tick();
        Assert.Equal(LiveIdentityRecoveryMethod.FreshEpochAfterMidstreamStart, state.RecoveryMethod);
        Assert.Contains(LiveMeterDashboard.Lines(state, "metrics", true), s => s.Contains("FreshEpochAfterMidstreamStart"));
        Assert.Contains(LiveMeterDashboard.Lines(state, "metrics", false), s => s.StartsWith("Binding   : Resolved"));
    }

    [Fact]
    public void LongMidstreamWaitRetainsNoRawHistoryAndStillRecoversAfterOldPayloadLimit()
    {
        var h = new Harness(); h.Initialize(); var payload = new byte[1024];
        var measurements = new List<object>();
        for (var i = 0; i < 70_000; i++)
        {
            h.Add(payload); h.ServerSequence += (uint)payload.Length; h.Inputs.Clear();
            if (i % 5000 == 0)
            {
                Assert.Equal(CurrentPlayerBindingStatus.Unknown, h.Tick().BindingStatus);
                Assert.Equal(0, h.Pipeline.SelectedPacketCount); Assert.Equal(0, h.Pipeline.RetainedPayloadBytes);
                Assert.Equal(0, h.Feed.RetainedIdentities); Assert.Equal(0, h.Pipeline.VerificationBytes);
            }
            if (i + 1 is 10_000 or 30_000 or 60_000 or 70_000)
                measurements.Add(new { Phase = "MidstreamWait", PacketsSeen = i + 1, Packets = h.Pipeline.SelectedPacketCount,
                    Payload = h.Pipeline.RetainedPayloadBytes, Identities = h.Feed.RetainedIdentities, Memory = GC.GetTotalMemory(true) });
        }
        var waiting = Assert.Single(h.Pipeline.Snapshot());
        Assert.Equal(70_000, waiting.DiscardedMidstreamPackets); Assert.Equal(70_000L * 1024, waiting.DiscardedMidstreamBytes);
        Assert.Equal("Active", waiting.Lifecycle); h.Handshake(1000, 9000); h.Bind();
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Tick().BindingStatus);
        var combat = Enumerable.Range(0, 100).SelectMany(_ => Hit(1)).ToArray();
        for (var batch = 1; batch <= 1200; batch++)
        {
            h.Frame(combat); var state = h.Tick(); Assert.Equal(batch * 100m, state.TotalDamage);
            Assert.Equal(batch * 100L, h.Feed.PublishedCount); Assert.Equal(2, h.Pipeline.SelectedPacketCount);
            Assert.Equal(0, h.Pipeline.RetainedPayloadBytes); Assert.Equal(0, h.Feed.RetainedIdentities);
            Assert.Equal(2, h.Pipeline.BindingEvidenceCount); Assert.InRange(h.Pipeline.VerificationBytes, 0, 65536);
            Assert.Equal(LiveIdentityRecoveryMethod.FreshEpochAfterMidstreamStart, state.RecoveryMethod);
            h.Inputs.Clear(); h.Events.Clear();
            if (batch is 100 or 300 or 600 or 1200)
                measurements.Add(new { Phase = "RecoveredCombat", Events = h.Feed.PublishedCount,
                    Checkpoints = h.Pipeline.CheckpointCount, Packets = h.Pipeline.SelectedPacketCount,
                    Payload = h.Pipeline.RetainedPayloadBytes, Identities = h.Feed.RetainedIdentities,
                    Verification = h.Pipeline.VerificationBytes, Memory = GC.GetTotalMemory(true) });
        }
        Assert.Equal(1201, h.Pipeline.CheckpointCount);
        var output = Environment.GetEnvironmentVariable("AION_STARTUP_RECOVERY_SOAK");
        if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void OrdinaryFreshStartProvenanceAndUnsupported36RemainUnchanged()
    {
        var h = Fresh(); h.Frame(Hit(category: 0x36)); var state = h.Tick();
        Assert.Equal(LiveIdentityRecoveryMethod.FreshInitialization, state.RecoveryMethod);
        Assert.NotNull(state.IdentityValidFrom); Assert.Empty(h.Events); Assert.Equal(0m, state.TotalDamage);
        Assert.Equal(1, state.UnsupportedCandidates);
    }
}
