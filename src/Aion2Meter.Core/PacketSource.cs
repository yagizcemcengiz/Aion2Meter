namespace Aion2Meter.Core;

/// <summary>Source ordering and interface context, independent of PCAP file offsets.</summary>
public sealed record SourcePacket(string SourceId, string? InterfaceId, long PacketIndex, CapturedPacket Packet);

/// <summary>One ordered, cancellable capture session. Implementations own their native/file handles.</summary>
public interface IPacketSource
{
    string SourceId { get; }
    IAsyncEnumerable<SourcePacket> ReadAsync(CancellationToken cancellationToken = default);
}
