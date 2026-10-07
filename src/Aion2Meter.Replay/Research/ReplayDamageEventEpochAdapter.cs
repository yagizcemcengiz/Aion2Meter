using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

/// <summary>
/// Explicit finite replay epoch bridge. Derives transport scope and record membership from its own
/// selected decode, never from an event's path or a caller-supplied binding. No mutable decoder data escapes.
/// </summary>
public sealed class ReplayDamageEventEpochAdapter
{
    public CurrentPlayerBinding Binding { get; }
    public ReplayConnectionScope? Scope => Binding.Scope;
    public IReadOnlyList<DamageEvent> Events { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    private readonly HashSet<DamageEvent> eventOccurrences;
    private readonly IReadOnlyList<CurrentPlayerBindingEvidence> confirmations;
    private readonly long? closurePacketIndex;

    private ReplayDamageEventEpochAdapter(CurrentPlayerBinding binding, IEnumerable<DamageEvent> events,
        IEnumerable<string> diagnostics, long? closurePacketIndex = null)
    {
        Binding = binding; Events = Array.AsReadOnly(events.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        // Generic DamageEvent identity intentionally lacks ISNs. Even an identical location hash from
        // another decode cannot attest to this transport epoch; only this adapter's owned projection can.
        eventOccurrences = Events.ToHashSet<DamageEvent>(ReferenceEqualityComparer.Instance);
        confirmations = Array.AsReadOnly(binding.QualifyingEvidence.Where(e => e.RecordTag == "3336").ToArray());
        this.closurePacketIndex = closurePacketIndex;
    }

    public static ReplayDamageEventEpochAdapter Create(ResearchCapture capture,
        TcpConnectionSelection connection, string? sessionId = null, ProtocolDecodeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(connection);
        try
        {
            var streams = Enum.GetValues<TrafficDirection>().Select(d => TcpStreamReassembler.Assemble(capture.Packets, d)).ToArray();
            var decoded = new ReplayProtocolDecoder(limits).Decode(capture.Path, streams, capture.OriginUtc);
            var binding = new ReplayCurrentPlayerBindingResolver().Resolve(capture, connection, decoded.Records, sessionId);
            var accepted = decoded.CombatCandidates.Where(c => c.Status == "Supported").Select(SupportedCombatRecord.From)
                .OrderBy(c => c.RawRecord, ResearchRecordArrivalComparer.Instance);
            return new(binding, new DamageEventProjector().ProjectMany(accepted),
                [$"Selected finite decode: gaps={streams.Sum(s => s.Gaps.Count)}; conflicts={streams.Sum(s => s.Conflicts.Count)}; unsupported combat candidates={decoded.CombatCandidates.Count(c => c.Status != "Supported")}."],
                ClosurePacket(capture, binding));
        }
        catch (InvalidDataException ex)
        {
            var binding = new ReplayCurrentPlayerBindingResolver().Analyze(capture, connection, sessionId, limits);
            return new(binding, [], ["No supported event projection from unmodeled replay: " + ex.Message]);
        }
    }

    internal bool Contains(DamageEvent e) => Binding.Status == CurrentPlayerBindingStatus.Resolved && eventOccurrences.Contains(e);

    internal bool WithinObservedClosure(DamageEvent e) => Binding.ValidUntil is not { } stop ||
        closurePacketIndex is { } index && e.Timestamp <= stop && e.Provenance.CompletionTimestamp <= stop &&
        (e.Timestamp != stop || e.Provenance.PacketIndex <= index) &&
        (e.Provenance.CompletionTimestamp != stop || e.Provenance.CompletionPacketIndex <= index);

    private static long? ClosurePacket(ResearchCapture capture, CurrentPlayerBinding binding)
    {
        if (binding.ValidUntil is not { } stop) return null;
        var packets = capture.Packets.OrderBy(p => p.Segment.TimestampUtc).ThenBy(p => p.Segment.PacketIndex).ToArray();
        var reset = packets.FirstOrDefault(p => p.Segment.Flags.HasFlag(TcpFlags.Rst));
        if (reset is not null) return reset.Segment.PacketIndex;
        var acks = new List<ResearchPacket>();
        foreach (var d in Enum.GetValues<TrafficDirection>())
        {
            var fin = packets.FirstOrDefault(p => p.Direction == d && p.Segment.Flags.HasFlag(TcpFlags.Fin));
            if (fin is null) return null;
            var s = fin.Segment;
            var ack = packets.FirstOrDefault(p => p.Direction != d && p.Segment.TimestampUtc >= s.TimestampUtc &&
                p.Segment.Flags.HasFlag(TcpFlags.Ack) && p.Segment.AcknowledgmentNumber == unchecked(s.SequenceNumber + (uint)s.DeclaredPayloadLength + 1));
            if (ack is null) return null;
            acks.Add(ack);
        }
        return acks.Where(p => p.Segment.TimestampUtc == stop).Select(p => (long?)p.Segment.PacketIndex).Max();
    }

    internal bool Confirms(CurrentPlayerBindingEvidence evidence) => confirmations.Any(c =>
        c.ProvenanceIdentity == evidence.ProvenanceIdentity && c.RawRecordSha256 == evidence.RawRecordSha256 &&
        c.EntityId == evidence.EntityId && c.SourceCapture == evidence.SourceCapture && c.RecordId == evidence.RecordId &&
        c.Direction == evidence.Direction && c.StreamOffset == evidence.StreamOffset && c.OuterFrameId == evidence.OuterFrameId &&
        c.OuterFrameOffset == evidence.OuterFrameOffset && c.Timestamp == evidence.Timestamp &&
        c.CompletionTimestamp == evidence.CompletionTimestamp && c.PacketIndex == evidence.PacketIndex &&
        c.CompletionPacketIndex == evidence.CompletionPacketIndex && c.FrameLength == evidence.FrameLength &&
        c.ContainerPath.SequenceEqual(evidence.ContainerPath));
}
