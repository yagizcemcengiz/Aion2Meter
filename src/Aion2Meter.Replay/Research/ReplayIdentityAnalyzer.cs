namespace Aion2Meter.Replay.Research;

public sealed class ReplayIdentityAnalyzer
{
    public IdentityResearchResult Analyze(string captureId, ProtocolDecodeResult decoded,
        ISkillMetadataProvider? skillMetadataProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureId);
        if (decoded.Records.Any(r => r.SourceCapture != captureId) || decoded.CombatCandidates.Any(c => c.RawRecord.SourceCapture != captureId))
            throw new ArgumentException("Replay identity analysis is scoped to one capture.", nameof(decoded));
        var provider = skillMetadataProvider ?? new EmptySkillMetadataProvider();
        var extracted = decoded.Records.Select(IdentityRecordDecoder.Decode).ToArray();
        var identities = extracted.SelectMany(x => x.Identities).ToArray();
        var relationships = extracted.SelectMany(x => x.Relationships).ToArray();
        var contexts = extracted.SelectMany(x => x.Contexts).ToArray();
        var issues = extracted.SelectMany(x => x.Issues).ToArray();
        var directory = new ReplayIdentityDirectory(captureId, identities);
        var supported = decoded.CombatCandidates.Where(c => c.Status == "Supported")
            .Select(SupportedCombatRecord.From).OrderBy(c => c.RawRecord.TimestampUtc)
            .ThenBy(c => c.RawRecord.StreamOffset).ThenBy(c => c.RawRecord.RecordId).ToArray();
        var correlations = supported.Select(Correlate).ToArray();
        var nameEdges = identities.Where(o => o.Name is not null).Select(o => new IdentityNameEdge(o.EntityId, o.Name!, o.ObservationId)).ToArray();
        var nodes = identities.Select(o => o.EntityId).Concat(contexts.Select(o => o.EntityId))
            .Concat(relationships.SelectMany(o => new[] { o.HeaderEntityId, o.RelatedEntityIdCandidate }))
            .Concat(supported.SelectMany(c => new[] { c.SourceEntityId, c.TargetEntityId })).Distinct().Order().ToArray();
        var duplicates = identities.GroupBy(o => (o.EntityId, o.Name)).Where(g => g.Count() > 1)
            .Select(g => new IdentityDuplicate(g.Key.EntityId, g.Key.Name, g.Select(o => o.ObservationId).ToArray())).ToArray();
        var conflicts = nodes.Select(id => new IdentityNameConflict(id, directory.GetRetrospectiveSameCaptureNames(id)))
            .Where(c => c.Resolution.State == NameResolutionState.Conflict).ToArray();
        var graph = new ReplayIdentityGraph(captureId, nodes, nameEdges, relationships, contexts, duplicates, conflicts);
        var skills = supported.GroupBy(c => c.RawSkillCode).OrderBy(g => g.Key).Select(g => new RawSkillInventory(g.Key, g.Count(),
            g.Select(c => c.SourceEntityId).Distinct().Order().ToArray(), g.Select(c => c.TargetEntityId).Distinct().Order().ToArray(),
            g.Min(c => c.AggregateAmount), g.Max(c => c.AggregateAmount), g.Count(c => c.OptionalComponents.Count > 0),
            g.Sum(c => c.OptionalComponents.Count), g.GroupBy(c => (c.TypeCandidate, c.ModifierCandidate, c.DirectionCandidate))
                .OrderBy(f => f.Key).Select(f => new RawFlagCombination(f.Key.TypeCandidate, f.Key.ModifierCandidate, f.Key.DirectionCandidate, f.Count())).ToArray(),
            g.Select(c => c.SourceEntityId).Distinct().Order().Select(id => new SkillEntityNames(id, directory.GetRetrospectiveSameCaptureNames(id))).ToArray(),
            g.Select(c => c.TargetEntityId).Distinct().Order().Select(id => new SkillEntityNames(id, directory.GetRetrospectiveSameCaptureNames(id))).ToArray())).ToArray();
        var summary = new IdentityResearchSummary(supported.Length, identities.Length,
            identities.Count(o => o.EvidenceType == IdentityEvidenceType.NameEnvelope4536),
            identities.Count(o => o.EvidenceType == IdentityEvidenceType.EntityHeader4136),
            nameEdges.Select(e => (e.EntityId, e.Name)).Distinct().Count(), conflicts.Length, duplicates.Length,
            correlations.Count(c => c.SourceIdentityObservationIds.Count > 0 || c.SourceRelationshipObservationIds.Count > 0),
            correlations.Count(c => c.TargetIdentityObservationIds.Count > 0 || c.TargetRelationshipObservationIds.Count > 0),
            correlations.Count(c => c.SourcePrecedingName.State == NameResolutionState.Resolved),
            correlations.Count(c => c.SourcePrecedingName.State == NameResolutionState.Unknown && c.SourceRetrospectiveName.State == NameResolutionState.Resolved),
            correlations.Count(c => c.SourceRetrospectiveName.State == NameResolutionState.Resolved),
            correlations.Count(c => c.SourceRetrospectiveName.State == NameResolutionState.Unknown),
            correlations.Count(c => c.TargetPrecedingName.State == NameResolutionState.Resolved),
            correlations.Count(c => c.TargetPrecedingName.State == NameResolutionState.Unknown && c.TargetRetrospectiveName.State == NameResolutionState.Resolved),
            correlations.Count(c => c.TargetRetrospectiveName.State == NameResolutionState.Resolved),
            correlations.Count(c => c.TargetRetrospectiveName.State == NameResolutionState.Unknown),
            correlations.Count(c => c.SourceRetrospectiveName.State == NameResolutionState.Conflict),
            correlations.Count(c => c.TargetRetrospectiveName.State == NameResolutionState.Conflict),
            relationships.Length, relationships.Count(r => r.IsSelfId), contexts.Length, skills.Length, issues.Length);
        return new(captureId, summary, identities, relationships, contexts, supported, correlations, graph, skills, issues);

        CombatIdentityCorrelation Correlate(SupportedCombatRecord c)
        {
            SkillMetadata? metadata = null;
            if (provider.TryGetSkillMetadata(c.RawSkillCode, out var supplied))
            {
                if (supplied is null || supplied.RawSkillCode != c.RawSkillCode)
                    throw new InvalidDataException("Metadata provider returned a different raw code or no metadata for a successful lookup.");
                metadata = supplied;
            }
            return new(c.RawRecord.RecordId, captureId, c.SourceEntityId, c.TargetEntityId, c.RawSkillCode, c.RawRecord.TimestampUtc,
                directory.GetLatestPrecedingName(c.SourceEntityId, c.RawRecord.TimestampUtc),
                directory.GetRetrospectiveSameCaptureNames(c.SourceEntityId),
                directory.GetLatestPrecedingName(c.TargetEntityId, c.RawRecord.TimestampUtc),
                directory.GetRetrospectiveSameCaptureNames(c.TargetEntityId),
                directory.GetObservations(c.SourceEntityId).Select(o => o.ObservationId).ToArray(),
                directory.GetObservations(c.TargetEntityId).Select(o => o.ObservationId).ToArray(),
                relationships.Where(o => o.HeaderEntityId == c.SourceEntityId || o.RelatedEntityIdCandidate == c.SourceEntityId).Select(o => o.ObservationId).ToArray(),
                relationships.Where(o => o.HeaderEntityId == c.TargetEntityId || o.RelatedEntityIdCandidate == c.TargetEntityId).Select(o => o.ObservationId).ToArray(),
                metadata, c.Provenance);
        }
    }
}
