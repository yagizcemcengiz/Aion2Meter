namespace Aion2Meter.Replay.Research;

public sealed class ReplayIdentityDirectory
{
    private readonly Dictionary<ulong, IdentityObservation[]> observations;
    public string CaptureId { get; }

    public ReplayIdentityDirectory(string captureId, IEnumerable<IdentityObservation> observations)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureId);
        CaptureId = captureId;
        var items = observations.ToArray();
        if (items.Any(o => !string.Equals(o.CaptureId, captureId, StringComparison.Ordinal)))
            throw new ArgumentException("Identity directory cannot contain observations from another capture.", nameof(observations));
        this.observations = items.GroupBy(o => o.EntityId).ToDictionary(g => g.Key,
            g => g.OrderBy(o => o.Timestamp).ThenBy(o => o.ObservationId).ToArray());
    }

    public IReadOnlyList<IdentityObservation> GetObservations(ulong entityId) =>
        observations.TryGetValue(entityId, out var items) ? Array.AsReadOnly(items) : Array.Empty<IdentityObservation>();

    // Latest timestamp is exposed, but prior conflicting names are never silently discarded.
    public NameResolution GetLatestPrecedingName(ulong entityId, DateTimeOffset timestamp) =>
        Resolve(GetObservations(entityId).Where(o => o.Timestamp <= timestamp), NameLookupMode.PrecedingOnly);

    public NameResolution GetRetrospectiveSameCaptureNames(ulong entityId) =>
        Resolve(GetObservations(entityId), NameLookupMode.RetrospectiveSameCapture);

    private static NameResolution Resolve(IEnumerable<IdentityObservation> items, NameLookupMode mode)
    {
        var named = items.Where(o => o.Name is not null).ToArray();
        var names = named.Select(o => o.Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var state = names.Length switch { 0 => NameResolutionState.Unknown, 1 => NameResolutionState.Resolved, _ => NameResolutionState.Conflict };
        return new(mode, state, names, named.Select(o => o.ObservationId).ToArray(),
            named.Length == 0 ? null : named.Max(o => o.Timestamp));
    }
}
