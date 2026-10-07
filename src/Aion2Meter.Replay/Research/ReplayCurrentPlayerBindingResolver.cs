using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

/// <summary>Stateless, fail-closed resolver for complete selected fresh replay epochs.</summary>
public sealed class ReplayCurrentPlayerBindingResolver
{
    public CurrentPlayerBinding Analyze(ResearchCapture capture, TcpConnectionSelection connection,
        string? sessionId = null, ProtocolDecodeLimits? limits = null)
    {
        try
        {
            var decoded = SharedProtocolPipeline.Decode(capture, limits).Decoded;
            return Resolve(capture, connection, decoded.Records, sessionId);
        }
        catch (InvalidDataException ex) { return Unknown(capture, connection, sessionId, "Incomplete/unsupported replay: " + ex.Message); }
    }

    public CurrentPlayerBinding Resolve(ResearchCapture capture, TcpConnectionSelection connection,
        IReadOnlyList<RawProtocolRecord> records, string? sessionId = null)
    {
        var packets = capture.Packets.OrderBy(p => p.Segment.TimestampUtc).ThenBy(p => p.Segment.PacketIndex).ToArray();
        var syn = packets.FirstOrDefault(p => p.Direction == TrafficDirection.ClientToServer && p.Segment.Flags == TcpFlags.Syn);
        var synAck = packets.FirstOrDefault(p => p.Direction == TrafficDirection.ServerToClient && p.Segment.Flags.HasFlag(TcpFlags.Syn) && p.Segment.Flags.HasFlag(TcpFlags.Ack));
        var scope = Scope(capture, connection, sessionId, syn, synAck);
        DateTimeOffset? coverage = packets.Length == 0 ? null : packets[^1].Segment.TimestampUtc;
        var evidence = new List<CurrentPlayerBindingEvidence>();
        CurrentPlayerBinding End(CurrentPlayerBindingStatus status, string reason,
            CurrentPlayerBindingEvidence? first = null, CurrentPlayerBindingEvidence? confirm = null, DateTimeOffset? until = null) =>
            new(status, scope, status == CurrentPlayerBindingStatus.Resolved ? confirm!.EntityId : null,
                status == CurrentPlayerBindingStatus.Resolved ? confirm!.CharacterName : null,
                first?.Timestamp ?? evidence.Where(e => e.RecordTag == "1536").Select(e => (DateTimeOffset?)e.Timestamp).Min(),
                status == CurrentPlayerBindingStatus.Resolved ? confirm!.CompletionTimestamp : null,
                status == CurrentPlayerBindingStatus.Resolved ? until : null, coverage, evidence, [reason]);
        if (packets.Length == 0) return End(CurrentPlayerBindingStatus.Unknown, "No selected game replay packets; not applicable.");
        if (capture.HeaderErrors != 0 || packets.Any(p => p.Segment.IsTruncated || connection.Direction(p.Segment) != p.Direction || p.Segment.PacketIndex <= 0) ||
            packets.Select(p => p.Segment.PacketIndex).Distinct().Count() != packets.Length)
            return End(CurrentPlayerBindingStatus.Unknown, "Invalid/truncated connection or packet provenance.");
        if (syn is null || synAck is null || synAck.Segment.TimestampUtc < syn.Segment.TimestampUtc ||
            synAck.Segment.AcknowledgmentNumber != unchecked(syn.Segment.SequenceNumber + 1))
            return End(CurrentPlayerBindingStatus.Unknown, "Complete fresh client SYN/server SYN-ACK epoch not observed.");
        if (packets.Where(p => p.Segment.Flags.HasFlag(TcpFlags.Syn)).Any(p => p.Direction == TrafficDirection.ClientToServer
                ? p.Segment.SequenceNumber != syn.Segment.SequenceNumber || p.Segment.Flags != TcpFlags.Syn
                : p.Segment.SequenceNumber != synAck.Segment.SequenceNumber || !p.Segment.Flags.HasFlag(TcpFlags.Ack)) ||
            packets.Any(p => p.Segment.TimestampUtc < syn.Segment.TimestampUtc) ||
            packets.Any(p => p.Segment.Flags.HasFlag(TcpFlags.Syn) && packets.Any(q => q.Segment.Flags.HasFlag(TcpFlags.Rst) && q.Segment.TimestampUtc < p.Segment.TimestampUtc)))
            return End(CurrentPlayerBindingStatus.Unknown, "Multiple/unmodeled TCP epochs; no cross-epoch inheritance.");
        var handshake = packets.FirstOrDefault(p => p.Direction == TrafficDirection.ClientToServer && p.Segment.TimestampUtc >= synAck.Segment.TimestampUtc &&
            !p.Segment.Flags.HasFlag(TcpFlags.Syn) && p.Segment.Flags.HasFlag(TcpFlags.Ack) &&
            p.Segment.SequenceNumber == unchecked(syn.Segment.SequenceNumber + 1) && p.Segment.AcknowledgmentNumber == unchecked(synAck.Segment.SequenceNumber + 1));
        if (handshake is null) return End(CurrentPlayerBindingStatus.Unknown, "Handshake completion ACK not observed.");
        try
        {
            foreach (var d in Enum.GetValues<TrafficDirection>())
            {
                var stream = TcpStreamReassembler.Assemble(packets, d);
                var expected = unchecked((d == TrafficDirection.ClientToServer ? syn : synAck).Segment.SequenceNumber + 1);
                if (!stream.SynObserved || stream.Gaps.Count != 0 || stream.Conflicts.Count != 0 || stream.DeclaredSpan != 0 && stream.BaseSequence != expected)
                    return End(CurrentPlayerBindingStatus.Unknown, "Incomplete/conflicting reconstructed fresh stream.");
            }
        }
        catch (InvalidDataException ex) { return End(CurrentPlayerBindingStatus.Unknown, ex.Message); }
        if (records.Any(r => r.Direction == TrafficDirection.ServerToClient && r.DecodeStatus is
            "Suppressed" or "FailedContainer" or "ContainerWithUnparsedBytes" or "MalformedFraming" or "MalformedInnerFraming"))
            return End(CurrentPlayerBindingStatus.Unknown, "Incomplete/unsupported inbound application framing or container.");
        var packetLookup = packets.ToDictionary(p => p.Segment.PacketIndex);
        var recordLookup = records.GroupBy(r => r.RecordId).ToDictionary(g => g.Key, g => g.First());
        var unique = new Dictionary<string, (RawProtocolRecord Raw, IReadOnlyList<CurrentPlayerBindingEvidence> Candidates)>();
        foreach (var r in records.Where(r => r.Direction == TrafficDirection.ServerToClient && r.OpcodeCandidate is "1536" or "3336"))
        {
            if (!ValidProvenance(r, capture, packetLookup, recordLookup, handshake.Segment.TimestampUtc))
                return End(CurrentPlayerBindingStatus.Unknown, "Initialization has missing/mismatched epoch or completion provenance.");
            var candidates = LocalInitializationExtractor.Extract(r); evidence.AddRange(candidates);
            if (candidates.Count == 0) return End(CurrentPlayerBindingStatus.Unknown, "Malformed/unsupported initialization; no safe text/numeric candidate.");
            var key = LocalInitializationExtractor.ProvenanceIdentity(r);
            if (unique.TryGetValue(key, out var previous))
            {
                if (!r.RawBytes.AsSpan().SequenceEqual(previous.Raw.RawBytes)) return End(CurrentPlayerBindingStatus.Conflict, "Same provenance contains conflicting record bytes.");
                evidence.RemoveRange(evidence.Count - candidates.Count, candidates.Count);
            }
            else unique.Add(key, (r, candidates));
        }
        var left = unique.Values.Where(v => v.Raw.OpcodeCandidate == "1536").ToArray();
        var right = unique.Values.Where(v => v.Raw.OpcodeCandidate == "3336").ToArray();
        if (left.Length == 0 || right.Length == 0) return End(CurrentPlayerBindingStatus.Unknown, "Required complete 1536 + later 3336 pair missing.");
        var pairs = new List<(CurrentPlayerBindingEvidence First, CurrentPlayerBindingEvidence Confirm)>();
        var ambiguous = false; var contradiction = false;
        foreach (var a in left)
        foreach (var b in right)
        {
            if (!Precedes(a.Raw, b.Raw)) continue;
            var matches = (from x in a.Candidates from y in b.Candidates
                where x.EntityId == y.EntityId && StringComparer.Ordinal.Equals(x.CharacterName, y.CharacterName) select (First: x, Confirm: y)).ToArray();
            if (matches.Length > 1) ambiguous = true;
            else if (matches.Length == 1) pairs.Add(matches[0]);
            else if (a.Candidates.Count == 1 && b.Candidates.Count == 1) contradiction = true;
        }
        if (contradiction || pairs.Select(p => (p.First.EntityId, p.First.CharacterName)).Distinct().Count() > 1)
            return End(CurrentPlayerBindingStatus.Conflict, "Qualifying initialization assignments disagree; no majority vote.");
        if (ambiguous || pairs.Count != 1 || left.Length != 1 || right.Length != 1)
            return End(CurrentPlayerBindingStatus.Unknown, "Ambiguous fields/order or unmodeled multiple initialization sequences.");
        var pair = pairs[0]; var termination = Termination(packets);
        if (termination is { } stop && stop < pair.Confirm.CompletionTimestamp)
            return End(CurrentPlayerBindingStatus.Unknown, "Connection ended before complete binding confirmation.");
        return End(CurrentPlayerBindingStatus.Resolved,
            "Unique exact 1536/3336 agreement in a complete fresh replay epoch; live lifecycle remains unmodeled.", pair.First, pair.Confirm, termination);
    }

