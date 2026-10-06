using System.Net;
using System.Net.Sockets;

namespace Aion2Meter.Core;

public sealed record CaptureFilterResult(string? Filter, IReadOnlyList<string> Warnings, string? Error)
{
    public bool Success => Filter is not null && Error is null;
}

public static class CaptureFilterBuilder
{
    public const int MaximumEndpoints = 256;
    public const int MaximumFilterLength = 32_768;

    public static CaptureFilterResult Build(IReadOnlyList<ProcessNetworkEndpoint> endpoints, IReadOnlyList<IPAddress>? adapterAddresses = null)
    {
        if (endpoints.Count == 0) return new(null, [], "No endpoints found for the selected process. Refresh connections.");
        if (endpoints.Count > MaximumEndpoints) return new(null, [], "Too many endpoints for a safe capture filter. Capture was not started.");
        var clauses = new SortedSet<string>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal)
        { "Endpoint filtering is not PID filtering; shared/reused endpoints can produce false positives. New connections are not automatically included." };
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Protocol is not (TransportProtocol.Tcp or TransportProtocol.Udp) || endpoint.LocalPort == 0 ||
                !IPAddress.TryParse(endpoint.LocalIp, out var local) || local.AddressFamily != endpoint.AddressFamily ||
                endpoint.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return new(null, warnings.ToArray(), "An endpoint cannot be represented safely. Refresh connections.");
            IPAddress? remote = null;
            if (endpoint.RemoteIp is not null && (!IPAddress.TryParse(endpoint.RemoteIp, out remote) || remote.AddressFamily != local.AddressFamily))
                return new(null, warnings.ToArray(), "Invalid remote endpoint. Refresh connections.");
            var wildcard = local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any);
            var addresses = wildcard
                ? (adapterAddresses ?? []).Where(a => a.AddressFamily == local.AddressFamily && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Distinct().ToArray()
                : [local];
            if (addresses.Length == 0) return new(null, warnings.ToArray(), "Wildcard endpoint has no matching adapter IP address. Select the correct adapter.");
            if (wildcard) warnings.Add("Wildcard bindings are limited to the selected adapter's current IP addresses.");
            var connected = endpoint.Protocol == TransportProtocol.Tcp && endpoint.RemotePort is > 0 && remote is not null &&
                !remote.Equals(IPAddress.Any) && !remote.Equals(IPAddress.IPv6Any);
            if (!connected) warnings.Add("UDP/listening endpoints have no remote peer in the Windows owner table; the local endpoint is used in both directions.");
            var protocol = endpoint.Protocol == TransportProtocol.Tcp ? "tcp" : "udp";
            var ip = local.AddressFamily == AddressFamily.InterNetwork ? "ip" : "ip6";
            foreach (var address in addresses)
            {
                var outbound = $"src host {Literal(address)} and src port {endpoint.LocalPort}";
                var inbound = $"dst host {Literal(address)} and dst port {endpoint.LocalPort}";
                if (connected)
                {
                    outbound += $" and dst host {Literal(remote!)} and dst port {endpoint.RemotePort}";
                    inbound += $" and src host {Literal(remote!)} and src port {endpoint.RemotePort}";
                }
                clauses.Add($"({ip} and {protocol} and (({outbound}) or ({inbound})))");
                if (clauses.Sum(c => c.Length + 4) > MaximumFilterLength)
                    return new(null, warnings.ToArray(), "Capture filter is too large. Capture was not started.");
            }
        }
        return new(string.Join(" or ", clauses), warnings.Order(StringComparer.Ordinal).ToArray(), null);
    }

    // Numeric literals only, never a hostname. Scope IDs are interface metadata, not part of a BPF IP literal.
    private static string Literal(IPAddress address) => new IPAddress(address.GetAddressBytes()).ToString();
}
