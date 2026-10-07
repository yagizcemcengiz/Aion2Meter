using System.Globalization;
using Aion2Meter.Capture;
using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public static class Program
{
    private const string Usage = "Usage: dotnet run --project src/Aion2Meter.Replay -- <capture.pcap> [--dump 20] [--flows] [--top-flows 20] [--sizes]\n" +
        "       dotnet run --project src/Aion2Meter.Replay -- compare <captureA.pcap> <captureB.pcap>\n" +
        "       dotnet run --project src/Aion2Meter.Replay -- research --help";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "research") return Research.ResearchCli.Run(args[1..]);
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.Error.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var compare = args[0] == "compare";
        var dump = 0;
        int? top = null;
        var flows = false;
        var sizes = false;
        if (compare && args.Length != 3) return UsageError("compare requires exactly two capture files.");
        if (!compare)
        {
            if (args[0].StartsWith('-')) return UsageError("Specify a capture file first.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                var option = args[index];
                if (!seen.Add(option)) return UsageError($"Duplicate option: {option}");
                switch (option)
                {
                    case "--flows": flows = true; break;
                    case "--sizes": sizes = flows = true; break;
                    case "--dump":
                    case "--top-flows":
                        if (++index >= args.Length || !int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                            || count > 10_000 || count < (option == "--dump" ? 0 : 1))
                            return UsageError($"{option} requires a count between {(option == "--dump" ? 0 : 1)} and 10000.");
                        if (option == "--dump") dump = count;
                        else { top = count; flows = true; }
                        break;
                    default: return UsageError($"Unknown option: {option}");
                }
            }
        }
        try
        {
            var analyzer = new ReplayAnalyzer(new PcapPacketSource(), new PacketMetadataReader());
            if (compare)
            {
                var a = analyzer.Analyze(args[1], includeFlows: true);
                var b = analyzer.Analyze(args[2], includeFlows: true);
                var difference = CaptureComparison.Compare(a, b, LoadMetadata(args[1]), LoadMetadata(args[2]));
                Console.WriteLine("Capture comparison: all deltas are B - A; directional flow keys include exact IPs/ports.");
                Console.WriteLine($"Total packet delta: {difference.PacketDelta}");
                Console.WriteLine($"Total byte delta: {difference.ByteDelta}");
                Console.WriteLine($"Metadata errors: A={a.MetadataErrors}, B={b.MetadataErrors}; ungrouped packets: A={a.UngroupedPackets}, B={b.UngroupedPackets}");
                WriteDifferences("Only A", difference.OnlyA);
                WriteDifferences("Only B", difference.OnlyB);
                WriteDifferences("Common flows", difference.Common);
                var endpointKind = difference.RemoteEndpointsIdentified ? "remote endpoints (relative to adapter IPs in session metadata)"
                    : "observed endpoints (local/remote direction unavailable without both session metadata files)";
                Console.WriteLine($"New {endpointKind}: {difference.NewEndpoints.Count}");
                foreach (var endpoint in difference.NewEndpoints) Console.WriteLine(endpoint.Display);
                Console.WriteLine($"Lost {endpointKind}: {difference.LostEndpoints.Count}");
                foreach (var endpoint in difference.LostEndpoints) Console.WriteLine(endpoint.Display);
                return 0;
            }
            var result = analyzer.Analyze(args[0], dump, includeFlows: flows);
            WriteSummary(result);
            foreach (var packet in result.Dump) Console.WriteLine(packet.Format());
            if (flows)
            {
                var selected = top is { } limit ? FlowStatistics.Top(result.Flows, limit) : result.Flows;
                Console.WriteLine($"Directional flows: {result.Flows.Count}; shown: {selected.Count}; ungrouped packets: {result.UngroupedPackets}");
                Console.WriteLine("Protocol | Source IP | Source Port | Destination IP | Destination Port | Packets | Bytes | First Seen UTC | Last Seen UTC | Duration");
                foreach (var flow in selected)
                {
                    var key = flow.Key;
                    Console.WriteLine(FormattableString.Invariant($"{key.Protocol}({key.IpProtocolNumber}) | {key.SourceIp} | {key.SourcePort} | {key.DestinationIp} | {key.DestinationPort} | {flow.Packets} | {flow.Bytes} | {flow.FirstSeen:O} | {flow.LastSeen:O} | {flow.Duration:c}"));
                    if (sizes)
                    {
                        Console.WriteLine(FormattableString.Invariant($"  Sizes: packets={flow.Packets}, min={flow.MinimumCapturedLength}, max={flow.MaximumCapturedLength}, average={flow.AverageCapturedLength:F2}"));
                        Console.WriteLine("  Most common captured lengths: " + string.Join(", ", flow.CommonSizes().Select(s => $"{s.CapturedLength} bytes ({s.Packets} packets)")));
                    }
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Replay error: {ex.Message}");
            return 1;
        }
    }

    private static SessionMetadata? LoadMetadata(string capture)
    {
        var path = Path.ChangeExtension(capture, ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var metadata = SessionMetadataStore.Deserialize(File.ReadAllText(path));
            if (metadata.SelectedAdapter is null || metadata.SelectedAdapter.IpAddresses is null)
                throw new InvalidDataException("Missing adapter IP context.");
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"Warning: session metadata unavailable ({path}): {ex.Message}");
            return null;
        }
    }

    private static int UsageError(string message) { Console.Error.WriteLine(message); Console.Error.WriteLine(Usage); return 2; }
    private static void WriteDifferences(string title, IReadOnlyList<FlowDifference> flows)
    {
        Console.WriteLine($"{title}: {flows.Count}");
        foreach (var flow in flows)
            Console.WriteLine($"{flow.Key.Display} packets A={flow.PacketsA} B={flow.PacketsB} delta={flow.PacketDelta}; bytes A={flow.BytesA} B={flow.BytesB} delta={flow.ByteDelta}");
    }

    private static void WriteSummary(ReplayResult result)
    {
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
    }
}
