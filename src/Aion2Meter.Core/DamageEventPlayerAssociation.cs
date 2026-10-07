namespace Aion2Meter.Core;

public enum DamageEventPlayerAssociationStatus { Unknown, Self, Other }

/// <summary>Immutable overlay for one input occurrence; no damage totals or ownership semantics.</summary>
public sealed record DamageEventPlayerAssociation(int InputIndex, string EventIdentity,
    DamageEventPlayerAssociationStatus Status, CurrentPlayerBindingStatus BindingStatus,
    ulong? BindingEntityId, ulong EventSourceEntityId, ReplayConnectionScope? Scope,
    DateTimeOffset EventTimestamp, DateTimeOffset? BindingValidFrom, DateTimeOffset? BindingValidUntil,
    DateTimeOffset? EvidenceCoverageEnd, string Diagnostic, bool DuplicateInput);

public sealed class DamageEventPlayerAssociationAudit
{
    public int InputEventCount => Associations.Count;
    public int SelfCount { get; }
    public int OtherCount { get; }
    public int UnknownCount { get; }
    public IReadOnlyList<DamageEventPlayerAssociation> Associations { get; }

    public DamageEventPlayerAssociationAudit(IEnumerable<DamageEventPlayerAssociation> associations)
    {
        ArgumentNullException.ThrowIfNull(associations);
        Associations = Array.AsReadOnly(associations.ToArray());
        SelfCount = Associations.Count(a => a.Status == DamageEventPlayerAssociationStatus.Self);
        OtherCount = Associations.Count(a => a.Status == DamageEventPlayerAssociationStatus.Other);
        UnknownCount = Associations.Count(a => a.Status == DamageEventPlayerAssociationStatus.Unknown);
    }
}
