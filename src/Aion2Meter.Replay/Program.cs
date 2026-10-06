using System.Globalization;
using Aion2Meter.Capture;

namespace Aion2Meter.Replay;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length is not (1 or 3) || (args.Length == 1 && args[0] is "--help" or "-h"))
        {
            Console.Error.WriteLine("Usage: dotnet run --project src/Aion2Meter.Replay -- <capture-file> [--dump 20]");
            return args.Length == 1 && args[0] is "--help" or "-h" ? 0 : 2;
        }
        var dumpCount = 0;
        if (args.Length == 3 && (args[1] != "--dump" || !int.TryParse(args[2], out dumpCount) || dumpCount is < 0 or > 10_000))
        {
            Console.Error.WriteLine("--dump requires a packet count between 0 and 10000.");
            return 2;
        }
        try
        {
            var result = new ReplayAnalyzer(new PcapPacketSource(), new PacketMetadataReader()).Analyze(args[0], dumpCount);
            var stats = result.Statistics;
            Console.WriteLine($"Total packets: {stats.TotalPackets}");
            Console.WriteLine($"Total bytes: {stats.TotalBytes}");
            Console.WriteLine($"First timestamp: {stats.FirstTimestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a"}");
            Console.WriteLine($"Last timestamp: {stats.LastTimestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a"}");
            Console.WriteLine($"Duration: {stats.Duration.ToString("c", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"TCP count: {stats.TcpCount}");
            Console.WriteLine($"UDP count: {stats.UdpCount}");
            Console.WriteLine($"Other count: {stats.OtherCount}");
            Console.WriteLine($"Metadata errors: {result.MetadataErrors}");
            foreach (var packet in result.Dump) Console.WriteLine(packet.Format());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Replay error: {ex.Message}");
            return 1;
        }
    }
}
