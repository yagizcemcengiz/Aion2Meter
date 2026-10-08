using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public sealed record LiveMeterSnapshot(string? EpochId, string? CharacterName, ulong? EntityId,
    CurrentPlayerBindingStatus BindingStatus, string Status, decimal TotalDamage, double EncounterElapsedSeconds,
    decimal? Dps, long EncounterSelfHits, long SelfCount, long OtherCount, long UnknownCount,
    IReadOnlyList<ulong> RecentSelfAmounts, string Coverage, int Gaps, int Conflicts, int DuplicateSegments,
    long OverlapBytes, int UnsupportedCandidates, IReadOnlyList<string> Warnings,
    LiveIdentityRecoveryMethod RecoveryMethod = LiveIdentityRecoveryMethod.None, DateTimeOffset? IdentityValidFrom = null,
    IReadOnlyList<LiveMeterMemberSnapshot>? Members = null, decimal GroupTotalDamage = 0, PartyRosterSnapshot? PartyRoster = null);

public sealed record LiveMeterMemberSnapshot(ulong EntityId, string CharacterName, bool IsSelf,
    decimal TotalDamage, decimal? Dps, decimal ContributionPercent, long Hits, bool ActivePartyMember);

/// <summary>Bounded incremental CURRENT accounting. Only Self or independently active party sources extend combat.</summary>
public sealed class LiveCombatMeter
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);
    public const string Coverage = "PARTIAL - 06/26 supported; 0x36 pending";
    private sealed class Epoch
    {
        public required LiveEpochSnapshot Snapshot;
        public required PartyRosterResolver Roster;
        public Dictionary<ulong, (string Name, decimal Total, long Hits)> Participants { get; } = [];
        public long Self, Other, Unknown, Hits;
        public decimal Total;
        public DateTimeOffset? First, Last;
        public bool Active, Invalid, Ended;
        public Queue<ulong> Recent { get; } = [];
    }
    private readonly Dictionary<string, Epoch> epochs = [];
    private readonly TimeSpan idleTimeout;
    private long lastPublication;

    public LiveCombatMeter(LiveCombatFeed feed, TimeSpan? idleTimeout = null)
    {
        this.idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (this.idleTimeout < TimeSpan.FromMilliseconds(100) || this.idleTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be 0.1 to 3600 seconds.");
        feed.EpochObserved += Observe;
        feed.Published += Include;
        feed.EpochEnded += End;
        feed.RecordPublished += ObserveRecord;
    }

    private void Observe(LiveEpochSnapshot snapshot)
    {
        if (!epochs.TryGetValue(snapshot.EpochId, out var epoch))
        {
            if (epochs.Count >= 16)
            {
                var old = epochs.FirstOrDefault(p => p.Value.Ended);
                if (old.Key is null) throw new InvalidDataException("Live meter epoch bound reached.");
                epochs.Remove(old.Key);
            }
            epochs.Add(snapshot.EpochId, epoch = new() { Snapshot = snapshot, Roster = new(snapshot.EpochId) });
        }
        epoch.Snapshot = snapshot;
        epoch.Roster.ObserveBinding(snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? snapshot.EntityId : null,
            snapshot.CharacterName, snapshot.IdentityValidFrom);
        if (snapshot.Lifecycle == "Faulted")
        {
            epoch.Invalid = true; epoch.Active = false;
            epoch.Total = 0; epoch.Hits = 0; epoch.First = epoch.Last = null; epoch.Recent.Clear();
            epoch.Participants.Clear(); epoch.Roster.EndEpoch();
        }
    }

    private void ObserveRecord(string id, RawProtocolRecord record)
    {
        if (epochs.TryGetValue(id, out var e) && !e.Invalid && !e.Ended)
            e.Roster.Observe(record, e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? e.Snapshot.EntityId : null,
                e.Snapshot.CharacterName, e.Snapshot.IdentityValidFrom);
    }

    private void Include(LiveCombatEvent published)
    {
        if (published.PublicationSequence <= lastPublication) return;
        lastPublication = published.PublicationSequence;
        if (!epochs.TryGetValue(published.EpochId, out var epoch) || epoch.Invalid) return;
        var self = published.Association.Status == DamageEventPlayerAssociationStatus.Self;
        PartyMemberIdentity? member = null;
        switch (published.Association.Status)
        {
            case DamageEventPlayerAssociationStatus.Other:
                epoch.Other++;
                member = epoch.Roster.Eligible(published.DamageEvent.SourceEntityId, published.DamageEvent.Timestamp,
                    published.DamageEvent.Provenance.CompletionTimestamp);
                if (member is null) return;
                break;
            case DamageEventPlayerAssociationStatus.Unknown: epoch.Unknown++; return;
        }
        if (self) epoch.Self++;
        // Completion is the first defensible availability time. A reordered frame can have an earlier
        // first-byte timestamp; logical combat time never runs backwards within a publication scope.
        var time = published.DamageEvent.Provenance.CompletionTimestamp;
        if (epoch.Last is { } prior && time < prior) time = prior;
        if (epoch.First is null || epoch.Last is { } last && time - last >= idleTimeout)
        {
            epoch.Total = 0; epoch.Hits = 0; epoch.Recent.Clear(); epoch.First = time;
            epoch.Participants.Clear();
        }
        // Bound distinct participants, including frozen former members, without dropping already
        // counted damage or evicting evidence by an invented age. The next encounter releases them.
        if (!self && !epoch.Participants.ContainsKey(member!.EntityId) && epoch.Participants.Count >= 15) return;
        epoch.Active = true; epoch.Last = time;
        if (self)
        {
            epoch.Total += published.DamageEvent.Amount; // OptionalComponents are already inside Amount.
            epoch.Hits++;
            epoch.Recent.Enqueue(published.DamageEvent.Amount);
            while (epoch.Recent.Count > 5) epoch.Recent.Dequeue();
        }
        else
        {
            epoch.Participants.TryGetValue(member!.EntityId, out var current);
            epoch.Participants[member.EntityId] = (member.CharacterName, current.Total + published.DamageEvent.Amount, current.Hits + 1);
        }
    }

    private void End(string id)
    {
        if (epochs.TryGetValue(id, out var epoch)) { epoch.Active = false; epoch.Ended = true; epoch.Roster.EndEpoch(); }
    }

    public LiveMeterSnapshot Snapshot(DateTimeOffset now)
    {
        foreach (var e in epochs.Values)
            if (e.Active && e.Last is { } last && now - last >= idleTimeout) e.Active = false;
        // No arbitrary choice between two simultaneously resolved player scopes.
        var candidates = epochs.Values.Where(e => !e.Ended && e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved).ToArray();
        if (candidates.Length > 1) return Empty("AMBIGUOUS EPOCHS - Self meter paused");
        var epoch = candidates.SingleOrDefault() ?? epochs.Values.LastOrDefault(e => !e.Ended) ?? epochs.Values.LastOrDefault();
        if (epoch is null) return Empty("Waiting for fresh character identity...");
        var s = epoch.Snapshot;
        var elapsed = epoch.First is { } first && epoch.Last is { } stop ? Math.Max(0, (stop - first).TotalSeconds) : 0;
        var status = epoch.Invalid ? "UNTRUSTED - reconnect required" : epoch.Ended ? "STOPPED" :
            s.BindingStatus == CurrentPlayerBindingStatus.Conflict ? "CONFLICT - Self meter paused" :
            s.BindingStatus != CurrentPlayerBindingStatus.Resolved ? s.RecoveryMethod == LiveIdentityRecoveryMethod.WaitingForFreshEpoch
                ? "Waiting for identity... next fresh game connection" : "Waiting for fresh character identity..." :
            epoch.Active ? "IN COMBAT" : epoch.First is null ? "READY" : "IDLE - last encounter";
        var roster = epoch.Roster.Snapshot();
        var groupTotal = epoch.Total + epoch.Participants.Values.Sum(p => p.Total);
        decimal? Rate(decimal amount) => elapsed >= 0.001 && !epoch.Invalid ? amount / (decimal)elapsed : null;
        decimal Contribution(decimal amount, bool self) => groupTotal > 0 ? amount / groupTotal * 100m : self ? 100m : 0m;
        var members = new List<LiveMeterMemberSnapshot>();
        if (s.BindingStatus == CurrentPlayerBindingStatus.Resolved && s.EntityId is { } selfId && s.CharacterName is { } selfName && !epoch.Invalid)
        {
            members.Add(new(selfId, selfName, true, epoch.Total, Rate(epoch.Total), Contribution(epoch.Total, true), epoch.Hits, false));
            foreach (var id in epoch.Participants.Keys.Concat(roster.ActiveMembers.Select(m => m.EntityId)).Distinct())
            {
                epoch.Participants.TryGetValue(id, out var p);
                var active = roster.ActiveMembers.FirstOrDefault(m => m.EntityId == id);
                members.Add(new(id, active?.CharacterName ?? p.Name, false, p.Total, Rate(p.Total), Contribution(p.Total, false), p.Hits, active is not null));
            }
        }
        return new(s.EpochId, s.CharacterName, s.EntityId, s.BindingStatus, status, epoch.Total, elapsed,
            elapsed >= 0.001 && !epoch.Invalid ? epoch.Total / (decimal)elapsed : null,
            epoch.Hits, epoch.Self, epoch.Other, epoch.Unknown, epoch.Recent.Reverse().ToArray(), Coverage,
            s.Gaps, s.Conflicts, s.DuplicateSegments, s.OverlapBytes, s.UnsupportedCandidates, s.Warnings,
            s.RecoveryMethod, s.IdentityValidFrom, Array.AsReadOnly(members.ToArray()), groupTotal, roster);
    }

    private static LiveMeterSnapshot Empty(string status) => new(null, null, null, CurrentPlayerBindingStatus.Unknown,
        status, 0, 0, null, 0, 0, 0, 0, [], Coverage, 0, 0, 0, 0, 0, []);
}
