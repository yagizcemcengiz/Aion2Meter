using System.Net;
using System.Net.Sockets;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class CaptureFilterTests
{
    [Fact]
    public void TcpConnectionConstrainsBothDirectionsToFullTuple()
    {
        var result = CaptureFilterBuilder.Build([ProcessDiscoveryTests.Endpoint()]);
        Assert.True(result.Success);
        Assert.Equal("(ip and tcp and ((src host 192.0.2.1 and src port 49152 and dst host 198.51.100.2 and dst port 443) or (dst host 192.0.2.1 and dst port 49152 and src host 198.51.100.2 and src port 443)))", result.Filter);
        Assert.Contains(result.Warnings, w => w.Contains("false positives"));
    }

    [Fact]
    public void UdpHasNoInventedRemotePeer()
    {
        var result = CaptureFilterBuilder.Build([ProcessDiscoveryTests.Endpoint() with
            { Protocol = TransportProtocol.Udp, RemoteIp = null, RemotePort = null, TcpState = null }]);
        Assert.True(result.Success);
        Assert.Equal("(ip and udp and ((src host 192.0.2.1 and src port 49152) or (dst host 192.0.2.1 and dst port 49152)))", result.Filter);
        Assert.Contains(result.Warnings, w => w.Contains("no remote peer"));
    }

    [Fact]
    public void Ipv6UsesNumericIpLiteralWithoutScopeSuffix()
    {
        var result = CaptureFilterBuilder.Build([ProcessDiscoveryTests.Endpoint() with
            { AddressFamily = AddressFamily.InterNetworkV6, LocalIp = "fe80::1%7", RemoteIp = "fe80::2%7" }]);
        Assert.True(result.Success);
        Assert.StartsWith("(ip6 and tcp", result.Filter);
        Assert.Contains("src host fe80::1", result.Filter);
        Assert.DoesNotContain('%', result.Filter!);
    }

    [Fact]
    public void WildcardIsExpandedOnlyToMatchingAdapterAddresses()
    {
        var endpoint = ProcessDiscoveryTests.Endpoint() with { LocalIp = "0.0.0.0", RemoteIp = "0.0.0.0", RemotePort = 0, TcpState = "Listen" };
        Assert.False(CaptureFilterBuilder.Build([endpoint]).Success);
        var result = CaptureFilterBuilder.Build([endpoint], [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::1")]);
        Assert.True(result.Success);
        Assert.Contains("host 192.0.2.10", result.Filter);
        Assert.DoesNotContain("2001:db8", result.Filter!);
        Assert.DoesNotContain("host 0.0.0.0", result.Filter!);
    }

    [Fact]
    public void Ipv6WildcardUsesOnlyIpv6AdapterAddresses()
    {
        var endpoint = ProcessDiscoveryTests.Endpoint() with { AddressFamily = AddressFamily.InterNetworkV6,
            LocalIp = "::", RemoteIp = null, RemotePort = null, Protocol = TransportProtocol.Udp };
        var result = CaptureFilterBuilder.Build([endpoint], [IPAddress.Parse("fe80::1%7"), IPAddress.Parse("192.0.2.1")]);
        Assert.True(result.Success);
        Assert.Contains("ip6 and udp", result.Filter);
        Assert.Contains("host fe80::1", result.Filter);
        Assert.DoesNotContain("192.0.2.1", result.Filter!);
    }

    [Fact]
    public void InvalidRemoteAndUnsupportedProtocolAreRejected()
    {
        var endpoint = ProcessDiscoveryTests.Endpoint();
        Assert.False(CaptureFilterBuilder.Build([endpoint with { RemoteIp = "bad or ip" }]).Success);
        Assert.False(CaptureFilterBuilder.Build([endpoint with { RemoteIp = "2001:db8::1" }]).Success);
        Assert.False(CaptureFilterBuilder.Build([endpoint with { Protocol = TransportProtocol.Other }]).Success);
    }

    [Fact]
    public void EmptyEndpointsCannotFallBackToAllTraffic()
    {
        var result = CaptureFilterBuilder.Build([]);
        Assert.False(result.Success);
        Assert.Null(result.Filter);
        Assert.Contains("No endpoints", result.Error);
    }

    [Fact]
    public void MultipleEndpointsAreDeduplicatedGroupedAndOrdered()
    {
        var a = ProcessDiscoveryTests.Endpoint();
        var b = a with { LocalPort = 49153 };
        var result = CaptureFilterBuilder.Build([b, a, a]);
        Assert.Equal(CaptureFilterBuilder.Build([a, b]).Filter, result.Filter);
        Assert.Equal(CaptureFilterBuilder.Build([a]).Filter + " or " + CaptureFilterBuilder.Build([b]).Filter, result.Filter);
    }

    [Theory]
    [InlineData("bad host or tcp", 123)]
    [InlineData("192.0.2.1", 0)]
    [InlineData("2001:db8::1", 123)]
    public void RejectsInvalidOrInjectableEndpointInsteadOfBroadening(string ip, ushort port)
    {
        Assert.False(CaptureFilterBuilder.Build([ProcessDiscoveryTests.Endpoint() with { LocalIp = ip, LocalPort = port }]).Success);
    }

    [Fact]
    public void OversizedFilterAndEndpointListsFailClosed()
    {
        var endpoint = ProcessDiscoveryTests.Endpoint();
        Assert.False(CaptureFilterBuilder.Build(Enumerable.Repeat(endpoint, CaptureFilterBuilder.MaximumEndpoints + 1).ToArray()).Success);
        var wildcard = endpoint with { LocalIp = "0.0.0.0" };
        var addresses = Enumerable.Range(1, 1000).Select(i => IPAddress.Parse($"192.0.{i / 256}.{i % 256}")).ToArray();
        var result = CaptureFilterBuilder.Build([wildcard], addresses);
        Assert.False(result.Success);
        Assert.Contains("too large", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-123)]
    public async Task CaptureRejectsInvalidPidBeforeTouchingNpcap(int pid)
    {
        await using var engine = new PassiveCaptureEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(Adapter(), "unused", new(CaptureMode.SelectedProcessTraffic, SelectedPid: pid)));
        Assert.Null(engine.Session);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task CaptureRejectsEmptyAndForeignPidEndpointsBeforeTouchingNpcap()
    {
        await using var engine = new PassiveCaptureEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(Adapter(), "unused", new(CaptureMode.SelectedProcessTraffic, SelectedPid: 1234)));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(Adapter(), "unused", new(CaptureMode.SelectedProcessTraffic, SelectedPid: 999, ConnectionsAtStart: [ProcessDiscoveryTests.Endpoint()])));
        Assert.Null(engine.Session);
    }

    [Fact]
    public async Task AllTrafficKeepsPhaseOneIpFilterRegardlessOfSelectedEndpoints()
    {
        await using var engine = new PassiveCaptureEngine();
        var logs = new List<DiagnosticMessage>();
        engine.Diagnostic += logs.Add;
        // A remote adapter is rejected before native initialization; verify the resolved mode/filter.
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(Adapter() with { Identifier = "rpcap://invalid" }, "unused",
            new(CaptureMode.AllTraffic, SelectedPid: -1, ConnectionsAtStart: [ProcessDiscoveryTests.Endpoint()])));
        Assert.Contains(logs, log => log.Message == "BPF filter: ip or ip6");
        Assert.Null(engine.Session);
    }

    [Fact]
    public async Task UnknownCaptureModeIsRejectedBeforeTouchingNpcap()
    {
        await using var engine = new PassiveCaptureEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(Adapter(), "unused", new((CaptureMode)99)));
        Assert.Null(engine.Session);
    }

    internal static NetworkAdapter Adapter() => new("synthetic", "Test adapter", "Synthetic adapter", [IPAddress.Parse("192.0.2.1")], [], null, "Up");
}
