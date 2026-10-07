namespace Aion2Meter.Replay.Research;

// Research contracts belong to offline replay, not Core's live capture or skill metadata API.
public enum ActionAnchorSource { OutboundSequence, UserActionCue, ManualAnnotation, ImportedResearchAnnotation }
public enum ResearchMatchStatus { Exact, StructuralOnly, Ambiguous, Unmatched, Contradicted }
public enum RecordContextClassification
{
    ControlledTupleInsideWindow, ControlledTupleOutsideWindow,
    OtherTupleInsideWindow, OtherTupleOutsideWindow, Unresolved
}

public sealed record ResearchAnnotationRevision(string Field, string? PreviousValue, string? CorrectedValue,
    string Provenance, DateTimeOffset? Timestamp = null);

public sealed record VisualDamageObservation
{
    public string ObservationId { get; init; } = "";
    public ulong? FinalAmount { get; init; }
    public ulong? BaseAmount { get; init; }
    public IReadOnlyList<ulong>? ComponentValues { get; init; }
    public string? PositionLabel { get; init; }
    public bool? CriticalObserved { get; init; }
    public bool? PerfectObserved { get; init; }
    public bool? DoubleObserved { get; init; }
    public bool? FrontObserved { get; init; }
    public bool? BackObserved { get; init; }
    // Optional external selectors narrow context before checking numeric equality. Position is zero-based.
    public int? RecordId { get; init; }
    public int? GroupRecordPosition { get; init; }
    public uint? RawSkillCode { get; init; }
    public ulong? SourceEntityId { get; init; }
    public ulong? TargetEntityId { get; init; }
    public ResearchConfidence Confidence { get; init; } = ResearchConfidence.Unresolved;
    public string? Notes { get; init; }
    public IReadOnlyList<ResearchAnnotationRevision> CorrectionHistory { get; init; } = [];
}

public sealed record ManualActionObservation
{
    public string? ManualSkillName { get; init; }
    public string? ManualPhaseLabel { get; init; }
    public bool? ProcObserved { get; init; }
    public IReadOnlyList<VisualDamageObservation> VisualDamageGroups { get; init; } = [];
    public IReadOnlyList<string> VisibleFlags { get; init; } = [];
    public ResearchConfidence Confidence { get; init; } = ResearchConfidence.Unresolved;
    public string? Notes { get; init; }
}

public sealed record ResearchActionAnchor
{
    public string AnchorId { get; init; } = "";
    public string CaptureId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public ActionAnchorSource AnchorSource { get; init; } = ActionAnchorSource.ImportedResearchAnnotation;
    public long? PacketIndex { get; init; }
    public IReadOnlyList<long> SequencePacketIndices { get; init; } = [];
    public string? Label { get; init; }
    public ulong? SourceEntityId { get; init; }
    public ulong? TargetEntityId { get; init; }
    public ManualActionObservation? ExternalAnnotation { get; init; }
}

public sealed record ActionWindowPolicy
{
    public double BeforeMilliseconds { get; init; }
    public double AfterMilliseconds { get; init; } = 4000;
    public bool CapAtNextAnchor { get; init; } = true;
    public double AuxiliaryProximityMilliseconds { get; init; } = 3000;
    public void Validate()
    {
        if (!double.IsFinite(BeforeMilliseconds) || BeforeMilliseconds < 0 || BeforeMilliseconds > 3_600_000 ||
            !double.IsFinite(AfterMilliseconds) || AfterMilliseconds <= 0 || AfterMilliseconds > 3_600_000 ||
            !double.IsFinite(AuxiliaryProximityMilliseconds) || AuxiliaryProximityMilliseconds < 0 || AuxiliaryProximityMilliseconds > 3_600_000)
            throw new ArgumentException("Window durations must be finite, nonnegative (after > 0), and at most one hour.");
    }
}

