using System.Net;

namespace Aion2Meter.Core;

public enum TrafficDirection { ClientToServer, ServerToClient }

public sealed record TcpSegment(long PacketIndex, DateTimeOffset TimestampUtc, IPAddress SourceIp, ushort SourcePort,
    IPAddress DestinationIp, ushort DestinationPort, uint SequenceNumber, uint AcknowledgmentNumber,
    TcpFlags Flags, int CapturedFrameLength, int DeclaredPayloadLength, byte[] Payload, bool IsTruncated)
{
    public bool IsAckOnly => !IsTruncated && DeclaredPayloadLength == 0 && Flags == TcpFlags.Ack;
    public uint PayloadSequence => unchecked(SequenceNumber + (Flags.HasFlag(TcpFlags.Syn) ? 1u : 0u));
}

public sealed record TcpConnectionSelection(IPAddress LocalIp, ushort LocalPort, IPAddress RemoteIp, ushort RemotePort)
{
    public TrafficDirection? Direction(TcpSegment packet) =>
        packet.SourceIp.Equals(LocalIp) && packet.SourcePort == LocalPort && packet.DestinationIp.Equals(RemoteIp) && packet.DestinationPort == RemotePort
            ? TrafficDirection.ClientToServer
            : packet.SourceIp.Equals(RemoteIp) && packet.SourcePort == RemotePort && packet.DestinationIp.Equals(LocalIp) && packet.DestinationPort == LocalPort
                ? TrafficDirection.ServerToClient : null;
}
