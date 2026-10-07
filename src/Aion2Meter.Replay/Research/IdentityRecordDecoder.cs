using System.Buffers.Binary;
using System.Text;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

/// <summary>Original bounded replay extraction; unknown relationships stay candidates.</summary>
public static class IdentityRecordDecoder
{
    private static readonly UTF8Encoding ExactUtf8 = new(false, true);

    public static IdentityRecordResult Decode(RawProtocolRecord record)
    {
        var identities = new List<IdentityObservation>();
        var relationships = new List<RelationshipObservation>();
        var contexts = new List<TextContextObservation>();
        var issues = new List<IdentityDecodeIssue>();
        if (record.OpcodeCandidate is not ("4536" or "4136" or "338A")) return Result();
        if (record.Direction != TrafficDirection.ServerToClient || record.DecodeStatus == "Suppressed")
        {
            issues.Add(new(record, "No verified inbound identity envelope; no identity inferred.")); return Result();
        }
        try
        {
            var bytes = record.RawBytes;
            var framing = ApplicationFraming.Read(bytes);
            if (!framing.Success || framing.TotalLength != bytes.Length || framing.PrefixLength != record.PrefixLength || record.FrameLength != bytes.Length)
                throw new InvalidDataException("Identity record has inconsistent or incomplete framing.");
            var cursor = new Cursor(bytes, record.PrefixLength);
            if (Convert.ToHexString(cursor.Take(2)) != record.OpcodeCandidate)
                throw new InvalidDataException("Identity tag does not match raw bytes.");
            var entity = cursor.Number();
            switch (record.OpcodeCandidate)
            {
                case "4536":
                    var mask = cursor.U32(); var presence = cursor.Byte();
                    // Only the observed presence branch is promoted; no reference-only mask inference.
                    if (presence != 0x07)
                    {
                        identities.Add(new(record.RecordId, entity, null, IdentityEvidenceType.Unresolved4536Presence,
                            EvidenceClassification.E, ResearchConfidence.Unresolved, mask, presence, null, cursor.Rest(), record));
                        issues.Add(new(record, "Unverified 4536 presence branch; remainder retained without a name.")); break;
                    }
                    var name = cursor.Text();
                    identities.Add(new(record.RecordId, entity, name, IdentityEvidenceType.NameEnvelope4536,
                        EvidenceClassification.B, ResearchConfidence.High, mask, presence, null, cursor.Rest(), record));
                    break;
                case "338A":
                    var auxiliary = cursor.U32();
                    if (!cursor.Take(2).SequenceEqual(new byte[] { 0, 0 })) throw new InvalidDataException("Unverified 338A padding.");
                    var world = cursor.U16(); var text = cursor.Text();
                    contexts.Add(new(record.RecordId, entity, auxiliary, world, text, cursor.Rest(), record));
                    break;
                case "4136":
                    var kind = cursor.Byte();
                    if (kind is not (0x5f or 0x0d or 0x1d or 0x1f)) throw new InvalidDataException("Unverified 4136 kind/layout.");
                    identities.Add(new(record.RecordId, entity, null, IdentityEvidenceType.EntityHeader4136,
                        EvidenceClassification.A, ResearchConfidence.High, null, null, kind, cursor.Rest(), record));
                    ExtractRelationships(record, entity, kind, cursor.Position, relationships, issues);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        { issues.Add(new(record, ex.Message)); }
        return Result();

        IdentityRecordResult Result() => new(identities.ToArray(), relationships.ToArray(), contexts.ToArray(), issues.ToArray());
    }

    private static void ExtractRelationships(RawProtocolRecord record, ulong entity, byte kind, int start,
        List<RelationshipObservation> observations, List<IdentityDecodeIssue> issues)
    {
        var bytes = record.RawBytes;
        for (var offset = start; offset < bytes.Length; offset++)
        {
            if (offset <= bytes.Length - 8 && bytes.AsSpan(offset, 8).SequenceEqual(new byte[] { 255,255,255,255,255,255,255,255 }))
            {
                try
                {
                    var cursor = new Cursor(bytes, offset + 8);
                    var unknown = cursor.Take(8).ToArray(); var fieldOffset = cursor.Position;
                    var related = cursor.Number();
                    observations.Add(new($"{record.RecordId}:anchor:{offset}", entity, related,
                        RelationshipEvidenceType.FfAnchorRelatedId, fieldOffset, null, null, null,
                        unknown, cursor.Rest(), EvidenceClassification.A, ResearchConfidence.High, record));
                }
                catch (InvalidDataException ex) { issues.Add(new(record, "Incomplete FF-anchor candidate: " + ex.Message)); }
                offset += 7; // Non-overlapping FF×8 anchors, including valid self-ID observations.
                continue;
            }
            if (kind != 0x5f || offset > bytes.Length - 3 || !bytes.AsSpan(offset, 3).SequenceEqual(new byte[] { 7,2,6 })) continue;
            try
            {
                var cursor = new Cursor(bytes, offset + 3); var fieldOffset = cursor.Position;
                var related = cursor.U32(); var auxiliary = cursor.U32();
                if (!cursor.Take(2).SequenceEqual(new byte[] { 0,0 })) throw new InvalidDataException("Unverified suffix padding.");
                var world = cursor.U16(); var label = cursor.Text();
                observations.Add(new($"{record.RecordId}:suffix:{offset}", entity, related,
                    RelationshipEvidenceType.SuffixRelatedId, fieldOffset, auxiliary, world, label,
                    [], cursor.Rest(), EvidenceClassification.B, ResearchConfidence.High, record));
            }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
            { issues.Add(new(record, "Unresolved 070206 suffix candidate: " + ex.Message)); }
        }
    }

    private ref struct Cursor(ReadOnlySpan<byte> bytes, int position)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        public int Position { get; private set; } = position;
        public ReadOnlySpan<byte> Take(int count)
        {
            if (Position < 0 || count > bytes.Length - Position) throw new InvalidDataException("Truncated identity field.");
            var result = bytes.Slice(Position, count); Position += count; return result;
        }
        public byte Byte() => Take(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public ulong Number()
        {
            var decoded = UnsignedVarint.Read(bytes[Position..], requireCanonical: true);
            if (!decoded.Success) throw new InvalidDataException(decoded.Error);
            Position += decoded.BytesConsumed; return decoded.Value;
        }
        public string Text()
        {
            var length = Byte();
            if (length == 0) throw new InvalidDataException("Empty text field outside verified envelope.");
            return ExactUtf8.GetString(Take(length));
        }
        public byte[] Rest() => bytes[Position..].ToArray();
    }
}
