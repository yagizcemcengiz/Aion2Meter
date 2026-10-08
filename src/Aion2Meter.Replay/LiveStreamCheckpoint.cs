using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

/// <summary>Transport retirement context only. All assembly, framing and decoding remain shared.</summary>
internal sealed class LiveStreamCheckpoint
{
    internal const int VerificationBytesPerDirection = 64 * 1024;
    private readonly long[] retired = new long[2];
    private readonly byte[][] verification = [[], []];
    public CurrentPlayerBinding? Binding { get; set; }
    public long Count { get; private set; }
    public int VerificationBytes => verification.Sum(b => b.Length);
    public long RetiredBytes => retired.Sum();
    public long Cursor(TrafficDirection direction) => retired[(int)direction];
    public uint Sequence(TrafficDirection direction, uint clientIsn, uint serverIsn) => unchecked(
        (direction == TrafficDirection.ClientToServer ? clientIsn : serverIsn) + 1 + (uint)Cursor(direction));

    // An irreversible ACKed prefix is never decoded again. Verify overlap still in our bounded audit
    // window; older unverifiable payload fails closed rather than pretending it matched released bytes.
    public TcpSegment ValidateAndTrim(TcpSegment segment, TrafficDirection direction, uint clientIsn, uint serverIsn)
    {
        var offset = Cursor(direction) + unchecked((int)(segment.PayloadSequence - Sequence(direction, clientIsn, serverIsn)));
        var windowEnd = offset + segment.Payload.Length;
        if (windowEnd > Cursor(direction) + 64 * 1024 * 1024 || offset < 0)
            throw new InvalidDataException("Sequence outside bounded checkpoint window.");
        var overlap = (int)Math.Clamp(Cursor(direction) - offset, 0, segment.Payload.Length);
        if (overlap == 0) return segment;
        var cache = verification[(int)direction]; var cacheStart = Cursor(direction) - cache.Length;
        if (offset < cacheStart) throw new InvalidDataException("Retired retransmission predates verification window; fresh reconnect required.");
        if (!segment.Payload.AsSpan(0, overlap).SequenceEqual(cache.AsSpan(checked((int)(offset - cacheStart)), overlap)))
            throw new InvalidDataException("Conflicting bytes overlap a committed checkpoint; fresh reconnect required.");
        return segment with { SequenceNumber = unchecked(segment.SequenceNumber + (uint)overlap),
            Payload = segment.Payload[overlap..], DeclaredPayloadLength = segment.DeclaredPayloadLength - overlap };
    }

    public IReadOnlyList<ReassembledStream> Reassemble(ResearchCapture capture, uint clientIsn, uint serverIsn) =>
        SharedProtocolPipeline.Reassemble(capture).Select(s =>
        {
            var cursor = Cursor(s.Direction);
            var shift = s.DeclaredSpan == 0 ? cursor : cursor + unchecked((int)(s.BaseSequence - Sequence(s.Direction, clientIsn, serverIsn)));
            if (shift < cursor) throw new InvalidDataException("Untrimmed retired stream bytes.");
            return s with { Chunks = s.Chunks.Select(c => c with { Offset = c.Offset + shift }).ToArray(),
                Gaps = (shift > cursor ? new[] { new ByteRange(cursor, shift - cursor) } : [])
                    .Concat(s.Gaps.Select(g => g with { Offset = g.Offset + shift })).ToArray(),
                Conflicts = s.Conflicts.Select(g => g with { Offset = g.Offset + shift }).ToArray(),
                DeclaredSpan = s.DeclaredSpan + shift };
        }).ToArray();

    public void CheckPrivacy(IReadOnlyList<ReassembledStream> streams)
    {
        foreach (var s in streams)
        {
            var cache = verification[(int)s.Direction];
            if (cache.Length == 0) continue;
            var tail = cache.TakeLast(1024).ToArray();
            var withContext = s with { Chunks = [new(Cursor(s.Direction) - tail.Length, tail, default, 0), .. s.Chunks] };
            if (PayloadPrivacy.IsSensitive(withContext)) throw new InvalidDataException("Sensitive stream material invalidates checkpoint publication.");
        }
    }

    public IReadOnlyDictionary<TrafficDirection, long> Commit(IReadOnlyList<ReassembledStream> stable)
    {
        var ends = new Dictionary<TrafficDirection, long>(); var advanced = false;
        foreach (var s in stable)
        {
            var end = s.Chunks.LastOrDefault()?.End ?? Cursor(s.Direction);
            ends.Add(s.Direction, end);
            if (end == Cursor(s.Direction)) continue;
            var combined = verification[(int)s.Direction].Concat(s.Chunks.SelectMany(c => c.Bytes));
            verification[(int)s.Direction] = combined.TakeLast(VerificationBytesPerDirection).ToArray();
            retired[(int)s.Direction] = end; advanced = true;
        }
        if (advanced) Count++;
        return ends;
    }

    public void Clear() { Binding = null; verification[0] = []; verification[1] = []; }
}
