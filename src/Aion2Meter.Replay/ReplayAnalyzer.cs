using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public sealed record ReplayResult(StatisticsSnapshot Statistics, IReadOnlyList<PacketMetadata> Dump, long MetadataErrors)
{
    public IReadOnlyList<FlowSnapshot> Flows { get; init; } = [];
    public long UngroupedPackets { get; init; }
}

public sealed class ReplayAnalyzer(IOfflinePacketSource source, IPacketMetadataReader metadataReader)
{
    public ReplayResult Analyze(string path, int dumpCount = 0, CancellationToken cancellationToken = default, bool includeFlows = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dumpCount);
        if (dumpCount > 10_000) throw new ArgumentOutOfRangeException(nameof(dumpCount), "Dump limit is 10000 packets.");
        var statistics = new PacketStatistics();
        var dump = new List<PacketMetadata>();
        var flows = includeFlows ? new FlowStatistics() : null;
        long metadataErrors = 0;
        foreach (var packet in source.Read(path, cancellationToken))
        {
            PacketMetadata metadata;
            try { metadata = metadataReader.Read(packet); }
            catch (Exception)
            {
                metadataErrors++;
                metadata = new(packet.TimestampUtc, packet.Data.Length, packet.OriginalLength);
            }
            statistics.Add(metadata);
            flows?.Add(metadata);
            if (dump.Count < dumpCount) dump.Add(metadata);
        }
        return new(statistics.Snapshot(), dump, metadataErrors) { Flows = flows?.Snapshot() ?? [], UngroupedPackets = flows?.UngroupedPackets ?? 0 };
    }
}
