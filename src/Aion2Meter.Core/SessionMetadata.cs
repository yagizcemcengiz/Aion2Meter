using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2Meter.Core;

public enum CaptureMode { AllTraffic, SelectedProcessTraffic }

public sealed record CaptureOptions(CaptureMode Mode = CaptureMode.AllTraffic, string SessionLabel = "session",
    int? SelectedPid = null, string? SelectedProcessName = null,
    IReadOnlyList<ProcessNetworkEndpoint>? ConnectionsAtStart = null, string? UserNotes = null);

public sealed record SessionMetadata(Guid SessionId, string SessionLabel, DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc, TimeSpan Duration, NetworkAdapterMetadata SelectedAdapter, CaptureMode CaptureMode,
    int? SelectedPid, string? SelectedProcessName, IReadOnlyList<ProcessNetworkEndpoint> ConnectionsAtStart,
    string BpfFilter, long TotalPackets, long TotalBytes, long TcpPackets, long UdpPackets, long OtherPackets,
    string? UserNotes, long QueueDroppedPackets = 0, long MetadataErrors = 0, string Status = "Running")
{
    public IReadOnlyList<TestMarker> TestMarkers { get; init; } = [];

    public static SessionMetadata Create(CaptureSession session, NetworkAdapter adapter, CaptureOptions options, string filter) =>
        new(session.SessionId, CaptureSessionFactory.SanitizeLabel(options.SessionLabel), session.StartedUtc, null, TimeSpan.Zero,
            new(adapter.Identifier, adapter.FriendlyName, adapter.Description,
                adapter.IPv4Addresses.Concat(adapter.IPv6Addresses).Select(a => a.ToString()).ToArray()), options.Mode,
            options.SelectedPid, options.SelectedProcessName, options.ConnectionsAtStart?.ToArray() ?? [], filter,
            0, 0, 0, 0, 0, options.UserNotes);

    public SessionMetadata Finish(DateTimeOffset endedUtc, StatisticsSnapshot statistics, long dropped, long errors, string status) => this with
    {
        EndedUtc = endedUtc.ToUniversalTime(), Duration = endedUtc > StartedUtc ? endedUtc - StartedUtc : TimeSpan.Zero,
        TotalPackets = statistics.TotalPackets, TotalBytes = statistics.TotalBytes, TcpPackets = statistics.TcpCount,
        UdpPackets = statistics.UdpCount, OtherPackets = statistics.OtherCount, QueueDroppedPackets = dropped,
        MetadataErrors = errors, Status = status
    };
}

public sealed record NetworkAdapterMetadata(string Identifier, string FriendlyName, string Description, IReadOnlyList<string> IpAddresses);

public static class SessionMetadataStore
{
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Serialize(SessionMetadata metadata) => JsonSerializer.Serialize(metadata, Options);
    public static SessionMetadata Deserialize(string json) => JsonSerializer.Deserialize<SessionMetadata>(json, Options)
        ?? throw new InvalidDataException("Missing session metadata.");

    public static async Task WriteAsync(string path, SessionMetadata metadata)
    {
        // Readers see either the previous complete JSON or the new complete JSON.
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, Serialize(metadata)).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
