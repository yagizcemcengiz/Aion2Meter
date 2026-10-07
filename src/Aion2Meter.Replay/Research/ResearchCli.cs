using System.Globalization;
using System.Net;
using System.Text.Json;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public static class ResearchCli
{
    private const string Usage = "research <capture.pcap> --local IP:port --remote IP:port [options]\n" +
        "research compare <a.pcap> <b.pcap> [...] --local IP:port --remote IP:port --sequence CSV [options]\n" +
        "research blocks --help (candidate framing, families, numeric hypotheses)\n" +
        "research decode|containers --help (bounded replay protocol/container decoding)\n" +
        "research identities|id-graph|skills --help (capture-scoped replay identity correlation)\n" +
        "research damage-events --help (neutral supported events and provenance accounting audit)\n" +
        "research self-binding --help (conservative fresh-epoch replay CurrentPlayer binding)\n" +
        "research action-windows|record-groups|compare-groups --help (offline action/record correlation)\n" +
        "Options: --timeline --payload --max-payload-bytes 64 --max-packets 200\n" +
        "  --from seconds --to seconds --direction out|in|both\n" +
        "  --frame-length N --frame-lengths CSV --payload-length N\n" +
        "  --sequence CSV --sequence-window-ms 20 --streams --stream-offset 0 --stream-bytes 64 --stream-pattern HEX\n" +
        "Sequence matches are contiguous per direction; window bounds TOTAL first-to-last elapsed time. Output is console-only.";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        if (args.Length > 0 && args[0] is "action-windows" or "record-groups" or "compare-groups") return ActionResearchCli.Run(args[0], args[1..], output, error);
        if (args.Length > 0 && args[0] == "blocks") return BlockResearchCli.Run(args[1..], output, error);
        if (args.Length > 0 && args[0] is "decode" or "containers") return ProtocolResearchCli.Run(args[1..], output, error);
        if (args.Length > 0 && args[0] is "identities" or "id-graph" or "skills" or "damage-events" or "self-binding") return ProtocolResearchCli.Run(args[1..], output, error, args[0]);
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        try
        {
            var options = Parse(args);
            var connection = new TcpConnectionSelection(options.Local.Address, (ushort)options.Local.Port, options.Remote.Address, (ushort)options.Remote.Port);
            var captures = new List<ResearchCapture>();
            long totalBytes = 0, totalPackets = 0;
            foreach (var path in options.Paths)
            {
                var capture = new ResearchAnalyzer().Read(path, connection, MetadataStart(path, error));
                totalBytes += capture.Packets.Sum(p => (long)p.Segment.Payload.Length);
                totalPackets += capture.Packets.Count;
                if (totalBytes > 128 * 1024 * 1024 || totalPackets > 500_000)
                    throw new InvalidDataException("Combined research limit reached (128 MiB payload / 500000 packets). Use smaller captures.");
                captures.Add(capture);
            }
            var samples = new List<(string Label, SequenceMatch Match, bool Sensitive)>();
            foreach (var capture in captures)
            {
                output.WriteLine($"Capture: {Clean(Path.GetFileName(capture.Path))}; origin={capture.OriginUtc:O} ({capture.OriginSource})");
                output.WriteLine($"Selected connection TCP packets={capture.Packets.Count}; malformed headers in file={capture.HeaderErrors}; unsupported packets in file={capture.UnsupportedPackets}");
                var selected = ResearchAnalyzer.Select(capture, options.Selection);
                output.WriteLine($"Filtered packets={selected.Count}; ACK-only={selected.Count(p => p.Segment.IsAckOnly)}; payload-bearing={selected.Count(p => p.Segment.DeclaredPayloadLength > 0)}; truncated={selected.Count(p => p.Segment.IsTruncated)}");
                var temporal = ResearchAnalyzer.Select(capture, options.Selection with { FrameLengths = null, PayloadLength = null });
                var directions = Enum.GetValues<TrafficDirection>().Where(d => options.Selection.Direction is null || d == options.Selection.Direction).ToArray();
                var streams = options.Streams ? directions.Select(d => TcpStreamReassembler.Assemble(temporal, d)).ToArray() : [];
                // Check the full selected connection as well as overlaps: a token can straddle packets outside a display window.
                var sensitive = streams.Where(PayloadPrivacy.IsSensitive).Select(s => s.Direction).ToHashSet();
                foreach (var direction in directions)
                {
                    if (capture.Packets.Any(p => p.Direction == direction && PayloadPrivacy.IsSensitive(p.Segment.Payload))) sensitive.Add(direction);
                    if (sensitive.Contains(direction)) continue;
                    try { if (PayloadPrivacy.IsSensitive(TcpStreamReassembler.Assemble(capture.Packets, direction))) sensitive.Add(direction); }
                    catch (InvalidDataException) { sensitive.Add(direction); output.WriteLine($"Byte output suppressed for {direction}: complete connection stream could not be checked for sensitive material."); }
                }
                if (sensitive.Count > 0) output.WriteLine("Payload bytes suppressed for direction(s): " + string.Join(",", sensitive) + " (possible credential/auth material).");
                if (options.Timeline || options.Payload || options.Selection.FrameLengths is not null || options.Selection.PayloadLength is not null)
                {
                    var displayed = options.Payload && !options.Timeline ? selected.Where(p => p.Segment.DeclaredPayloadLength > 0).ToArray() : selected.ToArray();
                    foreach (var packet in displayed.Take(options.MaxPackets))
                    {
                        WritePacket(output, packet);
                        if (options.Payload && packet.Segment.DeclaredPayloadLength > 0)
                            Preview(output, packet.Segment.Payload, options.MaxPayloadBytes, sensitive.Contains(packet.Direction));
                    }
                    if (displayed.Length > options.MaxPackets) output.WriteLine($"Packet rows omitted={displayed.Length - options.MaxPackets}; increase --max-packets (max 10000).");
                }
                if (options.Sequence is not null)
                {
                    var matches = FrameSequenceSearch.Find(temporal, options.Sequence, options.SequenceWindowMs);
                    output.WriteLine($"Sequence [{string.Join(',', options.Sequence)}] matches={matches.Count}; total window <= {options.SequenceWindowMs.ToString(CultureInfo.InvariantCulture)} ms");
                    for (var i = 0; i < matches.Count; i++)
                    {
                        var match = matches[i];
                        if (i < options.MaxPackets) output.WriteLine($"  match {i + 1}: UTC={match.Packets[0].Segment.TimestampUtc:O} t={F(match.RelativeSeconds)}s {match.Direction} indices=[{string.Join(',', match.Packets.Select(p => p.Segment.PacketIndex))}] frames=[{string.Join(',', match.Packets.Select(p => p.Segment.CapturedFrameLength))}] payloads=[{string.Join(',', match.Packets.Select(p => p.Segment.DeclaredPayloadLength))}] span={(match.Packets[^1].Segment.TimestampUtc - match.Packets[0].Segment.TimestampUtc).TotalMilliseconds.ToString("F6", CultureInfo.InvariantCulture)}ms");
                        if (samples.Count >= 100_000) throw new InvalidDataException("Sequence sample limit reached (100000). Use a smaller time window.");
                        samples.Add(($"{Clean(Path.GetFileNameWithoutExtension(capture.Path))}#{i + 1}", match, sensitive.Contains(match.Direction)));
                    }
                    if (matches.Count > options.MaxPackets) output.WriteLine($"Match rows omitted={matches.Count - options.MaxPackets}; comparison still includes all matches.");
                }
                if (options.Streams) foreach (var stream in streams) WriteStream(output, stream, options, sensitive.Contains(stream.Direction));
            }
            if (options.Compare) WriteComparison(output, samples, options);
            return 0;
        }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); error.WriteLine(Usage); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
        { error.WriteLine($"Research failed: {ex.GetType().Name}. {Clean(ex.Message)}"); return 1; }
    }

    private static void WritePacket(TextWriter output, ResearchPacket packet)
    {
        var s = packet.Segment;
        output.WriteLine($"#{s.PacketIndex} UTC={s.TimestampUtc:O} t={F(packet.RelativeSeconds)}s {packet.Direction} frame={s.CapturedFrameLength} payload={s.DeclaredPayloadLength} available={s.Payload.Length} seq={s.SequenceNumber} ack={s.AcknowledgmentNumber} flags={s.Flags} {(s.IsTruncated ? "TRUNCATED" : s.IsAckOnly ? "ACK-only" : s.Payload.Length > 0 ? "payload" : "control/no-payload")}");
    }

    private static void Preview(TextWriter output, byte[] bytes, int limit, bool suppressed)
    {
        if (suppressed || PayloadPrivacy.IsSensitive(bytes)) { output.WriteLine("  [payload suppressed: possible credential/auth material]"); return; }
        var shown = bytes.AsSpan(0, Math.Min(limit, bytes.Length));
        output.WriteLine($"  hex={Convert.ToHexString(shown)} ascii={PayloadPrivacy.Ascii(shown)} shown={shown.Length}/{bytes.Length}");
    }

    private static void WriteComparison(TextWriter output, List<(string Label, SequenceMatch Match, bool Sensitive)> samples, Options options)
    {
        foreach (var direction in Enum.GetValues<TrafficDirection>())
        {
            var group = samples.Where(s => s.Match.Direction == direction).ToArray();
            if (group.Length == 0) continue;
            for (var position = 0; position < options.Sequence!.Length; position++)
            {
                output.WriteLine($"Comparison {direction}, sequence position {position + 1}, frame={options.Sequence[position]}, samples={group.Length}");
                var columns = Math.Min(options.MaxPackets, 16);
                output.WriteLine("  diff sample columns: " + string.Join(" | ", group.Take(columns).Select(s => s.Label)));
                if (group.Length > columns) output.WriteLine("  Further diff sample columns suppressed; aggregate ranges include all samples.");
                output.WriteLine("  payload lengths: " + string.Join(',', group.Take(options.MaxPackets).Select(s => s.Match.Packets[position].Segment.DeclaredPayloadLength)));
                if (group.Any(s => s.Match.Packets[position].Segment.IsTruncated)) { output.WriteLine("  Incomplete samples: byte comparison unavailable; select a window with complete samples."); continue; }
                if (group.Length < 2) { output.WriteLine("  Insufficient samples for comparison."); continue; }
                if (group.Any(s => s.Sensitive)) { output.WriteLine("  Byte comparison suppressed: possible credential/auth material."); continue; }
                var payloads = group.Select(s => s.Match.Packets[position].Segment.Payload).ToArray();
                var diff = ByteComparison.Compare(payloads);
                output.WriteLine($"  common prefix length={diff.CommonPrefixLength} hex={Convert.ToHexString(payloads[0].AsSpan(0, Math.Min(diff.CommonPrefixLength, options.MaxPayloadBytes)))}");
                output.WriteLine($"  common suffix length={diff.CommonSuffixLength} hex={Convert.ToHexString(payloads[0].AsSpan(payloads[0].Length - diff.CommonSuffixLength, Math.Min(diff.CommonSuffixLength, options.MaxPayloadBytes)))} (disjoint from prefix)");
                WriteRanges(output, "identical offsets", diff.IdenticalRanges, options.MaxPayloadBytes);
                WriteRanges(output, "varying offsets", diff.VaryingRanges, options.MaxPayloadBytes);
                var length = payloads.Max(p => p.Length);
                output.WriteLine("  offset | bytes per sample | same/different (missing byte = --)");
                for (var offset = 0; offset < Math.Min(length, options.MaxPayloadBytes); offset++)
                {
                    var same = payloads.All(p => offset < p.Length && p[offset] == payloads[0][offset]);
                    output.WriteLine($"  {offset} | {string.Join(" | ", payloads.Take(Math.Min(options.MaxPackets, 16)).Select(p => offset < p.Length ? p[offset].ToString("X2", CultureInfo.InvariantCulture) : "--"))} | {(same ? "same" : "different")}");
                }
                if (length > options.MaxPayloadBytes) output.WriteLine($"  Diff rows omitted={length - options.MaxPayloadBytes}; ranges above cover all bytes.");
            }
        }
    }

    private static void WriteStream(TextWriter output, ReassembledStream stream, Options options, bool sensitive)
    {
        output.WriteLine($"Stream {stream.Direction}: baseSeq={stream.BaseSequence} span={stream.DeclaredSpan} available={stream.Chunks.Sum(c => (long)c.Bytes.Length)} duplicateSegments={stream.DuplicateSegments} overlapBytes={stream.OverlapBytes} SYNobserved={stream.SynObserved}");
        output.WriteLine("  Offsets relative to lowest observed payload sequence; timestamps approximate original packet capture. First timestamp/index wins conflicting overlaps. No application framing inferred.");
        WriteRanges(output, "gaps", stream.Gaps, 64);
        WriteRanges(output, "conflicting overlap offsets", stream.Conflicts, 64);
        var counts = new long[256];
        foreach (var chunk in stream.Chunks) foreach (var b in chunk.Bytes) counts[b]++;
        var total = counts.Sum();
        var entropy = total == 0 ? 0 : counts.Where(c => c > 0).Sum(c => -(double)c / total * Math.Log2((double)c / total));
        var printable = total == 0 ? 0 : counts.Skip(32).Take(95).Sum() * 100.0 / total;
        output.WriteLine($"  byte entropy={entropy.ToString("F3", CultureInfo.InvariantCulture)} bits/byte; printable ASCII={printable.ToString("F2", CultureInfo.InvariantCulture)}%; descriptive statistics cannot establish encryption/compression or field meanings.");
        if (sensitive) { output.WriteLine("  Stream preview/pattern suppressed: possible credential/auth material."); return; }
        var end = options.StreamOffset + options.StreamBytes;
        var visibleChunks = stream.Chunks.Where(c => c.End > options.StreamOffset && c.Offset < end).ToArray();
        foreach (var chunk in visibleChunks.Take(options.MaxPackets))
        {
            var start = Math.Max(chunk.Offset, options.StreamOffset);
            var last = Math.Min(chunk.End, end);
            var bytes = chunk.Bytes.AsSpan((int)(start - chunk.Offset), (int)(last - start));
            output.WriteLine($"  offset={start}..{last - 1} approxUTC={chunk.TimestampUtc:O} sourcePacket=#{chunk.PacketIndex} hex={Convert.ToHexString(bytes)} ascii={PayloadPrivacy.Ascii(bytes)}");
        }
        if (visibleChunks.Length > options.MaxPackets) output.WriteLine($"  Stream chunk rows omitted={visibleChunks.Length - options.MaxPackets}; increase --max-packets.");
        if (options.StreamPattern is { } pattern)
        {
            var offsets = StreamPatternSearch.Find(stream, pattern);
            output.WriteLine($"  stream pattern {Convert.ToHexString(pattern)} count={offsets.Count}; offsets=[{string.Join(',', offsets.Take(options.MaxPackets))}] (may cross transport segment boundaries, never gaps)");
            if (offsets.Count > options.MaxPackets) output.WriteLine($"  Pattern offset rows omitted={offsets.Count - options.MaxPackets}.");
        }
    }

    private static void WriteRanges(TextWriter output, string name, IReadOnlyList<ByteRange> ranges, int limit) =>
        output.WriteLine($"  {name}: {(ranges.Count == 0 ? "none" : string.Join(',', ranges.Take(limit)))}{(ranges.Count > limit ? $" (+{ranges.Count - limit} ranges omitted)" : "")}");

    private static DateTimeOffset? MetadataStart(string path, TextWriter error)
    {
        var metadataPath = Path.ChangeExtension(path, ".json");
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var metadata = SessionMetadataStore.Deserialize(File.ReadAllText(metadataPath));
            if (metadata.StartedUtc == default) throw new InvalidDataException("Missing StartedUtc.");
            return metadata.StartedUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        { error.WriteLine($"Metadata unavailable for {Clean(Path.GetFileName(path))}; using earliest packet timestamp."); return null; }
    }
    private static string F(double value) => value.ToString("F7", CultureInfo.InvariantCulture);
    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? '_' : c).ToArray());

    private sealed class Options
    {
        public bool Compare, Timeline, Payload, Streams;
        public List<string> Paths { get; } = [];
        public IPEndPoint Local { get; set; } = null!;
        public IPEndPoint Remote { get; set; } = null!;
        public ResearchSelection Selection { get; set; } = new();
        public int[]? Sequence;
        public double SequenceWindowMs = 20;
        public int MaxPayloadBytes = 64, MaxPackets = 200, StreamBytes = 64;
        public long StreamOffset;
        public byte[]? StreamPattern;
    }
    private static Options Parse(string[] args)
    {
        var options = new Options { Compare = args.Length > 0 && args[0] == "compare" };
        var index = options.Compare ? 1 : 0;
        while (index < args.Length && !args[index].StartsWith('-')) options.Paths.Add(args[index++]);
        if (options.Paths.Count < (options.Compare ? 2 : 1) || options.Paths.Count > (options.Compare ? 16 : 1)) throw new ArgumentException("Specify one capture, or 2-16 captures for compare.");
        var seen = new HashSet<string>();
        while (index < args.Length)
        {
            var option = args[index++];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate option: {option}");
            string Value() => index < args.Length ? args[index++] : throw new ArgumentException($"Missing value for {option}.");
            int Number(int min = 0, int max = int.MaxValue) => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : throw new ArgumentException($"{option} requires an integer in {min}..{max}.");
            double Seconds() => double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : throw new ArgumentException($"{option} requires a finite nonnegative number.");
            int[] Csv() { var text = Value(); var pieces = text.Split(','); if (pieces.Length > 32 || pieces.Length == 0) throw new ArgumentException("CSV requires 1-32 integers."); return pieces.Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new ArgumentException("Invalid frame length CSV.")).ToArray(); }
            switch (option)
            {
                case "--local": options.Local = Endpoint(Value()); break;
                case "--remote": options.Remote = Endpoint(Value()); break;
                case "--timeline": options.Timeline = true; break;
                case "--payload": options.Payload = true; break;
                case "--streams": options.Streams = true; break;
                case "--from": options.Selection = options.Selection with { From = Seconds() }; break;
                case "--to": options.Selection = options.Selection with { To = Seconds() }; break;
                case "--frame-length": options.Selection = options.Selection with { FrameLengths = new HashSet<int> { Number() } }; break;
                case "--frame-lengths": options.Selection = options.Selection with { FrameLengths = Csv().ToHashSet() }; break;
                case "--payload-length": options.Selection = options.Selection with { PayloadLength = Number() }; break;
                case "--direction": options.Selection = options.Selection with { Direction = Value() switch { "out" => TrafficDirection.ClientToServer, "in" => TrafficDirection.ServerToClient, "both" => null, _ => throw new ArgumentException("Direction must be out, in or both.") } }; break;
                case "--sequence": options.Sequence = Csv(); break;
                case "--sequence-window-ms": options.SequenceWindowMs = Seconds(); break;
                case "--max-payload-bytes": options.MaxPayloadBytes = Number(1, 4096); break;
                case "--max-packets": options.MaxPackets = Number(1, 10000); break;
                case "--stream-offset": options.StreamOffset = Number(); break;
                case "--stream-bytes": options.StreamBytes = Number(1, 4096); break;
                case "--stream-pattern":
                    var hex = Value();
                    if (hex.Length is < 2 or > 128 || hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Stream pattern requires 1-64 hex bytes.");
                    options.StreamPattern = Convert.FromHexString(hex); options.Streams = true; break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }
        }
        if (options.Local is null || options.Remote is null) throw new ArgumentException("Both --local and --remote numeric IP:port endpoints are required.");
        if (seen.Contains("--frame-length") && seen.Contains("--frame-lengths")) throw new ArgumentException("Use --frame-length or --frame-lengths, not both.");
        if (options.Sequence is not null && (options.Selection.FrameLengths is not null || options.Selection.PayloadLength is not null)) throw new ArgumentException("Length filters cannot be combined with --sequence; use a separate invocation.");
        if (options.Compare && options.Sequence is null) throw new ArgumentException("compare requires --sequence.");
        if (seen.Contains("--sequence-window-ms") && options.Sequence is null) throw new ArgumentException("--sequence-window-ms requires --sequence.");
        if ((seen.Contains("--stream-offset") || seen.Contains("--stream-bytes")) && !options.Streams) throw new ArgumentException("Stream range options require --streams.");
        options.Selection.Validate();
        return options;
    }
    private static IPEndPoint Endpoint(string value) => IPEndPoint.TryParse(value, out var endpoint) && endpoint.Port > 0 ? endpoint : throw new ArgumentException("Endpoint must be a numeric IPv4:port or [IPv6]:port with port 1..65535.");
}
