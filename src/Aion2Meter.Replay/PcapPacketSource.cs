using System.Buffers.Binary;
using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public sealed class PcapPacketSource : IOfflinePacketSource
{
    public IEnumerable<CapturedPacket> Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Capture file was not found.", path);
        return ReadFile(path, cancellationToken);
    }

    private static IEnumerable<CapturedPacket> ReadFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var global = new byte[24];
        ReadExactly(stream, global, "Incomplete pcap file header.");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(global);
        var littleEndian = magic is 0xa1b2c3d4 or 0xa1b23c4d;
        var nanoseconds = magic is 0xa1b23c4d or 0x4d3cb2a1;
        if (magic is not (0xa1b2c3d4 or 0xd4c3b2a1 or 0xa1b23c4d or 0x4d3cb2a1))
            throw new InvalidDataException("Unsupported capture format. Use a classic .pcap file (pcapng is not supported).");
        if (U16(global, 4, littleEndian) != 2 || U16(global, 6, littleEndian) != 4)
            throw new InvalidDataException("Unsupported pcap version. Expected version 2.4.");
        var snaplen = U32(global, 16, littleEndian);
        if (snaplen == 0 || snaplen > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid or unsupported pcap snapshot length.");
        var linkType = U32(global, 20, littleEndian) & 0xffff;
        var record = new byte[16];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(stream, record, "Incomplete pcap packet header.");
            var seconds = U32(record, 0, littleEndian);
            var fraction = U32(record, 4, littleEndian);
            var captured = U32(record, 8, littleEndian);
            var original = U32(record, 12, littleEndian);
            if (fraction >= (nanoseconds ? 1_000_000_000u : 1_000_000u))
                throw new InvalidDataException("Invalid pcap timestamp fraction.");
            if (captured > snaplen || captured > original || original > int.MaxValue || captured > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid or truncated pcap packet length.");
            var data = new byte[checked((int)captured)];
            ReadExactly(stream, data, "Incomplete pcap packet data.");
            var ticks = (long)seconds * TimeSpan.TicksPerSecond + (nanoseconds ? fraction / 100 : fraction * 10L);
            yield return new(DateTimeOffset.UnixEpoch.AddTicks(ticks), (int)original, linkType, data);
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, string error)
    {
        try { stream.ReadExactly(buffer); }
        catch (EndOfStreamException ex) { throw new InvalidDataException(error, ex); }
    }

    private static uint U32(byte[] bytes, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static ushort U16(byte[] bytes, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
}
