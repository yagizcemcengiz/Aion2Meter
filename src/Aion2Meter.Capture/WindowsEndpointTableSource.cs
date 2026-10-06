using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Aion2Meter.Core;

namespace Aion2Meter.Capture;

// OWNER_PID rows contain DWORD fields (4-byte alignment), with addresses/ports in network order.
// Layouts: https://learn.microsoft.com/windows/win32/api/tcpmib/ns-tcpmib-mib_tcp6row_owner_pid
public static class WindowsEndpointTableDecoder
{
    public static IReadOnlyList<ProcessNetworkEndpoint> Decode(ReadOnlySpan<byte> table,
        TransportProtocol protocol, AddressFamily family)
    {
        if (protocol is not (TransportProtocol.Tcp or TransportProtocol.Udp) ||
            family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("Only IPv4/IPv6 TCP/UDP owner tables are supported.");
        var v6 = family == AddressFamily.InterNetworkV6;
        var tcp = protocol == TransportProtocol.Tcp;
        var rowSize = v6 ? (tcp ? 56 : 28) : (tcp ? 24 : 12);
        if (table.Length < 4) throw new InvalidDataException("Missing endpoint table header.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (table.Length - 4) / rowSize) throw new InvalidDataException("Truncated endpoint table.");
        var endpoints = new List<ProcessNetworkEndpoint>();
        for (var index = 0; index < count; index++)
        {
            var row = table.Slice(4 + index * rowSize, rowSize);
            var pidValue = UInt(row, rowSize - 4);
            if (pidValue is 0 or > int.MaxValue) continue;
            var localIp = v6 ? Ip6(row, 0, 16) : new IPAddress(row.Slice(tcp ? 4 : 0, 4)).ToString();
            var localPort = Port(row, v6 ? 20 : tcp ? 8 : 4);
            var remoteIp = !tcp ? null : v6 ? Ip6(row, 24, 40) : new IPAddress(row.Slice(12, 4)).ToString();
            ushort? remotePort = tcp ? Port(row, v6 ? 44 : 16) : null;
            var state = tcp ? State(UInt(row, v6 ? 48 : 0)) : null;
            endpoints.Add(new((int)pidValue, "Unavailable", protocol, family, localIp, localPort, remoteIp, remotePort, state));
        }
        return endpoints;
    }

    private static uint UInt(ReadOnlySpan<byte> row, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(row[offset..]);
    private static ushort Port(ReadOnlySpan<byte> row, int offset) => BinaryPrimitives.ReadUInt16BigEndian(row[offset..]);
    private static string Ip6(ReadOnlySpan<byte> row, int offset, int scope) => new IPAddress(row.Slice(offset, 16), UInt(row, scope)).ToString();
    private static string State(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => $"Unknown ({state})"
    };
}

public sealed class WindowsEndpointTableSource : IEndpointTableSource
{
    public EndpointTableResult Read()
    {
        if (!OperatingSystem.IsWindows()) return new([], ["Process endpoint discovery requires Windows."]);
        var endpoints = new List<ProcessNetworkEndpoint>();
        var warnings = new List<string>();
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        foreach (var protocol in new[] { TransportProtocol.Tcp, TransportProtocol.Udp })
        {
            try { endpoints.AddRange(ReadTable(protocol, family)); }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException or IOException or DllNotFoundException or EntryPointNotFoundException)
            { warnings.Add($"{family} {protocol} endpoint table unavailable: {ex.Message}"); }
        }
        return new(endpoints, warnings);
    }

    private static IReadOnlyList<ProcessNetworkEndpoint> ReadTable(TransportProtocol protocol, AddressFamily family)
    {
        uint size = 0;
        var tcp = protocol == TransportProtocol.Tcp;
        var af = family == AddressFamily.InterNetwork ? 2u : 23u;
        uint Fetch(IntPtr pointer) => tcp
            ? GetExtendedTcpTable(pointer, ref size, false, af, 5, 0) // TCP_TABLE_OWNER_PID_ALL
            : GetExtendedUdpTable(pointer, ref size, false, af, 1, 0); // UDP_TABLE_OWNER_PID
        var result = Fetch(IntPtr.Zero);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidDataException("Invalid endpoint table size.");
            var capacity = checked((int)size);
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                result = Fetch(buffer);
                if (result == 122) continue; // Table grew between calls; retry with the returned size.
                if (result != 0) throw new Win32Exception((int)result);
                if (size > capacity) throw new InvalidDataException("Endpoint table exceeds allocated buffer.");
                var bytes = new byte[checked((int)size)];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return WindowsEndpointTableDecoder.Decode(bytes, protocol, family);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Endpoint table kept changing. Refresh connections again.");
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, int tableClass, uint reserved);
}
