namespace Aion2Meter.Replay.Research;

public sealed record VarintResult(bool Success, ulong Value, int BytesConsumed, string? Error);

/// <summary>Independently written unsigned seven-bit decoder; no gameplay semantics.</summary>
public static class UnsignedVarint
{
    public static VarintResult Read(ReadOnlySpan<byte> bytes, int maximumWidth = 10,
        ulong maximumValue = ulong.MaxValue, bool requireCanonical = false)
    {
        if (maximumWidth is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        ulong value = 0;
        for (var index = 0; index < maximumWidth; index++)
        {
            if (index >= bytes.Length) return new(false, value, index, "Truncated varint.");
            var next = bytes[index];
            var group = (ulong)(next & 0x7f);
            if (index == 9 && group > 1) return new(false, value, index + 1, "Varint overflow.");
            value |= group << (index * 7);
            if (value > maximumValue) return new(false, value, index + 1, "Varint value exceeds bound.");
            if ((next & 0x80) == 0)
            {
                if (requireCanonical && index > 0 && group == 0)
                    return new(false, value, index + 1, "Noncanonical varint.");
                return new(true, value, index + 1, null);
            }
        }
        return new(false, value, maximumWidth, "Varint maximum width exceeded.");
    }
}

public sealed record FrameReadResult(bool Success, uint PrefixValue, int PrefixLength, int TotalLength, string? Error);

/// <summary>Conservative boundary reader, shared by TCP runs and decompressed containers.</summary>
public static class ApplicationFraming
{
    public static FrameReadResult Read(ReadOnlySpan<byte> bytes, int maximumFrameLength = CandidateBlockExtractor.MaximumBlockLength)
    {
        if (maximumFrameLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumFrameLength));
        var prefix = UnsignedVarint.Read(bytes, 5, uint.MaxValue, requireCanonical: true);
        if (!prefix.Success) return new(false, 0, prefix.BytesConsumed, 0, prefix.Error);
        var length = (long)prefix.Value + prefix.BytesConsumed - 4;
        if (length < prefix.BytesConsumed || length > maximumFrameLength)
            return new(false, (uint)prefix.Value, prefix.BytesConsumed, 0, "Invalid or over-limit candidate length.");
        if (length > bytes.Length)
            return new(false, (uint)prefix.Value, prefix.BytesConsumed, (int)length, "Incomplete candidate at run end (gap or capture boundary).");
        return new(true, (uint)prefix.Value, prefix.BytesConsumed, (int)length, null);
    }
}
