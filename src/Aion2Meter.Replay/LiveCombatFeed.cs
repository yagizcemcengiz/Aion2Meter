using Aion2Meter.Core;
using Aion2Meter.Replay.Research;
using System.Security.Cryptography;

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
        public Dictionary<string, string> SeenRecords { get; } = [];
        public bool Poisoned;
        public bool WasResolved;
        public bool Ended;
        public long[] Retired { get; } = new long[2];
    }
    private readonly Dictionary<string, State> states = [];
    public event Action<LiveEpochSnapshot>? EpochObserved;
    public event Action<LiveCombatEvent>? Published;
    public event Action<string>? EpochEnded;
    internal event Action<string, RawProtocolRecord>? RecordPublished;
    internal event Action<LiveEpochSnapshot, RawProtocolRecord>? InitializationWithheld;
    internal event Action<string, DateTimeOffset, string>? ProtocolObserved;
    public long PublishedCount { get; private set; }
    public int RetainedIdentities => states.Values.Sum(s => s.Seen.Count);
    public int RetainedPartyRecordIdentities => states.Values.Sum(s => s.SeenRecords.Count);

    internal void CommitCheckpoint(string epochId, IReadOnlyDictionary<TrafficDirection, long> ends)
    {
        var state = states[epochId];
        if (state.Poisoned) throw new InvalidDataException("Cannot checkpoint invalid publication.");
        foreach (var (direction, end) in ends)
        {
            if (end < state.Retired[(int)direction]) throw new InvalidDataException("Checkpoint cursor moved backwards.");
            state.Retired[(int)direction] = end;
        }
        // Every observed event belongs to the complete committed prefix; transport watermarks replace
        // identities, rather than evicting them by age or arbitrary count.
        state.Seen.Clear(); state.Seen.TrimExcess();
        state.SeenRecords.Clear(); state.SeenRecords.TrimExcess();
    }

    internal bool Observe(LiveEpochSnapshot snapshot, ReplayDamageEventEpochAdapter? adapter,
        IReadOnlyList<RawProtocolRecord>? records = null)
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
        var pendingRecords = new List<(RawProtocolRecord Record, string Location, string Fingerprint)>();
        if (!state.Poisoned && adapter is not null)
        {
            var audit = new ReplayDamageEventBindingAssociator().Analyze(adapter.Events, adapter.Binding, adapter);
            var occurrences = new HashSet<string>();
            foreach (var association in audit.Associations)
            {
                var e = adapter.Events[association.InputIndex];
                if (e.Provenance.OuterFrameOffset < state.Retired[(int)e.Provenance.Direction])
                { state.Poisoned = true; break; }
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
        if (!state.Poisoned && records is not null)
        {
            var occurrences = new HashSet<string>();
            foreach (var r in records.Where(r => r.Direction == TrafficDirection.ServerToClient &&
                r.OpcodeCandidate is "3336" or "4536" or "1B92" or "0892" or "0D92" or "0092" or "1392" or "2192" or "2F92"))
            {
                var location = Location(r);
                var fingerprint = Convert.ToHexString(SHA256.HashData(r.RawBytes));
                if (r.SourceCapture != snapshot.EpochId || r.OuterFrameOffset < state.Retired[(int)r.Direction] || !occurrences.Add(location))
                { state.Poisoned = true; break; }
                if (state.SeenRecords.TryGetValue(location, out var previous))
                { if (previous != fingerprint) { state.Poisoned = true; break; } continue; }
                pendingRecords.Add((r, location, fingerprint));
            }
            if (state.SeenRecords.Keys.Any(key => !occurrences.Contains(key)) ||
                states.Values.Sum(s => s.SeenRecords.Count) + pendingRecords.Count > maximumEventsPerEpoch)
                state.Poisoned = true;
        }
        if (state.Poisoned)
            snapshot = snapshot with { Lifecycle = "Faulted", BindingStatus = CurrentPlayerBindingStatus.Unknown,
                EntityId = null, CharacterName = null, IdentityValidFrom = null, RecoveryMethod = LiveIdentityRecoveryMethod.None,
                Warnings = [.. snapshot.Warnings, "Publication invalidated; discard this epoch's meter. Fresh reconnect required."] };
        state.WasResolved |= snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved;
        EpochObserved?.Invoke(snapshot);
        if (records?.LastOrDefault(r => r.Direction == TrafficDirection.ServerToClient) is { } lastRecord)
            ProtocolObserved?.Invoke(snapshot.EpochId, lastRecord.CompletionUtc, lastRecord.OpcodeCandidate);
        if (state.Poisoned && records is not null)
            foreach (var record in records.Where(r => r.Direction == TrafficDirection.ServerToClient && r.OpcodeCandidate is "1536" or "3336").TakeLast(2))
                InitializationWithheld?.Invoke(snapshot, record);
        if (!state.Poisoned)
        {
            // Membership and combat must advance on one complete-record timeline. Applying the final
            // roster before a batch of past hits would erase valid pre-leave/rejoin contributions.
            var timeline = pending.Select((p, i) => (Record: OrderingRecord(p.Event), IsCombat: true, Index: i))
                .Concat(pendingRecords.Select((p, i) => (p.Record, IsCombat: false, Index: i)))
                .OrderBy(p => p.Record.CompletionUtc).ThenBy(p => p.Record.CompletionPacketIndex)
                .ThenBy(p => p.Record, ResearchRecordArrivalComparer.Instance);
            foreach (var entry in timeline)
            {
                if (!entry.IsCombat)
                {
                    var record = pendingRecords[entry.Index];
                    state.SeenRecords.Add(record.Location, record.Fingerprint);
                    RecordPublished?.Invoke(snapshot.EpochId, record.Record); continue;
                }
                var item = pending[entry.Index];
                state.Seen.Add(item.Location, (item.Fingerprint, item.Association.Status));
                Published?.Invoke(new(snapshot.EpochId, item.Event, item.Association, ++PublishedCount, item.Fingerprint));
            }
        }
        if (state.Poisoned || snapshot.Lifecycle is not ("Active" or "HalfClosed"))
        {
            state.Ended = true;
            state.Seen.Clear(); // Epoch IDs cannot recur; release dedup history once publication ends.
            state.Seen.TrimExcess();
            state.SeenRecords.Clear(); state.SeenRecords.TrimExcess();
            EpochEnded?.Invoke(snapshot.EpochId);
        }
        return !state.Poisoned;
    }

    // RecordId / container record ordinals change when outbound history grows. Locations do not.
    private static string Location(DamageEvent e) => $"{e.Provenance.Direction}/{e.Provenance.OuterFrameOffset}/{e.Provenance.StreamOffset}/" +
        string.Join('/', e.Provenance.ContainerPath.Select(p => p.InnerOffset));
    private static string Location(RawProtocolRecord r) => $"{r.Direction}/{r.OuterFrameOffset}/{r.StreamOffset}/" +
        string.Join('/', r.ContainerPath.Select(p => p.InnerOffset));
    private static RawProtocolRecord OrderingRecord(DamageEvent e)
    {
        var p = e.Provenance;
        return new(p.RecordId, p.CaptureScope, p.Direction, p.StreamOffset, p.OuterFrameId, p.OuterFrameOffset,
            p.Timestamp, p.PacketIndex, p.CompletionPacketIndex, p.CompletionTimestamp, p.PrefixLength,
            p.FrameLength, [], p.RecordTag, p.ContainerPath.Select(c => new ContainerLocation(c.ContainerRecordId, c.InnerOffset)).ToArray(), "Supported", []);
    }

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
