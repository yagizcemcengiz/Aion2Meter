using System.Globalization;
using System.Net;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class PacketMetadataTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-10-07T00:15:30.123456Z");

    [Fact]
    public void FormatsUtcLengthsEndpointsAndFlagsWithoutPayload()
    {
        var metadata = new PacketMetadata(Timestamp.ToOffset(TimeSpan.FromHours(3)), 54, 100, 0x0800, 4,
            IPAddress.Parse("192.0.2.1"), IPAddress.Parse("198.51.100.2"), TransportProtocol.Tcp,
            12345, 443, TcpFlags.Syn | TcpFlags.Ack, 6);
        var formatted = metadata.Format();
        Assert.Contains("2026-10-07T00:15:30.1234560Z", formatted);
        Assert.Contains("captured=54 original=100", formatted);
        Assert.Contains("ether=0x0800 ip=4", formatted);
        Assert.Contains("192.0.2.1:12345 -> 198.51.100.2:443", formatted);
        Assert.Contains("protocol=Tcp(6)", formatted);
        Assert.Contains("flags=Syn, Ack", formatted);
    }

    [Fact]
    public void FormattingBracketsIpv6EndpointsAndHandlesMissingFields()
    {
        var packet = new PacketMetadata(Timestamp, 62, 62, SourceIp: IPAddress.IPv6Loopback, SourcePort: 53);
        Assert.Contains("[::1]:53 -> -", packet.Format());
        Assert.Contains("flags=-", packet.Format());
    }

    [Fact]
    public void FormattingIsCultureIndependent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Contains(".1234560Z", new PacketMetadata(Timestamp, 1, 2).Format());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void ReadsTcpMetadataAndPreservesOriginalLength()
    {
        var metadata = new PacketMetadataReader().Read(TestFiles.Packet(6, Timestamp, 100));
        Assert.Equal(54, metadata.CapturedLength);
        Assert.Equal(100, metadata.OriginalLength);
        Assert.Equal(Timestamp, metadata.TimestampUtc);
        Assert.Equal((ushort)0x0800, metadata.EthernetType);
        Assert.Equal(4, metadata.IpVersion);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), metadata.SourceIp);
        Assert.Equal(IPAddress.Parse("198.51.100.2"), metadata.DestinationIp);
        Assert.Equal(TransportProtocol.Tcp, metadata.TransportProtocol);
        Assert.Equal((ushort)12345, metadata.SourcePort);
        Assert.Equal((ushort)443, metadata.DestinationPort);
        Assert.Equal(TcpFlags.Syn | TcpFlags.Ack, metadata.TcpFlags);
    }

    [Fact]
    public void ReadsIpv6UdpMetadata()
    {
        var metadata = new PacketMetadataReader().Read(TestFiles.IPv6Udp(Timestamp));
        Assert.Equal(6, metadata.IpVersion);
        Assert.Equal((ushort)0x86dd, metadata.EthernetType);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), metadata.SourceIp);
        Assert.Equal(IPAddress.Parse("2001:db8::2"), metadata.DestinationIp);
        Assert.Equal(TransportProtocol.Udp, metadata.TransportProtocol);
        Assert.Equal((ushort)53, metadata.DestinationPort);
        Assert.Null(metadata.TcpFlags);
    }

    [Fact]
    public void NonInitialIpv4FragmentDoesNotInterpretPayloadAsPorts()
    {
        var packet = TestFiles.Packet(17, Timestamp);
        packet.Data[20] = 0x00;
        packet.Data[21] = 0x01;
        var metadata = new PacketMetadataReader().Read(packet);
        Assert.Equal(TransportProtocol.Udp, metadata.TransportProtocol);
        Assert.Null(metadata.SourcePort);
        Assert.Null(metadata.DestinationPort);
    }

    [Fact]
    public void IcmpIsOtherAndHasNoPorts()
    {
        var metadata = new PacketMetadataReader().Read(TestFiles.Packet(1, Timestamp));
        Assert.Equal(TransportProtocol.Other, metadata.TransportProtocol);
        Assert.Equal((byte)1, metadata.IpProtocolNumber);
        Assert.Null(metadata.SourcePort);
    }

    [Fact]
    public void NonInitialIpv6FragmentDoesNotInterpretPayloadAsPorts()
    {
        var data = Convert.FromHexString("00112233445566778899aabb86dd" +
            "6000000000102c40" + "20010db8000000000000000000000001" + "20010db8000000000000000000000002" +
            "1100000900000001" + "3039003500080000");
        var metadata = new PacketMetadataReader().Read(new(Timestamp, data.Length, 1, data));
        Assert.Equal(6, metadata.IpVersion);
        Assert.Equal(TransportProtocol.Udp, metadata.TransportProtocol);
        Assert.Null(metadata.SourcePort);
        Assert.Null(metadata.DestinationPort);
    }

    [Fact]
    public void IpTunnelDoesNotMixOuterEndpointsWithInnerTransportHeader()
    {
        var inner = TestFiles.Packet(6, Timestamp).Data[14..];
        var outer = TestFiles.Packet(4, Timestamp).Data[..34];
        outer[16] = 0;
        outer[17] = (byte)(20 + inner.Length);
        var data = outer.Concat(inner).ToArray();
        var metadata = new PacketMetadataReader().Read(new(Timestamp, data.Length, 1, data));
        Assert.Equal(TransportProtocol.Other, metadata.TransportProtocol);
        Assert.Equal((byte)4, metadata.IpProtocolNumber);
        Assert.Null(metadata.SourcePort);
        Assert.Null(metadata.DestinationPort);
    }
}
