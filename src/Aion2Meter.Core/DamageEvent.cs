using System.Security.Cryptography;
using System.Text;

namespace Aion2Meter.Core;

public sealed record DamageContainerLocation(int ContainerRecordId, int InnerOffset);

/// <summary>Immutable snapshot of an accepted record's capture and source location.</summary>
public sealed class DamageEventProvenance
{
    public string CaptureScope { get; }
    public int RecordId { get; }
    public TrafficDirection Direction { get; }
    public long StreamOffset { get; }
    public int OuterFrameId { get; }
    public long OuterFrameOffset { get; }
    public DateTimeOffset Timestamp { get; }
    public long PacketIndex { get; }
    public long CompletionPacketIndex { get; }
    public DateTimeOffset CompletionTimestamp { get; }
    public int PrefixLength { get; }
    public int FrameLength { get; }
    public string RecordTag { get; }
    public IReadOnlyList<DamageContainerLocation> ContainerPath { get; }
    public string Identity { get; }

    public DamageEventProvenance(string captureScope, int recordId, TrafficDirection direction,
        long streamOffset, int outerFrameId, long outerFrameOffset, DateTimeOffset timestamp,
        long packetIndex, long completionPacketIndex, DateTimeOffset completionTimestamp,
        int prefixLength, int frameLength, string recordTag, IEnumerable<DamageContainerLocation> containerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureScope);
        ArgumentNullException.ThrowIfNull(recordTag);
        ArgumentNullException.ThrowIfNull(containerPath);
        CaptureScope = captureScope; RecordId = recordId; Direction = direction;
        StreamOffset = streamOffset; OuterFrameId = outerFrameId; OuterFrameOffset = outerFrameOffset;
        Timestamp = timestamp; PacketIndex = packetIndex; CompletionPacketIndex = completionPacketIndex;
        CompletionTimestamp = completionTimestamp; PrefixLength = prefixLength; FrameLength = frameLength;
        RecordTag = recordTag;
        ContainerPath = Array.AsReadOnly(containerPath.Select(p => p is null
            ? throw new ArgumentException("Container locations cannot be null.", nameof(containerPath))
            : new DamageContainerLocation(p.ContainerRecordId, p.InnerOffset)).ToArray());
        Identity = CreateIdentity();
    }

    // Versioned, length-prefixed binary encoding; no delimiter ambiguity or value/token heuristics.
    private string CreateIdentity()
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(CaptureScope); writer.Write(RecordId); writer.Write((int)Direction);
            writer.Write(StreamOffset); writer.Write(OuterFrameId); writer.Write(OuterFrameOffset);
            writer.Write(Timestamp.UtcTicks); writer.Write(PacketIndex); writer.Write(CompletionPacketIndex);
            writer.Write(CompletionTimestamp.UtcTicks); writer.Write(PrefixLength); writer.Write(FrameLength);
            writer.Write(RecordTag); writer.Write(ContainerPath.Count);
            foreach (var p in ContainerPath) { writer.Write(p.ContainerRecordId); writer.Write(p.InnerOffset); }
        }
        return "damage-record-v1:" + Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}

/// <summary>
/// One accepted supported record aggregate. Timestamp is capture arrival, not execution/cast time.
/// Amount already includes OptionalComponents. IDs and opaque codes carry no ownership/skill semantics.
/// </summary>
public sealed class DamageEvent
{
    public DateTimeOffset Timestamp => Provenance.Timestamp;
    public ulong SourceEntityId { get; }
    public ulong TargetEntityId { get; }
    public uint RawSkillCode { get; }
    public ulong Amount { get; }
    public ulong DerivedBaseAmount { get; }
    public IReadOnlyList<ulong> OptionalComponents { get; }
    public ulong TypeRaw { get; }
    public byte ModifierRaw { get; }
    public byte DirectionRaw { get; }
    public DamageEventProvenance Provenance { get; }
    public string Identity => Provenance.Identity;

    public DamageEvent(ulong sourceEntityId, ulong targetEntityId, uint rawSkillCode, ulong amount,
        ulong derivedBaseAmount, IEnumerable<ulong> optionalComponents, ulong typeRaw,
        byte modifierRaw, byte directionRaw, DamageEventProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(optionalComponents);
        ArgumentNullException.ThrowIfNull(provenance);
        SourceEntityId = sourceEntityId; TargetEntityId = targetEntityId; RawSkillCode = rawSkillCode;
        Amount = amount; DerivedBaseAmount = derivedBaseAmount;
        OptionalComponents = Array.AsReadOnly(optionalComponents.ToArray());
        TypeRaw = typeRaw; ModifierRaw = modifierRaw; DirectionRaw = directionRaw; Provenance = provenance;
    }
}
