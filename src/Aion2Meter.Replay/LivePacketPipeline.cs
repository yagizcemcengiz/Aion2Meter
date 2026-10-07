using System.Net;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public sealed record LivePipelineLimits(long MaximumPayloadBytes = 64 * 1024 * 1024,
    int MaximumPackets = 250_000, int MaximumConnections = 8);

public sealed record LiveEpochSnapshot(string EpochId, TcpConnectionSelection Connection,
    uint? ClientIsn, uint? ServerIsn, string Lifecycle, bool ProtocolObserved,
    CurrentPlayerBindingStatus BindingStatus, ulong? EntityId, string? CharacterName,
    int AcceptedEvents, int SelfCount, int OtherCount, int UnknownCount,
    IReadOnlyList<ulong> RecentSelfAmounts, int UnsupportedCandidates, int Gaps, int Conflicts,
    int DuplicateSegments, long OverlapBytes, IReadOnlyList<string> Warnings);

/// <summary>
/// Single-consumer, bounded live diagnostic foundation. Complete epoch snapshots replace prior results;
/// they are not an append-only accounting feed. The finite replay stack is reused without a live parser.
/// </summary>
public sealed class LivePacketPipeline
{
    private sealed class Epoch(string id, TcpConnectionSelection connection, DateTimeOffset origin)
    {
        public string Id { get; } = id;
        public TcpConnectionSelection Connection { get; } = connection;
        public DateTimeOffset Origin { get; } = origin;
        public List<ResearchPacket> Packets { get; } = [];
        public uint? ClientIsn;
        public uint? ServerIsn;
        public bool ClientFin, ServerFin;
        public bool Closing;
        public bool HandshakeCompleted;
        public bool Dirty = true;
        public long Bytes;
        public string? Fault;
        public LiveEpochSnapshot? Snapshot;
    }

    private readonly HashSet<IPAddress> localAddresses;
    private readonly ushort servicePort;
    private readonly LivePipelineLimits limits;
    private readonly TcpResearchPacketReader reader = new();
    private readonly Dictionary<TcpConnectionSelection, Epoch> epochs = [];
    private readonly Queue<LiveEpochSnapshot> ended = [];
    private string? sourceId, interfaceId;
    private long lastIndex, nextEpoch, bytes;
    private int packetCount;
    private bool completed;
    public long MalformedPackets { get; private set; }
    public long UnsupportedPackets { get; private set; }
    public long IgnoredPackets { get; private set; }
    public long RejectedFlows { get; private set; }

    public LivePacketPipeline(IEnumerable<IPAddress> localAddresses, ushort servicePort = 13328, LivePipelineLimits? limits = null)
    {
        this.localAddresses = localAddresses.ToHashSet(); this.servicePort = servicePort; this.limits = limits ?? new();
        if (this.localAddresses.Count == 0 || servicePort == 0 || this.limits.MaximumPayloadBytes < 1 ||
            this.limits.MaximumPackets < 1 || this.limits.MaximumConnections is < 1 or > 32)
            throw new ArgumentException("Local IP context, service port and positive bounded limits are required.");
    }

