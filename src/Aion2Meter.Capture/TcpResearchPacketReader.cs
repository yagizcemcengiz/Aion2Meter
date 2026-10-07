using System.Buffers.Binary;
using System.Net;
using Aion2Meter.Core;

namespace Aion2Meter.Capture;

// Standard link/IP/TCP headers only. Application bytes are copied without interpreting fields.
public sealed class TcpResearchPacketReader
{
    public TcpSegment? Read(CapturedPacket packet, long packetIndex)
    {
        var data = packet.Data.AsSpan();
        var ipOffset = packet.LinkLayerType switch
        {
            1 => EthernetOffset(data), 0 or 108 => LoopbackOffset(data), 101 => 0,
            113 => CookedOffset(data, 16, 14), 276 => CookedOffset(data, 20, 0),
            _ => throw new NotSupportedException("Unsupported research link-layer type.")
        };
        if (ipOffset < 0) return null;
        Require(data.Length >= ipOffset + 1);
        var ip = data[ipOffset..];
        var version = ip[0] >> 4;
        int tcpOffset, declaredIpLength;
        IPAddress source, destination;
        byte protocol;
        if (version == 4)
        {
            Require(ip.Length >= 20);
            var headerLength = (ip[0] & 15) * 4;
            declaredIpLength = U16(ip, 2);
            Require(headerLength >= 20 && declaredIpLength >= headerLength && ip.Length >= headerLength);
            if ((U16(ip, 6) & 0x3fff) != 0) throw new NotSupportedException("IP fragments are excluded; no IP fragment reassembly is performed.");
            protocol = ip[9];
            source = new(ip.Slice(12, 4)); destination = new(ip.Slice(16, 4));
            tcpOffset = headerLength;
        }
        else if (version == 6)
        {
            Require(ip.Length >= 40);
            var ipPayloadLength = U16(ip, 4);
            if (ipPayloadLength == 0) throw new NotSupportedException("IPv6 jumbograms are not supported.");
            declaredIpLength = 40 + ipPayloadLength;
            source = new(ip.Slice(8, 16)); destination = new(ip.Slice(24, 16));
            protocol = ip[6]; tcpOffset = 40;
            var headers = 0;
            while (protocol is 0 or 43 or 60 or 51 or 44)
            {
                if (++headers > 16) throw new InvalidDataException("Too many IPv6 extension headers.");
                Require(ip.Length >= tcpOffset + 8 && declaredIpLength >= tcpOffset + 8);
                if (protocol == 44 && (U16(ip, tcpOffset + 2) & 0xfff9) != 0)
                    throw new NotSupportedException("IP fragments are excluded; no IP fragment reassembly is performed.");
                var length = protocol == 44 ? 8 : protocol == 51 ? (ip[tcpOffset + 1] + 2) * 4 : (ip[tcpOffset + 1] + 1) * 8;
                Require(ip.Length >= tcpOffset + length && declaredIpLength >= tcpOffset + length);
                protocol = ip[tcpOffset]; tcpOffset += length;
            }
        }
        else return null;
        if (protocol != 6) return null;
        Require(declaredIpLength >= tcpOffset + 20 && ip.Length >= tcpOffset + 20);
        var tcp = ip[tcpOffset..];
        var tcpHeaderLength = (tcp[12] >> 4) * 4;
        Require(tcpHeaderLength >= 20 && tcp.Length >= tcpHeaderLength && declaredIpLength >= tcpOffset + tcpHeaderLength);
        var declared = declaredIpLength - tcpOffset - tcpHeaderLength;
        var available = Math.Min(declared, ip.Length - tcpOffset - tcpHeaderLength);
        return new(packetIndex, packet.TimestampUtc.ToUniversalTime(), source, U16(tcp, 0), destination, U16(tcp, 2),
            U32(tcp, 4), U32(tcp, 8), (TcpFlags)tcp[13], data.Length, declared,
            tcp.Slice(tcpHeaderLength, available).ToArray(), available < declared);
    }

    private static int EthernetOffset(ReadOnlySpan<byte> data)
    {
        Require(data.Length >= 14);
        var type = U16(data, 12);
        var offset = 14;
        var tags = 0;
        while (type is 0x8100 or 0x88a8 or 0x9100)
        {
            Require(++tags <= 4 && data.Length >= offset + 4);
            type = U16(data, offset + 2); offset += 4;
        }
        return type is 0x0800 or 0x86dd ? offset : -1;
    }
    private static int CookedOffset(ReadOnlySpan<byte> data, int length, int protocolOffset)
    {
        Require(data.Length >= length);
        return U16(data, protocolOffset) is 0x0800 or 0x86dd ? length : -1;
    }
    private static int LoopbackOffset(ReadOnlySpan<byte> data)
    {
        Require(data.Length >= 4);
        var little = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var big = BinaryPrimitives.ReadUInt32BigEndian(data);
        // DLT_NULL uses the capture host's byte order; AF_INET6 values vary across hosts.
        return little is 2 or 10 or 24 or 28 or 30 || big is 2 or 10 or 24 or 28 or 30 ? 4 : -1;
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    private static void Require(bool valid) { if (!valid) throw new InvalidDataException("Incomplete or invalid link/IP/TCP header."); }
}
