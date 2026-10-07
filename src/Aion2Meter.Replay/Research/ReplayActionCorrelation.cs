namespace Aion2Meter.Replay.Research;

// Time first; physical capture packet, outer byte offset and nested offsets break ties.
// RecordId is the final decoder-stable tie breaker, never token/code/amount.
public sealed class ResearchRecordArrivalComparer : IComparer<RawProtocolRecord>
{
    public static ResearchRecordArrivalComparer Instance { get; } = new();
    public int Compare(RawProtocolRecord? x, RawProtocolRecord? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var result = x.TimestampUtc.CompareTo(y.TimestampUtc);
        if (result != 0) return result;
        result = x.PacketIndex.CompareTo(y.PacketIndex);
        if (result != 0) return result;
        result = x.OuterFrameOffset.CompareTo(y.OuterFrameOffset);
        if (result != 0) return result;
        for (var i = 0; i < Math.Min(x.ContainerPath.Count, y.ContainerPath.Count); i++)
        {
            result = x.ContainerPath[i].InnerOffset.CompareTo(y.ContainerPath[i].InnerOffset);
            if (result != 0) return result;
        }
        result = x.ContainerPath.Count.CompareTo(y.ContainerPath.Count);
        return result != 0 ? result : x.RecordId.CompareTo(y.RecordId);
    }
}

public static class VisualNetworkMatcher
{
    public static VisualNetworkComparison Match(VisualDamageObservation observation, IReadOnlyList<SupportedCombatRecord> records)
    {
        if (string.IsNullOrWhiteSpace(observation.ObservationId) || observation.GroupRecordPosition is < 0)
            throw new ArgumentException("Visual observations require an ID and nonnegative optional position.");
        IEnumerable<SupportedCombatRecord> candidates = records;
        if (observation.GroupRecordPosition is { } position)
            candidates = position < records.Count ? [records[position]] : [];
        candidates = candidates.Where(c => (observation.RecordId is null || observation.RecordId == c.RawRecord.RecordId) &&
            (observation.RawSkillCode is null || observation.RawSkillCode == c.RawSkillCode) &&
            (observation.SourceEntityId is null || observation.SourceEntityId == c.SourceEntityId) &&
            (observation.TargetEntityId is null || observation.TargetEntityId == c.TargetEntityId));
        var structural = candidates.ToArray();
        var hasAmount = observation.FinalAmount is not null || observation.BaseAmount is not null || observation.ComponentValues is not null;
        var exact = structural.Where(c => (observation.FinalAmount is null || observation.FinalAmount == c.AggregateAmount) &&
            (observation.BaseAmount is null || observation.BaseAmount == c.DerivedBaseAmount) &&
            (observation.ComponentValues is null || observation.ComponentValues.SequenceEqual(c.OptionalComponents))).ToArray();
        ResearchMatchStatus status;
        if (structural.Length == 0) status = ResearchMatchStatus.Unmatched;
        else if (!hasAmount) status = structural.Length == 1 ? ResearchMatchStatus.StructuralOnly : ResearchMatchStatus.Ambiguous;
        else if (exact.Length == 1) status = ResearchMatchStatus.Exact;
        else if (exact.Length > 1) status = ResearchMatchStatus.Ambiguous;
        else status = observation.RecordId is not null || observation.GroupRecordPosition is not null ? ResearchMatchStatus.Contradicted : ResearchMatchStatus.Unmatched;
        return new(observation.ObservationId, observation, status,
            (exact.Length > 0 ? exact : structural).Select(c => c.RawRecord.RecordId).ToArray(),
            "Context/explicit selectors precede exact numeric comparison; no nearest-value search, flag conversion or decoder adjustment. Null observations remain unknown.");
    }
}

