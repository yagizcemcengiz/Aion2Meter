namespace Aion2Meter.Replay.Research;

public static class StreamPatternSearch
{
    // KMP search across contiguous chunks, reset at every gap. No message-boundary assumptions.
    public static IReadOnlyList<long> Find(ReassembledStream stream, byte[] pattern)
    {
        if (pattern.Length is < 1 or > 64) throw new ArgumentException("Pattern must contain 1-64 bytes.");
        var prefix = new int[pattern.Length];
        for (var i = 1; i < pattern.Length; i++)
        {
            var j = prefix[i - 1];
            while (j > 0 && pattern[i] != pattern[j]) j = prefix[j - 1];
            if (pattern[i] == pattern[j]) j++;
            prefix[i] = j;
        }
        var offsets = new List<long>();
        var matched = 0;
        long end = -1;
        foreach (var chunk in stream.Chunks)
        {
            if (chunk.Offset != end) matched = 0;
            for (var i = 0; i < chunk.Bytes.Length; i++)
            {
                while (matched > 0 && chunk.Bytes[i] != pattern[matched]) matched = prefix[matched - 1];
                if (chunk.Bytes[i] == pattern[matched]) matched++;
                if (matched != pattern.Length) continue;
                if (offsets.Count >= 1_000_000) throw new InvalidDataException("Stream pattern match limit exceeded (1000000). Use a smaller time window.");
                offsets.Add(chunk.Offset + i - pattern.Length + 1);
                matched = prefix[matched - 1];
            }
            end = chunk.End;
        }
        return offsets;
    }
}
