using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public sealed record StreamChunk(long Offset, byte[] Bytes, DateTimeOffset TimestampUtc, long PacketIndex)
{
    public long End => Offset + Bytes.Length;
}
public sealed record ReassembledStream(TrafficDirection Direction, uint BaseSequence, IReadOnlyList<StreamChunk> Chunks,
    IReadOnlyList<ByteRange> Gaps, IReadOnlyList<ByteRange> Conflicts, int DuplicateSegments, long OverlapBytes,
    long DeclaredSpan, bool SynObserved);

public static class TcpStreamReassembler
{
    public static ReassembledStream Assemble(IReadOnlyList<ResearchPacket> packets, TrafficDirection direction)
    {
        var timeline = packets.Where(p => p.Direction == direction).Select(p => p.Segment)
            .OrderBy(p => p.TimestampUtc).ThenBy(p => p.PacketIndex).ToArray();
        // Multiple connection epochs cannot safely be joined by a four-tuple alone.
        if (timeline.Where(p => p.Flags.HasFlag(TcpFlags.Syn)).Select(p => p.SequenceNumber).Distinct().Count() > 1)
            throw new InvalidDataException("Multiple TCP SYN sequence origins: select a time window containing one connection epoch.");
        var payloads = timeline.Where(p => p.DeclaredPayloadLength > 0).ToArray();
        if (payloads.Length == 0) return new(direction, 0, [], [], [], 0, 0, 0, timeline.Any(p => p.Flags.HasFlag(TcpFlags.Syn)));
        var reference = payloads[0].PayloadSequence;
        // Serial arithmetic handles a single wrap, with an explicitly bounded (< 2 GiB) observed sequence span.
        long Delta(TcpSegment p) => unchecked((int)(p.PayloadSequence - reference));
        var minimum = payloads.Min(Delta);
        var maximum = payloads.Max(p => Delta(p) + p.DeclaredPayloadLength);
        if (maximum - minimum >= int.MaxValue)
            throw new InvalidDataException("TCP sequence span is ambiguous or exceeds the supported 2 GiB range.");
        var chunks = new List<StreamChunk>();
        var conflicts = new List<ByteRange>();
        var duplicates = 0;
        long overlap = 0;
        foreach (var packet in payloads)
        {
            var start = Delta(packet) - minimum;
            var end = start + packet.Payload.Length;
            var cursor = start;
            var additions = new List<StreamChunk>();
            var different = false;
            // Binary search avoids scanning the complete stream for each normal in-order segment.
            var lo = 0;
            var hi = chunks.Count;
            while (lo < hi) { var mid = (lo + hi) / 2; if (chunks[mid].End <= start) lo = mid + 1; else hi = mid; }
            var insertion = lo;
            for (var i = lo; i < chunks.Count && chunks[i].Offset < end; i++)
            {
                var existing = chunks[i];
                if (cursor < existing.Offset) Add(cursor, Math.Min(existing.Offset, end));
                var first = Math.Max(start, existing.Offset);
                var last = Math.Min(end, existing.End);
                overlap += last - first;
                long? conflictStart = null;
                for (var offset = first; offset < last; offset++)
                {
                    var differs = packet.Payload[(int)(offset - start)] != existing.Bytes[(int)(offset - existing.Offset)];
                    if (differs) { different = true; conflictStart ??= offset; }
                    else if (conflictStart is { } c) { AddConflict(c, offset - c); conflictStart = null; }
                }
                if (conflictStart is { } tail) AddConflict(tail, last - tail);
                cursor = Math.Max(cursor, last);
            }
            if (cursor < end) Add(cursor, end);
            if (packet.Payload.Length > 0 && additions.Count == 0 && !different) duplicates++;
            foreach (var chunk in additions)
            {
                while (insertion < chunks.Count && chunks[insertion].Offset < chunk.Offset) insertion++;
                chunks.Insert(insertion++, chunk);
            }
            void Add(long first, long last)
            {
                if (last <= first) return;
                additions.Add(new(first, packet.Payload.AsSpan((int)(first - start), (int)(last - first)).ToArray(), packet.TimestampUtc, packet.PacketIndex));
                cursor = last;
            }
        }
        var span = maximum - minimum;
        var gaps = new List<ByteRange>();
        long previous = 0;
        foreach (var chunk in chunks) { if (chunk.Offset > previous) gaps.Add(new(previous, chunk.Offset - previous)); previous = chunk.End; }
        if (previous < span) gaps.Add(new(previous, span - previous));
        return new(direction, unchecked(reference + (uint)minimum), chunks, gaps, MergeRanges(conflicts), duplicates, overlap, span,
            timeline.Any(p => p.Flags.HasFlag(TcpFlags.Syn)));

        void AddConflict(long offset, long length)
        {
            if (conflicts.Count >= 100_000) throw new InvalidDataException("TCP overlap conflict range limit reached (100000). Use a smaller time window.");
            conflicts.Add(new(offset, length));
        }
    }

    private static IReadOnlyList<ByteRange> MergeRanges(List<ByteRange> ranges)
    {
        var result = new List<ByteRange>();
        foreach (var range in ranges.OrderBy(r => r.Offset))
        {
            if (result.Count == 0 || result[^1].End < range.Offset) result.Add(range);
            else { var last = result[^1]; result[^1] = new(last.Offset, Math.Max(last.End, range.End) - last.Offset); }
        }
        return result;
    }
}
