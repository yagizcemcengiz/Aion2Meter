namespace Aion2Meter.Replay.Research;

public static class ResearchGroupComparison
{
    public static DifferentialAssociation Compare(string positiveLabel, IReadOnlyList<ResearchTrial> positives,
        string negativeLabel, IReadOnlyList<ResearchTrial> negatives)
    {
        if (string.IsNullOrWhiteSpace(positiveLabel) || string.IsNullOrWhiteSpace(negativeLabel) || positiveLabel == negativeLabel || positives.Count == 0 || negatives.Count == 0)
            throw new ArgumentException("Comparison needs two distinct labels and nonempty trial groups (empty response trials are valid).");
        var keys = positives.Concat(negatives).Select(t => (t.CaptureId, t.TrialId)).ToArray();
        if (keys.Distinct().Count() != keys.Length) throw new ArgumentException("Repeated/overlapping trial selections would bias the comparison.");
        foreach (var t in positives.Concat(negatives))
            if (t.OrderedRecords.Any(c => c.RawRecord.SourceCapture != t.CaptureId) || t.AuxiliaryEdges.Any(e => e.CaptureId != t.CaptureId))
                throw new ArgumentException("Trial records/edges must remain capture scoped.");
        var positiveSets = positives.Select(t => t.OrderedRecords.Select(c => c.RawSkillCode).ToHashSet()).ToArray();
        var negativeSets = negatives.Select(t => t.OrderedRecords.Select(c => c.RawSkillCode).ToHashSet()).ToArray();
        var positiveUnion = positiveSets.SelectMany(s => s).ToHashSet();
        var negativeUnion = negativeSets.SelectMany(s => s).ToHashSet();
        var positiveCommon = positiveUnion.Where(code => positiveSets.All(s => s.Contains(code))).Order().ToArray();
        var allCodes = positiveUnion.Union(negativeUnion).Order().ToArray();
        var multiplicity = allCodes.Select(code =>
        {
            var p = positives.Select(t => t.OrderedRecords.Count(c => c.RawSkillCode == code)).ToArray();
            var n = negatives.Select(t => t.OrderedRecords.Count(c => c.RawSkillCode == code)).ToArray();
            return new MultiplicityDifference(code, p.Min(), p.Max(), n.Min(), n.Max(), p.Min() > n.Max());
        }).ToArray();
        var ambiguous = allCodes.Where(code =>
            positiveSets.Any(s => s.Contains(code)) && !positiveSets.All(s => s.Contains(code)) ||
            negativeSets.Any(s => s.Contains(code)) && !negativeSets.All(s => s.Contains(code))).ToArray();
        var pp = Patterns(positives); var np = Patterns(negatives);
        if ((long)pp.Count * np.Count > 100_000) throw new InvalidDataException("Ordered pattern-pair limit reached (100000).");
        var differences = np.SelectMany(n => pp.Select(p => Difference(n.OrderedCodes, p.OrderedCodes))).ToArray();
        return new(positiveLabel, negativeLabel, positives.Count, negatives.Count, positiveCommon,
            positiveCommon.Where(code => !negativeUnion.Contains(code)).ToArray(),
            positiveUnion.Intersect(negativeUnion).Order().ToArray(), ambiguous, multiplicity, pp, np, differences,
            Distributions(positives), Distributions(negatives), EvidenceClassification.A,
            ["Derived presence/multiplicity/order facts only; no code is automatically named as a skill, phase, manual input or mechanic.",
             "Positive-only means every positive and no negative. Mixed presence remains ambiguous; nominal proc probabilities are not applied.",
             "Ordered differences use exact subsequence comparison, not value/code normalization; amounts and flags are per-record distributions, never group sums."]);
    }

    private static IReadOnlyList<OrderedPatternObservation> Patterns(IReadOnlyList<ResearchTrial> trials) => trials
        .GroupBy(t => string.Join(',', t.OrderedRecords.Select(r => r.RawSkillCode)), StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new OrderedPatternObservation(g.First().OrderedRecords.Select(r => r.RawSkillCode).ToArray(), g.Count())).ToArray();

    private static OrderedSubsequenceDifference Difference(IReadOnlyList<uint> negative, IReadOnlyList<uint> positive)
    {
        var next = 0; var added = new List<uint>();
        foreach (var code in positive)
            if (next < negative.Count && code == negative[next]) next++;
            else added.Add(code);
        return new(negative, positive, next == negative.Count, next == negative.Count ? added : []);
    }

    private static IReadOnlyList<CodeGroupDistribution> Distributions(IReadOnlyList<ResearchTrial> trials) => trials
        .SelectMany(t => t.OrderedRecords).Select(r => r.RawSkillCode).Distinct().Order().Select(code =>
        {
            var rows = trials.SelectMany(t => t.OrderedRecords.Where(r => r.RawSkillCode == code).Select(r => (t, r))).ToArray();
            var deltas = rows.Select(x => (x.r.RawRecord.TimestampUtc - x.t.AnchorTimestamp).TotalMilliseconds).ToArray();
            var edges = trials.SelectMany(t => t.AuxiliaryEdges.Where(e => e.RawSkillCode == code)).ToArray();
            return new CodeGroupDistribution(code, trials.Count(t => t.OrderedRecords.Any(r => r.RawSkillCode == code)),
                rows.Select(x => x.r.AggregateAmount).ToArray(),
                rows.GroupBy(x => (x.r.TypeCandidate, x.r.ModifierCandidate, x.r.DirectionCandidate)).OrderBy(g => g.Key)
                    .Select(g => new RawFlagCombination(g.Key.TypeCandidate, g.Key.ModifierCandidate, g.Key.DirectionCandidate, g.Count())).ToArray(),
                edges.Select(e => e.Tag).Distinct().Order(StringComparer.Ordinal).ToArray(),
                edges.GroupBy(e => e.Tag).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new AuxiliaryTagCount(g.Key, g.Count(), g.Select(e => (e.CaptureId, e.CombatRecordId)).Distinct().Count())).ToArray(),
                new ObservedRange(deltas.Min(), deltas.Max()));
        }).ToArray();
}
