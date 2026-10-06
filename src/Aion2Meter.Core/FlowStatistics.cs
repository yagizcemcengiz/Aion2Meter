namespace Aion2Meter.Core;

// Directional network-header key. Reverse traffic is a separate flow.
public sealed record FlowKey(TransportProtocol Protocol, string SourceIp, ushort? SourcePort,
    string DestinationIp, ushort? DestinationPort, byte? IpProtocolNumber)
{
    public string Display => $"{Protocol}({IpProtocolNumber}) {SourceIp}:{SourcePort?.ToString() ?? "—"} -> {DestinationIp}:{DestinationPort?.ToString() ?? "—"}";
}

public sealed record PacketSizeFrequency(int CapturedLength, long Packets);
public sealed record FlowSnapshot(FlowKey Key, long Packets, long Bytes, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, int MinimumCapturedLength, int MaximumCapturedLength, IReadOnlyList<PacketSizeFrequency> Sizes)
{
    public TimeSpan Duration => LastSeen - FirstSeen;
    public double AverageCapturedLength => Packets == 0 ? 0 : (double)Bytes / Packets;
    public IReadOnlyList<PacketSizeFrequency> CommonSizes(int count = 5) => Sizes.OrderByDescending(s => s.Packets)
        .ThenBy(s => s.CapturedLength).Take(count).ToArray();
}

public sealed class FlowStatistics
{
    private sealed class Accumulator(FlowKey key, DateTimeOffset time)
    {
        public FlowKey Key { get; } = key;
        public long Packets, Bytes;
        public DateTimeOffset First = time, Last = time;
        public int Minimum = int.MaxValue, Maximum;
        public Dictionary<int, long> Sizes { get; } = [];
        public FlowSnapshot Snapshot() => new(Key, Packets, Bytes, First, Last, Minimum, Maximum,
            Sizes.OrderBy(p => p.Key).Select(p => new PacketSizeFrequency(p.Key, p.Value)).ToArray());
    }

    private readonly Dictionary<FlowKey, Accumulator> flows = [];
    public long UngroupedPackets { get; private set; }
    public void Add(PacketMetadata packet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packet.CapturedLength);
        if (packet.SourceIp is null || packet.DestinationIp is null) { UngroupedPackets++; return; }
        var key = new FlowKey(packet.TransportProtocol, packet.SourceIp.ToString(), packet.SourcePort,
            packet.DestinationIp.ToString(), packet.DestinationPort, packet.IpProtocolNumber);
        var time = packet.TimestampUtc.ToUniversalTime();
        if (!flows.TryGetValue(key, out var flow)) flows.Add(key, flow = new(key, time));
        flow.Packets++;
        flow.Bytes += packet.CapturedLength;
        if (time < flow.First) flow.First = time;
        if (time > flow.Last) flow.Last = time;
        flow.Minimum = Math.Min(flow.Minimum, packet.CapturedLength);
        flow.Maximum = Math.Max(flow.Maximum, packet.CapturedLength);
        flow.Sizes[packet.CapturedLength] = flow.Sizes.GetValueOrDefault(packet.CapturedLength) + 1;
    }

    public IReadOnlyList<FlowSnapshot> Snapshot() => flows.Values.Select(f => f.Snapshot()).OrderBy(f => f.Key.Display, StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<FlowSnapshot> Top(IReadOnlyList<FlowSnapshot> flows, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return flows.OrderByDescending(f => f.Bytes).ThenByDescending(f => f.Packets).ThenBy(f => f.Key.Display, StringComparer.Ordinal).Take(count).ToArray();
    }
}
