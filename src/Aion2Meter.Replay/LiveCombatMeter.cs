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

public sealed record LiveMeterMemberSnapshot(ulong? EntityId, string CharacterName, bool IsSelf,
    decimal TotalDamage, decimal? Dps, decimal ContributionPercent, long Hits, bool ActivePartyMember,
    string? MembershipKey = null, PlayerClass Class = PlayerClass.Unknown, PlayerClassEvidence? ClassEvidence = null);

/// <summary>Bounded incremental CURRENT accounting. Only Self or independently active party sources extend combat.</summary>
public sealed class LiveCombatMeter
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);
    public const string Coverage = "PARTIAL - 06/26 supported; 0x36 pending";
    private sealed class Epoch
    {
        public required LiveEpochSnapshot Snapshot;
        public required PartyRosterResolver Roster;
        public required PlayerProfileDirectory Profiles;
        public Dictionary<string, (ulong Id, string Name, decimal Total, long Hits, string? Key)> Participants { get; } = [];
        public Dictionary<string, PlayerClassEvidence> StableClasses { get; } = [];
        public string? DiagnosticState;
        public ulong? LastResolvedSelfId;
        public long Self, Other, Unknown, Hits;
        public decimal Total;
        public DateTimeOffset? First, Last;
        public DateTimeOffset? ResetFrom;
        public bool Active, Invalid, Ended;
        public Queue<ulong> Recent { get; } = [];
    }
    private readonly Dictionary<string, Epoch> epochs = [];
    private readonly TimeSpan idleTimeout;
    private long lastPublication;
    private string? authorityState;
    public LiveDiagnosticBuffer Diagnostics { get; } = new();

    public LiveCombatMeter(LiveCombatFeed feed, TimeSpan? idleTimeout = null)
    {
        this.idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (this.idleTimeout < TimeSpan.FromMilliseconds(100) || this.idleTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be 0.1 to 3600 seconds.");
        feed.EpochObserved += Observe;
        feed.Published += Include;
        feed.EpochEnded += End;
        feed.RecordPublished += ObserveRecord;
        feed.ProtocolObserved += (scope, at, tag) => Diagnostics.Protocol(scope, at, tag);
        feed.InitializationWithheld += (snapshot, record) =>
        {
            // Candidate fields explain a rejected refresh; they never become an authoritative binding.
            var candidates = LocalInitializationExtractor.Extract(record)
                .Where(c => record.OpcodeCandidate != "3336" || c.NumericRange.Offset == record.PrefixLength + 2)
                .Take(8).Select(c => new LiveDiagnosticInitializationCandidate(c.EntityId, c.CharacterName,
                    c.NumericRange.Offset, c.NumericRange.Length, c.NameRange.Offset, c.NameRange.Length)).ToArray();
            Diagnostics.Record(record.CompletionUtc, "InitializationWithheld", snapshot,
                "Initialization/refresh withheld by untrusted epoch: " + string.Join("; ", snapshot.Warnings),
                evidence: Evidence(record), priorSelfRuntimeId: epochs.GetValueOrDefault(snapshot.EpochId)?.LastResolvedSelfId,
                initializationCandidates: candidates);
        };
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
            epochs.Add(snapshot.EpochId, epoch = new() { Snapshot = snapshot, Roster = new(snapshot.EpochId), Profiles = new(snapshot.EpochId) });
        }
        var diagnosticState = $"{snapshot.Lifecycle}/{snapshot.BindingStatus}/{snapshot.EntityId}/{snapshot.Gaps}/{snapshot.Conflicts}/" + string.Join(";", snapshot.Warnings);
        if (epoch.DiagnosticState != diagnosticState)
        {
            Diagnostics.Record(DateTimeOffset.UtcNow, "EpochState", snapshot, string.Join("; ", snapshot.Warnings),
                epoch.Roster.Snapshot(), epoch.Roster.Snapshot(), priorSelfRuntimeId: epoch.LastResolvedSelfId);
            epoch.DiagnosticState = diagnosticState;
        }
        epoch.Snapshot = snapshot;
        if (snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved) epoch.LastResolvedSelfId = snapshot.EntityId;
        epoch.Roster.ObserveBinding(snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? snapshot.EntityId : null,
            snapshot.CharacterName, snapshot.IdentityValidFrom);
        if (snapshot.Lifecycle == "Faulted")
        {
            epoch.Invalid = true; epoch.Active = false;
            epoch.Total = 0; epoch.Hits = 0; epoch.First = epoch.Last = null; epoch.Recent.Clear();
            epoch.Participants.Clear();
            var before = epoch.Roster.Snapshot();
            epoch.Roster.SuspendEpoch(DateTimeOffset.UtcNow, "Untrusted transport/binding; runtime suspended, membership evidence retained in retired scope.");
            Diagnostics.Record(DateTimeOffset.UtcNow, "RuntimeSuspended", snapshot, "Faulted epoch; no eligibility or rows until independent fresh authority.", before, epoch.Roster.Snapshot());
            epoch.Profiles.Clear();
        }
    }

    private void ObserveRecord(string id, RawProtocolRecord record)
    {
        if (epochs.TryGetValue(id, out var e) && !e.Invalid && !e.Ended)
        {
            Diagnostics.Protocol(id, record.CompletionUtc, record.OpcodeCandidate);
            var before = e.Roster.Snapshot();
            RememberClasses(e, before);
            if (record.OpcodeCandidate == "3336" && e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved &&
                e.Snapshot.IdentityValidFrom is { } from && record.TimestampUtc > from &&
                LocalInitializationExtractor.Extract(record).Any(x => x.EntityId == e.Snapshot.EntityId && x.CharacterName == e.Snapshot.CharacterName))
            {
                // Feed only publishes accepted, attested refreshes. This changes remote scene ownership,
                // never the Self binding or its checkpoint trust rules.
                e.Roster.RetireRuntime(Evidence(record), "Accepted same-identity 3336 refresh; scene actor evidence retired.");
                e.Profiles.Clear();
            }
            e.Profiles.Observe(record);
            e.Roster.Observe(record, e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? e.Snapshot.EntityId : null,
                e.Snapshot.CharacterName, e.Snapshot.IdentityValidFrom);
            var after = e.Roster.Snapshot();
            RememberClasses(e, after);
            foreach (var key in e.StableClasses.Keys.Where(k => !(after.Memberships ?? []).Any(m => m.Key == k)).ToArray()) e.StableClasses.Remove(key);
            if (record.OpcodeCandidate != "4536" || !(before.Memberships ?? []).SequenceEqual(after.Memberships ?? []))
                Diagnostics.Record(record.CompletionUtc, record.OpcodeCandidate == "3336" ? (e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? "AcceptedInitialization" : "InitializationAwaitingBinding") : "PartyEvidence",
                    e.Snapshot, after.Diagnostic ?? (record.OpcodeCandidate == "0092" ? "Authoritative full replacement." :
                        record.OpcodeCandidate == "1392" ? "Authoritative disband." : "Independent party/profile evidence."), before, after, Evidence(record));
        }
    }

    private void Include(LiveCombatEvent published)
    {
        if (published.PublicationSequence <= lastPublication) return;
        lastPublication = published.PublicationSequence;
        if (!epochs.TryGetValue(published.EpochId, out var epoch) || epoch.Invalid) return;
        Diagnostics.Protocol(published.EpochId, published.DamageEvent.Provenance.CompletionTimestamp, published.DamageEvent.Provenance.RecordTag);
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
        if (epoch.ResetFrom is { } reset && published.DamageEvent.Provenance.CompletionTimestamp < reset) return;
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
        var participantKey = member?.MembershipKey ?? member?.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!self && !epoch.Participants.ContainsKey(participantKey!) && epoch.Participants.Count >= 15) return;
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
            epoch.Participants.TryGetValue(participantKey!, out var current);
            epoch.Participants[participantKey!] = (member!.EntityId, member.CharacterName, current.Total + published.DamageEvent.Amount, current.Hits + 1, member.MembershipKey);
        }
    }

    private void End(string id)
    {
        if (epochs.TryGetValue(id, out var epoch))
        {
            epoch.Active = false; epoch.Ended = true;
            var before = epoch.Roster.Snapshot();
            epoch.Roster.SuspendEpoch(DateTimeOffset.UtcNow, "Transport scope ended; no cross-epoch inheritance by name.");
            Diagnostics.Record(DateTimeOffset.UtcNow, "EpochEnded", epoch.Snapshot, epoch.Snapshot.Lifecycle, before, epoch.Roster.Snapshot());
            epoch.Profiles.Clear();
        }
    }

    /// <summary>Explicit user reset, called on the ingestion owner thread. Identity, roster and deduplication survive.</summary>
    public void ResetCurrent(DateTimeOffset from)
    {
        foreach (var epoch in epochs.Values.Where(e => !e.Ended))
        {
            Diagnostics.Record(from, "CurrentReset", epoch.Snapshot, "Combat only; membership and runtime unchanged.", epoch.Roster.Snapshot(), epoch.Roster.Snapshot());
            epoch.Total = 0; epoch.Hits = 0; epoch.First = epoch.Last = null;
            epoch.Active = false; epoch.Recent.Clear(); epoch.Participants.Clear();
            epoch.ResetFrom = epoch.ResetFrom is { } prior && prior > from ? prior : from;
        }
    }

    public LiveMeterSnapshot Snapshot(DateTimeOffset now)
    {
        foreach (var e in epochs.Values)
            if (e.Active && e.Last is { } last && now - last >= idleTimeout) e.Active = false;
        // No arbitrary choice between two simultaneously resolved player scopes.
        var candidates = epochs.Values.Where(e => !e.Ended && e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved).ToArray();
        if (candidates.Length > 1)
        {
            var state = "Ambiguous: " + string.Join(", ", candidates.Select(e => e.Snapshot.EpochId));
            if (authorityState != state) Diagnostics.Record(now, "AuthorityAmbiguous", candidates[0].Snapshot, state);
            authorityState = state;
            return Empty("AMBIGUOUS EPOCHS - Self meter paused");
        }
        var epoch = candidates.SingleOrDefault() ?? epochs.Values.LastOrDefault(e => !e.Ended) ?? epochs.Values.LastOrDefault();
        if (epoch is null) return Empty("Waiting for fresh character identity...");
        var s = epoch.Snapshot;
        var selectedState = s.EpochId + "/" + s.BindingStatus + "/" + epoch.Invalid + "/" + epoch.Ended;
        if (authorityState != selectedState)
        {
            Diagnostics.Record(now, "AuthoritySelected", s, "Previous authority: " + (authorityState ?? "none") + "; selected " + selectedState,
                after: epoch.Roster.Snapshot());
            authorityState = selectedState;
        }
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
            var membershipByKey = (roster.Memberships ?? []).ToDictionary(m => m.Key);
            foreach (var key in epoch.Participants.Keys.Concat(membershipByKey.Keys).Distinct())
            {
                epoch.Participants.TryGetValue(key, out var p);
                membershipByKey.TryGetValue(key, out var membership);
                var actor = membership?.CurrentRuntimeEntityId;
                members.Add(new(membership is null ? p.Id : actor, membership?.CharacterName ?? p.Name, false,
                    p.Total, p.Hits == 0 ? null : Rate(p.Total), Contribution(p.Total, false), p.Hits, membership is not null,
                    membership?.Key ?? p.Key));
            }
        }
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var profile = member.EntityId is { } id ? epoch.Profiles.Get(id, member.CharacterName, member.IsSelf, member.IsSelf ? s.IdentityValidFrom : null) : null;
            if (!member.IsSelf && member.ActivePartyMember && member.MembershipKey is { } key && epoch.StableClasses.TryGetValue(key, out var stable)) profile = stable;
            members[i] = member with { Class = profile?.Class ?? PlayerClass.Unknown, ClassEvidence = profile };
        }
        return new(s.EpochId, s.CharacterName, s.EntityId, s.BindingStatus, status, epoch.Total, elapsed,
            elapsed >= 0.001 && !epoch.Invalid ? epoch.Total / (decimal)elapsed : null,
            epoch.Hits, epoch.Self, epoch.Other, epoch.Unknown, epoch.Recent.Reverse().ToArray(), Coverage,
            s.Gaps, s.Conflicts, s.DuplicateSegments, s.OverlapBytes, s.UnsupportedCandidates, s.Warnings,
            s.RecoveryMethod, s.IdentityValidFrom, Array.AsReadOnly(members.ToArray()), groupTotal, roster);
    }

    private static PartyMembershipEvidence Evidence(RawProtocolRecord r) => new(r.SourceCapture, r.OpcodeCandidate,
        r.OuterFrameOffset, r.StreamOffset, r.CompletionPacketIndex, r.CompletionUtc,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(r.RawBytes)));

    private static void RememberClasses(Epoch epoch, PartyRosterSnapshot roster)
    {
        foreach (var member in roster.ActiveMembers.Where(m => m.MemberUuid is not null && m.OpaqueToken is not null && m.MembershipKey is not null))
        {
            var profile = epoch.Profiles.Get(member.EntityId, member.CharacterName, false);
            if (profile is null) continue;
            var key = member.MembershipKey!;
            if (epoch.StableClasses.TryGetValue(key, out var prior))
            {
                if (prior.Conflict) continue;
                if (prior.Class != profile.Class || profile.Conflict) epoch.StableClasses[key] = profile with { Class = PlayerClass.Unknown, Conflict = true };
            }
            else if (epoch.StableClasses.Count < 5) epoch.StableClasses[key] = profile;
        }
    }

    private static LiveMeterSnapshot Empty(string status) => new(null, null, null, CurrentPlayerBindingStatus.Unknown,
        status, 0, 0, null, 0, 0, 0, 0, [], Coverage, 0, 0, 0, 0, 0, []);
}
