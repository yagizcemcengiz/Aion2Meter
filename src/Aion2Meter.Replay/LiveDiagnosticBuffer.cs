using System.Net;
using System.Text.Json;
using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public sealed record LiveDiagnosticMember(string Key, string Name, ulong? RuntimeId, string MembershipSource,
    DateTimeOffset MembershipFrom, string EvidenceHash, ulong? RosterIdCandidate = null,
    DateTimeOffset? ActorValidFrom = null, string? ProfileTag = null, DateTimeOffset? ProfileAt = null,
    string? ProfileEvidenceHash = null, ushort? OriginServerId = null);
public sealed record LiveDiagnosticInitializationCandidate(ulong RuntimeIdCandidate, string NameCandidate,
    int NumericOffset, int NumericLength, int NameOffset, int NameLength, string? LayoutFingerprint = null, string? Decision = null);
public sealed record LiveDiagnosticTransition(long Sequence, DateTimeOffset At, string Kind, string Scope,
    string LocalEndpoint, string RemoteEndpoint, string Lifecycle, string Binding, ulong? SelfRuntimeId,
    DateTimeOffset? SelfValidFrom, int Gaps, int Conflicts, string Reason,
    int MembersBefore, int MembersAfter, IReadOnlyList<LiveDiagnosticMember> Before,
    IReadOnlyList<LiveDiagnosticMember> After, string? RecordTag = null, long? Packet = null,
    long? StreamOffset = null, string? EvidenceHash = null, ulong? PriorSelfRuntimeId = null,
    IReadOnlyList<LiveDiagnosticInitializationCandidate>? InitializationCandidates = null,
    long Initialization1536Count = 0, long Initialization3336Count = 0, string? StableSelfName = null,
    string? StableSelfClass = null, string? RuntimeBindingState = null,
    IReadOnlyList<LiveCheckpointDiagnostic>? CheckpointStates = null,
    LiveDiagnosticProfile? ProfileLayout = null, DateTimeOffset? SelfValidUntil = null,
    DateTimeOffset? PriorSelfValidUntil = null, PartyLayoutDiagnostic? PartyLayout = null);
public sealed record LiveDiagnosticProfile(string LayoutType, string Decision, byte? Marker, ushort? ServerField,
    uint? ClassCode, byte? FactionCode);
public sealed record LiveDiagnosticActivity(string Scope, DateTimeOffset At, string Tag);
public sealed record LiveDiagnosticCombatTarget(string Scope, ulong SourceEntityId, ulong TargetEntityId,
    DateTimeOffset At, string Tag, long Packet, long StreamOffset, string ProvenanceIdentity,
    string ActorAssociation);
public sealed record LiveDiagnosticCombatAttribution(string Scope, DateTimeOffset At, string Opcode,
    ulong? Category, ulong? SourceEntityId, ulong? TargetEntityId, ulong? Amount,
    string Classification, string? MatchedPartyKeyHash, IReadOnlyList<ulong> ExpectedRuntimeIds,
    string Decision, DateTimeOffset? ValidFrom = null);
public sealed record LiveDiagnosticRemoteProfile(string Scope, DateTimeOffset At, ulong? EntityId, byte? Marker,
    bool NameEnvelopeRecognized, bool ClassProfileRecognized, uint? ClassCode, string Decision);
public sealed record LiveDiagnosticCurrentParty(string? Scope, string Binding, string Authority,
    string RowSource, int StableIdentityCount, int MembershipCount, int LegacyMemberCount,
    IReadOnlyList<string> VisibleNames);
public sealed record LiveDiagnosticExport(int Schema, DateTimeOffset ExportedAt, int Capacity, long TotalTransitions,
    IReadOnlyList<LiveDiagnosticTransition> Transitions, IReadOnlyList<LiveDiagnosticActivity> LastProtocolActivity,
    IReadOnlyList<LiveDiagnosticCombatTarget>? RecentCombatTargets = null,
    IReadOnlyList<LiveDiagnosticCombatAttribution>? RecentCombatAttribution = null,
    IReadOnlyList<LiveDiagnosticRemoteProfile>? RecentRemoteProfiles = null,
    LiveDiagnosticCurrentParty? CurrentPartyPresentation = null);

/// <summary>Compact, detached, JSON-safe transitions. No packets, IPAddress objects or combat history.</summary>
public sealed class LiveDiagnosticBuffer
{
    public const int Capacity = 256;
    private readonly object gate = new();
    private readonly Queue<LiveDiagnosticTransition> ring = [];
    private readonly Dictionary<string, LiveDiagnosticActivity> activity = [];
    private long sequence;
    private readonly Queue<LiveDiagnosticCombatTarget> targets = [];
    private readonly Queue<LiveDiagnosticCombatAttribution> attribution = [];
    private readonly Queue<LiveDiagnosticRemoteProfile> remoteProfiles = [];
    private LiveDiagnosticCurrentParty? currentParty;
    private static string Short(string? text) => string.Concat((text ?? "").Where(c => !char.IsControl(c)).Take(512));
    private static LiveDiagnosticMember[] Members(PartyRosterSnapshot? roster) => (roster?.Memberships ?? [])
        .Take(5).Select(m => {
            var actor = roster!.ActiveMembers.FirstOrDefault(a => a.MembershipKey == m.Key);
            return new LiveDiagnosticMember(m.Key, Short(m.CharacterName), m.CurrentRuntimeEntityId,
                m.Evidence.Tag, m.ValidFrom, m.Evidence.RawSha256, m.RosterEntityIdCandidate,
                actor?.ValidFrom, actor?.IdentityEvidence?.Tag, actor?.IdentityEvidence?.CompletedAt,
                actor?.IdentityEvidence?.RawSha256, m.OriginServerId);
        }).ToArray();

