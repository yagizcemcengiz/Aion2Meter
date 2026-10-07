using System.Buffers.Binary;
using System.Globalization;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public sealed record CandidateBlock(int Index, TrafficDirection Direction, DateTimeOffset TimestampUtc,
    double RelativeSeconds, long StreamOffset, long PacketIndex, long CompletionPacketIndex,
    DateTimeOffset CompletionUtc, uint PrefixValue, int PrefixLength, byte[] Bytes)
{
    public int Length => Bytes.Length;
    // A neutral grouping heuristic, not an opcode or a validated field boundary.
    public string StructuralTag => Convert.ToHexString(Bytes.AsSpan(PrefixLength, Math.Min(2, Length - PrefixLength)));
    public string FamilyKey => $"{Direction}/length={Length}/body-prefix={StructuralTag}";
}

public sealed record FramingIssue(long StreamOffset, long UnparsedBytes, string Reason);
public sealed record CandidateExtraction(IReadOnlyList<CandidateBlock> Blocks, IReadOnlyList<FramingIssue> Issues,
    long AvailableBytes, long CoveredBytes, int ContiguousRuns);

public static class CandidateBlockExtractor
{
    public const string Hypothesis = "UNVALIDATED framing hypothesis: unsigned base-128 prefix V of N bytes; total block length = V + N - 4. Run starts are candidate boundaries, not established message starts. No resynchronization or gameplay interpretation.";
    public const int MaximumBlockLength = 1024 * 1024;
    public const int MaximumBlocks = 250_000;

    public static CandidateExtraction Extract(ReassembledStream stream, DateTimeOffset origin)
    {
        var blocks = new List<CandidateBlock>();
        var issues = new List<FramingIssue>();
        var chunks = stream.Chunks.OrderBy(c => c.Offset).ToArray();
        var conflicts = stream.Conflicts.OrderBy(c => c.Offset).ToArray();
        var conflictIndex = 0;
        var available = chunks.Sum(c => (long)c.Bytes.Length);
        if (available > 64 * 1024 * 1024) throw new InvalidDataException("Block extraction limit: 64 MiB stream bytes.");
        long covered = 0;
        var runs = 0;
        for (var first = 0; first < chunks.Length;)
        {
            var last = first + 1;
            while (last < chunks.Length && chunks[last - 1].End == chunks[last].Offset) last++;
            var runStart = chunks[first].Offset;
            var size = checked((int)(chunks[last - 1].End - runStart));
            var bytes = new byte[size];
            for (var i = first; i < last; i++) chunks[i].Bytes.CopyTo(bytes, (int)(chunks[i].Offset - runStart));
            runs++;
            var position = 0;
            var source = first;
            while (position < size)
            {
                var start = runStart + position;
                if (!ReadPrefix(bytes.AsSpan(position), out var value, out var width, out var reason))
                { issues.Add(new(start, size - position, reason)); break; }
                var length = (long)value + width - 4;
                if (length < width || length > MaximumBlockLength)
                { issues.Add(new(start, size - position, "Invalid or over-limit candidate length.")); break; }
                if (length > size - position)
                { issues.Add(new(start, size - position, "Incomplete candidate at run end (gap or capture boundary).")); break; }
                while (conflictIndex < conflicts.Length && conflicts[conflictIndex].End <= start) conflictIndex++;
                if (conflictIndex < conflicts.Length && conflicts[conflictIndex].Offset < start + length)
                { issues.Add(new(start, size - position, "Conflicting TCP overlap; ambiguous bytes. Run stopped.")); break; }
                if (blocks.Count >= MaximumBlocks) throw new InvalidDataException("Candidate block limit reached (250000).");
                while (source + 1 < last && chunks[source].End <= start) source++;
                var completion = source;
                while (completion + 1 < last && chunks[completion].End < start + length) completion++;
                var latest = chunks[source];
                for (var i = source + 1; i <= completion; i++)
                    if (chunks[i].TimestampUtc > latest.TimestampUtc || chunks[i].TimestampUtc == latest.TimestampUtc && chunks[i].PacketIndex > latest.PacketIndex) latest = chunks[i];
                blocks.Add(new(blocks.Count + 1, stream.Direction, chunks[source].TimestampUtc,
                    (chunks[source].TimestampUtc - origin).TotalSeconds, start, chunks[source].PacketIndex,
                    latest.PacketIndex, latest.TimestampUtc, value, width, bytes.AsSpan(position, (int)length).ToArray()));
                position += (int)length;
                covered += length;
            }
            first = last;
        }
        return new(blocks, issues, available, covered, runs);
    }

    private static bool ReadPrefix(ReadOnlySpan<byte> bytes, out uint value, out int width, out string reason)
    {
        value = 0; width = 0; reason = "Incomplete prefix.";
        for (var i = 0; i < Math.Min(5, bytes.Length); i++)
        {
            var b = bytes[i]; width++;
            if (i == 4 && (b & 0xf0) != 0) { reason = "Overflowing or unterminated uint32 prefix."; return false; }
            value |= (uint)(b & 0x7f) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (i > 0 && b == 0) { reason = "Noncanonical prefix."; return false; }
                return true;
            }
        }
        if (bytes.Length >= 5) reason = "Unterminated prefix.";
        return false;
    }
}

public sealed record BlockSample(string Label, CandidateBlock Block);
public sealed record CandidateFamily(string Key, IReadOnlyList<BlockSample> Samples, ByteComparisonResult Comparison,
    string Signature);

public static class CandidateBlockFamilies
{
    public static IReadOnlyList<CandidateFamily> Group(IReadOnlyList<BlockSample> samples) => samples
        .GroupBy(s => s.Block.FamilyKey, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g =>
        {
            var members = g.OrderBy(s => s.Label, StringComparer.Ordinal).ThenBy(s => s.Block.StreamOffset).ToArray();
            var bytes = members.Select(s => s.Block.Bytes).ToArray();
            var comparison = ByteComparison.Compare(bytes.Length == 1 ? [bytes[0], bytes[0]] : bytes);
            var signature = string.Join(' ', Enumerable.Range(0, bytes[0].Length)
                .Select(i => bytes.All(b => b[i] == bytes[0][i]) ? bytes[0][i].ToString("X2", CultureInfo.InvariantCulture) : "??"));
            return new CandidateFamily(g.Key, members, comparison, signature);
        }).ToArray();
}

public sealed record CandidateNumericMatch(uint Value, string Representation, int Offset, string Hex);

public static class NumericHypothesisSearch
{
    public static IReadOnlyList<CandidateNumericMatch> Find(byte[] block, uint value)
    {
        var representations = new List<(string Name, byte[] Bytes)>();
        if (value <= byte.MaxValue) representations.Add(("uint8", [(byte)value]));
        if (value <= ushort.MaxValue)
        {
            var le = new byte[2]; var be = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(le, (ushort)value); BinaryPrimitives.WriteUInt16BigEndian(be, (ushort)value);
            representations.Add(("uint16 LE", le)); representations.Add(("uint16 BE", be));
        }
        var le32 = new byte[4]; var be32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le32, value); BinaryPrimitives.WriteUInt32BigEndian(be32, value);
        representations.Add(("uint32 LE", le32)); representations.Add(("uint32 BE", be32));
        var matches = new List<CandidateNumericMatch>();
        foreach (var (name, bytes) in representations)
            for (var offset = 0; offset <= block.Length - bytes.Length; offset++)
                if (block.AsSpan(offset, bytes.Length).SequenceEqual(bytes)) matches.Add(new(value, name, offset, Convert.ToHexString(bytes)));
        return matches;
    }
}
