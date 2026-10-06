using System.Net;

namespace Aion2Meter.Core;

public sealed record NetworkAdapter(
    string Identifier, string FriendlyName, string Description,
    IReadOnlyList<IPAddress> IPv4Addresses, IReadOnlyList<IPAddress> IPv6Addresses,
    string? MacAddress, string Status)
{
    public string DisplayName => $"{FriendlyName} — {Status}";
    public string Details => $"Description: {Description}\nInterface: {Identifier}\n" +
        $"IPv4: {string.Join(", ", IPv4Addresses)}\nIPv6: {string.Join(", ", IPv6Addresses)}\n" +
        $"MAC: {MacAddress ?? "Unavailable"}\nStatus: {Status}";
}

public sealed record AdapterDiscoveryResult(bool NpcapReady, IReadOnlyList<NetworkAdapter> Adapters, string? Error = null);
public interface IAdapterDiscovery { AdapterDiscoveryResult Discover(); }

public sealed record CaptureSession(string FilePath, string AdapterIdentifier, DateTimeOffset StartedUtc, Guid SessionId = default)
{
    public string MetadataPath => Path.ChangeExtension(FilePath, ".json");
}
public sealed record DiagnosticMessage(DateTimeOffset TimestampUtc, string Level, string Message);

public interface ICaptureEngine : IAsyncDisposable
{
    event Action<DiagnosticMessage>? Diagnostic;
    bool IsRunning { get; }
    CaptureSession? Session { get; }
    StatisticsSnapshot Statistics { get; }
    long QueueDroppedPackets { get; }
    long MetadataErrors { get; }
    Task StartAsync(NetworkAdapter adapter, string directory);
    Task StartAsync(NetworkAdapter adapter, string directory, CaptureOptions options);
    Task StopAsync();
}
