using System.Runtime.CompilerServices;
using Aion2Meter.Core;

namespace Aion2Meter.Replay;

/// <summary>Adapts the existing PCAP reader to the same boundary used by Npcap.</summary>
public sealed class ReplayPacketSource(string path, IOfflinePacketSource? reader = null) : IPacketSource
{
    public string SourceId { get; } = Path.GetFullPath(path);

    public IEnumerable<SourcePacket> Read(CancellationToken cancellationToken = default)
    {
        long index = 0;
        foreach (var packet in (reader ?? new PcapPacketSource()).Read(SourceId, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(SourceId, null, ++index, packet);
        }
    }

    public async IAsyncEnumerable<SourcePacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var input in Read(cancellationToken)) yield return input;
        await Task.CompletedTask;
    }
}
