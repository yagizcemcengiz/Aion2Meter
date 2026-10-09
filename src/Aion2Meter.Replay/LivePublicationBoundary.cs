using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

/// <summary>ACKed contiguous complete-frame prefix, using the existing reassembler and extractor.</summary>
internal static class LivePublicationBoundary
{
    public static ResearchCapture Select(ResearchCapture capture, IReadOnlyList<ReassembledStream> observed,
        uint clientIsn, uint serverIsn, LiveStreamCheckpoint? checkpoint = null,
        Action<LiveCheckpointDiagnostic>? observe = null)
    {
        var ends = new Dictionary<TrafficDirection, long>();
        foreach (var stream in observed)
        {
            var cursor = checkpoint?.Cursor(stream.Direction) ?? 0;
            var origin = unchecked((stream.Direction == TrafficDirection.ClientToServer ? clientIsn : serverIsn) + 1 + (uint)cursor);
            long contiguous = cursor;
            if (stream.BaseSequence == origin)
                foreach (var chunk in stream.Chunks)
                {
                    if (chunk.Offset != contiguous) break;
                    contiguous = chunk.End;
                }
            var acknowledged = capture.Packets.Where(p => p.Direction != stream.Direction &&
                    p.Segment.Flags.HasFlag(TcpFlags.Ack))
                .Select(p => (long)unchecked((int)(p.Segment.AcknowledgmentNumber - origin)))
                .Where(offset => offset is >= 0 and <= 64 * 1024 * 1024).DefaultIfEmpty(0).Max() + cursor;
            var available = Math.Min(contiguous, acknowledged);
            var prefix = stream with
            {
                Chunks = stream.Chunks.Where(c => c.Offset < available).Select(c => c with
                    { Bytes = c.Bytes[..checked((int)(Math.Min(c.End, available) - c.Offset))] }).ToArray(),
                Gaps = [], Conflicts = [], DeclaredSpan = available
            };
            var extraction = CandidateBlockExtractor.Extract(prefix, capture.OriginUtc);
            ends[stream.Direction] = extraction.Blocks.LastOrDefault() is { } last
                ? last.StreamOffset + last.Length : cursor;
            var end = ends[stream.Direction];
            var tail = prefix.Chunks.Where(c => c.End > end).SelectMany(c =>
                c.Bytes.Skip(checked((int)Math.Max(0, end - c.Offset)))).ToArray();
            var frame = ApplicationFraming.Read(tail);
            var state = tail.Length == 0 ? "Complete" : frame.State.ToString();
            var unparsed = stream.Chunks.Sum(c => (long)c.Bytes.Length) - extraction.CoveredBytes;
            if (tail.Length == 0 && unparsed > 0) state = "Waiting";
            var reason = tail.Length > 0 ? frame.Error ?? "" : unparsed > 0
                ? "Await contiguous peer-ACKed bytes at the established frame boundary." : "Complete peer-ACKed frame boundary.";
            var body = frame.PrefixLength;
            var container = tail.Length >= body + 2 && tail[body] == 0xff && tail[body + 1] == 0xff;
            observe?.Invoke(new(stream.Direction.ToString(), cursor, end, unparsed, state,
                tail.Length == 0 ? "Not available" : frame.TotalLength == 0 && frame.State == FrameReadState.Waiting ? "Partial varint" : "Length available",
                tail.Length == 0 ? 0 : frame.TotalLength == 0 && frame.State == FrameReadState.Waiting ? Math.Min(5, tail.Length + 1) : frame.PrefixLength,
                Math.Min(tail.Length, frame.PrefixLength), frame.TotalLength, tail.Length,
                container ? tail.Length < body + 6 ? "Waiting for outer container header" : "Waiting for outer compressed payload" : "Not identified",
                reason, IrreversibleInvariant: state == "Invalid" ? "Invalid canonical length at established contiguous peer-ACKed stream boundary; no resynchronization." : null));
            if (state == "Invalid") throw new InvalidDataException("Invalid ACKed application prefix at stream offset " + end + ": " + frame.Error);
        }
        // Keep packet/control provenance, but only expose bytes inside established frame boundaries.
        // No parsing, realignment or speculative flush is performed here.
        var packets = capture.Packets.Select(p =>
        {
            var s = p.Segment;
            var cursor = checkpoint?.Cursor(p.Direction) ?? 0;
            var origin = unchecked((p.Direction == TrafficDirection.ClientToServer ? clientIsn : serverIsn) + 1 + (uint)cursor);
            var offset = cursor + (long)unchecked((int)(s.PayloadSequence - origin));
            var length = offset < 0 ? 0 : (int)Math.Clamp(ends[p.Direction] - offset, 0, s.Payload.Length);
            return p with { Segment = s with { Payload = s.Payload[..length], DeclaredPayloadLength = length } };
        }).ToArray();
        return capture with { Packets = packets };
    }
}
