namespace Aion2Meter.Core;

public sealed record StatisticsSnapshot(
    long TotalPackets, long TotalBytes, long TcpCount, long UdpCount, long OtherCount,
    DateTimeOffset? FirstTimestamp, DateTimeOffset? LastTimestamp)
{
    public TimeSpan Duration => FirstTimestamp is { } first && LastTimestamp is { } last ? last - first : TimeSpan.Zero;
}

public sealed class PacketStatistics
{
    private readonly object gate = new();
    private long packets, bytes, tcp, udp, other;
    private DateTimeOffset? first, last;

    public void Add(PacketMetadata packet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packet.CapturedLength);
        lock (gate)
        {
            packets++;
            bytes += packet.CapturedLength;
            switch (packet.TransportProtocol)
            {
                case TransportProtocol.Tcp: tcp++; break;
                case TransportProtocol.Udp: udp++; break;
                default: other++; break;
            }
            var utc = packet.TimestampUtc.ToUniversalTime();
            if (first is null || utc < first) first = utc;
            if (last is null || utc > last) last = utc;
        }
    }

    public StatisticsSnapshot Snapshot()
    {
        lock (gate) return new(packets, bytes, tcp, udp, other, first, last);
    }

    public void Reset()
    {
        lock (gate)
        {
            packets = bytes = tcp = udp = other = 0;
            first = last = null;
        }
    }
}
