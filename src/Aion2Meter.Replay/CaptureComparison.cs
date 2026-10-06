using System.Net;
using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public sealed record FlowDifference(FlowKey Key, long PacketsA, long PacketsB, long BytesA, long BytesB)
{
    public long PacketDelta => PacketsB - PacketsA;
    public long ByteDelta => BytesB - BytesA;
}
public sealed record ObservedEndpoint(TransportProtocol Protocol, string Ip, ushort? Port, byte? IpProtocolNumber)
{
    public string Display => $"{Protocol}({IpProtocolNumber}) [{Ip}]:{Port?.ToString() ?? "—"}";
}
public sealed record CaptureComparisonResult(IReadOnlyList<FlowDifference> OnlyA, IReadOnlyList<FlowDifference> OnlyB,
    IReadOnlyList<FlowDifference> Common, long PacketDelta, long ByteDelta,
    IReadOnlyList<ObservedEndpoint> NewEndpoints, IReadOnlyList<ObservedEndpoint> LostEndpoints, bool RemoteEndpointsIdentified);

public static class CaptureComparison
{
    public static CaptureComparisonResult Compare(ReplayResult a, ReplayResult b,
        SessionMetadata? metadataA = null, SessionMetadata? metadataB = null)
    {
        var left = a.Flows.ToDictionary(f => f.Key);
        var right = b.Flows.ToDictionary(f => f.Key);
        var onlyA = new List<FlowDifference>();
        var onlyB = new List<FlowDifference>();
        var common = new List<FlowDifference>();
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(k => k.Display, StringComparer.Ordinal))
        {
            left.TryGetValue(key, out var fa);
            right.TryGetValue(key, out var fb);
            var difference = new FlowDifference(key, fa?.Packets ?? 0, fb?.Packets ?? 0, fa?.Bytes ?? 0, fb?.Bytes ?? 0);
            if (fa is null) onlyB.Add(difference);
            else if (fb is null) onlyA.Add(difference);
            else common.Add(difference);
        }
        var remoteIdentified = HasLocalContext(metadataA) && HasLocalContext(metadataB);
        var endpointsA = Endpoints(a.Flows, remoteIdentified ? metadataA : null);
        var endpointsB = Endpoints(b.Flows, remoteIdentified ? metadataB : null);
        return new(onlyA, onlyB, common, b.Statistics.TotalPackets - a.Statistics.TotalPackets,
            b.Statistics.TotalBytes - a.Statistics.TotalBytes,
            endpointsB.Except(endpointsA).OrderBy(e => e.Display, StringComparer.Ordinal).ToArray(),
            endpointsA.Except(endpointsB).OrderBy(e => e.Display, StringComparer.Ordinal).ToArray(), remoteIdentified);
    }

    private static bool HasLocalContext(SessionMetadata? metadata) => metadata?.SelectedAdapter.IpAddresses.Count > 0;
    private static HashSet<ObservedEndpoint> Endpoints(IReadOnlyList<FlowSnapshot> flows, SessionMetadata? metadata)
    {
        var local = new HashSet<string>((metadata?.SelectedAdapter.IpAddresses ?? []).Select(CanonicalIp), StringComparer.Ordinal);
        var endpoints = new HashSet<ObservedEndpoint>();
        foreach (var flow in flows)
        {
            var key = flow.Key;
            if (metadata is null || local.Contains(CanonicalIp(key.DestinationIp)) && !local.Contains(CanonicalIp(key.SourceIp)))
                endpoints.Add(new(key.Protocol, key.SourceIp, key.SourcePort, key.IpProtocolNumber));
            if (metadata is null || local.Contains(CanonicalIp(key.SourceIp)) && !local.Contains(CanonicalIp(key.DestinationIp)))
                endpoints.Add(new(key.Protocol, key.DestinationIp, key.DestinationPort, key.IpProtocolNumber));
        }
        return endpoints;
    }

    private static string CanonicalIp(string ip) => IPAddress.TryParse(ip, out var address)
        ? new IPAddress(address.GetAddressBytes()).ToString() : ip;
}