    private static bool ValidProvenance(RawProtocolRecord r, ResearchCapture capture,
        IReadOnlyDictionary<long, ResearchPacket> packets, IReadOnlyDictionary<int, RawProtocolRecord> records, DateTimeOffset handshake)
    {
        if (r.SourceCapture != capture.Path || r.RecordId <= 0 || r.OuterFrameId <= 0 || r.StreamOffset < 0 || r.OuterFrameOffset < 0 ||
            r.TimestampUtc < handshake || r.CompletionUtc < r.TimestampUtc || !packets.TryGetValue(r.PacketIndex, out var arrival) ||
            !packets.TryGetValue(r.CompletionPacketIndex, out var completion) || arrival.Direction != r.Direction || completion.Direction != r.Direction ||
            arrival.Segment.TimestampUtc != r.TimestampUtc || completion.Segment.TimestampUtc != r.CompletionUtc) return false;
        var seen = new HashSet<int>();
        foreach (var p in r.ContainerPath)
            if (p.InnerOffset < 0 || p.ContainerRecordId == r.RecordId || !seen.Add(p.ContainerRecordId) ||
                !records.TryGetValue(p.ContainerRecordId, out var container) || container.DecodeStatus != "DecodedContainer" ||
                container.Direction != r.Direction || container.SourceCapture != r.SourceCapture || container.CompletionUtc != r.CompletionUtc ||
                container.OuterFrameOffset != r.OuterFrameOffset) return false;
        return true;
    }