    public void Protocol(string scope, DateTimeOffset at, string tag)
    {
        lock (gate)
        {
            if (!activity.ContainsKey(scope) && activity.Count >= 16) activity.Remove(activity.Keys.First());
            if (!activity.TryGetValue(scope, out var old) || at >= old.At) activity[scope] = new(scope, at, tag);
        }
    }

    public void Record(DateTimeOffset at, string kind, LiveEpochSnapshot snapshot, string reason,
        PartyRosterSnapshot? before = null, PartyRosterSnapshot? after = null,
        PartyMembershipEvidence? evidence = null, ulong? priorSelfRuntimeId = null,
        IReadOnlyList<LiveDiagnosticInitializationCandidate>? initializationCandidates = null,
        long initialization1536Count = 0, long initialization3336Count = 0, StableCharacterIdentity? stableIdentity = null,
        LiveDiagnosticProfile? profileLayout = null, PartyLayoutDiagnostic? partyLayout = null)
    {
        var a = Members(before); var b = Members(after);
        lock (gate)
        {
            ring.Enqueue(new(++sequence, at, kind, snapshot.EpochId,
                new IPEndPoint(snapshot.Connection.LocalIp, snapshot.Connection.LocalPort).ToString(),
                new IPEndPoint(snapshot.Connection.RemoteIp, snapshot.Connection.RemotePort).ToString(),
                snapshot.Lifecycle, snapshot.BindingStatus.ToString(), snapshot.EntityId, snapshot.IdentityValidFrom,
                snapshot.Gaps, snapshot.Conflicts, Short(reason), a.Length, b.Length,
                Array.AsReadOnly(a), Array.AsReadOnly(b), evidence?.Tag, evidence?.CompletionPacket,
                evidence?.StreamOffset, evidence?.RawSha256, priorSelfRuntimeId,
                initializationCandidates is null ? null : Array.AsReadOnly(initializationCandidates.Take(8).ToArray()),
                initialization1536Count, initialization3336Count, stableIdentity?.CharacterName,
                stableIdentity?.Class.ToString(), snapshot.BindingStatus == CurrentPlayerBindingStatus.Resolved ? "Resolved" :
                snapshot.BindingStatus == CurrentPlayerBindingStatus.Conflict ? "Conflict" : stableIdentity is not null ? "AwaitingActor" : snapshot.BindingStatus.ToString(),
                snapshot.CheckpointStates is null ? null : Array.AsReadOnly(snapshot.CheckpointStates.Take(3).ToArray()),
                profileLayout, snapshot.IdentityValidUntil, snapshot.PriorIdentityValidUntil, partyLayout));
            while (ring.Count > Capacity) ring.Dequeue();
        }
    }

    public LiveDiagnosticExport Snapshot(DateTimeOffset now)
    {
        lock (gate) return new(1, now, Capacity, sequence, Array.AsReadOnly(ring.ToArray()),
            Array.AsReadOnly(activity.Values.ToArray()), Array.AsReadOnly(targets.ToArray()),
            Array.AsReadOnly(attribution.ToArray()), Array.AsReadOnly(remoteProfiles.ToArray()), currentParty);
    }
    public void CurrentParty(LiveMeterSnapshot snapshot, string rowSource)
    {
        var visible = snapshot.CurrentMembers ?? snapshot.Members ?? [];
        lock (gate) currentParty = new(snapshot.EpochId, snapshot.BindingStatus.ToString(),
            snapshot.CurrentPartyAuthority.ToString(), rowSource, snapshot.StablePartyIdentities?.Count ?? 0,
            snapshot.PartyRoster?.Memberships?.Count ?? 0, snapshot.Members?.Count ?? 0,
            Array.AsReadOnly(visible.Take(6).Select(m => Short(m.CharacterName)).ToArray()));
    }
    public void RemoteProfile(LiveDiagnosticRemoteProfile value)
    {
        lock (gate)
        {
            remoteProfiles.Enqueue(value with { Decision = Short(value.Decision) });
            while (remoteProfiles.Count > 16) remoteProfiles.Dequeue();
        }
    }
    public void CombatAttribution(LiveDiagnosticCombatAttribution value)
    {
        lock (gate)
        {
            attribution.Enqueue(value with { ExpectedRuntimeIds = Array.AsReadOnly(value.ExpectedRuntimeIds.Take(5).ToArray()) });
            while (attribution.Count > 32) attribution.Dequeue();
        }
    }
    // Research observation only; it does not assert NPC/boss identity, HP, or membership.
    public void CombatTarget(DamageEvent damage, string actorAssociation)
    {
        var p = damage.Provenance;
        lock (gate)
        {
            targets.Enqueue(new(p.CaptureScope, damage.SourceEntityId, damage.TargetEntityId,
                p.CompletionTimestamp, p.RecordTag, p.CompletionPacketIndex, p.StreamOffset, p.Identity, actorAssociation));
            while (targets.Count > 16) targets.Dequeue();
        }
    }
    public void SessionFailure(DateTimeOffset at, string reason)
    {
        lock (gate)
        {
            ring.Enqueue(new(++sequence, at, "CaptureFailure", "session", "", "", "Faulted", "Unknown", null, null,
                0, 0, Short(reason), 0, 0, [], []));
            while (ring.Count > Capacity) ring.Dequeue();
        }
    }
    public string ToJson(DateTimeOffset now) => JsonSerializer.Serialize(Snapshot(now), new JsonSerializerOptions { WriteIndented = true });
}
