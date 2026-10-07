namespace Aion2Meter.Core;

public enum CurrentPlayerBindingStatus { Unknown, Resolved, Conflict }

/// <summary>Replay identity only; a source path is not a future live connection identity.</summary>
public sealed record ReplayConnectionScope(string SourceCapture, string? SessionId,
    string LocalEndpoint, string RemoteEndpoint, uint? ClientIsn, uint? ServerIsn,
    DateTimeOffset? ClientSynTimestamp, long? ClientSynPacketIndex);

public sealed record BindingByteRange(int Offset, int Length);
public sealed record BindingContainerLocation(int ContainerRecordId, int InnerOffset);

/// <summary>Immutable field-level candidate snapshot, including complete-record provenance.</summary>
public sealed class CurrentPlayerBindingEvidence
{
    public string SourceCapture { get; }
    public int RecordId { get; }
    public string RecordTag { get; }
    public TrafficDirection Direction { get; }
    public long StreamOffset { get; }
    public int OuterFrameId { get; }
    public long OuterFrameOffset { get; }
    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset CompletionTimestamp { get; }
    public long PacketIndex { get; }
    public long CompletionPacketIndex { get; }
    public int FrameLength { get; }
    public IReadOnlyList<BindingContainerLocation> ContainerPath { get; }
    public string ProvenanceIdentity { get; }
    public string RawRecordSha256 { get; }
    public string RawRecordBase64 { get; }
    public ulong EntityId { get; }
    public string CharacterName { get; }
    public string NumericRepresentation { get; }
    public BindingByteRange NumericRange { get; }
    public BindingByteRange NameRange { get; }
    public int NameLengthOffset { get; }
    public IReadOnlyList<byte> NumericBytes { get; }
    public IReadOnlyList<byte> NameBytes { get; }

    public CurrentPlayerBindingEvidence(string sourceCapture, int recordId, string recordTag,
        TrafficDirection direction, long streamOffset, int outerFrameId, long outerFrameOffset,
        DateTimeOffset timestamp, DateTimeOffset completionTimestamp, long packetIndex,
        long completionPacketIndex, int frameLength, IEnumerable<BindingContainerLocation> containerPath,
        string provenanceIdentity, string rawRecordSha256, string rawRecordBase64, ulong entityId, string characterName,
        string numericRepresentation, BindingByteRange numericRange, BindingByteRange nameRange,
        int nameLengthOffset, IEnumerable<byte> numericBytes, IEnumerable<byte> nameBytes)
    {
        SourceCapture = sourceCapture; RecordId = recordId; RecordTag = recordTag; Direction = direction;
        StreamOffset = streamOffset; OuterFrameId = outerFrameId; OuterFrameOffset = outerFrameOffset;
        Timestamp = timestamp; CompletionTimestamp = completionTimestamp; PacketIndex = packetIndex;
        CompletionPacketIndex = completionPacketIndex; FrameLength = frameLength;
        ContainerPath = Array.AsReadOnly(containerPath.ToArray());
        ProvenanceIdentity = provenanceIdentity; RawRecordSha256 = rawRecordSha256;
        RawRecordBase64 = rawRecordBase64;
        EntityId = entityId; CharacterName = characterName; NumericRepresentation = numericRepresentation;
        NumericRange = numericRange; NameRange = nameRange; NameLengthOffset = nameLengthOffset;
        NumericBytes = Array.AsReadOnly(numericBytes.ToArray()); NameBytes = Array.AsReadOnly(nameBytes.ToArray());
    }
}

public sealed class CurrentPlayerBinding
{
    public CurrentPlayerBindingStatus Status { get; }
    public ReplayConnectionScope? Scope { get; }
    public ulong? EntityId { get; }
    public string? CharacterName { get; }
    public DateTimeOffset? CandidateObservedFrom { get; }
    public DateTimeOffset? ValidFrom { get; }
    public DateTimeOffset? ValidUntil { get; }
    public DateTimeOffset? EvidenceCoverageEnd { get; }
    public IReadOnlyList<CurrentPlayerBindingEvidence> Evidence { get; }
    public IReadOnlyList<CurrentPlayerBindingEvidence> QualifyingEvidence { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public CurrentPlayerBinding(CurrentPlayerBindingStatus status, ReplayConnectionScope? scope,
        ulong? entityId, string? characterName, DateTimeOffset? candidateObservedFrom,
        DateTimeOffset? validFrom, DateTimeOffset? validUntil, DateTimeOffset? evidenceCoverageEnd,
        IEnumerable<CurrentPlayerBindingEvidence> evidence, IEnumerable<string> diagnostics)
    {
        if (status == CurrentPlayerBindingStatus.Resolved &&
            (scope is null || entityId is null || characterName is null || validFrom is null))
            throw new ArgumentException("Resolved binding requires scope, identity and confirmation time.");
        if (status != CurrentPlayerBindingStatus.Resolved &&
            (entityId is not null || characterName is not null || validFrom is not null || validUntil is not null))
            throw new ArgumentException("Unknown/Conflict cannot expose an authoritative identity or lifetime.");
        Status = status; Scope = scope; EntityId = entityId; CharacterName = characterName;
        CandidateObservedFrom = candidateObservedFrom; ValidFrom = validFrom;
        ValidUntil = validUntil; EvidenceCoverageEnd = evidenceCoverageEnd;
        Evidence = Array.AsReadOnly(evidence.ToArray()); Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        QualifyingEvidence = Array.AsReadOnly(Status == CurrentPlayerBindingStatus.Resolved
            ? Evidence.Where(e => e.EntityId == EntityId && StringComparer.Ordinal.Equals(e.CharacterName, CharacterName)).ToArray()
            : []);
    }
}
