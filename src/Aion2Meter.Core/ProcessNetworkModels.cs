using System.Net.Sockets;

namespace Aion2Meter.Core;

public sealed record ProcessNetworkEndpoint(int Pid, string ProcessName, TransportProtocol Protocol,
    AddressFamily AddressFamily, string LocalIp, ushort LocalPort, string? RemoteIp = null,
    ushort? RemotePort = null, string? TcpState = null)
{
    public string LocalEndpoint => Format(LocalIp, LocalPort);
    public string RemoteEndpoint => RemoteIp is null ? "—" : Format(RemoteIp, RemotePort);
    public string IpVersion => AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
    private static string Format(string ip, ushort? port) => port is null ? ip
        : ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";
}

public sealed record NetworkProcess(int Pid, string ProcessName)
{
    public string DisplayName => $"{ProcessName} (PID {Pid})";
}

public sealed record EndpointTableResult(IReadOnlyList<ProcessNetworkEndpoint> Endpoints, IReadOnlyList<string> Warnings);
public sealed record ProcessNetworkSnapshot(IReadOnlyList<NetworkProcess> Processes,
    IReadOnlyList<ProcessNetworkEndpoint> Endpoints, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<ProcessNetworkEndpoint> ForPid(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        return Endpoints.Where(e => e.Pid == pid).ToArray();
    }
}

public interface IEndpointTableSource { EndpointTableResult Read(); }
public interface IProcessNameResolver { string? Resolve(int pid); }