    public void Ingest(SourcePacket input)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceId) || input.PacketIndex <= 0 ||
            sourceId is not null && (input.SourceId != sourceId || input.InterfaceId != interfaceId || input.PacketIndex != lastIndex + 1))
        {
            FaultAll("Missing/reordered source provenance; capture cannot be trusted.");
            throw new InvalidDataException("A pipeline requires one ordered, lossless source session.");
        }
        sourceId ??= input.SourceId; interfaceId ??= input.InterfaceId; lastIndex = input.PacketIndex;
        TcpSegment? segment;
        try { segment = reader.Read(input.Packet, input.PacketIndex); }
        catch (InvalidDataException) { MalformedPackets++; return; }
        catch (NotSupportedException) { UnsupportedPackets++; return; }
        if (segment is null) { IgnoredPackets++; return; }
        TcpConnectionSelection? connection = null;
        if (localAddresses.Contains(segment.SourceIp) && segment.DestinationPort == servicePort && !localAddresses.Contains(segment.DestinationIp))
            connection = new(segment.SourceIp, segment.SourcePort, segment.DestinationIp, segment.DestinationPort);
        else if (localAddresses.Contains(segment.DestinationIp) && segment.SourcePort == servicePort && !localAddresses.Contains(segment.SourceIp))
            connection = new(segment.DestinationIp, segment.DestinationPort, segment.SourceIp, segment.SourcePort);
        if (connection is null) { IgnoredPackets++; return; }
        var direction = connection.Direction(segment)!.Value;
        epochs.TryGetValue(connection, out var epoch);
        var clientSyn = direction == TrafficDirection.ClientToServer && segment.Flags == TcpFlags.Syn;
        var serverSyn = direction == TrafficDirection.ServerToClient && segment.Flags.HasFlag(TcpFlags.Syn) && segment.Flags.HasFlag(TcpFlags.Ack);
        // Repeated SYN during the same open handshake is a retransmission. A SYN after FIN, or a
        // changed ISN, starts a new scope. A changed server ISN cannot inherit the old client SYN.
        if (epoch is not null && (clientSyn && (epoch.ClientIsn != segment.SequenceNumber || epoch.HandshakeCompleted || epoch.Closing || epoch.Fault is not null) ||
            serverSyn && epoch.ServerIsn is { } priorServer && priorServer != segment.SequenceNumber))
        { Retire(epoch, "ReplacedByNewHandshake"); epoch = null; }
        if (epoch is null)
        {
            // Late ACK/FIN/RST traffic cannot manufacture a new epoch. After an observed close,
            // require a new client SYN rather than attaching late old payload to a midstream scope.
            if (!clientSyn && (ended.Any(e => e.Connection == connection && (!serverSyn || e.ServerIsn == segment.SequenceNumber)) ||
                !serverSyn && segment.Payload.Length == 0))
            { IgnoredPackets++; return; }
            if (epochs.Count >= limits.MaximumConnections) { RejectedFlows++; return; }
            epoch = new($"{input.SourceId}/epoch-{++nextEpoch}", connection, segment.TimestampUtc);
            epochs.Add(connection, epoch);
        }
        if (epoch.Fault is not null) return;
        if (clientSyn) epoch.ClientIsn = segment.SequenceNumber;
        if (serverSyn) epoch.ServerIsn = segment.SequenceNumber;
        if (direction == TrafficDirection.ClientToServer && segment.Flags.HasFlag(TcpFlags.Ack) && !segment.Flags.HasFlag(TcpFlags.Syn) &&
            epoch.ClientIsn is { } clientOrigin && epoch.ServerIsn is { } serverOrigin &&
            segment.SequenceNumber == unchecked(clientOrigin + 1) && segment.AcknowledgmentNumber == unchecked(serverOrigin + 1))
            epoch.HandshakeCompleted = true;
        if (segment.IsTruncated)
        { Fault(epoch, "Truncated selected TCP packet; restart with a fresh epoch."); return; }
        var isn = direction == TrafficDirection.ClientToServer ? epoch.ClientIsn : epoch.ServerIsn;
        if (!segment.Flags.HasFlag(TcpFlags.Syn) && isn is { } origin &&
            unchecked((int)(segment.SequenceNumber - (origin + 1))) is < 0 or > 64 * 1024 * 1024)
        { Fault(epoch, "Sequence outside bounded fresh epoch; possible stale session traffic."); return; }
        if (packetCount >= limits.MaximumPackets || bytes + segment.Payload.Length > limits.MaximumPayloadBytes)
        { Fault(epoch, "Live diagnostic retention bound reached; reconnect/restart for a fresh epoch. No rolling self recovery."); return; }
        // Own payload memory; mutable source buffers never become epoch state.
        segment = segment with { Payload = segment.Payload.ToArray() };
        epoch.Packets.Add(new(segment, direction, (segment.TimestampUtc - epoch.Origin).TotalSeconds));
        packetCount++; epoch.Bytes += segment.Payload.Length; bytes += segment.Payload.Length; epoch.Dirty = true;
        if (segment.Flags.HasFlag(TcpFlags.Fin))
        {
            epoch.Closing = true;
            if (direction == TrafficDirection.ClientToServer) epoch.ClientFin = true; else epoch.ServerFin = true;
        }
        if (segment.Flags.HasFlag(TcpFlags.Rst)) Retire(epoch, "Reset");
        // Retain both half-closes until the shared resolver observes the FIN acknowledgments.
        else if (epoch.ClientFin && epoch.ServerFin && segment.IsAckOnly)
        {
            if (FinAcknowledged(epoch, TrafficDirection.ClientToServer) && FinAcknowledged(epoch, TrafficDirection.ServerToClient))
                Retire(epoch, "Closed");
        }
    }

    public IReadOnlyList<LiveEpochSnapshot> Snapshot()
    {
        foreach (var epoch in epochs.Values) if (epoch.Dirty) Update(epoch);
        return ended.Concat(epochs.Values.Select(e => e.Snapshot!)).ToArray();
    }

    public void FaultAll(string reason)
    {
        foreach (var epoch in epochs.Values) Fault(epoch, reason);
        // Historic closed snapshots remain diagnostic only and explicitly keep their lifecycle label.
    }

    public IReadOnlyList<LiveEpochSnapshot> Complete()
    {
        if (!completed)
        {
            foreach (var epoch in epochs.Values.ToArray()) Retire(epoch, "CaptureStopped");
            completed = true;
        }
        return Snapshot();
    }

    private ResearchCapture Capture(Epoch epoch) => new(epoch.Id, epoch.Origin, "source epoch first packet",
        epoch.Packets.OrderBy(p => p.Segment.TimestampUtc).ThenBy(p => p.Segment.PacketIndex).ToArray(), 0, 0);

    private static bool FinAcknowledged(Epoch epoch, TrafficDirection direction)
    {
        var fin = epoch.Packets.FirstOrDefault(p => p.Direction == direction && p.Segment.Flags.HasFlag(TcpFlags.Fin));
        return fin is not null && epoch.Packets.Any(p => p.Direction != direction &&
            p.Segment.TimestampUtc >= fin.Segment.TimestampUtc && p.Segment.Flags.HasFlag(TcpFlags.Ack) &&
            p.Segment.AcknowledgmentNumber == unchecked(fin.Segment.SequenceNumber + (uint)fin.Segment.DeclaredPayloadLength + 1));
    }

    private void Fault(Epoch epoch, string reason)
    {
        epoch.Fault = reason; epoch.Dirty = true;
        Release(epoch);
    }

    private void Release(Epoch epoch)
    {
        bytes -= epoch.Bytes; packetCount -= epoch.Packets.Count; epoch.Bytes = 0; epoch.Packets.Clear();
    }

    private void Retire(Epoch epoch, string lifecycle)
    {
        Update(epoch);
        ended.Enqueue(epoch.Snapshot! with { Lifecycle = lifecycle });
        while (ended.Count > limits.MaximumConnections) ended.Dequeue();
        Release(epoch); epochs.Remove(epoch.Connection);
    }

    private void Update(Epoch epoch)
    {
        epoch.Dirty = false;
        LiveEpochSnapshot Empty(string warning, int gaps = 0, int conflicts = 0) => new(epoch.Id, epoch.Connection,
            epoch.ClientIsn, epoch.ServerIsn, epoch.Fault is null ? epoch.Closing ? "HalfClosed" : "Active" : "Faulted",
            false, CurrentPlayerBindingStatus.Unknown, null, null, 0, 0, 0, 0, [], 0, gaps, conflicts, 0, 0, [warning]);
        if (epoch.Fault is { } failure) { epoch.Snapshot = Empty(failure); return; }
        if (epoch.ClientIsn is null || epoch.ServerIsn is null)
        { epoch.Snapshot = Empty("Port candidate; fresh SYN/SYN-ACK missing. Unknown identity and unproven framing origin; wait for reconnect."); return; }
        try
        {
            var capture = Capture(epoch);
            var shared = SharedProtocolPipeline.Decode(capture);
            var gaps = shared.Streams.Sum(s => s.Gaps.Count); var conflicts = shared.Streams.Sum(s => s.Conflicts.Count);
            var missingOrigin = shared.Streams.Any(s => s.DeclaredSpan != 0 && s.BaseSequence != unchecked(
                (s.Direction == TrafficDirection.ClientToServer ? epoch.ClientIsn!.Value : epoch.ServerIsn!.Value) + 1));
            // A later contiguous run after a gap is not independently known to start on a frame boundary.
            if (gaps != 0 || conflicts != 0 || missingOrigin)
            { epoch.Snapshot = Empty("Incomplete/conflicting stream; wait for missing segments. No framing resynchronization.", gaps, conflicts); return; }
            var adapter = ReplayDamageEventEpochAdapter.FromDecoded(capture, epoch.Connection, shared);
            var audit = new ReplayDamageEventBindingAssociator().Analyze(adapter.Events, adapter.Binding, adapter);
            var observed = shared.Decoded.Records.Any(r => r.Direction == TrafficDirection.ServerToClient &&
                r.OpcodeCandidate is "1536" or "3336" && LocalInitializationExtractor.Extract(r).Count != 0) || shared.Decoded.CombatCandidates.Any(c => c.Status == "Supported");
            var warnings = adapter.Binding.Diagnostics.Concat(shared.Decoded.Records.Where(r => r.DecodeStatus is
                "MalformedFraming" or "FailedContainer" or "ContainerWithUnparsedBytes" or "Suppressed").SelectMany(r => r.DecodeWarnings)).Distinct().Take(8).ToArray();
            epoch.Snapshot = new(epoch.Id, epoch.Connection, epoch.ClientIsn, epoch.ServerIsn,
                epoch.Closing ? "HalfClosed" : "Active", observed, adapter.Binding.Status, adapter.Binding.EntityId, adapter.Binding.CharacterName,
                adapter.Events.Count, audit.SelfCount, audit.OtherCount, audit.UnknownCount,
                audit.Associations.Where(a => a.Status == DamageEventPlayerAssociationStatus.Self).TakeLast(5)
                    .Select(a => adapter.Events[a.InputIndex].Amount).ToArray(),
                shared.Decoded.CombatCandidates.Count(c => c.Status != "Supported"), gaps, conflicts,
                shared.Streams.Sum(s => s.DuplicateSegments), shared.Streams.Sum(s => s.OverlapBytes), warnings);
        }
        catch (InvalidDataException ex)
        { Fault(epoch, ex.Message); epoch.Dirty = false; epoch.Snapshot = Empty(ex.Message); }
    }
}
