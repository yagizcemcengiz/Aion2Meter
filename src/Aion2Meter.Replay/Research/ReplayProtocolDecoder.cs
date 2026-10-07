using System.Buffers.Binary;
using Aion2Meter.Core;
using K4os.Compression.LZ4;

namespace Aion2Meter.Replay.Research;

/// <summary>Replay-only decoding. Unknown messages remain records; no combat totals or owners.</summary>
public sealed class ReplayProtocolDecoder
{
    private readonly ProtocolDecodeLimits limits;
    private readonly List<RawProtocolRecord> records = [];
    private readonly List<RawCombatCandidate> combat = [];
    private readonly List<ContainerAudit> containers = [];
    private readonly HashSet<TrafficDirection> sensitive = [];
    private long captureBudget;
    private bool started;

    public ReplayProtocolDecoder(ProtocolDecodeLimits? limits = null)
    {
        this.limits = limits ?? new(); this.limits.Validate();
    }

    // Single-use instances keep resource accounting local to one capture.
    public ProtocolDecodeResult Decode(string sourceCapture, IReadOnlyList<ReassembledStream> streams, DateTimeOffset origin)
    {
        if (started) throw new InvalidOperationException("Create a decoder for each capture.");
        started = true;
        foreach (var stream in streams)
        {
            if (PayloadPrivacy.IsSensitive(stream)) sensitive.Add(stream.Direction);
            var extraction = CandidateBlockExtractor.Extract(stream, origin);
            foreach (var block in extraction.Blocks)
            {
                var raw = new RawProtocolRecord(0, sourceCapture, block.Direction, block.StreamOffset, block.Index,
                    block.StreamOffset, block.TimestampUtc, block.PacketIndex, block.CompletionPacketIndex, block.CompletionUtc,
                    block.PrefixLength, block.Length, block.Bytes, block.StructuralTag, [], "Unknown", []);
                long outerBudget = 0;
                Visit(raw, ref outerBudget);
            }
            foreach (var issue in extraction.Issues)
            {
                var chunks = stream.Chunks.Where(c => c.Offset < issue.StreamOffset + issue.UnparsedBytes && c.End > issue.StreamOffset).ToArray();
                var bytes = chunks.SelectMany(c => c.Bytes.Skip((int)Math.Max(0, issue.StreamOffset - c.Offset))
                    .Take((int)(Math.Min(c.End, issue.StreamOffset + issue.UnparsedBytes) - Math.Max(c.Offset, issue.StreamOffset)))).ToArray();
                var first = chunks.FirstOrDefault();
                var latest = chunks.OrderBy(c => c.TimestampUtc).ThenBy(c => c.PacketIndex).LastOrDefault();
                Add(new(0, sourceCapture, stream.Direction, issue.StreamOffset, 0, issue.StreamOffset,
                    first?.TimestampUtc ?? origin, first?.PacketIndex ?? 0, latest?.PacketIndex ?? 0, latest?.TimestampUtc ?? origin,
                    0, bytes.Length, bytes, "", [], "MalformedFraming", [issue.Reason]));
            }
        }
        // A sensitive decompressed payload suppresses the whole direction, including earlier records.
        var safeRecords = records.Select(r => sensitive.Contains(r.Direction)
            ? r with { RawBytes = [], DecodeStatus = "Suppressed", DecodeWarnings = [.. r.DecodeWarnings, "Possible credential/auth material; direction bytes and candidates suppressed."] } : r).ToArray();
        return new(safeRecords, combat.Where(c => !sensitive.Contains(c.RawRecord.Direction)).ToArray(), containers.ToArray());
    }

    private int Add(RawProtocolRecord record)
    {
        if (records.Count >= limits.MaximumRecordsPerCapture)
            throw new InvalidDataException("Capture record bound reached; no complete result available.");
        var id = records.Count + 1; records.Add(record with { RecordId = id }); return id - 1;
    }

