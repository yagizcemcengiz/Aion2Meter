using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

/// <summary>Pure stateless association; preserves occurrences, with no accounting/deduplication policy.</summary>
public sealed class ReplayDamageEventBindingAssociator
{
    public DamageEventPlayerAssociationAudit Analyze(IEnumerable<DamageEvent> events, CurrentPlayerBinding binding,
        ReplayDamageEventEpochAdapter? epoch)
    {
        ArgumentNullException.ThrowIfNull(events); ArgumentNullException.ThrowIfNull(binding);
        var inputs = events.ToArray();
        foreach (var e in inputs) ArgumentNullException.ThrowIfNull(e);
        var duplicates = inputs.GroupBy(e => e.Identity, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return new(inputs.Select((e, i) => Associate(e, binding, epoch, i, duplicates.Contains(e.Identity))));
    }

    public DamageEventPlayerAssociation Associate(DamageEvent e, CurrentPlayerBinding binding,
        ReplayDamageEventEpochAdapter? epoch, int inputIndex = 0, bool duplicateInput = false)
    {
        ArgumentNullException.ThrowIfNull(e); ArgumentNullException.ThrowIfNull(binding);
        DamageEventPlayerAssociation Result(DamageEventPlayerAssociationStatus status, string reason) =>
            new(inputIndex, e.Identity, status, binding.Status, binding.EntityId, e.SourceEntityId, epoch?.Scope,
                e.Timestamp, binding.ValidFrom, binding.ValidUntil, binding.EvidenceCoverageEnd,
                reason + (duplicateInput ? " Duplicate input identity: occurrence preserved; accounting audit remains separate." : ""), duplicateInput);
        DamageEventPlayerAssociation Unknown(string reason) => Result(DamageEventPlayerAssociationStatus.Unknown, reason);
        if (binding.Status != CurrentPlayerBindingStatus.Resolved) return Unknown("Binding" + binding.Status);
        if (epoch?.Scope is null || epoch.Binding.Status != CurrentPlayerBindingStatus.Resolved) return Unknown("MissingOrAmbiguousEpoch");
        if (binding.Scope != epoch.Scope) return Unknown("ScopeMismatch");
        if (!epoch.Contains(e)) return Unknown("UnsupportedEventProvenance");
        var confirmations = binding.QualifyingEvidence.Where(c => c.RecordTag == "3336").ToArray();
        if (confirmations.Length != 1 || !epoch.Confirms(confirmations[0]) ||
            confirmations[0].EntityId != binding.EntityId || binding.ValidFrom != confirmations[0].CompletionTimestamp)
            return Unknown("UnsupportedConfirmationProvenance");
        var confirm = confirmations[0]; var p = e.Provenance;
        if (binding.EvidenceCoverageEnd is not { } coverage || coverage < binding.ValidFrom ||
            epoch.Binding.EvidenceCoverageEnd is not { } observedEnd || coverage > observedEnd)
            return Unknown("UnsupportedCoverage");
        if (e.Timestamp < binding.ValidFrom || p.CompletionTimestamp < binding.ValidFrom) return Unknown("BeforeConfirmation");
        // Both arrival and full-record availability must fit the inclusive finite evidence window.
        if (e.Timestamp > coverage || p.CompletionTimestamp > coverage) return Unknown("AfterCoverage");
        var until = binding.ValidUntil;
        if (epoch.Binding.ValidUntil is { } actualStop && (until is null || actualStop < until)) until = actualStop;
        if (until is { } stop && (e.Timestamp > stop || p.CompletionTimestamp > stop)) return Unknown("AfterValidUntil");
        if (!epoch.WithinObservedClosure(e)) return Unknown("AfterObservedClosureOrder");
        if (!FollowsConfirmation(p, confirm)) return Unknown("BeforeOrAmbiguousConfirmationOrder");
        return e.SourceEntityId == binding.EntityId
            ? Result(DamageEventPlayerAssociationStatus.Self, "EligibleDirectSourceEqualsBinding")
            : Result(DamageEventPlayerAssociationStatus.Other, "EligibleDirectSourceDiffersFromBinding; ownership unknown.");
    }

    private static bool FollowsConfirmation(DamageEventProvenance e, CurrentPlayerBindingEvidence c)
    {
        // Same inbound stream must advance; arrival timestamp alone cannot order records in one packet/container.
        if (e.Direction != c.Direction || e.OuterFrameOffset < c.OuterFrameOffset || e.StreamOffset < c.StreamOffset) return false;
        if (e.Timestamp == c.CompletionTimestamp && e.PacketIndex < c.CompletionPacketIndex) return false;
        if (e.CompletionTimestamp == c.CompletionTimestamp && e.CompletionPacketIndex < c.CompletionPacketIndex) return false;
        if (e.OuterFrameOffset > c.OuterFrameOffset) return e.StreamOffset > c.StreamOffset;
        if (e.OuterFrameId != c.OuterFrameId) return false;
        var common = Math.Min(e.ContainerPath.Count, c.ContainerPath.Count);
        for (var i = 0; i < common; i++)
        {
            if (e.ContainerPath[i].ContainerRecordId != c.ContainerPath[i].ContainerRecordId) return false;
            if (e.ContainerPath[i].InnerOffset != c.ContainerPath[i].InnerOffset)
                return e.ContainerPath[i].InnerOffset > c.ContainerPath[i].InnerOffset;
        }
        // Equal paths, parent/child ambiguity or identical record location cannot prove a later event.
        return false;
    }
}
