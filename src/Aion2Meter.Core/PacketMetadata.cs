using System.Globalization;
using System.Net;

namespace Aion2Meter.Core;

public enum TransportProtocol { Other, Tcp, Udp }

[Flags]
public enum TcpFlags : byte
{
    None = 0, Fin = 1, Syn = 2, Rst = 4, Psh = 8, Ack = 16, Urg = 32, Ece = 64, Cwr = 128
}

public sealed record PacketMetadata(
    DateTimeOffset TimestampUtc,
    int CapturedLength,
    int OriginalLength,
    ushort? EthernetType = null,
    int? IpVersion = null,
    IPAddress? SourceIp = null,
    IPAddress? DestinationIp = null,
    TransportProtocol TransportProtocol = TransportProtocol.Other,
    ushort? SourcePort = null,
    ushort? DestinationPort = null,
    TcpFlags? TcpFlags = null,
    byte? IpProtocolNumber = null)
{
    public string Format() => string.Create(CultureInfo.InvariantCulture,
        $"{TimestampUtc.UtcDateTime:O} captured={CapturedLength} original={OriginalLength} " +
        $"ether={FormatEthernetType()} ip={IpVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
        $"{Endpoint(SourceIp, SourcePort)} -> {Endpoint(DestinationIp, DestinationPort)} " +
        $"protocol={TransportProtocol}({IpProtocolNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"}) flags={TcpFlags?.ToString() ?? "-"}");

    private string FormatEthernetType() => EthernetType is { } type ? $"0x{type:X4}" : "-";
    private static string Endpoint(IPAddress? address, ushort? port) => address is null ? "-"
        : port is null ? address.ToString()
        : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}" : $"{address}:{port}";
}

public sealed record CapturedPacket(DateTimeOffset TimestampUtc, int OriginalLength, uint LinkLayerType, byte[] Data);

public interface IPacketMetadataReader
{
    PacketMetadata Read(CapturedPacket packet);
}

public interface IOfflinePacketSource
{
    IEnumerable<CapturedPacket> Read(string path, CancellationToken cancellationToken = default);
}
