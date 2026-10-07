using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Replay;
using Aion2Meter.Replay.Research;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class LivePipelineTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2025-03-04T00:00:00Z");
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.5"), Remote = IPAddress.Parse("198.51.100.7");
    private sealed class Harness(LivePipelineLimits? limits = null)
    {
        public LivePacketPipeline Pipeline { get; } = new([Local], limits: limits);
        public List<SourcePacket> Inputs { get; } = [];
        public uint ServerSequence = 901;
        public long Index;
        public void Add(byte[] payload, uint? sequence = null, TcpFlags flags = TcpFlags.Psh | TcpFlags.Ack,
            bool server = true, uint? ack = null, ushort localPort = 24001, ushort remotePort = 13328,
            IPAddress? remote = null, int padding = 0)
        {
            var packet = Wire(payload, sequence ?? ServerSequence, flags, server, ack ?? (server ? 101u : 901u),
                ++Index, localPort, remotePort, remote ?? Remote, padding);
            var input = new SourcePacket("synthetic-source", "synthetic-interface", Index, packet);
            Inputs.Add(input); Pipeline.Ingest(input);
        }
        public void Handshake(uint client = 100, uint server = 900, ushort port = 24001)
        {
            Add([], client, TcpFlags.Syn, false, 0, port);
            Add([], server, TcpFlags.Syn | TcpFlags.Ack, true, client + 1, port);
            Add([], client + 1, TcpFlags.Ack, false, server + 1, port);
            ServerSequence = server + 1;
        }
        public void Frame(byte[] bytes, ushort port = 24001)
        { Add(bytes, localPort: port); ServerSequence += (uint)bytes.Length; }
        public void Bind()
        { Frame(Init(false)); Frame(Init(true)); }
        public LiveEpochSnapshot Active() => Assert.Single(Pipeline.Snapshot(), e => e.Lifecycle == "Active");
    }

    [Fact]
    public void FreshBindingAssociatesDirectSourcesAndKeepsUnsupported36()
    {
        var h = new Harness(); h.Handshake(); h.Bind();
        h.Frame(ReplayProtocolDecoderTests.Combat(400, [], actor: 200));
        h.Frame(ReplayProtocolDecoderTests.Combat(500, [5], actor: 201));
        h.Frame(ReplayProtocolDecoderTests.Combat(420, [3, 3], actor: 201, category: 0x36));
        var s = h.Active();
        Assert.True(s.ProtocolObserved); Assert.Equal(CurrentPlayerBindingStatus.Resolved, s.BindingStatus);
        Assert.Equal(200ul, s.EntityId); Assert.Equal("Local", s.CharacterName);
        Assert.Equal(2, s.AcceptedEvents); Assert.Equal(1, s.SelfCount); Assert.Equal(1, s.OtherCount);
        Assert.Equal(0, s.UnknownCount); Assert.Equal(new[] { 400ul }, s.RecentSelfAmounts); Assert.Equal(1, s.UnsupportedCandidates);
    }

    [Fact]
    public async Task ReplayAndSyntheticSourceProduceTheSameDownstreamSnapshot()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); h.Frame(ReplayProtocolDecoderTests.Pack(ReplayProtocolDecoderTests.Combat(786, [15])));
        using var files = new TestFiles(); var path = files.WritePcap(h.Inputs.Select(p => p.Packet).ToArray());
        var pipeline = new LivePacketPipeline([Local]);
        await foreach (var input in new ReplayPacketSource(path).ReadAsync()) pipeline.Ingest(input);
        var a = h.Active(); var b = Assert.Single(pipeline.Snapshot());
        Assert.Equal(a.BindingStatus, b.BindingStatus); Assert.Equal(a.CharacterName, b.CharacterName);
        Assert.Equal(a.AcceptedEvents, b.AcceptedEvents); Assert.Equal(a.SelfCount, b.SelfCount); Assert.Equal(a.RecentSelfAmounts, b.RecentSelfAmounts);
        var capture = new ResearchAnalyzer().Read(path, new(Local, 24001, Remote, 13328));
        var replay = ReplayDamageEventEpochAdapter.Create(capture, new(Local, 24001, Remote, 13328));
        Assert.Equal(replay.Events.Count, b.AcceptedEvents); Assert.Equal(replay.Binding.Status, b.BindingStatus);
        Assert.Equal(replay.Events.Select(e => e.Amount), b.RecentSelfAmounts);
    }

    [Fact]
    public void DuplicateAndOverlappingRetransmissionDoNotDuplicateEvents()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); var bytes = ReplayProtocolDecoderTests.Combat(500, []);
        var sequence = h.ServerSequence; h.Frame(bytes);
        h.Add(bytes, sequence); h.Add(bytes[3..], sequence + 3);
        var a = h.Active(); var b = h.Active();
        Assert.Equal(1, a.AcceptedEvents); Assert.Equal(1, a.SelfCount); Assert.True(a.DuplicateSegments >= 2);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SplitRecordWaitsForCompletionAndOutOfOrderGapDoesNotInventBoundary()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); var bytes = ReplayProtocolDecoderTests.Combat(500, []);
        var sequence = h.ServerSequence;
        h.Add(bytes[7..], sequence + 7);
        Assert.Equal(0, h.Active().AcceptedEvents);
        h.Add(bytes[..4], sequence);
        Assert.Equal(0, h.Active().AcceptedEvents);
        h.Add(bytes[4..7], sequence + 4);
        Assert.Equal(1, h.Active().AcceptedEvents); Assert.Equal(1, h.Active().SelfCount);
    }

    [Fact]
    public void TrailingPartialFrameIsNeverProjectedAndShutdownDoesNotFlushIt()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); h.Frame(ReplayProtocolDecoderTests.Combat(500, [])[..^1]);
        Assert.Equal(0, h.Active().AcceptedEvents);
        var stop = Assert.Single(h.Pipeline.Complete()); Assert.Equal("CaptureStopped", stop.Lifecycle); Assert.Equal(0, stop.AcceptedEvents);
        Assert.Equal(h.Pipeline.Complete(), h.Pipeline.Complete());
        Assert.Throws<ObjectDisposedException>(() => h.Pipeline.Ingest(h.Inputs[^1]));
    }

    [Fact]
    public void ConflictingOverlapRevokesTheDiagnosticSnapshotRatherThanAddingCounts()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); var bytes = ReplayProtocolDecoderTests.Combat(500, []);
        var sequence = h.ServerSequence; h.Frame(bytes); Assert.Equal(1, h.Active().SelfCount);
        var changed = bytes.ToArray(); changed[^3] ^= 1; h.Add(changed, sequence);
        var s = h.Active(); Assert.Equal(0, s.AcceptedEvents); Assert.Equal(CurrentPlayerBindingStatus.Unknown, s.BindingStatus); Assert.True(s.Conflicts > 0);
    }

    [Fact]
    public void NewHandshakeResetsBindingStreamAndRuntimeProvenance()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); h.Frame(ReplayProtocolDecoderTests.Combat(500, [])); var first = h.Active();
        h.Handshake(1000, 9000); h.Frame(ReplayProtocolDecoderTests.Combat(600, []));
        var second = h.Active(); Assert.NotEqual(first.EpochId, second.EpochId);
        Assert.Equal(1000u, second.ClientIsn); Assert.Equal(9000u, second.ServerIsn);
        Assert.Null(second.EntityId); Assert.Equal(CurrentPlayerBindingStatus.Unknown, second.BindingStatus);
        Assert.Equal(0, second.SelfCount); Assert.Equal(1, second.UnknownCount);
        Assert.Contains(h.Pipeline.Snapshot(), e => e.Lifecycle == "ReplacedByNewHandshake");
    }

    [Fact]
    public void SynRetransmissionDuringHandshakeIsOneEpochButSynAfterEstablishedIsFresh()
    {
        var h = new Harness(); h.Add([], 100, TcpFlags.Syn, false, 0); var id = h.Active().EpochId;
        h.Add([], 100, TcpFlags.Syn, false, 0); Assert.Equal(id, h.Active().EpochId);
        h.Add([], 900, TcpFlags.Syn | TcpFlags.Ack, true, 101);
        h.Add([], 101, TcpFlags.Ack, false, 901); h.Bind();
        Assert.Equal(CurrentPlayerBindingStatus.Resolved, h.Active().BindingStatus);
        h.Handshake(); Assert.NotEqual(id, h.Active().EpochId); Assert.Null(h.Active().EntityId);
    }

    [Fact]
    public void ChangedServerIsnCannotReuseOldClientHandshake()
    {
        var h = new Harness(); h.Handshake(); h.Bind();
        h.Add([], 9000, TcpFlags.Syn | TcpFlags.Ack, true, 101);
        Assert.Null(h.Active().ClientIsn); Assert.Null(h.Active().EntityId); Assert.Equal(CurrentPlayerBindingStatus.Unknown, h.Active().BindingStatus);
    }

    [Fact]
    public void SecondConnectionNeverInheritsFirstConnectionsIdentity()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); h.Frame(ReplayProtocolDecoderTests.Combat(500, []));
        h.Handshake(1000, 9000, 24002); h.Frame(ReplayProtocolDecoderTests.Combat(600, []), 24002);
        var s = h.Pipeline.Snapshot(); Assert.Equal(2, s.Count);
        Assert.Equal(1, s.Single(e => e.Connection.LocalPort == 24001).SelfCount);
        Assert.Equal(1, s.Single(e => e.Connection.LocalPort == 24002).UnknownCount);
    }

    [Fact]
    public void MidstreamInitializationDoesNotRecoverSelfOrAssumeFramingOrigin()
    {
        var h = new Harness(); h.Bind(); h.Frame(ReplayProtocolDecoderTests.Combat(500, []));
        var s = h.Active(); Assert.Null(s.EntityId); Assert.Equal(CurrentPlayerBindingStatus.Unknown, s.BindingStatus); Assert.Equal(0, s.AcceptedEvents);
    }

    [Fact]
    public void ResetAndLatePacketsDoNotReopenOldEpochAndRepeatedIsnsAfterResetAreFresh()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); var id = h.Active().EpochId;
        h.Add([], h.ServerSequence, TcpFlags.Rst | TcpFlags.Ack);
        h.Add(ReplayProtocolDecoderTests.Combat(500, []));
        Assert.Single(h.Pipeline.Snapshot()); Assert.Equal("Reset", h.Pipeline.Snapshot()[0].Lifecycle);
        h.Handshake(); Assert.NotEqual(id, h.Active().EpochId); Assert.Null(h.Active().EntityId);
    }

    [Fact]
    public void FinHalfCloseAndAcknowledgedClosureKeepEpochsSeparate()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); var end = h.ServerSequence;
        h.Add([], end, TcpFlags.Fin | TcpFlags.Ack, true, 101);
        Assert.Equal("HalfClosed", Assert.Single(h.Pipeline.Snapshot()).Lifecycle);
        h.Add([], 101, TcpFlags.Fin | TcpFlags.Ack, false, end + 1);
        h.Add([], end + 1, TcpFlags.Ack, true, 102);
        Assert.Equal("Closed", Assert.Single(h.Pipeline.Snapshot()).Lifecycle);
    }

    [Fact]
    public void ChangedRemoteIpIsAllowedButUnrelatedTrafficAndMalformedPacketsAreIgnored()
    {
        var h = new Harness(); h.Add([], 100, TcpFlags.Syn, false, 0, remotePort: 443);
        var malformed = new SourcePacket("synthetic-source", "synthetic-interface", ++h.Index, new(Start, 3, 1, [1, 2, 3]));
        h.Pipeline.Ingest(malformed); Assert.Empty(h.Pipeline.Snapshot()); Assert.Equal(1, h.Pipeline.MalformedPackets);
        h.Add([], 100, TcpFlags.Syn, false, 0, remote: IPAddress.Parse("203.0.113.99"));
        Assert.Equal(IPAddress.Parse("203.0.113.99"), h.Active().Connection.RemoteIp);
    }

    [Fact]
    public void EthernetPaddingDoesNotBecomeApplicationPayload()
    {
        var h = new Harness(); h.Handshake(); h.Bind(); h.Add(ReplayProtocolDecoderTests.Combat(500, []), padding: 32);
        Assert.Equal(1, h.Active().SelfCount); Assert.Equal(1, h.Active().AcceptedEvents);
    }

    [Fact]
    public void LossAndResourceBoundsFailClosedWithoutLeakingBinding()
    {
        var h = new Harness(new(MaximumPackets: 3)); h.Handshake(); h.Frame(Init(false));
        var s = Assert.Single(h.Pipeline.Snapshot()); Assert.Equal("Faulted", s.Lifecycle); Assert.Equal(0, s.AcceptedEvents);
        var good = new Harness(); good.Handshake(); good.Bind(); Assert.Equal(CurrentPlayerBindingStatus.Resolved, good.Active().BindingStatus);
        var missing = good.Inputs[^1] with { PacketIndex = good.Index + 2 };
        Assert.Throws<InvalidDataException>(() => good.Pipeline.Ingest(missing));
        Assert.Equal(CurrentPlayerBindingStatus.Unknown, Assert.Single(good.Pipeline.Snapshot()).BindingStatus);
    }

    [Fact]
    public async Task CancellationDisposesSourceAndEmitsFinalBoundedSnapshot()
    {
        using var cancellation = new CancellationTokenSource(); var source = new WaitingSource();
        var pipeline = new LivePacketPipeline([Local]); var reports = 0;
        var task = LiveDiagnosticRunner.RunAsync(source, pipeline, _ => reports++, TimeSpan.FromMilliseconds(100), cancellation.Token);
        await source.Entered.Task; cancellation.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(source.Disposed); Assert.True(reports >= 1);
    }

    [Theory]
    [InlineData("--interface", "0")] [InlineData("--port", "65536")] [InlineData("--unknown", "x")]
    public void LiveCliRejectsInvalidOptionsBeforeNativeCapture(string option, string value)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, ResearchCli.Run(["live-smoke", option, value], output, error)); Assert.Contains("live-smoke", error.ToString());
    }

    private sealed class WaitingSource : IPacketSource
    {
        public string SourceId => "waiting";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public async IAsyncEnumerable<SourcePacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { Entered.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Disposed = true; }
            yield break;
        }
    }

    private static byte[] Init(bool confirmation)
    {
        var name = Encoding.UTF8.GetBytes("Local");
        return ReplayProtocolDecoderTests.Frame(confirmation
            ? [0x33, 0x36, .. ReplayProtocolDecoderTests.Varint(200), (byte)name.Length, .. name]
            : [0x15, 0x36, 200, 0, 0, 0, (byte)name.Length, .. name]);
    }

    private static CapturedPacket Wire(byte[] payload, uint sequence, TcpFlags flags, bool server, uint ack,
        long index, ushort localPort, ushort remotePort, IPAddress remote, int padding)
    {
        var timestamp = Start.AddMilliseconds(index); var data = new byte[54 + payload.Length + padding];
        TestFiles.Packet(6, timestamp).Data.CopyTo(data, 0);
        (server ? remote : Local).GetAddressBytes().CopyTo(data, 26); (server ? Local : remote).GetAddressBytes().CopyTo(data, 30);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)(40 + payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(34), server ? remotePort : localPort);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(36), server ? localPort : remotePort);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(38), sequence); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(42), ack);
        data[47] = (byte)flags; payload.CopyTo(data, 54);
        if (padding > 0) data.AsSpan(54 + payload.Length).Fill(0xff);
        return new(timestamp, data.Length, 1, data);
    }
}
