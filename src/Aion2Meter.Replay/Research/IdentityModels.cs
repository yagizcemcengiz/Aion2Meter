namespace Aion2Meter.Replay.Research;

public enum EvidenceClassification { A, B, C, D, E }
public enum ResearchConfidence { High, Unresolved }
public enum IdentityEvidenceType { NameEnvelope4536, EntityHeader4136, Unresolved4536Presence }
public enum RelationshipEvidenceType { FfAnchorRelatedId, SuffixRelatedId }
public enum NameLookupMode { PrecedingOnly, RetrospectiveSameCapture }
public enum NameResolutionState { Unknown, Resolved, Conflict }

public sealed record RecordProvenance(int RecordId, string SourceCapture, string Direction,
    long StreamOffset, int OuterFrameId, long OuterFrameOffset, long PacketIndex,
    long CompletionPacketIndex, DateTimeOffset CompletionUtc, IReadOnlyList<ContainerLocation> ContainerPath)
{
    public static RecordProvenance From(RawProtocolRecord r) => new(r.RecordId, r.SourceCapture,
        r.Direction.ToString(), r.StreamOffset, r.OuterFrameId, r.OuterFrameOffset, r.PacketIndex,
        r.CompletionPacketIndex, r.CompletionUtc, r.ContainerPath);
}

public sealed record IdentityObservation(int ObservationId, ulong EntityId, string? Name,
    IdentityEvidenceType EvidenceType, EvidenceClassification Classification, ResearchConfidence Confidence,
    uint? UnknownMask, byte? PresenceByte, byte? KindCandidate, byte[] UnresolvedRemainder, RawProtocolRecord RawRecord)
{
    public string CaptureId => RawRecord.SourceCapture;
    public string RecordTag => RawRecord.OpcodeCandidate;
    public DateTimeOffset Timestamp => RawRecord.TimestampUtc;
    public RecordProvenance Provenance => RecordProvenance.From(RawRecord);
}

public sealed record RelationshipObservation(string ObservationId, ulong HeaderEntityId,
    ulong RelatedEntityIdCandidate, RelationshipEvidenceType EvidenceType, int FieldOffset,
    uint? AuxiliaryValue, ushort? ContextWorldCandidate, string? ContextLabel,
    byte[] UnknownAnchorBytes, byte[] UnresolvedRemainder, EvidenceClassification Classification,
    ResearchConfidence Confidence, RawProtocolRecord RawRecord)
{
    public string CaptureId => RawRecord.SourceCapture;
    public DateTimeOffset Timestamp => RawRecord.TimestampUtc;
    public bool IsSelfId => HeaderEntityId == RelatedEntityIdCandidate;
    public RecordProvenance Provenance => RecordProvenance.From(RawRecord);
}

// Context text must never be inserted into the character-name directory.
public sealed record TextContextObservation(int ObservationId, ulong EntityId, uint AuxiliaryValue,
    ushort ContextWorldCandidate, string ContextLabel, byte[] UnresolvedRemainder, RawProtocolRecord RawRecord)
{
    public string CaptureId => RawRecord.SourceCapture;
    public DateTimeOffset Timestamp => RawRecord.TimestampUtc;
    public EvidenceClassification Classification => EvidenceClassification.A;
    public ResearchConfidence Confidence => ResearchConfidence.High;
    public RecordProvenance Provenance => RecordProvenance.From(RawRecord);
}

public sealed record IdentityDecodeIssue(RawProtocolRecord RawRecord, string Reason);
public sealed record IdentityRecordResult(IReadOnlyList<IdentityObservation> Identities,
    IReadOnlyList<RelationshipObservation> Relationships, IReadOnlyList<TextContextObservation> Contexts,
    IReadOnlyList<IdentityDecodeIssue> Issues);

public sealed record NameResolution(NameLookupMode Mode, NameResolutionState State,
    IReadOnlyList<string> Names, IReadOnlyList<int> ObservationIds, DateTimeOffset? LatestObservationTimestamp)
{
    public string? Name => State == NameResolutionState.Resolved ? Names[0] : null;
}

