using System.Net;
using System.Text.Json;
using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public sealed record LiveDiagnosticMember(string Key, string Name, ulong? RuntimeId, string MembershipSource,
    DateTimeOffset MembershipFrom, string EvidenceHash);
public sealed record LiveDiagnosticInitializationCandidate(ulong RuntimeIdCandidate, string NameCandidate,
    int NumericOffset, int NumericLength, int NameOffset, int NameLength);
public sealed record LiveDiagnosticTransition(long Sequence, DateTimeOffset At, string Kind, string Scope,
    string LocalEndpoint, string RemoteEndpoint, string Lifecycle, string Binding, ulong? SelfRuntimeId,
    DateTimeOffset? SelfValidFrom, int Gaps, int Conflicts, string Reason,
    int MembersBefore, int MembersAfter, IReadOnlyList<LiveDiagnosticMember> Before,
    IReadOnlyList<LiveDiagnosticMember> After, string? RecordTag = null, long? Packet = null,
    long? StreamOffset = null, string? EvidenceHash = null, ulong? PriorSelfRuntimeId = null,
    IReadOnlyList<LiveDiagnosticInitializationCandidate>? InitializationCandidates = null);
public sealed record LiveDiagnosticActivity(string Scope, DateTimeOffset At, string Tag);
public sealed record LiveDiagnosticExport(int Schema, DateTimeOffset ExportedAt, int Capacity, long TotalTransitions,
    IReadOnlyList<LiveDiagnosticTransition> Transitions, IReadOnlyList<LiveDiagnosticActivity> LastProtocolActivity);

/// <summary>Compact, detached, JSON-safe transitions. No packets, IPAddress objects or combat history.</summary>
public sealed class LiveDiagnosticBuffer
{
    public const int Capacity = 256;
    private readonly object gate = new();
    private readonly Queue<LiveDiagnosticTransition> ring = [];
    private readonly Dictionary<string, LiveDiagnosticActivity> activity = [];
    private long sequence;
    private static string Short(string? text) => string.Concat((text ?? "").Where(c => !char.IsControl(c)).Take(512));
    private static LiveDiagnosticMember[] Members(PartyRosterSnapshot? roster) => (roster?.Memberships ?? [])
        .Take(5).Select(m => new LiveDiagnosticMember(m.Key, Short(m.CharacterName), m.CurrentRuntimeEntityId,
            m.Evidence.Tag, m.ValidFrom, m.Evidence.RawSha256)).ToArray();

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
        IReadOnlyList<LiveDiagnosticInitializationCandidate>? initializationCandidates = null)
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
                initializationCandidates is null ? null : Array.AsReadOnly(initializationCandidates.Take(8).ToArray())));
            while (ring.Count > Capacity) ring.Dequeue();
        }
    }

    public LiveDiagnosticExport Snapshot(DateTimeOffset now)
    {
        lock (gate) return new(1, now, Capacity, sequence, Array.AsReadOnly(ring.ToArray()),
            Array.AsReadOnly(activity.Values.ToArray()));
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
