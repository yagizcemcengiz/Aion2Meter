using Aion2Meter.Core;
using PacketDotNet;

namespace Aion2Meter.Capture;

public sealed class PacketMetadataReader : IPacketMetadataReader
{
    public PacketMetadata Read(CapturedPacket capture)
    {
        var metadata = new PacketMetadata(capture.TimestampUtc.ToUniversalTime(), capture.Data.Length, capture.OriginalLength);
        if (capture.Data.Length == 0) return metadata;
        var packet = Packet.ParsePacket((LinkLayers)capture.LinkLayerType, capture.Data);
        var ethernet = packet.Extract<EthernetPacket>();
        if (ethernet is not null) metadata = metadata with { EthernetType = (ushort)ethernet.Type };
        var ip = packet.Extract<IPPacket>();
        if (ip is null) return metadata;
        var protocol = (byte)ip.Protocol;
        metadata = metadata with
        {
            IpVersion = ip.Version == IPVersion.IPv4 ? 4 : 6,
            SourceIp = ip.SourceAddress, DestinationIp = ip.DestinationAddress,
            IpProtocolNumber = protocol,
            TransportProtocol = protocol switch { 6 => TransportProtocol.Tcp, 17 => TransportProtocol.Udp, _ => TransportProtocol.Other }
        };

        // Non-initial IPv4 fragments do not contain a transport header.
        if (ip is IPv4Packet ipv4 && ipv4.FragmentOffset != 0) return metadata;
        if (ip is IPv6Packet ipv6 && ipv6.ExtensionHeaders.OfType<IPv6FragmentationExtensionHeader>().Any(h => h.FragmentOffset != 0))
            return metadata;
        var tcp = protocol == 6 ? ip.PayloadPacket as TcpPacket : null;
        if (tcp is not null)
        {
            return metadata with
            {
                TransportProtocol = TransportProtocol.Tcp,
                SourcePort = tcp.SourcePort, DestinationPort = tcp.DestinationPort,
                TcpFlags = (TcpFlags)(tcp.Flags & 0xff)
            };
        }
        var udp = protocol == 17 ? ip.PayloadPacket as UdpPacket : null;
        return udp is null ? metadata : metadata with
        {
            TransportProtocol = TransportProtocol.Udp, SourcePort = udp.SourcePort, DestinationPort = udp.DestinationPort
        };
    }
}
