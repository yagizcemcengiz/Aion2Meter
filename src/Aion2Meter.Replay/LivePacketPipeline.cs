using System.Net;
using System.Diagnostics;
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
/// Single-consumer bounded live foundation. Diagnostic snapshots replace prior results; optional combat
/// publication uses an ACKed complete-frame boundary. The finite shared stack remains the only parser.
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
        public LiveStreamCheckpoint Checkpoint { get; } = new();
        public CurrentPlayerBinding? LastBinding;
        public int RetiredDuplicates, CommittedDuplicates;
        public long RetiredOverlap, CommittedOverlap;
    }

    private readonly HashSet<IPAddress> localAddresses;
    private readonly ushort servicePort;
    private readonly LivePipelineLimits limits;
    private readonly LiveCombatFeed? combatFeed;
    private readonly bool checkpointRetention;
    private long completedCheckpoints, completedRetiredBytes;
    private readonly TcpResearchPacketReader reader = new();
    private readonly Dictionary<TcpConnectionSelection, Epoch> epochs = [];
    private readonly Queue<LiveEpochSnapshot> ended = [];
    private string? sourceId, interfaceId;
    private long lastIndex, nextEpoch, bytes;
    private int packetCount;
    private bool completed;
    private DateTimeOffset? lastTimestamp;
    public double LastRecomputeMilliseconds { get; private set; }
    public double LastSnapshotMilliseconds { get; private set; }
    public long RecomputeCount { get; private set; }
    public int SelectedPacketCount => packetCount;
    public long RetainedPayloadBytes => bytes;
    public long CheckpointCount => completedCheckpoints + epochs.Values.Sum(e => e.Checkpoint.Count);
    public long RetiredPayloadBytes => completedRetiredBytes + epochs.Values.Sum(e => e.Checkpoint.RetiredBytes);
    public int VerificationBytes => epochs.Values.Sum(e => e.Checkpoint.VerificationBytes);
    public int BindingEvidenceCount => epochs.Values.Sum(e => e.LastBinding?.Evidence.Count ?? 0);
    public long BindingEvidenceBytes => epochs.Values.Sum(e => e.LastBinding?.Evidence.Sum(b => (long)Convert.FromBase64String(b.RawRecordBase64).Length) ?? 0);
    public long MalformedPackets { get; private set; }
    public long UnsupportedPackets { get; private set; }
    public long IgnoredPackets { get; private set; }
    public long RejectedFlows { get; private set; }

    public LivePacketPipeline(IEnumerable<IPAddress> localAddresses, ushort servicePort = 13328, LivePipelineLimits? limits = null,
        LiveCombatFeed? combatFeed = null, bool checkpointRetention = true)
    {
        this.localAddresses = localAddresses.ToHashSet(); this.servicePort = servicePort; this.limits = limits ?? new();
        this.combatFeed = combatFeed;
        this.checkpointRetention = checkpointRetention && combatFeed is not null;
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
        if (combatFeed is not null && lastTimestamp is { } previousTime && input.Packet.TimestampUtc < previousTime)
        {
            FaultAll("Capture timestamps moved backwards; published arrival provenance cannot be revised.");
            throw new InvalidDataException("Live publication requires nondecreasing capture timestamps.");
        }
        lastTimestamp = input.Packet.TimestampUtc;
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
        if (this.checkpointRetention && epoch.Checkpoint.Binding is not null && epoch.ClientIsn is { } ci && epoch.ServerIsn is { } si)
        {
            try
            {
                var originalLength = segment.Payload.Length;
                segment = epoch.Checkpoint.ValidateAndTrim(segment, direction, ci, si);
                epoch.RetiredOverlap += originalLength - segment.Payload.Length;
                if (originalLength > 0 && segment.Payload.Length == 0) epoch.RetiredDuplicates++;
            }
            catch (InvalidDataException ex) { Fault(epoch, ex.Message); return; }
        }
        else if (!segment.Flags.HasFlag(TcpFlags.Syn) && isn is { } origin &&
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
        var started = Stopwatch.GetTimestamp();
        foreach (var epoch in epochs.Values) if (epoch.Dirty) Update(epoch);
        LastSnapshotMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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
        epoch.Checkpoint.Clear();
        epoch.LastBinding = null;
    }

    private void Release(Epoch epoch)
    {
        bytes -= epoch.Bytes; packetCount -= epoch.Packets.Count; epoch.Bytes = 0; epoch.Packets.Clear(); epoch.Packets.TrimExcess();
    }

    private void Retire(Epoch epoch, string lifecycle)
    {
        Update(epoch);
        var snapshot = epoch.Snapshot! with { Lifecycle = epoch.Snapshot!.Lifecycle == "Faulted" ? "Faulted" : lifecycle };
        ended.Enqueue(snapshot);
        combatFeed?.Observe(snapshot, null);
        while (ended.Count > limits.MaximumConnections) ended.Dequeue();
        completedCheckpoints += epoch.Checkpoint.Count; completedRetiredBytes += epoch.Checkpoint.RetiredBytes;
        Release(epoch); epoch.Checkpoint.Clear(); epochs.Remove(epoch.Connection);
    }

    private void Update(Epoch epoch)
    {
        var started = Stopwatch.GetTimestamp();
        try { UpdateCore(epoch); }
        finally { LastRecomputeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; RecomputeCount++; }
    }

    private void UpdateCore(Epoch epoch)
    {
        epoch.Dirty = false;
        LiveEpochSnapshot Empty(string warning, int gaps = 0, int conflicts = 0) => new(epoch.Id, epoch.Connection,
            epoch.ClientIsn, epoch.ServerIsn, epoch.Fault is null ? epoch.Closing ? "HalfClosed" : "Active" : "Faulted",
            false, CurrentPlayerBindingStatus.Unknown, null, null, 0, 0, 0, 0, [], 0, gaps, conflicts, 0, 0, [warning]);
        void Observe(ReplayDamageEventEpochAdapter? adapter = null)
        {
            if (combatFeed?.Observe(epoch.Snapshot!, adapter) == false && epoch.Fault is null)
            {
                Fault(epoch, "Publication/binding provenance invalidated; fresh reconnect required.");
                epoch.Dirty = false;
                epoch.Snapshot = epoch.Snapshot! with { Lifecycle = "Faulted", BindingStatus = CurrentPlayerBindingStatus.Unknown,
                    EntityId = null, CharacterName = null, Warnings = [epoch.Fault!] };
            }
        }
        if (epoch.Fault is { } failure) { epoch.Snapshot = Empty(failure); Observe(); return; }
        if (epoch.ClientIsn is null || epoch.ServerIsn is null)
        { epoch.Snapshot = Empty("Port candidate; fresh SYN/SYN-ACK missing. Unknown identity and unproven framing origin; wait for reconnect."); Observe(); return; }
        try
        {
            var capture = Capture(epoch);
            var checkpoint = checkpointRetention && epoch.Checkpoint.Binding is not null ? epoch.Checkpoint : null;
            var streams = checkpoint?.Reassemble(capture, epoch.ClientIsn.Value, epoch.ServerIsn.Value) ?? SharedProtocolPipeline.Reassemble(capture);
            checkpoint?.CheckPrivacy(streams);
            var gaps = streams.Sum(s => s.Gaps.Count); var conflicts = streams.Sum(s => s.Conflicts.Count);
            var missingOrigin = streams.Any(s => s.DeclaredSpan != 0 && s.BaseSequence != unchecked(
                (s.Direction == TrafficDirection.ClientToServer ? epoch.ClientIsn!.Value : epoch.ServerIsn!.Value) + 1));
            var pendingBytes = false;
            if (combatFeed is not null)
            {
                if (conflicts != 0)
                {
                    Fault(epoch, "Conflicting TCP bytes invalidate live publication; fresh reconnect required.");
                    epoch.Dirty = false; epoch.Snapshot = Empty(epoch.Fault!, gaps, conflicts); Observe(); return;
                }
                var stable = LivePublicationBoundary.Select(capture, streams, epoch.ClientIsn.Value, epoch.ServerIsn.Value, checkpoint);
                pendingBytes = stable.Packets.Sum(p => p.Segment.Payload.Length) < capture.Packets.Sum(p => p.Segment.Payload.Length);
                capture = stable;
                // The exact shared stack decodes this prefix; no alternate framing or combat grammar.
                streams = checkpoint?.Reassemble(capture, epoch.ClientIsn.Value, epoch.ServerIsn.Value) ?? SharedProtocolPipeline.Reassemble(capture);
            }
            // A later contiguous run after a gap is not independently known to start on a frame boundary.
            if (combatFeed is null && (gaps != 0 || conflicts != 0 || missingOrigin))
            { epoch.Snapshot = Empty("Incomplete/conflicting stream; wait for missing segments. No framing resynchronization.", gaps, conflicts); return; }
            var shared = SharedProtocolPipeline.DecodeStreams(capture, streams);
            if (checkpointRetention && shared.Decoded.Records.Any(r => r.DecodeStatus is
                "Suppressed" or "FailedContainer" or "ContainerWithUnparsedBytes" or "MalformedFraming" or "MalformedInnerFraming"))
                throw new InvalidDataException("Unvalidated application/container prefix cannot be checkpointed; fresh reconnect required.");
            var closure = epoch.Packets.FirstOrDefault(p => p.Segment.Flags.HasFlag(TcpFlags.Rst));
            if (closure is null && epoch.ClientFin && epoch.ServerFin && FinAcknowledged(epoch, TrafficDirection.ClientToServer) && FinAcknowledged(epoch, TrafficDirection.ServerToClient))
                closure = epoch.Packets.Last();
            var adapter = ReplayDamageEventEpochAdapter.FromDecoded(capture, epoch.Connection, shared,
                checkpointBinding: checkpoint?.Binding, closedAt: closure?.Segment.TimestampUtc, closedPacket: closure?.Segment.PacketIndex);
            var audit = new ReplayDamageEventBindingAssociator().Analyze(adapter.Events, adapter.Binding, adapter);
            epoch.LastBinding = adapter.Binding;
            var observed = shared.Decoded.Records.Any(r => r.Direction == TrafficDirection.ServerToClient &&
                r.OpcodeCandidate is "1536" or "3336" && LocalInitializationExtractor.Extract(r).Count != 0) || shared.Decoded.CombatCandidates.Any(c => c.Status == "Supported");
            var warnings = adapter.Binding.Diagnostics.Concat(pendingBytes ? ["Waiting for contiguous complete frames and peer ACK; pending bytes are not published."] : Array.Empty<string>()).Concat(shared.Decoded.Records.Where(r => r.DecodeStatus is
                "MalformedFraming" or "FailedContainer" or "ContainerWithUnparsedBytes" or "Suppressed").SelectMany(r => r.DecodeWarnings)).Distinct().Take(8).ToArray();
            var prior = checkpoint is null ? null : epoch.Snapshot;
            epoch.Snapshot = new(epoch.Id, epoch.Connection, epoch.ClientIsn, epoch.ServerIsn,
                epoch.Closing ? "HalfClosed" : "Active", observed || prior?.ProtocolObserved == true, adapter.Binding.Status, adapter.Binding.EntityId, adapter.Binding.CharacterName,
                (prior?.AcceptedEvents ?? 0) + adapter.Events.Count, (prior?.SelfCount ?? 0) + audit.SelfCount,
                (prior?.OtherCount ?? 0) + audit.OtherCount, (prior?.UnknownCount ?? 0) + audit.UnknownCount,
                (prior?.RecentSelfAmounts ?? []).Concat(audit.Associations.Where(a => a.Status == DamageEventPlayerAssociationStatus.Self)
                    .Select(a => adapter.Events[a.InputIndex].Amount)).TakeLast(5).ToArray(),
                (prior?.UnsupportedCandidates ?? 0) + shared.Decoded.CombatCandidates.Count(c => c.Status != "Supported"), gaps, conflicts,
                epoch.RetiredDuplicates + epoch.CommittedDuplicates + shared.Streams.Sum(s => s.DuplicateSegments),
                epoch.RetiredOverlap + epoch.CommittedOverlap + shared.Streams.Sum(s => s.OverlapBytes), warnings);
            Observe(adapter);
            if (checkpointRetention && epoch.Fault is null && adapter.Binding.Status == CurrentPlayerBindingStatus.Resolved)
            {
                epoch.Checkpoint.Binding = adapter.Binding;
                var ends = epoch.Checkpoint.Commit(streams);
                combatFeed!.CommitCheckpoint(epoch.Id, ends);
                epoch.CommittedDuplicates += shared.Streams.Sum(s => s.DuplicateSegments);
                epoch.CommittedOverlap += shared.Streams.Sum(s => s.OverlapBytes);
                RetainTail(epoch);
            }
        }
        catch (InvalidDataException ex)
        { Fault(epoch, ex.Message); epoch.Dirty = false; epoch.Snapshot = Empty(ex.Message); Observe(); }
    }

    private void RetainTail(Epoch epoch)
    {
        var kept = new List<ResearchPacket>();
        // Preserve the latest ACK and half-close evidence, not all historical control packets.
        var controls = new List<ResearchPacket>();
        foreach (var d in Enum.GetValues<TrafficDirection>())
        {
            var peer = d == TrafficDirection.ClientToServer ? TrafficDirection.ServerToClient : TrafficDirection.ClientToServer;
            var origin = epoch.Checkpoint.Sequence(peer, epoch.ClientIsn!.Value, epoch.ServerIsn!.Value);
            var ack = epoch.Packets.Where(p => p.Direction == d && p.Segment.Flags.HasFlag(TcpFlags.Ack))
                .OrderBy(p => unchecked((int)(p.Segment.AcknowledgmentNumber - origin))).ThenBy(p => p.Segment.PacketIndex).LastOrDefault();
            if (ack is not null) controls.Add(ack);
            var fin = epoch.Packets.FirstOrDefault(p => p.Direction == d && p.Segment.Flags.HasFlag(TcpFlags.Fin));
            if (fin is not null)
            {
                controls.Add(fin);
                var confirmation = epoch.Packets.FirstOrDefault(p => p.Direction != d && p.Segment.Flags.HasFlag(TcpFlags.Ack) &&
                    p.Segment.AcknowledgmentNumber == unchecked(fin.Segment.SequenceNumber + (uint)fin.Segment.DeclaredPayloadLength + 1));
                if (confirmation is not null) controls.Add(confirmation);
            }
        }
        var indexes = controls.Select(p => p.Segment.PacketIndex).ToHashSet();
        foreach (var packet in epoch.Packets)
        {
            var s = packet.Segment;
            var sequence = epoch.Checkpoint.Sequence(packet.Direction, epoch.ClientIsn!.Value, epoch.ServerIsn!.Value);
            var skip = (int)Math.Clamp(-(long)unchecked((int)(s.PayloadSequence - sequence)), 0, s.Payload.Length);
            if (skip < s.Payload.Length || indexes.Contains(s.PacketIndex))
                kept.Add(packet with { Segment = s with { SequenceNumber = unchecked(s.SequenceNumber + (uint)skip),
                    Payload = s.Payload[skip..], DeclaredPayloadLength = s.DeclaredPayloadLength - skip } });
        }
        bytes -= epoch.Bytes; packetCount -= epoch.Packets.Count;
        epoch.Packets.Clear(); epoch.Packets.AddRange(kept); epoch.Packets.TrimExcess();
        epoch.Bytes = kept.Sum(p => (long)p.Segment.Payload.Length); bytes += epoch.Bytes; packetCount += kept.Count;
    }
}
