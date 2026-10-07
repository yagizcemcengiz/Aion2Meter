using System.Buffers.Binary;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public static class AuxiliaryRecordDecoder
{
    public static AuxiliaryDecodeResult Decode(IReadOnlyList<RawProtocolRecord> records)
    {
        var observations = new List<AuxiliaryRecordObservation>();
        var issues = new List<AuxiliaryDecodeIssue>();
        foreach (var r in records)
        {
            if (r.Direction != TrafficDirection.ServerToClient || r.OpcodeCandidate is not ("0238" or "0338" or "0638")) continue;
            var p = 0;
            try
            {
                var frame = ApplicationFraming.Read(r.RawBytes);
                if (!frame.Success || frame.TotalLength != r.RawBytes.Length || frame.PrefixLength != r.PrefixLength)
                    throw new InvalidDataException("Unverified auxiliary frame boundary.");
                p = frame.PrefixLength;
                if (Convert.ToHexString(Take(2)) != r.OpcodeCandidate) throw new InvalidDataException("Auxiliary tag/bytes disagree.");
                var source = Number();
                if (r.OpcodeCandidate != "0638" && Number() != 0) throw new InvalidDataException("Unresolved nonzero auxiliary prefix variant.");
                ulong? target = r.OpcodeCandidate == "0338" ? Number() : null;
                var code = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
                var token = Take(1)[0];
                ulong? mode = null;
                if (r.OpcodeCandidate == "0238") { mode = Number(); target = Number(); }
                observations.Add(new(source, target, code, token, mode, r.RawBytes[p..], r));
            }
            catch (InvalidDataException ex) { issues.Add(new(r, ex.Message)); }

            ReadOnlySpan<byte> Take(int n)
            {
                if (n > r.RawBytes.Length - p) throw new InvalidDataException("Truncated auxiliary field.");
                var value = r.RawBytes.AsSpan(p, n); p += n; return value;
            }
            ulong Number()
            {
                var value = UnsignedVarint.Read(r.RawBytes.AsSpan(p), requireCanonical: true);
                if (!value.Success) throw new InvalidDataException(value.Error);
                p += value.BytesConsumed; return value.Value;
            }
        }
        return new(observations, issues);
    }
}

public static class AuxiliaryRecordCorrelation
{
    public static IReadOnlyList<AuxiliaryRecordEdge> Match(IReadOnlyList<SupportedCombatRecord> combat,
        IReadOnlyList<AuxiliaryRecordObservation> observations, double proximityMilliseconds)
    {
        if (!double.IsFinite(proximityMilliseconds) || proximityMilliseconds < 0)
            throw new ArgumentException("Auxiliary proximity must be finite and nonnegative.");
        var lookup = observations.ToLookup(o => (o.CaptureId, o.SourceEntityId, o.RawSkillCode, o.CorrelationTokenCandidate));
        var edges = new List<AuxiliaryRecordEdge>();
        foreach (var c in combat)
        foreach (var o in lookup[(c.RawRecord.SourceCapture, c.SourceEntityId, c.RawSkillCode, c.UnknownAfterSkill)])
        {
            if (o.TargetEntityId is { } target && target != c.TargetEntityId) continue;
            var delta = (o.RawRecord.TimestampUtc - c.RawRecord.TimestampUtc).TotalMilliseconds;
            if (Math.Abs(delta) > proximityMilliseconds) continue;
            if (edges.Count >= 1_000_000) throw new InvalidDataException("Auxiliary edge limit reached (1000000); narrow the proximity policy.");
            edges.Add(new(c.RawRecord.SourceCapture, c.RawRecord.RecordId, o.RawRecord.RecordId, o.Tag,
                c.SourceEntityId, o.TargetEntityId, c.RawSkillCode, c.UnknownAfterSkill, delta, ResearchConfidence.High));
        }
        return edges;
    }
}