// Non-null promoted fields exist only after the complete supported grammar has passed.
// RawCombatCandidate continues to retain tentative fields from unsupported/partial decoding.
public sealed record SupportedCombatRecord(ulong TargetEntityId, ulong SourceEntityId, uint RawSkillCode,
    ulong CategoryOrSwitch, ulong Unknown0, byte UnknownAfterSkill, ulong TypeCandidate,
    byte ModifierCandidate, byte DirectionCandidate, IReadOnlyDictionary<string, byte[]> UnknownRegions,
    ulong ZeroUnknownCandidate, ulong PreValueCandidate, ulong AggregateAmount,
    IReadOnlyList<ulong> OptionalComponents, ulong DerivedBaseAmount, byte[] TerminalBytes, RawProtocolRecord RawRecord)
{
    public EvidenceClassification Classification => EvidenceClassification.B;
    public ResearchConfidence Confidence => ResearchConfidence.High;
    public RecordProvenance Provenance => RecordProvenance.From(RawRecord);

    public static SupportedCombatRecord From(RawCombatCandidate c)
    {
        if (c.Status != "Supported") throw new ArgumentException("Only complete supported category-6 records can be promoted.", nameof(c));
        return new(c.TargetIdCandidate!.Value, c.ActorIdCandidate!.Value, c.RawSkillCodeCandidate!.Value,
            c.CategoryOrSwitch!.Value, c.Unknown0!.Value, c.UnknownAfterSkill!.Value, c.TypeCandidate!.Value,
            c.ModifierCandidate!.Value, c.DirectionCandidate!.Value, c.UnknownRegions,
            c.ZeroUnknownCandidate!.Value, c.PreValueCandidate!.Value, c.AggregateAmount!.Value,
            c.OptionalComponents, c.DerivedBaseAmount!.Value, c.TerminalBytes, c.RawRecord);
    }
}

public sealed record CombatIdentityCorrelation(int CombatRecordId, string CaptureId, ulong SourceEntityId,
    ulong TargetEntityId, uint RawSkillCode, DateTimeOffset Timestamp, NameResolution SourcePrecedingName,
    NameResolution SourceRetrospectiveName, NameResolution TargetPrecedingName, NameResolution TargetRetrospectiveName,
    IReadOnlyList<int> SourceIdentityObservationIds, IReadOnlyList<int> TargetIdentityObservationIds,
    IReadOnlyList<string> SourceRelationshipObservationIds, IReadOnlyList<string> TargetRelationshipObservationIds,
    SkillMetadata? SkillMetadata, RecordProvenance Provenance)
{
    public EvidenceClassification Classification => EvidenceClassification.B;
    public ResearchConfidence Confidence => ResearchConfidence.High;
}

public sealed record IdentityNameEdge(ulong EntityId, string Name, int ObservationId);
public sealed record IdentityDuplicate(ulong EntityId, string? Name, IReadOnlyList<int> ObservationIds);
public sealed record IdentityNameConflict(ulong EntityId, NameResolution Resolution);
public sealed record ReplayIdentityGraph(string CaptureId, IReadOnlyList<ulong> EntityNodes,
    IReadOnlyList<IdentityNameEdge> NameEdges, IReadOnlyList<RelationshipObservation> RelatedIdCandidateEdges,
    IReadOnlyList<TextContextObservation> ContextLabelEdges, IReadOnlyList<IdentityDuplicate> Duplicates,
    IReadOnlyList<IdentityNameConflict> Conflicts);

public sealed record RawFlagCombination(ulong TypeCandidate, byte ModifierCandidate, byte DirectionCandidate, int Count);
public sealed record SkillEntityNames(ulong EntityId, NameResolution RetrospectiveSameCapture);
public sealed record RawSkillInventory(uint RawSkillCode, int OccurrenceCount, IReadOnlyList<ulong> SourceEntityIds,
    IReadOnlyList<ulong> TargetEntityIds, ulong AggregateMinimum, ulong AggregateMaximum,
    int ComponentTailRows, int ComponentValueCount, IReadOnlyList<RawFlagCombination> RawFlagCombinations,
    IReadOnlyList<SkillEntityNames> SourceNames, IReadOnlyList<SkillEntityNames> TargetNames);

public sealed record IdentityResearchSummary(int SupportedCombatRecords, int IdentityObservations,
    int NameObservations4536, int HeaderObservations4136, int DistinctIdNamePairs,
    int SameCaptureNameConflicts, int DuplicateGroups, int SourceRowsWithStructuralMatch,
    int TargetRowsWithStructuralMatch, int PrecedingSourceNameRows, int RetrospectiveOnlySourceNameRows,
    int SourceRowsWithNames, int UnknownSourceRows, int PrecedingTargetNameRows,
    int RetrospectiveOnlyTargetNameRows, int TargetRowsWithNames, int UnknownTargetRows,
    int SourceConflictRows, int TargetConflictRows, int RelationshipObservations4136,
    int SelfIdRelationshipObservations, int TextContextObservations338A, int DistinctRawSkillCodes, int DecodeIssues);

public sealed record IdentityResearchResult(string CaptureId, IdentityResearchSummary Summary,
    IReadOnlyList<IdentityObservation> IdentityObservations, IReadOnlyList<RelationshipObservation> RelationshipObservations,
    IReadOnlyList<TextContextObservation> TextContextObservations, IReadOnlyList<SupportedCombatRecord> SupportedCombatRecords,
    IReadOnlyList<CombatIdentityCorrelation> CombatCorrelations, ReplayIdentityGraph Graph,
    IReadOnlyList<RawSkillInventory> Skills, IReadOnlyList<IdentityDecodeIssue> DecodeIssues);
