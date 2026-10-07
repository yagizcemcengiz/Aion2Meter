using System.Globalization;
using System.Net;
using System.Text.Json;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public static class BlockResearchCli
{
    private const string Usage = "research blocks <1-16 captures.pcap> --local IP:port --remote IP:port [options]\n" +
        "  --from seconds --to seconds --direction in|out|both (default in)\n" +
        "  --block-length N --body-prefix HEX --hypothesis-value uint32-decimal (repeatable)\n" +
        "  --sequence CSV --sequence-window-ms 20 --action-window-seconds 3\n" +
        "  --max-blocks 200 (max 10000) --byte-offsets --summary --json\n" +
        "Time/length/body filters apply AFTER full-stream extraction. Family counts and comparisons include all selected blocks. Output is console-only.";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (args.Length == 1 && args[0] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        try
        {
            var options = Parse(args);
            var connection = new TcpConnectionSelection(options.Local.Address, (ushort)options.Local.Port, options.Remote.Address, (ushort)options.Remote.Port);
            var reports = new List<object>();
            var samples = new List<BlockSample>();
            long totalBytes = 0, totalPackets = 0, totalBlocks = 0;
            foreach (var path in options.Paths)
            {
                var capture = new ResearchAnalyzer().Read(path, connection, MetadataStart(path, error));
                totalPackets += capture.Packets.Count; totalBytes += capture.Packets.Sum(p => (long)p.Segment.Payload.Length);
                if (totalPackets > 500_000 || totalBytes > 128 * 1024 * 1024) throw new InvalidDataException("Combined research limit: 500000 packets / 128 MiB.");
                var label = Clean(Path.GetFileNameWithoutExtension(path));
                var extracted = new List<CandidateBlock>();
                var streamReports = new List<object>();
                var incompleteEvidence = false;
                foreach (var direction in Enum.GetValues<TrafficDirection>().Where(d => options.Direction is null || d == options.Direction))
                {
                    // Privacy checks cover the complete connection, before any display or block filters.
                    var stream = TcpStreamReassembler.Assemble(capture.Packets, direction);
                    if (capture.Packets.Any(p => p.Direction == direction && PayloadPrivacy.IsSensitive(p.Segment.Payload)) || PayloadPrivacy.IsSensitive(stream))
                    { incompleteEvidence = true; streamReports.Add(new { Direction = direction.ToString(), Suppressed = true, Reason = "Possible credential/auth material; block analysis suppressed." }); continue; }
                    var result = CandidateBlockExtractor.Extract(stream, capture.OriginUtc);
                    incompleteEvidence |= result.Issues.Count > 0 || stream.Gaps.Count > 0 || stream.Conflicts.Count > 0;
                    totalBlocks += result.Blocks.Count;
                    if (totalBlocks > CandidateBlockExtractor.MaximumBlocks) throw new InvalidDataException("Combined candidate block limit: 250000.");
                    streamReports.Add(new { Direction = direction.ToString(), Suppressed = false, stream.BaseSequence, stream.DeclaredSpan,
                        stream.DuplicateSegments, stream.OverlapBytes, Gaps = stream.Gaps, Conflicts = stream.Conflicts,
                        result.AvailableBytes, result.CoveredBytes, result.ContiguousRuns, ExtractedBlocks = result.Blocks.Count, result.Issues });
                    extracted.AddRange(result.Blocks.Where(b => (options.From is null || b.RelativeSeconds >= options.From) &&
                        (options.To is null || b.RelativeSeconds <= options.To) && (options.Length is null || b.Length == options.Length) &&
                        (options.BodyPrefix is null || b.Bytes.AsSpan(b.PrefixLength).StartsWith(options.BodyPrefix))));
                }
                extracted = extracted.OrderBy(b => b.TimestampUtc).ThenBy(b => b.PacketIndex).ThenBy(b => b.StreamOffset).ToList();
                samples.AddRange(extracted.Select(b => new BlockSample(label, b)));
                var actions = options.Sequence is null ? [] : FrameSequenceSearch.Find(capture.Packets, options.Sequence, options.SequenceWindowMs)
                    .Where(m => m.Direction == TrafficDirection.ClientToServer && (options.From is null || m.RelativeSeconds >= options.From) && (options.To is null || m.RelativeSeconds <= options.To)).ToArray();
                var associations = actions.Select((a, i) =>
                {
                    var related = extracted.Where(b => b.Direction == TrafficDirection.ServerToClient && b.RelativeSeconds >= a.RelativeSeconds && b.RelativeSeconds - a.RelativeSeconds <= options.ActionWindow).ToArray();
                    return new { Index = i + 1, TimestampUtc = a.Packets[0].Segment.TimestampUtc, a.RelativeSeconds,
                        PacketIndices = a.Packets.Select(p => p.Segment.PacketIndex).ToArray(), RelatedBlockCount = related.Length,
                        Repetitions = related.GroupBy(b => b.FamilyKey).Select(g => new { Family = g.Key, Count = g.Count() }).ToArray(),
                        Blocks = related.Take(options.MaxBlocks).Select(b => new { b.Index, b.StreamOffset, b.FamilyKey,
                            DeltaMilliseconds = (b.RelativeSeconds - a.RelativeSeconds) * 1000 }).ToArray(),
                        OmittedBlocks = Math.Max(0, related.Length - options.MaxBlocks) };
                }).ToArray();
                var numeric = options.Values.Select(v =>
                {
                    var count = extracted.Sum(b => (long)NumericHypothesisSearch.Find(b.Bytes, v).Count);
                    return new { Value = v, MatchCount = count, Status = count > 0 ? "candidate numeric match" : incompleteEvidence ? "no match in extracted blocks; incomplete/suppressed evidence" : "not directly represented in selected candidate blocks" };
                }).ToArray();
                reports.Add(new { Capture = label, capture.OriginUtc, capture.OriginSource, capture.HeaderErrors, capture.UnsupportedPackets,
                    Streams = streamReports, SelectedBlockCount = extracted.Count,
                    Blocks = options.Summary ? [] : extracted.Take(options.MaxBlocks).Select(b => new { b.Index, Direction = b.Direction.ToString(), b.TimestampUtc,
                        b.RelativeSeconds, b.StreamOffset, b.Length, b.PacketIndex, b.CompletionPacketIndex, b.CompletionUtc,
                        b.PrefixValue, b.PrefixLength, b.FamilyKey, Hex = Convert.ToHexString(b.Bytes),
                        ByteOffsets = options.ByteOffsets ? b.Bytes.Take(4096).Select((value, offset) => new { Offset = offset, Hex = value.ToString("X2", CultureInfo.InvariantCulture) }).ToArray() : [],
                        OmittedByteOffsets = options.ByteOffsets ? Math.Max(0, b.Length - 4096) : 0,
                        NumericMatches = options.Values.SelectMany(v => NumericHypothesisSearch.Find(b.Bytes, v)).ToArray() }).ToArray(),
                    OmittedBlocks = options.Summary ? extracted.Count : Math.Max(0, extracted.Count - options.MaxBlocks), NumericHypotheses = numeric, ActionGroups = associations });
            }
            var families = CandidateBlockFamilies.Group(samples);
            if (families.Count > 4096 || families.Sum(f => (long)Math.Min(f.Samples[0].Block.Length, 4096)) > 100_000)
                throw new InvalidDataException("Comparison output limit: 4096 families / 100000 rows. Narrow the block or time filters.");
            var familyReports = families.Select(f => new { f.Key, Count = f.Samples.Count, f.Signature,
                Singleton = f.Samples.Count == 1, f.Comparison.CommonPrefixLength, f.Comparison.CommonSuffixLength,
                CommonPrefixHex = Convert.ToHexString(f.Samples[0].Block.Bytes.AsSpan(0, f.Comparison.CommonPrefixLength)),
                CommonSuffixHex = Convert.ToHexString(f.Samples[0].Block.Bytes.AsSpan(f.Samples[0].Block.Length - f.Comparison.CommonSuffixLength)),
                FixedOffsets = f.Comparison.IdenticalRanges, VariableOffsets = f.Comparison.VaryingRanges,
                Columns = f.Samples.Take(16).Select(s => $"{s.Label}#{s.Block.Index}@{s.Block.StreamOffset}").ToArray(), OmittedColumns = Math.Max(0, f.Samples.Count - 16),
                Offset32 = f.Samples.Take(options.MaxBlocks).Select(s => new { s.Label, s.Block.Index, s.Block.StreamOffset, Hex = s.Block.Length > 32 ? s.Block.Bytes[32].ToString("X2", CultureInfo.InvariantCulture) : "--" }).ToArray(),
                OmittedOffset32Rows = Math.Max(0, f.Samples.Count - options.MaxBlocks),
                ComparisonRows = Enumerable.Range(0, Math.Min(f.Samples[0].Block.Length, 4096)).Select(offset => new { Offset = offset,
                    Bytes = f.Samples.Take(16).Select(s => s.Block.Bytes[offset].ToString("X2", CultureInfo.InvariantCulture)).ToArray(),
                    Status = f.Samples.All(s => s.Block.Bytes[offset] == f.Samples[0].Block.Bytes[offset]) ? "same" : "different" }).ToArray(),
                OmittedRows = Math.Max(0, f.Samples[0].Block.Length - 4096) }).ToArray();
            var report = new { Framing = CandidateBlockExtractor.Hypothesis,
                Grouping = "Heuristic key: direction + total length + first two bytes after length prefix. Signature masks differing offsets across observed members. Similarity is not validated event identity; singleton constants are unvalidated.",
                Timing = "Start-byte capture timestamp, not game input time. CompletionUtc is latest contributing chunk time. Action groups use caller-supplied outbound frame sequence; one block is not one gameplay event.",
                Numeric = "Direct unsigned integer representations only. Numeric coincidences do not establish field meaning. No transformations, gameplay labels or visual inference.",
                Captures = reports, SelectedBlockCount = samples.Count, FamilyCount = families.Count, Families = familyReports };
            if (options.Json) output.WriteLine(JsonSerializer.Serialize(report));
            else WriteText(output, JsonSerializer.SerializeToElement(report));
            return 0;
        }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); error.WriteLine(Usage); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
        { error.WriteLine($"Block research failed: {ex.GetType().Name}. {Clean(ex.Message)}"); return 1; }
    }

    private static void WriteText(TextWriter output, JsonElement report)
    {
        foreach (var name in new[] { "Framing", "Grouping", "Timing", "Numeric" }) output.WriteLine(report.GetProperty(name).GetString());
        foreach (var capture in report.GetProperty("Captures").EnumerateArray())
        {
            output.WriteLine($"Capture: {capture.GetProperty("Capture")}; origin={capture.GetProperty("OriginUtc")}; selected blocks={capture.GetProperty("SelectedBlockCount")}");
            foreach (var stream in capture.GetProperty("Streams").EnumerateArray()) output.WriteLine("  Stream/framing: " + stream.GetRawText());
            foreach (var block in capture.GetProperty("Blocks").EnumerateArray())
            {
                output.WriteLine("  Candidate block: " + block.GetRawText());
                if (block.GetProperty("ByteOffsets").GetArrayLength() > 0)
                {
                    output.WriteLine("    offset | hex");
                    foreach (var row in block.GetProperty("ByteOffsets").EnumerateArray()) output.WriteLine($"    {row.GetProperty("Offset")} | {row.GetProperty("Hex")}");
                }
            }
            output.WriteLine("  Block rows omitted=" + capture.GetProperty("OmittedBlocks"));
            foreach (var numeric in capture.GetProperty("NumericHypotheses").EnumerateArray()) output.WriteLine("  Numeric hypothesis: " + numeric.GetRawText());
            foreach (var action in capture.GetProperty("ActionGroups").EnumerateArray()) output.WriteLine("  Action grouping candidate: " + action.GetRawText());
        }
        output.WriteLine($"Families={report.GetProperty("FamilyCount")}; selected blocks={report.GetProperty("SelectedBlockCount")}");
        foreach (var family in report.GetProperty("Families").EnumerateArray())
        {
            output.WriteLine($"Family {family.GetProperty("Key")}; count={family.GetProperty("Count")}; singleton={family.GetProperty("Singleton")}");
            foreach (var name in new[] { "Signature", "CommonPrefixLength", "CommonPrefixHex", "CommonSuffixLength", "CommonSuffixHex", "FixedOffsets", "VariableOffsets", "Offset32", "OmittedOffset32Rows" }) output.WriteLine($"  {name}: {family.GetProperty(name)}");
            output.WriteLine("  offset | " + string.Join(" | ", family.GetProperty("Columns").EnumerateArray().Select(e => e.GetString())) + " | same/different");
            foreach (var row in family.GetProperty("ComparisonRows").EnumerateArray()) output.WriteLine($"  {row.GetProperty("Offset")} | {string.Join(" | ", row.GetProperty("Bytes").EnumerateArray().Select(e => e.GetString()))} | {row.GetProperty("Status")}");
            output.WriteLine($"  Omitted columns={family.GetProperty("OmittedColumns")}; omitted rows={family.GetProperty("OmittedRows")}; aggregate signature/ranges include all bytes/samples.");
        }
    }

    private sealed class Options
    {
        public List<string> Paths { get; } = [];
        public IPEndPoint Local = null!, Remote = null!;
        public TrafficDirection? Direction = TrafficDirection.ServerToClient;
        public double? From, To;
        public int? Length;
        public int MaxBlocks = 200;
        public byte[]? BodyPrefix;
        public List<uint> Values { get; } = [];
        public int[]? Sequence;
        public double SequenceWindowMs = 20, ActionWindow = 3;
        public bool Json, Summary, ByteOffsets;
    }

    private static Options Parse(string[] args)
    {
        var o = new Options(); var i = 0;
        while (i < args.Length && !args[i].StartsWith('-')) o.Paths.Add(args[i++]);
        if (o.Paths.Count is < 1 or > 16) throw new ArgumentException("blocks requires 1-16 capture paths.");
        var seen = new HashSet<string>();
        while (i < args.Length)
        {
            var option = args[i++];
            if (!seen.Add(option) && option != "--hypothesis-value") throw new ArgumentException($"Duplicate option: {option}");
            string Value() => i < args.Length ? args[i++] : throw new ArgumentException($"Missing value for {option}.");
            double Seconds() => double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : throw new ArgumentException($"Invalid nonnegative seconds: {option}.");
            int Number(int max) => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= max ? n : throw new ArgumentException($"{option} requires 1..{max}.");
            switch (option)
            {
                case "--local": o.Local = Endpoint(Value()); break;
                case "--remote": o.Remote = Endpoint(Value()); break;
                case "--from": o.From = Seconds(); break;
                case "--to": o.To = Seconds(); break;
                case "--direction": o.Direction = Value() switch { "in" => TrafficDirection.ServerToClient, "out" => TrafficDirection.ClientToServer, "both" => null, _ => throw new ArgumentException("Direction must be in, out or both.") }; break;
                case "--block-length": o.Length = Number(CandidateBlockExtractor.MaximumBlockLength); break;
                case "--max-blocks": o.MaxBlocks = Number(10000); break;
                case "--body-prefix":
                    var hex = Value();
                    if (hex.Length is < 2 or > 128 || hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Body prefix requires 1-64 hex bytes.");
                    o.BodyPrefix = Convert.FromHexString(hex); break;
                case "--hypothesis-value":
                    if (!uint.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)) throw new ArgumentException("Hypothesis value requires decimal uint32.");
                    if (o.Values.Count >= 32) throw new ArgumentException("At most 32 numeric hypotheses.");
                    o.Values.Add(v); break;
                case "--sequence":
                    var csv = Value().Split(',');
                    if (csv.Length is < 1 or > 32) throw new ArgumentException("Sequence requires 1-32 frame lengths.");
                    o.Sequence = csv.Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new ArgumentException("Invalid frame length sequence.")).ToArray(); break;
                case "--sequence-window-ms": o.SequenceWindowMs = Seconds(); break;
                case "--action-window-seconds": o.ActionWindow = Seconds(); break;
                case "--json": o.Json = true; break;
                case "--summary": o.Summary = true; break;
                case "--byte-offsets": o.ByteOffsets = true; break;
                default: throw new ArgumentException($"Unknown block option: {option}");
            }
        }
        if (o.Local is null || o.Remote is null) throw new ArgumentException("Both --local and --remote numeric endpoints are required.");
        new ResearchSelection(o.From, o.To).Validate();
        if (o.Sequence is null && (seen.Contains("--sequence-window-ms") || seen.Contains("--action-window-seconds"))) throw new ArgumentException("Action options require --sequence.");
        return o;
    }

    private static IPEndPoint Endpoint(string value) => IPEndPoint.TryParse(value, out var p) && p.Port > 0 ? p : throw new ArgumentException("Numeric IP:port endpoint required.");
    private static DateTimeOffset? MetadataStart(string path, TextWriter error)
    {
        var metadata = Path.ChangeExtension(path, ".json");
        if (!File.Exists(metadata)) return null;
        try
        {
            var start = SessionMetadataStore.Deserialize(File.ReadAllText(metadata)).StartedUtc;
            if (start == default) throw new InvalidDataException("Missing StartedUtc.");
            return start;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        { error.WriteLine($"Metadata unavailable for {Clean(Path.GetFileName(path))}; using earliest packet timestamp."); return null; }
    }
    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? '_' : c).ToArray());
}
