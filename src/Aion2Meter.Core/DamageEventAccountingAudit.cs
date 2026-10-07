namespace Aion2Meter.Core;

public sealed record DamageEventOccurrence(int InputIndex, DamageEvent Event);
public sealed record DuplicateDamageEventProvenance(string Identity, bool HasConflictingContent,
    IReadOnlyList<DamageEventOccurrence> Occurrences);
public sealed record RawCodeDamageTotal(uint RawSkillCode, int EventCount, decimal TotalAmount);
public sealed record DamageEventAccountingAuditResult(int InputEventCount, int UniqueProvenanceCount,
    int ValidatedUniqueEventCount, int DuplicateInputCount, int RejectedProvenanceCount,
    decimal ValidatedTotalAmount, IReadOnlyList<DuplicateDamageEventProvenance> DuplicateProvenance,
    IReadOnlyList<RawCodeDamageTotal> RawCodeTotals);

/// <summary>A finite replay/input batch audit, with no encounter, self filtering, timers or live state.</summary>
public static class DamageEventAccountingAudit
{
    public static DamageEventAccountingAuditResult Analyze(IEnumerable<DamageEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var groups = new Dictionary<string, List<DamageEventOccurrence>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var e in events)
        {
            ArgumentNullException.ThrowIfNull(e);
            if (!groups.TryGetValue(e.Identity, out var occurrences)) groups.Add(e.Identity, occurrences = []);
            occurrences.Add(new(count++, e));
        }
        var duplicates = new List<DuplicateDamageEventProvenance>();
        var validated = new List<DamageEvent>();
        var rejected = 0;
        foreach (var (identity, occurrences) in groups)
        {
            var first = occurrences[0].Event;
            var conflict = occurrences.Skip(1).Any(o => !SameContent(first, o.Event));
            if (occurrences.Count > 1)
                duplicates.Add(new(identity, conflict, Array.AsReadOnly(occurrences.ToArray())));
            // A conflicting copy cannot establish an authoritative amount; reject the whole identity.
            if (conflict) rejected++; else validated.Add(first);
        }
        var totals = validated.GroupBy(e => e.RawSkillCode).OrderBy(g => g.Key)
            .Select(g => new RawCodeDamageTotal(g.Key, g.Count(), g.Sum(e => (decimal)e.Amount))).ToArray();
        return new(count, groups.Count, validated.Count, count - groups.Count, rejected,
            validated.Sum(e => (decimal)e.Amount), Array.AsReadOnly(duplicates.ToArray()), Array.AsReadOnly(totals));
    }

    private static bool SameContent(DamageEvent a, DamageEvent b) =>
        a.SourceEntityId == b.SourceEntityId && a.TargetEntityId == b.TargetEntityId &&
        a.RawSkillCode == b.RawSkillCode && a.Amount == b.Amount && a.DerivedBaseAmount == b.DerivedBaseAmount &&
        a.TypeRaw == b.TypeRaw && a.ModifierRaw == b.ModifierRaw && a.DirectionRaw == b.DirectionRaw &&
        a.OptionalComponents.SequenceEqual(b.OptionalComponents);
}