public sealed class ReplayActionCorrelation
{
    public ActionCorrelationResult Analyze(string captureId, ProtocolDecodeResult decoded,
        IReadOnlyList<ResearchActionAnchor> anchors, ActionWindowPolicy? policy = null,
        ulong? sourceEntityId = null, ulong? targetEntityId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        policy ??= new(); policy.Validate();
        if (decoded.Records.Any(r => r.SourceCapture != captureId) || decoded.CombatCandidates.Any(c => c.RawRecord.SourceCapture != captureId) ||
            anchors.Any(a => a is null || a.CaptureId != captureId || string.IsNullOrWhiteSpace(a.AnchorId)))
            throw new ArgumentException("Records and anchors must be scoped to the same capture, with nonempty anchor IDs.");
        if (anchors.Any(a => a.ExternalAnnotation is { VisualDamageGroups: null }))
            throw new ArgumentException("Visual observation lists cannot be null; omit them or supply an empty list.");
        if (anchors.Select(a => a.AnchorId).Distinct(StringComparer.Ordinal).Count() != anchors.Count || anchors.Count > 10_000)
            throw new ArgumentException("Anchor IDs must be unique; at most 10000 anchors per capture.");
        var orderedAnchors = anchors.Select((a, index) => (a, index)).OrderBy(x => x.a.Timestamp)
            .ThenBy(x => x.a.PacketIndex ?? long.MaxValue).ThenBy(x => x.index).Select(x => x.a).ToArray();
        var combat = decoded.CombatCandidates.Where(c => c.Status == "Supported").Select(SupportedCombatRecord.From)
            .OrderBy(c => c.RawRecord, ResearchRecordArrivalComparer.Instance).ToArray();
        var auxiliary = AuxiliaryRecordDecoder.Decode(decoded.Records);
        var edges = AuxiliaryRecordCorrelation.Match(combat, auxiliary.Observations, policy.AuxiliaryProximityMilliseconds);
        var edgesByRecord = edges.ToLookup(e => e.CombatRecordId);
        var windows = new List<ResearchActionWindow>();
        long associations = 0;
        for (var index = 0; index < orderedAnchors.Length; index++)
        {
            var a = orderedAnchors[index];
            var previous = index > 0 ? orderedAnchors[index - 1] : null;
            var next = index + 1 < orderedAnchors.Length ? orderedAnchors[index + 1] : null;
            var start = a.Timestamp.AddMilliseconds(-policy.BeforeMilliseconds);
            var end = a.Timestamp.AddMilliseconds(policy.AfterMilliseconds);
            var warnings = new List<string>();
            if (policy.CapAtNextAnchor && next is not null)
            {
                var nextStart = next.Timestamp.AddMilliseconds(-policy.BeforeMilliseconds);
                if (nextStart < end) { end = nextStart; warnings.Add("End capped at next anchor's window start; bounds are start-inclusive/end-exclusive."); }
            }
            if (start == end) warnings.Add("Coincident anchors produced an empty ownership window; both anchors remain visible.");
            if (!policy.CapAtNextAnchor) warnings.Add("Windows may overlap; records retain every association rather than being deduplicated.");
            var source = a.SourceEntityId ?? sourceEntityId;
            var target = a.TargetEntityId ?? targetEntityId;
            if (source is null && target is null) warnings.Add("No controlled tuple supplied; contextual classification is unresolved.");
            else if (source is null || target is null) warnings.Add("Partial controlled context: only the explicitly supplied entity constraint applies.");
            var inside = combat.Where(c => c.RawRecord.TimestampUtc >= start && c.RawRecord.TimestampUtc < end).ToArray();
            associations += inside.Length;
            if (associations > 1_000_000) throw new InvalidDataException("Window association limit reached (1000000); narrow the policy/anchors.");
            var selected = inside.Where(c => Matches(c, source, target)).ToArray();
            var background = inside.Where(c => !Matches(c, source, target)).ToArray();
            var selectedEdges = selected.SelectMany(c => edgesByRecord[c.RawRecord.RecordId]).ToArray();
            var windowId = a.AnchorId;
            var groups = selected.GroupBy(c => (c.SourceEntityId, c.TargetEntityId)).Select(g =>
            {
                var records = g.ToArray();
                return new CombatRecordGroup($"{windowId}/tuple-{g.Key.SourceEntityId}-{g.Key.TargetEntityId}", windowId, captureId,
                    g.Key.SourceEntityId, g.Key.TargetEntityId, records,
                    records.SelectMany(c => edgesByRecord[c.RawRecord.RecordId]).ToArray(), Pattern(records), ResearchConfidence.High);
            }).ToArray();
            var annotation = a.ExternalAnnotation;
            if (annotation?.VisualDamageGroups.Select(v => v.ObservationId).Distinct(StringComparer.Ordinal).Count() != annotation?.VisualDamageGroups.Count)
                throw new ArgumentException("Visual observation IDs must be unique within an anchor.");
            var comparisons = annotation?.VisualDamageGroups.Select(v => VisualNetworkMatcher.Match(v, selected)).ToArray() ?? [];
            var relevantAuxiliaryIds = selectedEdges.Select(e => e.AuxiliaryRecordId).ToHashSet();
            windows.Add(new(windowId, a, start, end, previous, next, source, target,
                selected.Select(c => new WindowCombatRecord(c, (c.RawRecord.TimestampUtc - a.Timestamp).TotalMilliseconds)).ToArray(),
                auxiliary.Observations.Where(o => relevantAuxiliaryIds.Contains(o.RawRecord.RecordId) ||
                    o.RawRecord.TimestampUtc >= start && o.RawRecord.TimestampUtc < end).ToArray(),
                background, groups, comparisons, ResearchConfidence.High, warnings));
        }
        var all = combat.Select(c =>
        {
            var memberships = windows.Where(w => c.RawRecord.TimestampUtc >= w.WindowStart && c.RawRecord.TimestampUtc < w.WindowEnd)
                .Select(w => new RecordWindowAssociation(w.WindowId, w.SourceEntityId is null && w.TargetEntityId is null ? RecordContextClassification.Unresolved :
                    Matches(c, w.SourceEntityId, w.TargetEntityId) ? RecordContextClassification.ControlledTupleInsideWindow : RecordContextClassification.OtherTupleInsideWindow)).ToArray();
            var contexts = windows.Where(w => w.SourceEntityId is not null || w.TargetEntityId is not null).ToArray();
            var classification = memberships.Any(m => m.Classification == RecordContextClassification.ControlledTupleInsideWindow) ? RecordContextClassification.ControlledTupleInsideWindow :
                memberships.Any(m => m.Classification == RecordContextClassification.Unresolved) ? RecordContextClassification.Unresolved :
                memberships.Length > 0 ? RecordContextClassification.OtherTupleInsideWindow :
                contexts.Length == 0 ? (sourceEntityId is null && targetEntityId is null ? RecordContextClassification.Unresolved :
                    Matches(c, sourceEntityId, targetEntityId) ? RecordContextClassification.ControlledTupleOutsideWindow : RecordContextClassification.OtherTupleOutsideWindow) :
                contexts.Any(w => Matches(c, w.SourceEntityId, w.TargetEntityId)) ? RecordContextClassification.ControlledTupleOutsideWindow : RecordContextClassification.OtherTupleOutsideWindow;
            return new ClassifiedCombatRecord(c, classification, memberships);
        }).ToArray();
        return new(captureId, policy, orderedAnchors, windows, all, auxiliary.Observations, edges, auxiliary.Issues,
            decoded.CombatCandidates.Where(c => c.Status != "Supported").ToArray(),
            ["Anchors are observations, not inferred physical keypresses. Groups do not define casts or sum damage.",
             "Auxiliary tokens are contextual candidates, not globally unique identities; sparse flag correlations remain raw."]);
    }

    private static bool Matches(SupportedCombatRecord c, ulong? source, ulong? target) =>
        (source is null || c.SourceEntityId == source) && (target is null || c.TargetEntityId == target);

    public static RecordGroupPattern Pattern(IReadOnlyList<SupportedCombatRecord> records)
    {
        var codes = records.Select(c => c.RawSkillCode).ToArray();
        var positions = codes.Select((code, index) => (code, index)).GroupBy(x => x.code).OrderBy(g => g.Key).ToArray();
        return new(codes, positions.Select(g => new CodeMultiplicity(g.Key, g.Count())).ToArray(), positions.Select(g => g.Key).ToArray(),
            records.Count, records.Count == 0 ? 0 : (records[^1].RawRecord.TimestampUtc - records[0].RawRecord.TimestampUtc).TotalMilliseconds,
            positions.Where(g => g.Count() > 1).Select(g => new RepeatedCodePositions(g.Key, g.Select(x => x.index).ToArray())).ToArray());
    }
}
