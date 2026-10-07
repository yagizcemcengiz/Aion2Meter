using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

/// <summary>Bounded text/numeric hypotheses, not a parser for every initialization field.</summary>
public static class LocalInitializationExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<CurrentPlayerBindingEvidence> Extract(RawProtocolRecord record)
    {
        if (record.Direction != TrafficDirection.ServerToClient || record.OpcodeCandidate is not ("1536" or "3336") ||
            record.DecodeStatus != "Unknown" || record.DecodeWarnings.Count != 0) return Array.Empty<CurrentPlayerBindingEvidence>();
        var bytes = record.RawBytes;
        var framing = ApplicationFraming.Read(bytes);
        if (!framing.Success || framing.TotalLength != bytes.Length || framing.TotalLength != record.FrameLength ||
            framing.PrefixLength != record.PrefixLength || bytes.Length < record.PrefixLength + 2 ||
            Convert.ToHexString(bytes.AsSpan(record.PrefixLength, 2)) != record.OpcodeCandidate)
            return Array.Empty<CurrentPlayerBindingEvidence>();
        var start = record.PrefixLength + 2;
        VarintResult? leading = null;
        if (record.OpcodeCandidate == "3336")
        {
            leading = UnsignedVarint.Read(bytes.AsSpan(start), requireCanonical: true);
            if (!leading.Success) return Array.Empty<CurrentPlayerBindingEvidence>();
        }
        var candidates = new List<CurrentPlayerBindingEvidence>();
        var provenance = ProvenanceIdentity(record);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var rawSnapshot = Convert.ToBase64String(bytes);
        var firstLength = start + (leading?.BytesConsumed ?? 4);
        for (var offset = firstLength; offset < bytes.Length; offset++)
        {
            var length = bytes[offset];
            if (length == 0 || length > bytes.Length - offset - 1) continue;
            string name;
            try { name = StrictUtf8.GetString(bytes, offset + 1, length); }
            catch (DecoderFallbackException) { continue; }
            // This is a conservative text profile, not a universal character-name grammar.
            // Keep punctuation, spaces and multibyte text; reject binary/control/replacement material.
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsControl(c) || c == '\uFFFD')) continue;
            var numberOffset = leading is null ? offset - 4 : start;
            var numberLength = leading?.BytesConsumed ?? 4;
            var value = leading?.Value ?? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(numberOffset, 4));
            // Zero is deliberately retained. It is not a validated sentinel.
            candidates.Add(new(record.SourceCapture, record.RecordId, record.OpcodeCandidate, record.Direction,
                record.StreamOffset, record.OuterFrameId, record.OuterFrameOffset, record.TimestampUtc,
                record.CompletionUtc, record.PacketIndex, record.CompletionPacketIndex, record.FrameLength,
                record.ContainerPath.Select(p => new BindingContainerLocation(p.ContainerRecordId, p.InnerOffset)),
                provenance, digest, rawSnapshot, value, name,
                leading is null ? "u32LE" : "canonical unsigned base128 varint",
                new(numberOffset, numberLength), new(offset + 1, length), offset,
                bytes.AsSpan(numberOffset, numberLength).ToArray(), bytes.AsSpan(offset + 1, length).ToArray()));
            if (candidates.Count > 4096) return Array.Empty<CurrentPlayerBindingEvidence>(); // Resource bound; never a partial candidate selection.
        }
        return Array.AsReadOnly(candidates.ToArray());
    }

    internal static string ProvenanceIdentity(RawProtocolRecord r)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, StrictUtf8, leaveOpen: true))
        {
            writer.Write(r.SourceCapture); writer.Write(r.RecordId); writer.Write((int)r.Direction);
            writer.Write(r.StreamOffset); writer.Write(r.OuterFrameId); writer.Write(r.OuterFrameOffset);
            writer.Write(r.TimestampUtc.UtcTicks); writer.Write(r.CompletionUtc.UtcTicks);
            writer.Write(r.PacketIndex); writer.Write(r.CompletionPacketIndex);
            writer.Write(r.PrefixLength); writer.Write(r.FrameLength); writer.Write(r.OpcodeCandidate);
            writer.Write(r.ContainerPath.Count);
            foreach (var p in r.ContainerPath) { writer.Write(p.ContainerRecordId); writer.Write(p.InnerOffset); }
        }
        return "self-binding-record-v1:" + Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
