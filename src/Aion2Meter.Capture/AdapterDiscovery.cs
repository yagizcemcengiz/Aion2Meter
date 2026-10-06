using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Aion2Meter.Core;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2Meter.Capture;

public sealed class AdapterDiscovery : IAdapterDiscovery
{
    public const string MissingNpcapMessage = "Npcap is not installed or could not be loaded.";

    public AdapterDiscoveryResult Discover()
    {
        try
        {
            _ = Pcap.Version;
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var adapters = LibPcapLiveDeviceList.New()
                .Where(d => !d.Name.StartsWith("rpcap", StringComparison.OrdinalIgnoreCase))
                .Select(device => Describe(device, interfaces)).ToArray();
            return new(true, adapters);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            return new(false, [], MissingNpcapMessage);
        }
        catch (Exception ex)
        {
            return new(true, [], $"Could not list capture adapters: {ex.Message}");
        }
    }

    public static bool IsNativeLoadFailure(Exception exception) =>
        exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeLoadException
        || (exception.InnerException is { } inner && IsNativeLoadFailure(inner));

    private static NetworkAdapter Describe(LibPcapLiveDevice device, NetworkInterface[] interfaces)
    {
        var match = interfaces.FirstOrDefault(n => device.Name.Contains(n.Id, StringComparison.OrdinalIgnoreCase));
        var addresses = new List<IPAddress>();
        string? mac = null;
        var status = $"Capture-capable (flags 0x{device.Flags:X})";
        if (match is not null)
        {
            status = match.OperationalStatus.ToString();
            try
            {
                addresses.AddRange(match.GetIPProperties().UnicastAddresses.Select(a => a.Address));
                var bytes = match.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length > 0) mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
            catch (NetworkInformationException) { }
        }
        foreach (var address in device.Addresses)
        {
            if (address.Addr?.ipAddress is { } ip && !addresses.Contains(ip)) addresses.Add(ip);
        }
        return new(device.Name, match?.Name ?? device.Description ?? device.Name, device.Description ?? device.Name,
            addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray(),
            addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray(), mac, status);
    }
}
