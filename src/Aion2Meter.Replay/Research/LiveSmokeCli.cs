using System.Globalization;
using System.Text.Json;
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
        => RunCore(args, false, output, error);

    public static int RunMeter(string[] args, TextWriter? output = null, TextWriter? error = null)
        => RunCore(args, true, output, error);

    private static int RunCore(string[] args, bool meterMode, TextWriter? output, TextWriter? error)
    {
        output ??= Console.Out; error ??= Console.Error;
        var usage = meterMode ? "research live-meter --interface N [--port 13328] [--idle-seconds 30] [--json] [--verbose]\n" +
            "research live-meter --list-interfaces\nMay start mid-game; waits for authoritative identity on the next fresh game connection. Self only; partial 06/26 coverage.\n" +
            "ACKed complete-frame publication; Ctrl+C stops. Bounded 64 MiB / 250000 packets.\n" +
            "Elapsed/DPS use first-to-last completed Self hit; timeout closes the encounter." : Usage;
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(usage); return 0; }
        try
        {
            var list = false; var json = false; var verbose = false; int? index = null; ushort port = 13328;
            var idleSeconds = 30;
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
                    case "--idle-seconds" when meterMode: idleSeconds = Positive();
                        if (idleSeconds > 3600) throw new ArgumentException("Idle timeout must be 1 to 3600 seconds.");
                        break;
                    default: throw new ArgumentException("Unknown option: " + option);
                }
                int Positive() => ++i < args.Length && int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                    ? n : throw new ArgumentException("Expected positive integer.");
            }
            if (list && (index is not null || json || verbose || port != 13328 || seen.Contains("--idle-seconds")) || !list && index is null)
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
            var feed = meterMode ? new LiveCombatFeed() : null;
            var meter = feed is not null ? new LiveCombatMeter(feed, TimeSpan.FromSeconds(idleSeconds)) : null;
            var pipeline = new LivePacketPipeline(adapter.IPv4Addresses.Concat(adapter.IPv6Addresses), port, combatFeed: feed);
            var source = new NpcapLiveSource(adapter, port);
            output.WriteLine(json ? LiveDiagnosticJson.SerializeStart(source.SourceId, adapter.Identifier, port)
                : $"Interface={adapter.DisplayName}; source={source.SourceId}; TCP port={port}; passive capture; Ctrl+C stops.");
            using var dashboard = meterMode && !json ? new LiveMeterDashboard(output,
                ReferenceEquals(output, Console.Out) && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb", verbose,
                CursorVisible()) : null;
            using var cancellation = new CancellationTokenSource();
            void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }
            Console.CancelKeyPress += Cancel;
            try
            {
                LiveDiagnosticRunner.RunAsync(source, pipeline, epochs =>
                {
                    if (meter is not null && feed is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var state = meter.Snapshot(now);
                        if (json) output.WriteLine(LiveMeterJson.Serialize(pipeline, feed, epochs, state, now));
                        else
                        {
                            var p = LiveMeterJson.Performance(pipeline, feed);
                            dashboard!.Render(state, FormattableString.Invariant($"Tick {p.DiagnosticTickMilliseconds:F1}ms | Checkpoints {p.CheckpointCount} | Published {p.PublishedEvents} | Tail {p.SelectedPackets} packets / {p.RetainedPayloadBytes} bytes | Verify {p.VerificationBytes} bytes"));
                        }
                        return;
                    }
                    if (json)
                        output.WriteLine(SnapshotJson(pipeline, epochs, DateTimeOffset.UtcNow));
                    else
                    {
                        output.WriteLine($"UTC={DateTimeOffset.UtcNow:O}; flows={epochs.Count}; malformed={pipeline.MalformedPackets}; unsupported-link/IP={pipeline.UnsupportedPackets}; ignored={pipeline.IgnoredPackets}; rejected-flows={pipeline.RejectedFlows}");
                        foreach (var e in epochs)
                        {
                            output.WriteLine($"  {e.EpochId} {e.Connection.LocalIp}:{e.Connection.LocalPort} -> {e.Connection.RemoteIp}:{e.Connection.RemotePort} {e.Lifecycle}; protocol={e.ProtocolObserved}; ISNs={e.ClientIsn}/{e.ServerIsn}; binding={e.BindingStatus} {JsonSerializer.Serialize(e.CharacterName)} id={e.EntityId}; accepted={e.AcceptedEvents}; Self={e.SelfCount} Other={e.OtherCount} Unknown={e.UnknownCount}; recent-Self=[{string.Join(',', e.RecentSelfAmounts)}]; unsupported-combat={e.UnsupportedCandidates}; gaps={e.Gaps} conflicts={e.Conflicts} duplicates={e.DuplicateSegments}");
                            if (verbose) foreach (var warning in e.Warnings) output.WriteLine("    " + warning);
                        }
                    }
                }, TimeSpan.FromMilliseconds(meterMode ? 500 : 2000), cancellation.Token).GetAwaiter().GetResult();
            }
            finally { Console.CancelKeyPress -= Cancel; }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { error.WriteLine(ex.Message); error.WriteLine(usage); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or SharpPcap.PcapException || AdapterDiscovery.IsNativeLoadFailure(ex))
        { error.WriteLine("Live capture error: " + ex.Message); return 1; }
    }

    public static string SnapshotJson(LivePacketPipeline pipeline, IReadOnlyList<LiveEpochSnapshot> epochs, DateTimeOffset timestampUtc) =>
        LiveDiagnosticJson.SerializeSnapshot(pipeline, epochs, timestampUtc);

    private static bool CursorVisible()
    {
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
            try { return Console.CursorVisible; } catch (IOException) { }
        return true;
    }
}
