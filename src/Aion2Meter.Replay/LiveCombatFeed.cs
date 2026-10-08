using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public sealed record LiveCombatEvent(string EpochId, DamageEvent DamageEvent,
    DamageEventPlayerAssociation Association, long PublicationSequence, string PublicationIdentity)
{
    public DateTimeOffset SourceTimestamp => DamageEvent.Timestamp;
}

/// <summary>Single-consumer publication after shared decoding. No transport parser or gameplay model.</summary>
public sealed class LiveCombatFeed(int maximumEventsPerEpoch = 100_000, int maximumEpochs = 16)
{
    private sealed class State
    {
        public Dictionary<string, (string Fingerprint, DamageEventPlayerAssociationStatus Status)> Seen { get; } = [];
        public bool Poisoned;
        public bool WasResolved;
        public bool Ended;
    }
    private readonly Dictionary<string, State> states = [];
    public event Action<LiveEpochSnapshot>? EpochObserved;
    public event Action<LiveCombatEvent>? Published;
    public event Action<string>? EpochEnded;
    public long PublishedCount { get; private set; }
    public int RetainedIdentities => states.Values.Sum(s => s.Seen.Count);

    internal bool Observe(LiveEpochSnapshot snapshot, ReplayDamageEventEpochAdapter? adapter)
    {
        if (!states.TryGetValue(snapshot.EpochId, out var state))
        {
            if (states.Count >= maximumEpochs)
            {
                var ended = states.FirstOrDefault(p => p.Value.Ended);
                if (ended.Key is null) throw new InvalidDataException("Live publication epoch bound reached.");
                states.Remove(ended.Key);
            }
            states.Add(snapshot.EpochId, state = new());
        }
        if (state.Ended) return !state.Poisoned;
        var invalid = snapshot.Lifecycle == "Faulted" || state.WasResolved && snapshot.BindingStatus != CurrentPlayerBindingStatus.Resolved;
        state.Poisoned |= invalid;
        var pending = new List<(DamageEvent Event, DamageEventPlayerAssociation Association, string Location, string Fingerprint)>();
        if (!state.Poisoned && adapter is not null)
        {
            var audit = new ReplayDamageEventBindingAssociator().Analyze(adapter.Events, adapter.Binding, adapter);
            var occurrences = new HashSet<string>();
            foreach (var association in audit.Associations)
            {
                var e = adapter.Events[association.InputIndex];
                var location = Location(e);
                var fingerprint = CanonicalIdentity(e);
                if (e.Provenance.CaptureScope != snapshot.EpochId || !occurrences.Add(location)) { state.Poisoned = true; break; }
                if (state.Seen.TryGetValue(location, out var prior))
                {
                    if (prior.Fingerprint != fingerprint || prior.Status != association.Status) { state.Poisoned = true; break; }
                    continue;
                }
                pending.Add((e, association, location, fingerprint));
            }
            // Previously committed locations cannot disappear from a clean prefix recomputation.
            if (state.Seen.Keys.Any(key => !occurrences.Contains(key)) || state.Seen.Count + pending.Count > maximumEventsPerEpoch ||
                RetainedIdentities + pending.Count > maximumEventsPerEpoch)
                state.Poisoned = true;
        }
        if (state.Poisoned)
            snapshot = snapshot with { Lifecycle = "Faulted", BindingStatus = CurrentPlayerBindingStatus.Unknown,
                EntityId = null, CharacterName = null,
                Warnings = [.. snapshot.Warnings, "Publication invalidated; discard this epoch's meter. Fresh reconnect required."] };
        state.WasResolved |= snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved;
        EpochObserved?.Invoke(snapshot);
        if (!state.Poisoned)
            foreach (var item in pending)
            {
                state.Seen.Add(item.Location, (item.Fingerprint, item.Association.Status));
                Published?.Invoke(new(snapshot.EpochId, item.Event, item.Association, ++PublishedCount, item.Fingerprint));
            }
        if (state.Poisoned || snapshot.Lifecycle is not ("Active" or "HalfClosed"))
        {
            state.Ended = true;
            state.Seen.Clear(); // Epoch IDs cannot recur; release dedup history once publication ends.
            state.Seen.TrimExcess();
            EpochEnded?.Invoke(snapshot.EpochId);
        }
        return !state.Poisoned;
    }

    // RecordId / container record ordinals change when outbound history grows. Locations do not.
    private static string Location(DamageEvent e) => $"{e.Provenance.Direction}/{e.Provenance.OuterFrameOffset}/{e.Provenance.StreamOffset}/" +
        string.Join('/', e.Provenance.ContainerPath.Select(p => p.InnerOffset));

    private static string CanonicalIdentity(DamageEvent e)
    {
        var p = e.Provenance;
        // Reuse the versioned DamageEvent provenance identity encoding with decode ordinals normalized.
        // The original immutable event and its original Identity are published unchanged.
        var stable = new DamageEventProvenance(p.CaptureScope, 1, p.Direction, p.StreamOffset, 1,
            p.OuterFrameOffset, p.Timestamp, p.PacketIndex, p.CompletionPacketIndex, p.CompletionTimestamp,
            p.PrefixLength, p.FrameLength, p.RecordTag,
            p.ContainerPath.Select((c, i) => new DamageContainerLocation(i + 1, c.InnerOffset)));
        return stable.Identity + $"/{e.SourceEntityId}/{e.TargetEntityId}/{e.RawSkillCode}/{e.Amount}/{e.DerivedBaseAmount}/{e.TypeRaw}/{e.ModifierRaw}/{e.DirectionRaw}/" +
            string.Join(',', e.OptionalComponents);
    }
}
