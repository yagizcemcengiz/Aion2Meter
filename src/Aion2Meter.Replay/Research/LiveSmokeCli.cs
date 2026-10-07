using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2Meter.Capture;

namespace Aion2Meter.Replay.Research;

public static class LiveSmokeCli
{
    private const string Usage = "research live-smoke --list-interfaces\n" +
        "research live-smoke --interface N [--port 13328] [--json] [--verbose]\n" +
        "N is the one-based index from --list-interfaces. Passive capture; Ctrl+C stops.\n" +
        "Start before entering world for fresh-handshake 1536/3336 binding. Midstream stays Unknown.\n" +
        "Bounded epoch diagnostic snapshots, not append-only events/DPS: 64 MiB / 250000 packets / 8 flows.\n" +
        "No automatic adapter or remote-IP selection; no injection, overlay or 0x36 support.";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        try
        {
            var list = false; var json = false; var verbose = false; int? index = null; ushort port = 13328;
            var seen = new HashSet<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var option = args[i];
                if (!seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
                switch (option)
                {
                    case "--list-interfaces": list = true; break;
                    case "--json": json = true; break;
                    case "--verbose": verbose = true; break;
                    case "--interface": index = Positive(); break;
                    case "--port": port = checked((ushort)Positive()); break;
                    default: throw new ArgumentException("Unknown option: " + option);
                }
                int Positive() => ++i < args.Length && int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? n : throw new ArgumentException("Expected positive integer.");
            }
            if (list && (index is not null || json || verbose || port != 13328) || !list && index is null)
                throw new ArgumentException("Use --list-interfaces alone, or select --interface N.");
            var discovery = NpcapLiveSource.EnumerateInterfaces();
            if (!discovery.NpcapReady || discovery.Error is not null) throw new InvalidOperationException(discovery.Error ?? AdapterDiscovery.MissingNpcapMessage);
            var adapters = discovery.Adapters.Where(a => a.IPv4Addresses.Count + a.IPv6Addresses.Count > 0).ToArray();
            if (list)
            {
                for (var i = 0; i < adapters.Length; i++)
                    output.WriteLine($"{i + 1}: {adapters[i].DisplayName}\n  {adapters[i].Identifier}\n  IPs={string.Join(',', adapters[i].IPv4Addresses.Concat(adapters[i].IPv6Addresses))}");
                if (adapters.Length == 0) output.WriteLine("No local capture interfaces with IP context available.");
                return 0;
            }
            if (index < 1 || index > adapters.Length) throw new ArgumentException("Interface index unavailable; list interfaces again.");
            var adapter = adapters[index!.Value - 1];
            var pipeline = new LivePacketPipeline(adapter.IPv4Addresses.Concat(adapter.IPv6Addresses), port);
            var source = new NpcapLiveSource(adapter, port);
            var options = new JsonSerializerOptions(); options.Converters.Add(new JsonStringEnumConverter());
            output.WriteLine(json ? JsonSerializer.Serialize(new { Kind = "start", source.SourceId, Interface = adapter.Identifier, Port = port })
                : $"Interface={adapter.DisplayName}; source={source.SourceId}; TCP port={port}; passive capture; Ctrl+C stops.");
            using var cancellation = new CancellationTokenSource();
            void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }
            Console.CancelKeyPress += Cancel;
            try
            {
                LiveDiagnosticRunner.RunAsync(source, pipeline, epochs =>
                {
                    if (json)
                        output.WriteLine(JsonSerializer.Serialize(new { Kind = "snapshot", TimestampUtc = DateTimeOffset.UtcNow,
                            pipeline.MalformedPackets, pipeline.UnsupportedPackets, pipeline.IgnoredPackets, pipeline.RejectedFlows, Epochs = epochs }, options));
                    else
                    {
                        output.WriteLine($"UTC={DateTimeOffset.UtcNow:O}; flows={epochs.Count}; malformed={pipeline.MalformedPackets}; unsupported-link/IP={pipeline.UnsupportedPackets}; ignored={pipeline.IgnoredPackets}; rejected-flows={pipeline.RejectedFlows}");
                        foreach (var e in epochs)
                        {
                            output.WriteLine($"  {e.EpochId} {e.Connection.LocalIp}:{e.Connection.LocalPort} -> {e.Connection.RemoteIp}:{e.Connection.RemotePort} {e.Lifecycle}; protocol={e.ProtocolObserved}; ISNs={e.ClientIsn}/{e.ServerIsn}; binding={e.BindingStatus} {JsonSerializer.Serialize(e.CharacterName)} id={e.EntityId}; accepted={e.AcceptedEvents}; Self={e.SelfCount} Other={e.OtherCount} Unknown={e.UnknownCount}; recent-Self=[{string.Join(',', e.RecentSelfAmounts)}]; unsupported-combat={e.UnsupportedCandidates}; gaps={e.Gaps} conflicts={e.Conflicts} duplicates={e.DuplicateSegments}");
                            if (verbose) foreach (var warning in e.Warnings) output.WriteLine("    " + warning);
                        }
                    }
                }, TimeSpan.FromSeconds(2), cancellation.Token).GetAwaiter().GetResult();
            }
            finally { Console.CancelKeyPress -= Cancel; }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { error.WriteLine(ex.Message); error.WriteLine(Usage); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or SharpPcap.PcapException || AdapterDiscovery.IsNativeLoadFailure(ex))
        { error.WriteLine("Live capture error: " + ex.Message); return 1; }
    }
}
