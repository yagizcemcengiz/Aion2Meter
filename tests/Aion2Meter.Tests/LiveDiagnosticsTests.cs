using System.Net;
using System.Text.Json;
using Aion2Meter.Replay;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;
using static Aion2Meter.Tests.PartyMeterTests;
using static Aion2Meter.Tests.ScenePartyLifetimeTests;

namespace Aion2Meter.Tests;

public sealed class LiveDiagnosticsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyAndPopulatedExportUseSafeEndpointTextForIpv4AndScopedIpv6(bool ipv6)
    {
        var buffer = new LiveDiagnosticBuffer(); using var empty = JsonDocument.Parse(buffer.ToJson(DateTimeOffset.UnixEpoch));
        Assert.Empty(empty.RootElement.GetProperty("Transitions").EnumerateArray());
        var h = Fresh(); var snapshot = h.Pipeline.Snapshot().Single();
        var local = ipv6 ? IPAddress.Parse("fe80::1%3") : IPAddress.Parse("192.0.2.5");
        var remote = ipv6 ? IPAddress.Parse("2001:db8::7") : IPAddress.Parse("198.51.100.7");
        snapshot = snapshot with { Connection = new TcpConnectionSelection(local, 24001, remote, 13328) };
        buffer.Record(h.Now, "Test", snapshot, "test");
        var exception = Record.Exception(() => buffer.ToJson(h.Now)); Assert.Null(exception);
        var json = buffer.ToJson(h.Now); using var populated = JsonDocument.Parse(json);
        var t = populated.RootElement.GetProperty("Transitions")[0];
        Assert.Equal(new IPEndPoint(local, 24001).ToString(), t.GetProperty("LocalEndpoint").GetString());
        Assert.Equal(new IPEndPoint(remote, 13328).ToString(), t.GetProperty("RemoteEndpoint").GetString());
        Assert.DoesNotContain("ScopeId", json); Assert.DoesNotContain("RawBytes", json);
    }

    [Fact]
    public void SceneRetirementAndGenuineDisbandExportBeforeAfterKeysActorsAndExactReasons()
    {
        var h = Fresh(); h.Tick(); h.Frame(Invite()); h.Frame(Join()); h.Frame(Identity()); h.Tick();
        h.Frame(Control()); h.Tick(); h.Frame(Identity()); h.Tick(); h.Frame(Disband()); h.Tick();
        var events = h.Meter.Diagnostics.Snapshot(h.Now).Transitions;
        var scene = Assert.Single(events, e => e.RecordTag == "2F92");
        Assert.Equal(1, scene.MembersBefore); Assert.Equal(1, scene.MembersAfter);
        Assert.Equal(scene.Before[0].Key, scene.After[0].Key); Assert.Equal(300UL, scene.Before[0].RuntimeId);
        Assert.Null(scene.After[0].RuntimeId); Assert.Contains("runtime retired, membership retained", scene.Reason);
        Assert.Equal("0D92", scene.After[0].MembershipSource); Assert.NotNull(scene.EvidenceHash);
        var disband = Assert.Single(events, e => e.RecordTag == "1392"); Assert.Equal(1, disband.MembersBefore);
        Assert.Equal(0, disband.MembersAfter); Assert.Contains("disband", disband.Reason);
    }

    [Fact]
    public void ConflictingRefreshRecordsWithheldInitializationAndNoTrustRecovery()
    {
        var h = Fresh(); h.Tick(); h.Frame(ReplayProtocolDecoderTests.Frame([0x33, 0x36,
            .. ReplayProtocolDecoderTests.Varint(201), 5, .. System.Text.Encoding.UTF8.GetBytes("Local")]));
        Assert.Contains("UNTRUSTED", h.Tick().Status);
        var withheld = Assert.Single(h.Meter.Diagnostics.Snapshot(h.Now).Transitions,
            t => t.Kind == "InitializationWithheld" && t.RecordTag == "3336" && t.Binding == "Unknown");
        Assert.Equal(200UL, withheld.PriorSelfRuntimeId);
        Assert.Contains(withheld.InitializationCandidates!, c => c.RuntimeIdCandidate == 201 && c.NameCandidate == "Local");
        h.Frame(Hit()); Assert.Equal(0m, h.Tick().TotalDamage);
    }

    [Fact]
    public void DiagnosticsRemainBoundedDetachedAndSafeDuringConcurrentExports()
    {
        var h = Fresh(); var s = h.Pipeline.Snapshot().Single(); var buffer = new LiveDiagnosticBuffer();
        buffer.Record(h.Now, "Before", s, "before"); var detached = buffer.Snapshot(h.Now);
        Parallel.Invoke(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                buffer.Record(h.Now, "Test", s, new string('x', 10000));
                buffer.Protocol("scope-" + i, h.Now, "4536");
            }
        }, () => { for (var i = 0; i < 100; i++) using (JsonDocument.Parse(buffer.ToJson(h.Now))) { } });
        var export = buffer.Snapshot(h.Now); Assert.Equal(LiveDiagnosticBuffer.Capacity, export.Transitions.Count);
        Assert.Equal(1001, export.TotalTransitions); Assert.InRange(export.LastProtocolActivity.Count, 0, 16);
        Assert.All(export.Transitions, t => Assert.True(t.Reason.Length <= 512)); Assert.Single(detached.Transitions);
        Assert.Equal("Before", detached.Transitions[0].Kind);
    }

    [Fact]
    public async Task SourceFailureStillHasExportableDiagnosticsAfterWorkerStops()
    {
        await using var session = new Aion2Meter.Presentation.LiveOverlaySession(new FailedSource(), [IPAddress.Loopback]);
        await session.StopAsync(); Assert.NotNull(session.Error);
        using var json = JsonDocument.Parse(session.ExportDiagnostics());
        Assert.Contains(json.RootElement.GetProperty("Transitions").EnumerateArray(),
            t => t.GetProperty("Kind").GetString() == "CaptureFailure" && t.GetProperty("Reason").GetString() == "synthetic capture failure");
    }
    private sealed class FailedSource : IPacketSource
    {
        public string SourceId => "diagnostic-source";
        public async IAsyncEnumerable<SourcePacket> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); if (SourceId.Length > 0) throw new IOException("synthetic capture failure");
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