public sealed record AuxiliaryRecordObservation(ulong SourceEntityId, ulong? TargetEntityId, uint RawSkillCode,
    byte CorrelationTokenCandidate, ulong? ModeCandidate, byte[] UnresolvedRemainder, RawProtocolRecord RawRecord)
{
    public string CaptureId => RawRecord.SourceCapture;
    public string Tag => RawRecord.OpcodeCandidate;
}
public sealed record AuxiliaryDecodeIssue(RawProtocolRecord RawRecord, string Reason);
public sealed record AuxiliaryDecodeResult(IReadOnlyList<AuxiliaryRecordObservation> Observations,
    IReadOnlyList<AuxiliaryDecodeIssue> Issues);
public sealed record AuxiliaryRecordEdge(string CaptureId, int CombatRecordId, int AuxiliaryRecordId, string Tag,
    ulong SourceEntityId, ulong? TargetEntityId, uint RawSkillCode, byte CorrelationTokenCandidate,
    double DeltaMs, ResearchConfidence Confidence);

public sealed record CodeMultiplicity(uint RawSkillCode, int Count);
public sealed record RepeatedCodePositions(uint RawSkillCode, IReadOnlyList<int> Positions);
public sealed record RecordGroupPattern(IReadOnlyList<uint> ExactOrderedCodes, IReadOnlyList<CodeMultiplicity> CodeMultiset,
    IReadOnlyList<uint> UniqueCodeSet, int RecordCount, double ArrivalSpanMilliseconds,
    IReadOnlyList<RepeatedCodePositions> RepeatedCodePositions);
public sealed record CombatRecordGroup(string GroupId, string ActionWindowId, string CaptureId, ulong SourceEntityId,
    ulong TargetEntityId, IReadOnlyList<SupportedCombatRecord> OrderedRecords,
    IReadOnlyList<AuxiliaryRecordEdge> AuxiliaryEdges, RecordGroupPattern GroupPattern, ResearchConfidence Confidence)
{
    public IReadOnlyList<uint> OrderedRawSkillCodes => GroupPattern.ExactOrderedCodes;
    public IReadOnlyList<ulong> AggregateAmounts => OrderedRecords.Select(r => r.AggregateAmount).ToArray();
    public IReadOnlyList<IReadOnlyList<ulong>> ComponentLists => OrderedRecords.Select(r => r.OptionalComponents).ToArray();
    public IReadOnlyList<byte> CorrelationTokens => OrderedRecords.Select(r => r.UnknownAfterSkill).ToArray();
}
public sealed record WindowCombatRecord(SupportedCombatRecord CombatRecord, double DeltaFromAnchorMilliseconds);
public sealed record ResearchActionWindow(string WindowId, ResearchActionAnchor ActionAnchor,
    DateTimeOffset WindowStart, DateTimeOffset WindowEnd, ResearchActionAnchor? PreviousAnchor,
    ResearchActionAnchor? NextAnchor, ulong? SourceEntityId, ulong? TargetEntityId,
    IReadOnlyList<WindowCombatRecord> Records, IReadOnlyList<AuxiliaryRecordObservation> AuxiliaryObservations,
    IReadOnlyList<SupportedCombatRecord> BackgroundRecords, IReadOnlyList<CombatRecordGroup> RecordGroups,
    IReadOnlyList<VisualNetworkComparison> VisualComparisons, ResearchConfidence Confidence, IReadOnlyList<string> Warnings);
public sealed record RecordWindowAssociation(string WindowId, RecordContextClassification Classification);
public sealed record ClassifiedCombatRecord(SupportedCombatRecord CombatRecord, RecordContextClassification Classification,
    IReadOnlyList<RecordWindowAssociation> WindowAssociations);
public sealed record VisualNetworkComparison(string ObservationId, VisualDamageObservation Observation,
    ResearchMatchStatus Status, IReadOnlyList<int> CandidateRecordIds, string Notes);
