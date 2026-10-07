using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

// Explicit diagnostic wire contracts. Networking objects stay in transport models and are never
// passed to JsonSerializer: IPAddress's family-specific getters are unsafe to reflect over.
public sealed record LiveDiagnosticStart(string Kind, string SourceId, string Interface, ushort Port);

public sealed record LiveDiagnosticConnection(string LocalIp, ushort LocalPort, string RemoteIp, ushort RemotePort,
    string LocalEndpoint, string RemoteEndpoint, long? LocalScopeId, long? RemoteScopeId);

public sealed record LiveDiagnosticEpoch(string EpochId, LiveDiagnosticConnection Connection,
    uint? ClientIsn, uint? ServerIsn, string Lifecycle, bool ProtocolObserved,
    CurrentPlayerBindingStatus BindingStatus, ulong? EntityId, string? CharacterName,
    int AcceptedEvents, int SelfCount, int OtherCount, int UnknownCount,
    IReadOnlyList<ulong> RecentSelfAmounts, int UnsupportedCandidates, int Gaps, int Conflicts,
    int DuplicateSegments, long OverlapBytes, IReadOnlyList<string> Warnings);

public sealed record LiveDiagnosticSnapshot(string Kind, DateTimeOffset TimestampUtc,
    long MalformedPackets, long UnsupportedPackets, long IgnoredPackets, long RejectedFlows,
    IReadOnlyList<LiveDiagnosticEpoch> Epochs);

public static class LiveDiagnosticJson
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string SerializeStart(string sourceId, string interfaceId, ushort port) =>
        JsonSerializer.Serialize(new LiveDiagnosticStart("start", sourceId, interfaceId, port), Options);

    public static LiveDiagnosticSnapshot CreateSnapshot(LivePacketPipeline pipeline,
        IReadOnlyList<LiveEpochSnapshot> epochs, DateTimeOffset timestampUtc) =>
        new("snapshot", timestampUtc, pipeline.MalformedPackets, pipeline.UnsupportedPackets,
            pipeline.IgnoredPackets, pipeline.RejectedFlows, epochs.Select(Project).ToArray());

    public static string SerializeSnapshot(LivePacketPipeline pipeline,
        IReadOnlyList<LiveEpochSnapshot> epochs, DateTimeOffset timestampUtc) =>
        JsonSerializer.Serialize(CreateSnapshot(pipeline, epochs, timestampUtc), Options);

    private static LiveDiagnosticEpoch Project(LiveEpochSnapshot epoch)
    {
        var c = epoch.Connection;
        var connection = new LiveDiagnosticConnection(c.LocalIp.ToString(), c.LocalPort, c.RemoteIp.ToString(), c.RemotePort,
            new IPEndPoint(c.LocalIp, c.LocalPort).ToString(), new IPEndPoint(c.RemoteIp, c.RemotePort).ToString(),
            Scope(c.LocalIp), Scope(c.RemoteIp));
        return new(epoch.EpochId, connection, epoch.ClientIsn, epoch.ServerIsn, epoch.Lifecycle, epoch.ProtocolObserved,
            epoch.BindingStatus, epoch.EntityId, epoch.CharacterName, epoch.AcceptedEvents,
            epoch.SelfCount, epoch.OtherCount, epoch.UnknownCount, epoch.RecentSelfAmounts.ToArray(),
            epoch.UnsupportedCandidates, epoch.Gaps, epoch.Conflicts, epoch.DuplicateSegments,
            epoch.OverlapBytes, epoch.Warnings.ToArray());
    }

    private static long? Scope(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ScopeId : null;
}