    private void Visit(RawProtocolRecord raw, ref long outerBudget)
    {
        var index = Add(raw);
        raw = records[index];
        var body = raw.RawBytes.AsSpan(raw.PrefixLength);
        if (body.Length >= 2 && body[0] == 0xff && body[1] == 0xff)
        {
            DecodeContainer(raw, index, ref outerBudget); return;
        }
        if (raw.OpcodeCandidate == "0438")
        {
            var candidate = CombatCandidateDecoder.Decode(raw);
            records[index] = raw with { DecodeStatus = candidate.Status == "Supported" ? "CombatCandidate" : "UnresolvedCombatCandidate", DecodeWarnings = candidate.Warnings };
            combat.Add(candidate with { RawRecord = records[index] });
        }
    }

    private void DecodeContainer(RawProtocolRecord raw, int index, ref long outerBudget)
    {
        var body = raw.RawBytes.AsSpan(raw.PrefixLength);
        uint? declared = body.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(body[2..6]) : null;
        string? failure = null;
        if (declared is null) failure = "Truncated LZ4 size header.";
        else if (declared == 0 || declared > limits.MaximumDecompressedSize) failure = "Declared LZ4 output size outside bound.";
        else if (raw.ContainerDepth >= limits.MaximumContainerDepth) failure = "Nested container depth bound reached.";
        else if (declared > limits.MaximumDecompressedBytesPerOuter - outerBudget || declared > limits.MaximumDecompressedBytesPerCapture - captureBudget)
            failure = "Total decompression byte budget exceeded.";
        if (failure is not null) { Failed(failure); return; }
        // Charge attempts, including invalid compressed data, before allocation.
        outerBudget += declared!.Value; captureBudget += declared.Value;
        var output = new byte[(int)declared.Value];
        var decoded = LZ4Codec.Decode(raw.RawBytes, raw.PrefixLength + 6, body.Length - 6, output, 0, output.Length);
        if (decoded < 0) { Failed("Invalid raw LZ4 block."); return; }
        if (decoded != output.Length) { Failed("LZ4 declared/output size mismatch."); return; }
        if (PayloadPrivacy.IsSensitive(output)) sensitive.Add(raw.Direction);
        var offset = 0;
        var frameCount = 0;
        string? innerError = null;
        while (offset < output.Length)
        {
            if (frameCount >= limits.MaximumFramesPerContainer) { innerError = "Frames-per-container bound reached."; break; }
            var frame = ApplicationFraming.Read(output.AsSpan(offset));
            if (!frame.Success) { innerError = frame.Error; break; }
            var bytes = output.AsSpan(offset, frame.TotalLength).ToArray();
            var tag = Convert.ToHexString(bytes.AsSpan(frame.PrefixLength, Math.Min(2, bytes.Length - frame.PrefixLength)));
            Visit(raw with { RecordId = 0, PrefixLength = frame.PrefixLength, FrameLength = frame.TotalLength,
                RawBytes = bytes, OpcodeCandidate = tag, ContainerPath = [.. raw.ContainerPath, new(raw.RecordId, offset)],
                DecodeStatus = "Unknown", DecodeWarnings = [] }, ref outerBudget);
            offset += frame.TotalLength;
            frameCount++;
        }
        if (innerError is not null)
            Add(raw with { RecordId = 0, PrefixLength = 0, FrameLength = output.Length - offset, RawBytes = output[offset..],
                OpcodeCandidate = "", ContainerPath = [.. raw.ContainerPath, new(raw.RecordId, offset)], DecodeStatus = "MalformedInnerFraming", DecodeWarnings = [innerError] });
        records[index] = raw with { DecodeStatus = innerError is null ? "DecodedContainer" : "ContainerWithUnparsedBytes", DecodeWarnings = innerError is null ? [] : [innerError] };
        containers.Add(new(raw.RecordId, raw.OuterFrameId, raw.ContainerDepth, declared, true, output.Length,
            frameCount, offset, output.Length - offset, innerError is null ? 0 : 1, innerError));
        return;

        void Failed(string reason)
        {
            records[index] = raw with { DecodeStatus = "FailedContainer", DecodeWarnings = [reason] };
            containers.Add(new(raw.RecordId, raw.OuterFrameId, raw.ContainerDepth, declared, false, 0, 0, 0, 0, 0, reason));
        }
    }
}
