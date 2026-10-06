using System.Buffers.Binary;
using Aion2Meter.Core;

namespace Aion2Meter.Tests;

internal sealed class TestFiles : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "TestArtifacts", Guid.NewGuid().ToString("N"));

    public TestFiles() => Directory.CreateDirectory(DirectoryPath);

    public string WritePcap(IReadOnlyList<CapturedPacket> packets, bool littleEndian = true, bool nanoseconds = false, uint linkType = 1)
    {
        var path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".pcap");
        using var file = File.Create(path);
        var global = new byte[24];
        U32(global, 0, nanoseconds ? 0xa1b23c4d : 0xa1b2c3d4, littleEndian);
        U16(global, 4, 2, littleEndian);
        U16(global, 6, 4, littleEndian);
        U32(global, 16, 262_144, littleEndian);
        U32(global, 20, linkType, littleEndian);
        file.Write(global);
        foreach (var packet in packets)
        {
            var record = new byte[16];
            var ticks = packet.TimestampUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            U32(record, 0, (uint)(ticks / TimeSpan.TicksPerSecond), littleEndian);
            U32(record, 4, (uint)(nanoseconds ? ticks % TimeSpan.TicksPerSecond * 100 : ticks % TimeSpan.TicksPerSecond / 10), littleEndian);
            U32(record, 8, (uint)packet.Data.Length, littleEndian);
            U32(record, 12, (uint)packet.OriginalLength, littleEndian);
            file.Write(record);
            file.Write(packet.Data);
        }
        return path;
    }

    public static CapturedPacket Packet(byte protocol, DateTimeOffset timestamp, int? originalLength = null)
    {
        // Ethernet + IPv4 + a minimal TCP/UDP/ICMP header; contains no application payload.
        var transport = protocol == 6 ? "303901bb00000001000000005012010000000000"
            : protocol == 17 ? "3039003500080000" : "0800000000000000";
        var ipLength = 20 + transport.Length / 2;
        var hex = "00112233445566778899aabb0800" +
            $"4500{ipLength:x4}0001000040{protocol:x2}0000c0000201c6336402" + transport;
        var data = Convert.FromHexString(hex);
        return new(timestamp, originalLength ?? data.Length, 1, data);
    }

    public static CapturedPacket IPv6Udp(DateTimeOffset timestamp)
    {
        var data = Convert.FromHexString("00112233445566778899aabb86dd" +
            "6000000000081140" + "20010db8000000000000000000000001" + "20010db8000000000000000000000002" +
            "3039003500080000");
        return new(timestamp, data.Length, 1, data);
    }

    public static void U32(byte[] bytes, int offset, uint value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
    }

    private static void U16(byte[] bytes, int offset, ushort value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
        else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