    private static bool Precedes(RawProtocolRecord a, RawProtocolRecord b)
    {
        if (a.TimestampUtc > b.TimestampUtc || a.CompletionUtc > b.CompletionUtc) return false;
        if (a.StreamOffset != b.StreamOffset) return a.StreamOffset < b.StreamOffset;
        if (a.ContainerPath.Count != b.ContainerPath.Count || a.ContainerPath.Count == 0) return false;
        for (var i = 0; i < a.ContainerPath.Count; i++)
        {
            if (a.ContainerPath[i].ContainerRecordId != b.ContainerPath[i].ContainerRecordId) return false;
            if (a.ContainerPath[i].InnerOffset != b.ContainerPath[i].InnerOffset) return a.ContainerPath[i].InnerOffset < b.ContainerPath[i].InnerOffset;
        }
        return false;
    }

    private static DateTimeOffset? Termination(IReadOnlyList<ResearchPacket> packets)
    {
        var reset = packets.FirstOrDefault(p => p.Segment.Flags.HasFlag(TcpFlags.Rst));
        if (reset is not null) return reset.Segment.TimestampUtc;
        var fins = Enum.GetValues<TrafficDirection>().Select(d => packets.FirstOrDefault(p => p.Direction == d && p.Segment.Flags.HasFlag(TcpFlags.Fin))).ToArray();
        if (fins.Any(p => p is null)) return null;
        var acks = new List<DateTimeOffset>();
        foreach (var f in fins)
        {
            var s = f!.Segment;
            var ack = packets.FirstOrDefault(p => p.Direction != f.Direction && p.Segment.TimestampUtc >= s.TimestampUtc && p.Segment.Flags.HasFlag(TcpFlags.Ack) &&
                p.Segment.AcknowledgmentNumber == unchecked(s.SequenceNumber + (uint)s.DeclaredPayloadLength + 1));
            if (ack is null) return null;
            acks.Add(ack.Segment.TimestampUtc);
        }
        return acks.Max();
    }

    private static ReplayConnectionScope Scope(ResearchCapture capture, TcpConnectionSelection c, string? sessionId, ResearchPacket? syn, ResearchPacket? synAck) =>
        new(capture.Path, sessionId, new System.Net.IPEndPoint(c.LocalIp, c.LocalPort).ToString(), new System.Net.IPEndPoint(c.RemoteIp, c.RemotePort).ToString(),
            syn?.Segment.SequenceNumber, synAck?.Segment.SequenceNumber, syn?.Segment.TimestampUtc, syn?.Segment.PacketIndex);
    private static CurrentPlayerBinding Unknown(ResearchCapture capture, TcpConnectionSelection c, string? sessionId, string reason) =>
        new(CurrentPlayerBindingStatus.Unknown, Scope(capture, c, sessionId, null, null), null, null, null, null, null,
            capture.Packets.Count == 0 ? null : capture.Packets.Max(p => p.Segment.TimestampUtc), [], [reason]);
}
