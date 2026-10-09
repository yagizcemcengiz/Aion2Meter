using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public sealed record LiveMeterSnapshot(string? EpochId, string? CharacterName, ulong? EntityId,
    CurrentPlayerBindingStatus BindingStatus, string Status, decimal TotalDamage, double EncounterElapsedSeconds,
    decimal? Dps, long EncounterSelfHits, long SelfCount, long OtherCount, long UnknownCount,
    IReadOnlyList<ulong> RecentSelfAmounts, string Coverage, int Gaps, int Conflicts, int DuplicateSegments,
    long OverlapBytes, int UnsupportedCandidates, IReadOnlyList<string> Warnings,
    LiveIdentityRecoveryMethod RecoveryMethod = LiveIdentityRecoveryMethod.None, DateTimeOffset? IdentityValidFrom = null,
    IReadOnlyList<LiveMeterMemberSnapshot>? Members = null, decimal GroupTotalDamage = 0, PartyRosterSnapshot? PartyRoster = null,
    StableCharacterIdentity? StableIdentity = null, IReadOnlyList<PartyMemberStableIdentity>? StablePartyIdentities = null,
    IReadOnlyList<LiveMeterMemberSnapshot>? CurrentMembers = null,
    PartyAuthorityState CurrentPartyAuthority = PartyAuthorityState.Unknown);

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
        public ulong? PublishedSelfProfileId;
        public long Initialization1536Count, Initialization3336Count;
        public long AppliedPartyReplacementRevision;
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
    private StableCharacterIdentity? stableSelf;
    private ulong? lastSelectedRuntimeId;
    private sealed record StablePartyRow(PartyMemberStableIdentity Identity, string SourceEpoch)
    {
        public string Name => Identity.CharacterName;
        public PlayerClass Class => Identity.Class;
    }
    private readonly Dictionary<string, StablePartyRow> stableParty = [];
    private readonly Dictionary<string, string> partyRowKeys = [];
    private StableCharacterIdentity? partySelf;
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
        feed.UnsupportedCombatObserved += (scope, candidate) =>
        {
            if (!epochs.TryGetValue(scope, out var epoch) || epoch.Invalid || epoch.Ended) return;
            Diagnostics.CombatAttribution(new(scope, candidate.RawRecord.CompletionUtc, candidate.RawRecord.OpcodeCandidate,
                candidate.CategoryOrSwitch, candidate.ActorIdCandidate, candidate.TargetIdCandidate, null,
                "Unknown", null, epoch.Roster.ExpectedRuntimeIds, "UnsupportedCombatVariant"));
        };
        feed.InitializationWithheld += (snapshot, record) =>
        {
            // Candidate fields explain a rejected refresh; they never become an authoritative binding.
            var candidates = InitializationCandidates(record, "Withheld by transport/publication trust");
            Diagnostics.Record(record.CompletionUtc, "InitializationWithheld", snapshot,
                "Initialization/refresh withheld by untrusted epoch: " + string.Join("; ", snapshot.Warnings),
                evidence: Evidence(record), priorSelfRuntimeId: epochs.GetValueOrDefault(snapshot.EpochId)?.LastResolvedSelfId ?? lastSelectedRuntimeId,
                initializationCandidates: candidates, profileLayout: ProfileDiagnostic(record));
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
        var diagnosticState = $"{snapshot.Lifecycle}/{snapshot.BindingStatus}/{snapshot.EntityId}/{snapshot.Gaps}/{snapshot.Conflicts}/" + string.Join(";", snapshot.Warnings) +
            string.Join(";", (snapshot.CheckpointStates ?? []).Select(p => $"{p.Direction}/{p.State}/{p.PrefixState}/{p.ContainerState}/{p.ResolvedWaiting}"));
        if (epoch.DiagnosticState != diagnosticState)
        {
            Diagnostics.Record(DateTimeOffset.UtcNow, "EpochState", snapshot, string.Join("; ", snapshot.Warnings),
                epoch.Roster.Snapshot(), epoch.Roster.Snapshot(), priorSelfRuntimeId: epoch.LastResolvedSelfId ?? lastSelectedRuntimeId,
                initialization1536Count: epoch.Initialization1536Count, initialization3336Count: epoch.Initialization3336Count,
                stableIdentity: snapshot.StableIdentity ?? stableSelf);
            epoch.DiagnosticState = diagnosticState;
        }
        epoch.Snapshot = snapshot;
        if (snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved)
        {
            epoch.LastResolvedSelfId = snapshot.EntityId;
        }
        epoch.Roster.ObserveBinding(snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? snapshot.EntityId : null,
            snapshot.CharacterName, snapshot.IdentityValidFrom, snapshot.StableIdentity?.ServerId);
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
            if (record.OpcodeCandidate == "4536")
            {
                var inspection = PlayerProfileDecoder.Inspect(record);
                var identity = IdentityRecordDecoder.Decode(record).Identities.FirstOrDefault();
                Diagnostics.RemoteProfile(new(id, record.CompletionUtc, identity?.EntityId, inspection.Marker,
                    identity?.EvidenceType == IdentityEvidenceType.NameEnvelope4536, inspection.Profile is not null,
                    inspection.ClassCode, inspection.Decision));
            }
            var before = e.Roster.Snapshot();
            if (record.OpcodeCandidate is "1536" or "3336")
            {
                if (record.OpcodeCandidate == "1536") e.Initialization1536Count++;
                else e.Initialization3336Count++;
            }
            RememberClasses(e, before);
            var localProfile = ReplayCurrentPlayerBindingResolver.SelfProfile(record);
            if (record.OpcodeCandidate == "3336" &&
                (e.Snapshot.AwaitingActor && e.PublishedSelfProfileId is not null ||
                e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved &&
                (localProfile is not null && e.PublishedSelfProfileId is not null ||
                e.Snapshot.IdentityValidFrom is { } from && record.TimestampUtc > from &&
                LocalInitializationExtractor.Extract(record).Any(x => x.EntityId == e.Snapshot.EntityId && x.CharacterName == e.Snapshot.CharacterName))))
            {
                // Feed only publishes accepted, attested refreshes. This changes remote scene ownership,
                // never the Self binding or its checkpoint trust rules.
                e.Roster.RetireRuntime(Evidence(record), "Accepted local 3336 refresh; scene actor evidence retired.");
                e.Profiles.Clear();
            }
            if (localProfile is not null) e.PublishedSelfProfileId = localProfile.Value.Evidence.EntityId;
            e.Profiles.Observe(record);
            e.Roster.Observe(record, e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? e.Snapshot.EntityId : null,
                e.Snapshot.CharacterName, e.Snapshot.IdentityValidFrom, e.Snapshot.StableIdentity?.ServerId);
            var after = e.Roster.Snapshot();
            RememberClasses(e, after);
            foreach (var key in e.StableClasses.Keys.Where(k => !(after.Memberships ?? []).Any(m => m.Key == k)).ToArray()) e.StableClasses.Remove(key);
            var acceptedInitialization = e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved &&
                (localProfile is not null || e.Snapshot.StableIdentity is null);
            if (record.OpcodeCandidate is "1536" or "3336")
                Diagnostics.Record(record.CompletionUtc, record.OpcodeCandidate == "3336" ? (acceptedInitialization ? "AcceptedInitialization" : "InitializationAwaitingBinding") : "InitializationCandidate",
                    e.Snapshot, string.Join("; ", e.Snapshot.Warnings), before, after, Evidence(record), priorSelfRuntimeId: e.LastResolvedSelfId ?? lastSelectedRuntimeId,
                    initializationCandidates: InitializationCandidates(record, acceptedInitialization ? "Accepted local profile/pair; runtime interval limited by later initialization boundaries" : "This record grants no runtime authority; await fixed local profile"),
                    initialization1536Count: e.Initialization1536Count, initialization3336Count: e.Initialization3336Count,
                    stableIdentity: e.Snapshot.StableIdentity ?? stableSelf, profileLayout: ProfileDiagnostic(record));
            else if (record.OpcodeCandidate != "4536" || !(before.Memberships ?? []).SequenceEqual(after.Memberships ?? []))
                Diagnostics.Record(record.CompletionUtc, "PartyEvidence",
                    e.Snapshot, after.LastLayout?.RejectInvariant ?? after.LastLayout?.AuthorityDecision ?? after.Diagnostic ?? (record.OpcodeCandidate == "0092" ? "Authoritative full replacement." :
                        record.OpcodeCandidate == "1392" ? "Authoritative disband." : "Independent party/profile evidence."), before, after, Evidence(record), partyLayout: after.LastLayout);
        }
    }

    private void Include(LiveCombatEvent published)
    {
        if (published.PublicationSequence <= lastPublication) return;
        lastPublication = published.PublicationSequence;
        if (!epochs.TryGetValue(published.EpochId, out var epoch) || epoch.Invalid) return;
        Diagnostics.Protocol(published.EpochId, published.DamageEvent.Provenance.CompletionTimestamp, published.DamageEvent.Provenance.RecordTag);
        Diagnostics.CombatTarget(published.DamageEvent, published.Association.Status.ToString());
        var self = published.Association.Status == DamageEventPlayerAssociationStatus.Self;
        PartyMemberIdentity? member = null;
        var proof = epoch.Roster.InspectActor(published.DamageEvent.SourceEntityId, published.DamageEvent.Timestamp,
            published.DamageEvent.Provenance.CompletionTimestamp);
        void Decision(string reason, string classification)
        {
            var key = proof.Membership?.Key ?? proof.Actor?.MembershipKey;
            Diagnostics.CombatAttribution(new(published.EpochId, published.DamageEvent.Provenance.CompletionTimestamp,
                published.DamageEvent.Provenance.RecordTag,
                // The frozen accepted grammar requires a nonempty component list in 26,
                // and none in 06. This is derived from a supported projection only.
                published.DamageEvent.OptionalComponents.Count > 0 ? 0x26UL : 0x06UL,
                published.DamageEvent.SourceEntityId, published.DamageEvent.TargetEntityId, published.DamageEvent.Amount,
                classification, key is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
                epoch.Roster.ExpectedRuntimeIds, reason, self ? epoch.Snapshot.IdentityValidFrom : proof.Actor?.ValidFrom));
        }
        switch (published.Association.Status)
        {
            case DamageEventPlayerAssociationStatus.Other:
                epoch.Other++;
                member = epoch.Roster.Eligible(published.DamageEvent.SourceEntityId, published.DamageEvent.Timestamp,
                    published.DamageEvent.Provenance.CompletionTimestamp);
                if (member is null) { Decision(proof.Decision, "Other"); return; }
                break;
            case DamageEventPlayerAssociationStatus.Unknown: epoch.Unknown++; Decision("UnknownSelfAuthority", "Unknown"); return;
        }
        if (self) epoch.Self++;
        if (epoch.ResetFrom is { } reset && published.DamageEvent.Provenance.CompletionTimestamp < reset)
        { Decision("BeforeCurrentReset", self ? "Self" : "ValidatedParty"); return; }
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
        if (!self && !epoch.Participants.ContainsKey(participantKey!) && epoch.Participants.Count >= 15)
        { Decision("EncounterParticipantBound", "ValidatedParty"); return; }
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
        Decision("Counted", self ? "Self" : "ValidatedParty");
    }

    private void End(string id)
    {
        if (epochs.TryGetValue(id, out var epoch))
        {
            epoch.Active = false; epoch.Ended = true;
            var before = epoch.Roster.Snapshot();
            epoch.Roster.SuspendEpoch(DateTimeOffset.UtcNow, "Transport scope ended; no cross-epoch inheritance by name.");
            Diagnostics.Record(DateTimeOffset.UtcNow, "EpochEnded", epoch.Snapshot, epoch.Snapshot.Lifecycle, before, epoch.Roster.Snapshot(),
                priorSelfRuntimeId: epoch.LastResolvedSelfId, stableIdentity: epoch.Snapshot.StableIdentity ?? stableSelf);
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
        LiveMeterSnapshot Present(LiveMeterSnapshot value, string source)
        { Diagnostics.CurrentParty(value, source); return value; }
        foreach (var e in epochs.Values)
            if (e.Active && e.Last is { } last && now - last >= idleTimeout) e.Active = false;
        // No arbitrary choice between two simultaneously resolved player scopes.
        var candidates = epochs.Values.Where(e => !e.Ended && e.Snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved).ToArray();
        if (candidates.Length > 1)
        {
            var state = "Ambiguous: " + string.Join(", ", candidates.Select(e => e.Snapshot.EpochId));
            if (authorityState != state) Diagnostics.Record(now, "AuthorityAmbiguous", candidates[0].Snapshot, state);
            authorityState = state;
            return Present(Empty("AMBIGUOUS EPOCHS - Self meter paused"), "Withheld: ambiguous authority");
        }
        var epoch = candidates.SingleOrDefault() ?? epochs.Values.LastOrDefault(e => !e.Ended) ?? epochs.Values.LastOrDefault();
        if (epoch is null) return Present(Empty("Waiting for fresh character identity..."), "No selected scope");
        var s = epoch.Snapshot;
        // Only selected authority updates session presentation. A competing flow cannot overwrite it.
        if (s.BindingStatus == CurrentPlayerBindingStatus.Resolved && !epoch.Invalid && !epoch.Ended)
        {
            stableSelf = s.StableIdentity ?? new(s.CharacterName!, PlayerClass.Unknown, null, null, "attested-1536/3336");
            lastSelectedRuntimeId = s.EntityId;
        }
        else if (s.BindingStatus == CurrentPlayerBindingStatus.Conflict) stableSelf = null;
        else if (s.StableIdentity is not null && !epoch.Invalid) stableSelf = s.StableIdentity;
        var selectedState = s.EpochId + "/" + s.BindingStatus + "/" + epoch.Invalid + "/" + epoch.Ended;
        if (authorityState != selectedState)
        {
            Diagnostics.Record(now, "AuthoritySelected", s, "Previous authority: " + (authorityState ?? "none") + "; selected " + selectedState,
                after: epoch.Roster.Snapshot(), initialization1536Count: epoch.Initialization1536Count,
                initialization3336Count: epoch.Initialization3336Count, stableIdentity: stableSelf);
            authorityState = selectedState;
        }
        var elapsed = epoch.First is { } first && epoch.Last is { } stop ? Math.Max(0, (stop - first).TotalSeconds) : 0;
        var status = epoch.Invalid ? "UNTRUSTED - reconnect required" : epoch.Ended ? "STOPPED" :
            s.BindingStatus == CurrentPlayerBindingStatus.Conflict ? "CONFLICT - Self meter paused" :
            s.BindingStatus != CurrentPlayerBindingStatus.Resolved ? s.RecoveryMethod == LiveIdentityRecoveryMethod.WaitingForFreshEpoch
                ? "Waiting for identity... next fresh game connection" : "Waiting for fresh character identity..." :
            epoch.Active ? "IN COMBAT" : epoch.First is null ? "READY" : "IDLE - last encounter";
        var roster = epoch.Roster.Snapshot();
        UpdateStableParty(epoch, roster);
        var groupTotal = epoch.Total + epoch.Participants.Values.Sum(p => p.Total);
        decimal? Rate(decimal amount) => elapsed >= 0.001 && !epoch.Invalid ? amount / (decimal)elapsed : null;
        decimal Contribution(decimal amount, bool self) => groupTotal > 0 ? amount / groupTotal * 100m : self ? 100m : 0m;
        var members = new List<LiveMeterMemberSnapshot>();
        var awaitingTransport = epoch.Ended && s.Lifecycle is "Reset" or "Closed" or "ReplacedByNewHandshake" or "SupersededByFreshHandshake" or "MidstreamCloseObserved";
        if (stableSelf is not null && !epoch.Invalid &&
            (s.BindingStatus == CurrentPlayerBindingStatus.Unknown && !epoch.Ended || awaitingTransport))
        {
            // Presentation-only stable identities across handoff; no actor, rate, or damage eligibility.
            var waiting = new LiveMeterMemberSnapshot(null, stableSelf.CharacterName, true, 0, null, 0, 0, false,
                Class: stableSelf.Class);
            var waitingMembers = new[] { waiting }.Concat(stableParty.Select(p => new LiveMeterMemberSnapshot(null,
                p.Value.Name, false, 0, null, 0, 0, true, p.Key, p.Value.Class))).ToArray();
            return Present(new(s.EpochId, stableSelf.CharacterName, null, CurrentPlayerBindingStatus.Unknown, "AWAITING ACTOR - Waiting for current actor; combat paused",
                0, 0, null, 0, epoch.Self, epoch.Other, epoch.Unknown, [], Coverage, s.Gaps, s.Conflicts,
                s.DuplicateSegments, s.OverlapBytes, s.UnsupportedCandidates, s.Warnings, s.RecoveryMethod,
                null, Array.AsReadOnly(waitingMembers), 0, roster, stableSelf, Array.AsReadOnly(stableParty.Values.Select(p=>p.Identity).ToArray()),
                CurrentPartyAuthority: roster.Authority), "Stable preview while awaiting runtime proof");
        }
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
            if (!member.IsSelf && member.ActivePartyMember && member.MembershipKey is { } membershipKey &&
                (roster.Memberships ?? []).FirstOrDefault(m => m.Key == membershipKey) is { } identity)
                members[i] = members[i] with { MembershipKey = PartyPresentationKey(epoch, identity) };
            if (profile is null && members[i].MembershipKey is { } presentationKey &&
                stableParty.TryGetValue(presentationKey, out var stableRow) && stableRow.Name == member.CharacterName)
                members[i] = members[i] with { Class = stableRow.Class };
        }
        if (s.BindingStatus == CurrentPlayerBindingStatus.Resolved && !epoch.Invalid && !epoch.Ended)
            foreach (var p in stableParty.Where(p => !members.Any(m => m.MembershipKey == p.Key)))
                members.Add(new(null, p.Value.Name, false, 0, null, 0, 0, true, p.Key, p.Value.Class));
        // Stable identity/history may outlive current membership. A resolved CURRENT
        // view is owned by the selected scope's validated roster, never the carried
        // preview cache or former encounter participants. Unknown is not KnownEmpty:
        // retained identities remain available for future independent membership.
        var currentNamesByKey = (roster.Memberships ?? []).ToDictionary(m => PartyPresentationKey(epoch, m), m => m.CharacterName);
        var currentMembers = members.Where(m => m.IsSelf || m.ActivePartyMember && m.MembershipKey is { } key &&
            currentNamesByKey.TryGetValue(key, out var name) && name == m.CharacterName).ToArray();
        var currentTotal = currentMembers.Sum(m => m.TotalDamage);
        currentMembers = currentMembers.Select(m => m with { ContributionPercent = currentTotal > 0 ?
            m.TotalDamage / currentTotal * 100m : m.IsSelf ? 100m : 0m }).ToArray();
        return Present(new(s.EpochId, s.CharacterName, s.EntityId, s.BindingStatus, status, epoch.Total, elapsed,
            elapsed >= 0.001 && !epoch.Invalid ? epoch.Total / (decimal)elapsed : null,
            epoch.Hits, epoch.Self, epoch.Other, epoch.Unknown, epoch.Recent.Reverse().ToArray(), Coverage,
            s.Gaps, s.Conflicts, s.DuplicateSegments, s.OverlapBytes, s.UnsupportedCandidates, s.Warnings,
            s.RecoveryMethod, s.IdentityValidFrom, Array.AsReadOnly(members.ToArray()), groupTotal, roster, stableSelf,
            Array.AsReadOnly(stableParty.Values.Select(p=>p.Identity).ToArray()), Array.AsReadOnly(currentMembers), roster.Authority),
            "Selected scope validated membership; historical/carried rows excluded");
    }

    private string PartyPresentationKey(Epoch epoch, PartyRosterMembership membership)
    {
        var weak = epoch.Snapshot.EpochId + "/" + membership.Key;
        if (membership.MemberUuid is not { } uuid || membership.OpaqueToken is not { } token) return weak;
        var canonical = "party/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(uuid.ToUpperInvariant() + "/" + token)));
        if (partyRowKeys.TryGetValue(canonical, out var key)) return key;
        if (partyRowKeys.Count < 5 && stableParty.TryGetValue(weak, out var previous) &&
            previous.Name == membership.CharacterName && !partyRowKeys.Values.Contains(weak))
            return partyRowKeys[canonical] = weak; // Same epoch's validated status strengthened by UUID/token, presentation only.
        return canonical;
    }

    private void UpdateStableParty(Epoch epoch, PartyRosterSnapshot roster)
    {
        if (epoch.Invalid || epoch.Ended || stableSelf is null) return;
        if (partySelf is not null && (partySelf.CharacterName != stableSelf.CharacterName ||
            partySelf.ServerId is { } server && stableSelf.ServerId is { } nextServer && server != nextServer ||
            partySelf.FactionCode is { } faction && stableSelf.FactionCode is { } nextFaction && faction != nextFaction))
        { stableParty.Clear(); partyRowKeys.Clear(); }
        partySelf = stableSelf;
        var memberships = roster.Memberships ?? [];
        var keys = memberships.Select(m => PartyPresentationKey(epoch, m)).ToHashSet();
        var replaces = roster.AuthoritativeReplacementRevision > epoch.AppliedPartyReplacementRevision;
        foreach (var key in stableParty.Where(p => !keys.Contains(p.Key) && (replaces || p.Value.SourceEpoch == epoch.Snapshot.EpochId)).Select(p => p.Key).ToArray())
            stableParty.Remove(key);
        foreach (var alias in partyRowKeys.Where(p => !stableParty.ContainsKey(p.Value)).Select(p=>p.Key).ToArray()) partyRowKeys.Remove(alias);
        epoch.AppliedPartyReplacementRevision = roster.AuthoritativeReplacementRevision;
        foreach (var member in memberships.Where(m => m.CharacterName != "Party member (identity pending)"))
        {
            var key = PartyPresentationKey(epoch, member);
            if (!stableParty.ContainsKey(key) && stableParty.Count >= 5) continue;
            var profile = member.CurrentRuntimeEntityId is { } actor ? epoch.Profiles.Get(actor, member.CharacterName, false) : null;
            if (epoch.StableClasses.TryGetValue(member.Key, out var saved)) profile = saved;
            var previous = stableParty.GetValueOrDefault(key);
            var classProof = profile ?? (previous?.Name == member.CharacterName ? previous.Identity.ClassEvidence : null);
            stableParty[key] = new(new(key, member.CharacterName, member.MemberUuid, member.OpaqueToken,
                member.Evidence, classProof, member.OriginServerId, member.MemberSlot), epoch.Snapshot.EpochId);
        }
    }

    private static IReadOnlyList<LiveDiagnosticInitializationCandidate> InitializationCandidates(RawProtocolRecord record, string decision)
    {
        var fixedProfile = ReplayCurrentPlayerBindingResolver.SelfProfile(record);
        var fields = LocalInitializationExtractor.Extract(record)
            .OrderByDescending(c => fixedProfile?.Evidence.NameLengthOffset == c.NameLengthOffset)
            .Take(8).Select(c => new LiveDiagnosticInitializationCandidate(c.EntityId,
                string.Concat(c.CharacterName.Where(x => !char.IsControl(x)).Take(128)),
                c.NumericRange.Offset, c.NumericRange.Length, c.NameRange.Offset, c.NameRange.Length,
                fixedProfile?.Evidence.NameLengthOffset == c.NameLengthOffset ? fixedProfile.Value.Identity.LayoutFingerprint
                    : $"{record.OpcodeCandidate}:candidate/id-width={c.NumericRange.Length}/name-relative={c.NameLengthOffset - c.NumericRange.Offset}",
                fixedProfile?.Evidence.NameLengthOffset == c.NameLengthOffset ? decision + "; fixed " + fixedProfile.Value.Identity.LayoutFingerprint.Split(':')[0] + " profile" :
                "Unvalidated text/numeric hypothesis; not binding authority. " + (record.OpcodeCandidate == "3336" ? PlayerProfileDecoder.Inspect(record).Decision : "No fixed 1536 Self profile layout.") )).ToArray();
        return Array.AsReadOnly(fields);
    }

    private static LiveDiagnosticProfile? ProfileDiagnostic(RawProtocolRecord record)
    {
        if (record.OpcodeCandidate != "3336") return null;
        var inspected = PlayerProfileDecoder.Inspect(record);
        return new(inspected.LayoutType, inspected.Decision, inspected.Marker, inspected.ServerField, inspected.ClassCode, inspected.FactionCode);
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
