using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

/// <summary>Pure projection of accepted records; no file operations or correlation/identity lookups.</summary>
public sealed class DamageEventProjector
{
    public DamageEvent Project(SupportedCombatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Enforce the typed contract without adding new grammar or correlation acceptance rules.
        if (record.CategoryOrSwitch is not (0x06 or 0x26) ||
            record.RawRecord.Direction != TrafficDirection.ServerToClient || record.RawRecord.OpcodeCandidate != "0438")
            throw new ArgumentException("Expected an accepted supported inbound category-6 record.", nameof(record));
        var raw = record.RawRecord;
        var provenance = new DamageEventProvenance(raw.SourceCapture, raw.RecordId, raw.Direction,
            raw.StreamOffset, raw.OuterFrameId, raw.OuterFrameOffset, raw.TimestampUtc, raw.PacketIndex,
            raw.CompletionPacketIndex, raw.CompletionUtc, raw.PrefixLength, raw.FrameLength, raw.OpcodeCandidate,
            raw.ContainerPath.Select(p => new DamageContainerLocation(p.ContainerRecordId, p.InnerOffset)));
        return new(record.SourceEntityId, record.TargetEntityId, record.RawSkillCode, record.AggregateAmount,
            record.DerivedBaseAmount, record.OptionalComponents, record.TypeCandidate,
            record.ModifierCandidate, record.DirectionCandidate, provenance);
    }

    // Preserve each input occurrence and its order. Duplicate policy belongs to the explicit audit.
    public IReadOnlyList<DamageEvent> ProjectMany(IEnumerable<SupportedCombatRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Array.AsReadOnly(records.Select(Project).ToArray());
    }
}
