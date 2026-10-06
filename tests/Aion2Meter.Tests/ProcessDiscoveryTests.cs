using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class ProcessDiscoveryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void MapsOwnerPidAddressesNetworkOrderPortsAndTcpState(bool ipv6, bool tcp)
    {
        var rowSize = ipv6 ? tcp ? 56 : 28 : tcp ? 24 : 12;
        var table = new byte[4 + 2 * rowSize];
        U32(table, 0, 2);
        for (var index = 0; index < 2; index++)
        {
            var row = table.AsSpan(4 + index * rowSize, rowSize);
            var local = IPAddress.Parse(ipv6 ? "fe80::1234" : "192.0.2.1");
            var remote = IPAddress.Parse(ipv6 ? "2001:db8::2" : "198.51.100.2");
            local.GetAddressBytes().CopyTo(row[(ipv6 ? 0 : tcp ? 4 : 0)..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[(ipv6 ? 20 : tcp ? 8 : 4)..], (ushort)(49152 + index));
            if (ipv6) U32(row, 16, 7);
            if (tcp)
            {
                remote.GetAddressBytes().CopyTo(row[(ipv6 ? 24 : 12)..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[(ipv6 ? 44 : 16)..], 443);
                U32(row, ipv6 ? 48 : 0, 5);
            }
            U32(row, rowSize - 4, (uint)(1234 + index));
        }
        var endpoints = WindowsEndpointTableDecoder.Decode(table, tcp ? TransportProtocol.Tcp : TransportProtocol.Udp,
            ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        Assert.Equal(2, endpoints.Count);
        Assert.Equal(1234, endpoints[0].Pid);
        Assert.Equal(1235, endpoints[1].Pid);
        Assert.Equal(ipv6 ? "fe80::1234%7" : "192.0.2.1", endpoints[0].LocalIp);
        Assert.Equal(49152, endpoints[0].LocalPort);
        Assert.Equal(49153, endpoints[1].LocalPort);
        Assert.Equal(tcp ? "Established" : null, endpoints[0].TcpState);
        Assert.Equal(tcp ? (ushort?)443 : null, endpoints[0].RemotePort);
        Assert.Equal(tcp ? ipv6 ? "2001:db8::2" : "198.51.100.2" : null, endpoints[0].RemoteIp);
    }

    [Fact]
    public void RejectsMalformedTablesAndSkipsNonProcessRows()
    {
        Assert.Throws<InvalidDataException>(() => WindowsEndpointTableDecoder.Decode([], TransportProtocol.Tcp, AddressFamily.InterNetwork));
        var table = new byte[16];
        U32(table, 0, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => WindowsEndpointTableDecoder.Decode(table, TransportProtocol.Udp, AddressFamily.InterNetwork));
        U32(table, 0, 1);
        Assert.Empty(WindowsEndpointTableDecoder.Decode(table, TransportProtocol.Udp, AddressFamily.InterNetwork));
        U32(table, 12, uint.MaxValue);
        Assert.Empty(WindowsEndpointTableDecoder.Decode(table, TransportProtocol.Udp, AddressFamily.InterNetwork));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidPidWithoutOpeningProcess(int pid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessNetworkSnapshot([], [], []).ForPid(pid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsProcessNameResolver().Resolve(pid));
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("argument")]
    [InlineData("denied")]
    [InlineData("win32")]
    [InlineData("null")]
    public void RetainsPidWhenProcessDisappearsOrNameAccessIsDenied(string failure)
    {
        var snapshot = new ProcessNetworkDiscovery(new FakeTables(), new FailingNames(failure)).Refresh();
        var process = Assert.Single(snapshot.Processes);
        Assert.Equal(1234, process.Pid);
        Assert.Equal("Unavailable / exited", process.ProcessName);
        Assert.Equal(process.ProcessName, Assert.Single(snapshot.ForPid(1234)).ProcessName);
        Assert.Single(snapshot.Warnings);
    }

    [Fact]
    public void RefreshDoesNotCacheExitedProcesses()
    {
        var source = new FakeTables();
        var resolver = new CountingNames();
        var discovery = new ProcessNetworkDiscovery(source, resolver);
        source.Endpoints = [Endpoint(), Endpoint() with { LocalPort = 80 }];
        Assert.Single(discovery.Refresh().Processes);
        Assert.Equal(1, resolver.Calls);
        source.Endpoints = [];
        Assert.Empty(discovery.Refresh().Processes);
    }

    internal static ProcessNetworkEndpoint Endpoint() => new(1234, "Browser", TransportProtocol.Tcp,
        AddressFamily.InterNetwork, "192.0.2.1", 49152, "198.51.100.2", 443, "Established");
    private static void U32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private sealed class FakeTables : IEndpointTableSource
    {
        public IReadOnlyList<ProcessNetworkEndpoint> Endpoints { get; set; } = [Endpoint()];
        public EndpointTableResult Read() => new(Endpoints, ["Synthetic table warning"]);
    }
    private sealed class FailingNames(string failure) : IProcessNameResolver
    {
        public string? Resolve(int pid) => failure switch
        {
            "exited" => throw new InvalidOperationException("Exited"),
            "argument" => throw new ArgumentException("PID exited"),
            "denied" => throw new UnauthorizedAccessException("Denied"),
            "win32" => throw new Win32Exception(5), _ => null
        };
    }
    private sealed class CountingNames : IProcessNameResolver
    {
        public int Calls { get; private set; }
        public string Resolve(int pid) { Calls++; return "Browser"; }
    }
}