public sealed record ActionCorrelationResult(string CaptureId, ActionWindowPolicy Policy,
    IReadOnlyList<ResearchActionAnchor> ActionAnchors, IReadOnlyList<ResearchActionWindow> Windows,
    IReadOnlyList<ClassifiedCombatRecord> AllRecords, IReadOnlyList<AuxiliaryRecordObservation> AuxiliaryObservations,
    IReadOnlyList<AuxiliaryRecordEdge> AuxiliaryEdges, IReadOnlyList<AuxiliaryDecodeIssue> AuxiliaryIssues,
    IReadOnlyList<RawCombatCandidate> UnresolvedCombatCandidates, IReadOnlyList<string> Warnings)
{
    public string? SemanticLabel { get; init; }
}

// Generic JSON definitions contain externally supplied context; no dataset codes are parser rules.
public sealed record ActionResearchDefinition
{
    public string Capture { get; init; } = "";
    public string? Local { get; init; }
    public string? Remote { get; init; }
    public string? SemanticLabel { get; init; }
    public ulong? SourceEntityId { get; init; }
    public ulong? TargetEntityId { get; init; }
    public ActionWindowPolicy Policy { get; init; } = new();
    public IReadOnlyList<int>? OutboundSequence { get; init; }
    public double SequenceWindowMilliseconds { get; init; } = 20;
    public bool IncludeMetadataCues { get; init; }
    public IReadOnlyList<ResearchActionAnchor> Anchors { get; init; } = [];
}
public sealed record ResearchTrialSelection(string Definition, IReadOnlyList<string> AnchorIds);
public sealed record LabeledResearchTrialGroup(string Label, IReadOnlyList<ResearchTrialSelection> Trials);
public sealed record GroupComparisonDefinition(IReadOnlyList<LabeledResearchTrialGroup> Groups);
public sealed record ResearchTrial(string CaptureId, string TrialId, IReadOnlyList<SupportedCombatRecord> OrderedRecords,
    IReadOnlyList<AuxiliaryRecordEdge> AuxiliaryEdges, DateTimeOffset AnchorTimestamp);
public sealed record ObservedRange(double Minimum, double Maximum);
public sealed record MultiplicityDifference(uint RawSkillCode, int PositiveMinimum, int PositiveMaximum,
    int NegativeMinimum, int NegativeMaximum, bool ConsistentPositiveIncrease);
public sealed record AuxiliaryTagCount(string Tag, int EdgeCount, int CombatRecordCount);
public sealed record CodeGroupDistribution(uint RawSkillCode, int PresenceTrials, IReadOnlyList<ulong> AggregateAmounts,
    IReadOnlyList<RawFlagCombination> RawFlagCombinations, IReadOnlyList<string> AuxiliaryTags,
    IReadOnlyList<AuxiliaryTagCount> AuxiliaryFootprint, ObservedRange? DeltaFromAnchorMilliseconds);
public sealed record OrderedPatternObservation(IReadOnlyList<uint> OrderedCodes, int TrialCount);
public sealed record OrderedSubsequenceDifference(IReadOnlyList<uint> NegativePattern, IReadOnlyList<uint> PositivePattern,
    bool NegativeIsSubsequence, IReadOnlyList<uint> AdditionalOrderedCodes);
public sealed record DifferentialAssociation(string PositiveLabel, string NegativeLabel, int PositiveTrials,
    int NegativeControlTrials, IReadOnlyList<uint> CodesPresentInEveryPositive, IReadOnlyList<uint> PositiveOnlyCodes,
    IReadOnlyList<uint> CodesPresentInBothGroups, IReadOnlyList<uint> AmbiguousCodes,
    IReadOnlyList<MultiplicityDifference> MultiplicityDifferences,
    IReadOnlyList<OrderedPatternObservation> PositivePatterns, IReadOnlyList<OrderedPatternObservation> NegativePatterns,
    IReadOnlyList<OrderedSubsequenceDifference> OrderedSubsequenceDifferences,
    IReadOnlyList<CodeGroupDistribution> PositiveDistributions, IReadOnlyList<CodeGroupDistribution> NegativeDistributions,
    EvidenceClassification EvidenceClass, IReadOnlyList<string> Limitations);
