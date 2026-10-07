namespace Aion2Meter.Replay.Research;

public sealed record ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
    public override string ToString() => $"{Offset}..{End - 1}";
}

public sealed record ByteComparisonResult(int CommonPrefixLength, int CommonSuffixLength,
    IReadOnlyList<ByteRange> IdenticalRanges, IReadOnlyList<ByteRange> VaryingRanges);

public static class ByteComparison
{
    // Missing bytes in a shorter sample count as different. Prefix/suffix are disjoint.
    public static ByteComparisonResult Compare(IReadOnlyList<byte[]> samples)
    {
        if (samples.Count < 2) throw new ArgumentException("At least two complete payload samples are required.");
        var minimum = samples.Min(s => s.Length);
        var maximum = samples.Max(s => s.Length);
        bool Same(int offset) => samples.All(s => offset < s.Length && s[offset] == samples[0][offset]);
        var prefix = 0;
        while (prefix < minimum && Same(prefix)) prefix++;
        var suffix = 0;
        while (suffix < minimum - prefix && samples.All(s => s[s.Length - suffix - 1] == samples[0][samples[0].Length - suffix - 1])) suffix++;
        var identical = new List<ByteRange>();
        var varying = new List<ByteRange>();
        for (var offset = 0; offset < maximum;)
        {
            var start = offset;
            var same = Same(offset++);
            while (offset < maximum && Same(offset) == same) offset++;
            (same ? identical : varying).Add(new(start, offset - start));
        }
        return new(prefix, suffix, identical, varying);
    }
}
