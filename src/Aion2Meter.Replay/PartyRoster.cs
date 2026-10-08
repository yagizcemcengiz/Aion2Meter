using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public sealed record PartyMembershipEvidence(string EpochId, string Tag, long OuterOffset, long StreamOffset,
    long CompletionPacket, DateTimeOffset CompletedAt, string RawSha256);
public sealed record PartyMemberIdentity(ulong EntityId, string CharacterName, DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil, string EpochId, PartyMembershipEvidence Evidence,
    PartyMembershipEvidence? IdentityEvidence = null, PartyMembershipEvidence? InviteEvidence = null,
    PartyMembershipEvidence? TerminationEvidence = null, string? MemberUuid = null, string? OpaqueToken = null,
    string? MembershipKey = null, PartyMembershipEvidence? StatusEvidence = null);
public sealed record PartyRosterSnapshot(string EpochId, IReadOnlyList<PartyMemberIdentity> ActiveMembers,
    IReadOnlyList<PartyMemberIdentity> RecentIntervals, string? Diagnostic, PartyProofDiagnostics? Proofs = null,
    IReadOnlyList<PartyRosterMembership>? Memberships = null);
// Full-roster membership does not authorize a runtime actor. The numeric roster claim is
// only promoted after an independent same-epoch 4536 agrees on both ID and exact name.
public sealed record PartyRosterMembership(string Key, string CharacterName, ulong RosterEntityIdCandidate,
    DateTimeOffset ValidFrom, PartyMembershipEvidence Evidence, ulong? CurrentRuntimeEntityId,
    PartyMembershipEvidence? StatusEvidence = null, string? MemberUuid = null, string? OpaqueToken = null);
// Diagnostics distinguish a parsed roster claim from an independently bound, eligible actor.
// Pending claims never supply meter rows or damage eligibility.
public sealed record PendingPartyClaim(ulong RosterEntityId, string CharacterName, bool IndependentIdentityMatches);
public sealed record PartyTransitionDiagnostic(string Tag, long CompletionPacket, DateTimeOffset CompletedAt,
    int FrameLength, int EligibleMembers, int PendingMembers, string? Diagnostic);
public sealed record PartyProofDiagnostics(long RosterRecords, long JoinRecords, long InviteRecords,
    int IndependentIdentities, int RetainedInvites, IReadOnlyList<PendingPartyClaim> PendingReplacement,
    IReadOnlyList<PartyTransitionDiagnostic> RecentTransitions, long StatusRecords = 0, int RetainedStatuses = 0);

/// <summary>Independently written, bounded party evidence. Combat activity never grants membership.</summary>
public sealed class PartyRosterResolver(string epochId)
{
    private sealed record Identity(ulong Id, string Name, string Uuid, string Token);
    private sealed record Replacement(Identity[] Members, PartyMembershipEvidence Evidence);
    private readonly Dictionary<ulong, (string? Name, PartyMembershipEvidence Evidence)> names = [];
    private readonly Dictionary<ulong, (Identity Value, PartyMembershipEvidence Evidence)> invitations = [];
    private readonly Dictionary<ulong, PartyMemberIdentity> active = [];
    private readonly Dictionary<ulong, PartyRosterMembership> memberships = [];
    private readonly Queue<PartyMemberIdentity> closed = [];
    private string? diagnostic;
    private bool exhausted;
    private Replacement? pending;
    private readonly Dictionary<ulong, PartyMembershipEvidence> statuses = [];
    private readonly Dictionary<ulong, (Identity Identity, PartyMembershipEvidence Evidence)> joins = [];
    private bool statusBlocked;
    private DateTimeOffset? runtimeFrom;
    private readonly HashSet<ulong> claimsAwaitingAuthority = [];
    private long statusRecords;
    private long rosterRecords, joinRecords, inviteRecords;
    private readonly Queue<PartyTransitionDiagnostic> transitions = [];
    public int RetainedIdentityCount => names.Count;
    public int RetainedInvitationCount => invitations.Count;
    public int RetainedReplacementMemberCount => pending?.Members.Length ?? 0;
    public PartyRosterSnapshot Snapshot() => new(epochId, Array.AsReadOnly(active.Values.ToArray()),
        Array.AsReadOnly(closed.ToArray()), diagnostic,
        new(rosterRecords, joinRecords, inviteRecords, names.Count, invitations.Count,
            Array.AsReadOnly((pending?.Members ?? []).Select(m => new PendingPartyClaim(m.Id, m.Name,
                names.TryGetValue(m.Id, out var known) && known.Name == m.Name)).ToArray()),
            Array.AsReadOnly(transitions.ToArray()), statusRecords, statuses.Count),
        Array.AsReadOnly(memberships.Values.Select(m => m with
            { CurrentRuntimeEntityId = active.ContainsKey(m.RosterEntityIdCandidate) ? m.RosterEntityIdCandidate : null }).ToArray()));
    public PartyMemberIdentity? Eligible(ulong id, DateTimeOffset firstByte, DateTimeOffset complete) =>
        active.TryGetValue(id, out var member) && firstByte >= member.ValidFrom && complete >= member.ValidFrom ? member : null;

    public void ObserveBinding(ulong? selfId, string? selfName, DateTimeOffset? bindingFrom)
    {
        TryReplace(selfId, selfName, bindingFrom);
        ResolveEarly(selfId, selfName, bindingFrom);
    }

    public void Observe(RawProtocolRecord r, ulong? selfId, string? selfName, DateTimeOffset? bindingFrom)
    {
        if (r.SourceCapture != epochId || r.Direction != TrafficDirection.ServerToClient) return;
        if (runtimeFrom is { } scene && r.TimestampUtc < scene && r.OpcodeCandidate is "1B92" or "0892" or "0D92" or "0092") return;
        if (r.OpcodeCandidate == "4536")
        {
            if (r.DecodeStatus != "Unknown" || r.DecodeWarnings.Count != 0 || runtimeFrom is { } boundary && r.TimestampUtc < boundary) return;
            foreach (var i in IdentityRecordDecoder.Decode(r).Identities.Where(i => i.EvidenceType == IdentityEvidenceType.NameEnvelope4536))
            {
                if (!names.ContainsKey(i.EntityId) && names.Count >= 4096)
                { exhausted = true; Clear(r, "Party identity bound reached; fresh epoch required."); break; }
                if (names.TryGetValue(i.EntityId, out var previous) && previous.Name != i.Name)
                { names[i.EntityId] = (null, Evidence(r)); Clear(r, "Conflicting remote identity; party hidden."); }
                else if (!names.ContainsKey(i.EntityId)) names.Add(i.EntityId, (i.Name, Evidence(r)));
            }
            TryReplace(selfId, selfName, bindingFrom);
            ResolveEarly(selfId, selfName, bindingFrom);
            ResolveRetained(bindingFrom);
            return;
        }
        if (r.OpcodeCandidate is not ("1B92" or "0892" or "0D92" or "0092" or "1392" or "2192" or "2F92")) return;
        if (r.OpcodeCandidate == "0092") rosterRecords++;
        else if (r.OpcodeCandidate == "0D92") joinRecords++;
        else if (r.OpcodeCandidate == "0892") inviteRecords++;
        try
        {
            var c = new Cursor(r);
            if (r.OpcodeCandidate == "1B92")
            {
                statusRecords++;
                var id = c.Varint(); var current = c.Varint(); var maximum = c.Varint();
                var tail = c.Take(25); c.End();
                if (id == 0 || maximum == 0 || current > maximum || tail[^1] > 1)
                    throw new InvalidDataException("Unvalidated party status shape.");
                // Delta, never a replacement/removal. Strong termination blocks delayed revival.
                if (exhausted || id == selfId || statusBlocked && !memberships.ContainsKey(id) && !joins.ContainsKey(id)) return;
                if (!statuses.ContainsKey(id) && statuses.Count >= 5)
                { exhausted = true; Clear(r, "Party status capacity bound reached."); return; }
                statuses.TryAdd(id, Evidence(r));
                ResolveEarly(selfId, selfName, bindingFrom); return;
            }
            if (r.OpcodeCandidate == "0892")
            {
                c.Take(2); var uuid = c.Uuid(); var token = Convert.ToHexString(c.Take(8)); var id = c.Varint();
                c.Take(12); var name = c.Name(); c.Take(8); c.End();
                if (!invitations.ContainsKey(id) && invitations.Count >= 5) invitations.Clear();
                invitations[id] = (new(id, name, uuid, token), Evidence(r)); return; // Never active at invite time.
            }
            if (r.OpcodeCandidate == "0D92")
            {
                var identity = c.Member(); c.End();
                if (exhausted || selfId is null || selfName is null || bindingFrom is null ||
                    r.TimestampUtc < bindingFrom || r.CompletionUtc < bindingFrom || identity.Id == selfId) return;
                if (names.TryGetValue(identity.Id, out var known) && known.Name != identity.Name ||
                    invitations.TryGetValue(identity.Id, out var offered) && offered.Value != identity)
                { Clear(r, "Join contradicts independent identity/invite."); return; }
                if (!joins.ContainsKey(identity.Id) && joins.Count >= 5)
                {
                    var priorClaim = joins.FirstOrDefault(p => p.Key != identity.Id && p.Value.Identity.Uuid == identity.Uuid &&
                        p.Value.Identity.Token == identity.Token && p.Value.Identity.Name == identity.Name);
                    var corroborated = invitations.TryGetValue(identity.Id, out var invitation) && invitation.Value == identity &&
                        invitation.Evidence.CompletedAt <= r.CompletionUtc || statuses.ContainsKey(identity.Id);
                    if (priorClaim.Value.Identity is not null && corroborated) joins.Remove(priorClaim.Key);
                    else { exhausted = true; Clear(r, "Party capacity bound reached."); return; }
                }
                if (memberships.Values.Any(m => m.MemberUuid == identity.Uuid &&
                    (m.OpaqueToken != identity.Token || m.CharacterName != identity.Name)))
                { Clear(r, "Join contradicts stable party identity."); return; }
                // Complete join + matching invite grants named membership while far away.
                // An inviter without 0892 needs the independent status for this same ID.
                // Combat eligibility always waits for the independent 4536 identity.
                joins[identity.Id] = (identity, Evidence(r));
                ResolveEarly(selfId, selfName, bindingFrom); return;
            }
            if (r.OpcodeCandidate == "0092")
            {
                pending = null; statuses.Clear(); joins.Clear(); statusBlocked = true;
                // Complete, count-directed forms only. The paired reconnect and reciprocal-client
                // captures establish two entries: the first has a 3F layout byte after its opaque
                // 56-byte suffix prefix; the second omits that byte. Its meaning is not assigned.
                var mask = c.Byte(); c.Take(24);
                if (mask == 8)
                {
                    if (c.Varint() != 5 || c.Take(120).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Unvalidated roster optional branch.");
                }
                else if (mask != 0) throw new InvalidDataException("Unvalidated roster mask.");
                var count = c.Varint();
                Identity[] members;
                if (count == 1) members = [c.Member()];
                else if (count == 2 && mask == 8)
                    members = [c.RosterMember(1), c.RosterMember(2)];
                else throw new InvalidDataException("Unvalidated roster cardinality/layout.");
                c.Take(1); c.End();
                if (members.Select(m => m.Id).Distinct().Count() != members.Length ||
                    members.Select(m => m.Uuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length ||
                    members.Select(m => m.Token).Distinct().Count() != members.Length)
                    throw new InvalidDataException("Duplicate roster identity.");
                pending = new(members, Evidence(r));
                TryReplace(selfId, selfName, bindingFrom);
                if (pending is not null)
                { Clear(r, "Roster awaits independent same-epoch identity.", preserveMembership: true); invitations.Clear(); }
                return;
            }
            if (r.OpcodeCandidate == "1392")
            {
                pending = null;
                if (!c.Take(2).SequenceEqual(new byte[] { 0, 0 })) throw new InvalidDataException("Unvalidated party-end form.");
                c.End(); Clear(r, null); invitations.Clear(); return;
            }
            // External-only removal candidates: conservatively withdraw eligibility. No field or
            // member-removal semantics are claimed until those layouts occur in our own captures.
            var observedControlShape = r.OpcodeCandidate == "2F92" && r.RawBytes.Length - r.PrefixLength - 2 == 4;
            RetireRuntime(Evidence(r), "Unvalidated control " + r.OpcodeCandidate + "; runtime retired, membership retained." +
                (observedControlShape ? "" : " Fresh membership authority required."), !observedControlShape);
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException)
        {
            if (r.OpcodeCandidate is "2192" or "2F92") RetireRuntime(Evidence(r), "Unvalidated control form: " + e.Message, requireFreshClaim: true);
            else Clear(r, e.Message);
            invitations.Clear(); pending = null;
        }
        finally
        {
            transitions.Enqueue(new(r.OpcodeCandidate, r.CompletionPacketIndex, r.CompletionUtc,
                r.FrameLength, active.Count, RetainedReplacementMemberCount, diagnostic));
            while (transitions.Count > 8) transitions.Dequeue();
        }
    }

    private void ResolveEarly(ulong? selfId, string? selfName, DateTimeOffset? bindingFrom)
    {
        if (exhausted || selfId is null || selfName is null || bindingFrom is null) return;
        foreach (var id in statuses.Keys.Concat(joins.Keys).Distinct().ToArray())
        {
            if (id == selfId) continue;
            joins.TryGetValue(id, out var join);
            invitations.TryGetValue(id, out var invite);
            var hasInvite = join.Identity is not null && invite.Value == join.Identity && invite.Evidence.CompletedAt <= join.Evidence.CompletedAt;
            var hasJoin = join.Identity is not null && (hasInvite || statuses.ContainsKey(id));
            if (!hasJoin && (!statuses.ContainsKey(id) || statusBlocked && !memberships.ContainsKey(id))) continue;
            if (hasJoin && pending is { } replacement && replacement.Members.All(m => m.Id != id))
            {
                pending = null; // Only a corroborated new join supersedes the earlier pending snapshot.
                // A new join augments membership; it does not terminate far members in the prior roster.
            }
            var evidence = hasJoin ? join.Evidence! : statuses[id];
            if (claimsAwaitingAuthority.Remove(id) && memberships.TryGetValue(id, out var suspended))
                memberships[id] = suspended with { Evidence = evidence };
            var from = evidence.CompletedAt > bindingFrom ? evidence.CompletedAt : bindingFrom.Value;
            if (hasJoin && !hasInvite && statuses[id].CompletedAt > from) from = statuses[id].CompletedAt;
            names.TryGetValue(id, out var name);
            if (names.ContainsKey(id) && (name.Name is null || hasJoin && name.Name != join.Identity!.Name)) continue;
            if (hasJoin && active.TryGetValue(id, out var prior) && prior.MemberUuid is not null &&
                (prior.MemberUuid != join.Identity!.Uuid || prior.OpaqueToken != join.Identity.Token))
            { Clear(evidence, "Join conflicts with stable active member identity."); return; }
            var text = hasJoin ? join.Identity!.Name : name.Name ?? "Party member (identity pending)";
            if (!memberships.TryGetValue(id, out var membership))
            {
                if (hasJoin) MoveStableClaim(join.Identity!, evidence);
                if (!memberships.ContainsKey(id) && memberships.Count >= 5)
                { exhausted = true; Clear(evidence, "Party capacity bound reached."); return; }
                var key = hasJoin ? Key(join.Identity!) : "party-status/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                membership = memberships.TryGetValue(id, out var moved) ? moved : new(key, text, id, from, evidence, null,
                    hasJoin && !hasInvite ? statuses[id] : null,
                    hasJoin ? join.Identity!.Uuid : null, hasJoin ? join.Identity!.Token : null);
                memberships[id] = membership;
            }
            else if (membership.CharacterName == "Party member (identity pending)")
                memberships[id] = membership = membership with { CharacterName = text };
            else if (membership.CharacterName != text && text != "Party member (identity pending)")
            { Clear(evidence, "Conflicting early party identity."); return; }
            if (hasJoin)
                memberships[id] = membership = membership with { MemberUuid = join.Identity!.Uuid, OpaqueToken = join.Identity.Token };
            if (name.Name is null || active.ContainsKey(id)) continue;
            from = new[] { from, membership.ValidFrom, membership.Evidence.CompletedAt, name.Evidence.CompletedAt,
                bindingFrom.Value, runtimeFrom ?? DateTimeOffset.MinValue }.Max();
            active[id] = new(id, name.Name, from, null, epochId, membership.Evidence, name.Evidence,
                hasInvite ? invite.Evidence : null, MemberUuid: membership.MemberUuid,
                OpaqueToken: membership.OpaqueToken, MembershipKey: membership.Key,
                StatusEvidence: membership.StatusEvidence);
            diagnostic = null;
        }
    }

    private void TryReplace(ulong? selfId, string? selfName, DateTimeOffset? bindingFrom)
    {
        if (pending is not { } replacement || exhausted || selfId is null || selfName is null || bindingFrom is null) return;
        var local = replacement.Members.SingleOrDefault(m => m.Id == selfId);
        if (local?.Name != selfName)
        { RejectReplacement(replacement.Evidence, "Roster contradicts independently bound Self."); return; }
        var remote = replacement.Members.Where(m => m.Id != selfId).ToArray();
        foreach (var identity in remote)
        {
            if (names.TryGetValue(identity.Id, out var known) && known.Name != identity.Name)
            { RejectReplacement(replacement.Evidence, "Roster conflicts with independent remote identity."); return; }
            if (active.TryGetValue(identity.Id, out var current) &&
                (current.CharacterName != identity.Name || current.MemberUuid is not null &&
                    (current.MemberUuid != identity.Uuid || current.OpaqueToken != identity.Token)))
            { RejectReplacement(replacement.Evidence, "Roster conflicts with active member identity."); return; }
        }
        var membershipFrom = replacement.Evidence.CompletedAt > bindingFrom ? replacement.Evidence.CompletedAt : bindingFrom.Value;
        foreach (var identity in remote) MoveStableClaim(identity, replacement.Evidence);
        var desired = remote.Select(m => m.Id).ToHashSet();
        claimsAwaitingAuthority.Clear();
        foreach (var absent in memberships.Keys.Where(id => !desired.Contains(id)).ToArray()) memberships.Remove(absent);
        foreach (var identity in remote)
        {
            var key = Key(identity);
            if (memberships.TryGetValue(identity.Id, out var previous) &&
                (previous.Evidence.Tag == "1B92" || previous.StatusEvidence?.Tag == "1B92"))
                memberships[identity.Id] = previous with { CharacterName = identity.Name, Evidence = replacement.Evidence,
                    StatusEvidence = previous.StatusEvidence ?? previous.Evidence, MemberUuid = identity.Uuid, OpaqueToken = identity.Token };
            else if (previous is null || previous.Key != key || previous.CharacterName != identity.Name)
                memberships[identity.Id] = new(key, identity.Name, identity.Id, membershipFrom, replacement.Evidence, null,
                    MemberUuid: identity.Uuid, OpaqueToken: identity.Token);
        }
        if (remote.Any(identity => !names.ContainsKey(identity.Id))) return;
        var from = replacement.Evidence.CompletedAt > bindingFrom ? replacement.Evidence.CompletedAt : bindingFrom.Value;
        foreach (var identity in remote)
            if (names[identity.Id].Evidence.CompletedAt > from) from = names[identity.Id].Evidence.CompletedAt;
        foreach (var absent in active.Keys.Where(id => !desired.Contains(id)).ToArray())
        {
            Close(active[absent], replacement.Evidence, from);
            active.Remove(absent);
        }
        foreach (var identity in remote)
        {
            if (active.ContainsKey(identity.Id))
            {
                if (active[identity.Id].MemberUuid is null)
                    active[identity.Id] = active[identity.Id] with { MemberUuid = identity.Uuid, OpaqueToken = identity.Token,
                        Evidence = replacement.Evidence, StatusEvidence = active[identity.Id].Evidence,
                        MembershipKey = memberships[identity.Id].Key };
                continue; // All identity fields matched above. Preserve the original interval.
            }
            active[identity.Id] = new(identity.Id, identity.Name, from, null, epochId, replacement.Evidence,
                names[identity.Id].Evidence, MemberUuid: identity.Uuid, OpaqueToken: identity.Token, MembershipKey: memberships[identity.Id].Key);
        }
        invitations.Clear(); pending = null; diagnostic = null;
    }

    // Moving a claim requires a complete, independently corroborated join/full roster with the
    // same UUID AND opaque token AND exact name. A profile/name alone never chooses another ID.
    private void MoveStableClaim(Identity identity, PartyMembershipEvidence evidence)
    {
        var old = memberships.FirstOrDefault(p => p.Key != identity.Id &&
            string.Equals(p.Value.MemberUuid, identity.Uuid, StringComparison.OrdinalIgnoreCase) &&
            p.Value.OpaqueToken == identity.Token && p.Value.CharacterName == identity.Name);
        if (old.Value is null) return;
        if (active.Remove(old.Key, out var actor)) Close(actor, evidence, evidence.CompletedAt);
        memberships.Remove(old.Key); claimsAwaitingAuthority.Remove(old.Key);
        memberships[identity.Id] = old.Value with { RosterEntityIdCandidate = identity.Id,
            Evidence = evidence, CurrentRuntimeEntityId = null };
        names.Remove(old.Key); statuses.Remove(old.Key); joins.Remove(old.Key); invitations.Remove(old.Key);
    }

    private void ResolveRetained(DateTimeOffset? bindingFrom)
    {
        if (exhausted || bindingFrom is null) return;
        foreach (var membership in memberships.Values)
        {
            var id = membership.RosterEntityIdCandidate;
            if (claimsAwaitingAuthority.Contains(id) || active.ContainsKey(id) || !names.TryGetValue(id, out var profile) || profile.Name != membership.CharacterName) continue;
            var from = new[] { membership.ValidFrom, membership.Evidence.CompletedAt, profile.Evidence.CompletedAt,
                bindingFrom.Value, runtimeFrom ?? DateTimeOffset.MinValue }.Max();
            active[id] = new(id, membership.CharacterName, from, null, epochId, membership.Evidence,
                profile.Evidence, MemberUuid: membership.MemberUuid, OpaqueToken: membership.OpaqueToken,
                MembershipKey: membership.Key, StatusEvidence: membership.StatusEvidence);
        }
    }

    /// <summary>Withdraw scene actor eligibility, never infer party termination from a control/refresh.</summary>
    public void RetireRuntime(PartyMembershipEvidence evidence, string reason, bool requireFreshClaim = false)
    {
        foreach (var actor in active.Values) Close(actor, evidence, evidence.CompletedAt);
        if (requireFreshClaim) foreach (var id in memberships.Keys) claimsAwaitingAuthority.Add(id);
        active.Clear(); names.Clear(); invitations.Clear(); statuses.Clear(); joins.Clear(); pending = null;
        runtimeFrom = runtimeFrom is { } prior && prior > evidence.CompletedAt ? prior : evidence.CompletedAt;
        statusBlocked = true; diagnostic = reason;
    }

    /// <summary>Transport ownership ended. Evidence stays inspectable but never migrates by name to another epoch.</summary>
    public void SuspendEpoch(DateTimeOffset at, string reason)
    {
        RetireRuntime(new(epochId, "Epoch", 0, 0, 0, at, ""), reason, requireFreshClaim: true);
    }

    private void RejectReplacement(PartyMembershipEvidence evidence, string reason)
    { pending = null; invitations.Clear(); Clear(evidence, reason); }

    private void Close(PartyMemberIdentity member, PartyMembershipEvidence evidence, DateTimeOffset until)
    {
        closed.Enqueue(member with { ValidUntil = until, TerminationEvidence = evidence });
        while (closed.Count > 16) closed.Dequeue();
    }

    private void Clear(RawProtocolRecord r, string? reason, bool preserveMembership = false)
        => Clear(Evidence(r), reason, preserveMembership);

    private void Clear(PartyMembershipEvidence evidence, string? reason, bool preserveMembership = false)
    {
        foreach (var member in active.Values)
        {
            Close(member, evidence, evidence.CompletedAt);
        }
        active.Clear(); statuses.Clear(); joins.Clear(); statusBlocked = true;
        if (!preserveMembership) { memberships.Clear(); claimsAwaitingAuthority.Clear(); } diagnostic = reason;
    }
    public void EndEpoch()
    {
        active.Clear(); memberships.Clear(); invitations.Clear(); names.Clear(); closed.Clear(); pending = null;
        transitions.Clear(); statuses.Clear(); joins.Clear(); statusBlocked = false; exhausted = false; runtimeFrom = null; claimsAwaitingAuthority.Clear();
        rosterRecords = joinRecords = inviteRecords = statusRecords = 0;
    }
    private PartyMembershipEvidence Evidence(RawProtocolRecord r) => new(epochId, r.OpcodeCandidate,
        r.OuterFrameOffset, r.StreamOffset, r.CompletionPacketIndex, r.CompletionUtc,
        Convert.ToHexString(SHA256.HashData(r.RawBytes)));

    private static string Key(Identity identity) => "party/" + Convert.ToHexString(
        SHA256.HashData(Encoding.ASCII.GetBytes(identity.Uuid.ToUpperInvariant() + "/" + identity.Token)));

    private sealed class Cursor
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly byte[] bytes;
        private int position;
        public Cursor(RawProtocolRecord r)
        {
            bytes = r.RawBytes;
            var f = ApplicationFraming.Read(bytes);
            if (r.DecodeStatus != "Unknown" || r.DecodeWarnings.Count != 0 || !f.Success || f.TotalLength != bytes.Length ||
                f.PrefixLength != r.PrefixLength || r.FrameLength != bytes.Length) throw new InvalidDataException("Incomplete party framing.");
            position = r.PrefixLength;
            if (Convert.ToHexString(Take(2)) != r.OpcodeCandidate) throw new InvalidDataException("Party tag mismatch.");
        }
        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || position > bytes.Length - count) throw new InvalidDataException("Incomplete party structure.");
            var value = bytes.AsSpan(position, count); position += count; return value;
        }
        public byte Byte() => Take(1)[0];
        public ulong Varint()
        {
            var v = UnsignedVarint.Read(bytes.AsSpan(position), requireCanonical: true);
            if (!v.Success) throw new InvalidDataException("Invalid party varint.");
            position += v.BytesConsumed; return v.Value;
        }
        public string Uuid()
        {
            if (Byte() != 36) throw new InvalidDataException("Unvalidated party UUID length.");
            var raw = Take(36);
            if (raw.ContainsAnyInRange((byte)128, byte.MaxValue)) throw new InvalidDataException("Non-ASCII UUID.");
            var s = Encoding.ASCII.GetString(raw);
            if (!Guid.TryParseExact(s, "D", out _)) throw new InvalidDataException("Invalid party UUID.");
            return s.ToUpperInvariant();
        }
        public string Name()
        {
            var size = Byte();
            if (size is < 1 or > 64) throw new InvalidDataException("Invalid party name length.");
            var name = Utf8.GetString(Take(size));
            if (name.Any(char.IsControl)) throw new InvalidDataException("Invalid party name.");
            return name;
        }
        private Identity MemberCore(int? expectedSlot = null)
        {
            if (Byte() != 0) throw new InvalidDataException("Unvalidated member mask.");
            var slot = Byte();
            if (slot == 0 || expectedSlot is { } expected && slot != expected) throw new InvalidDataException("Unvalidated member slot.");
            var id = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            var a = BinaryPrimitives.ReadUInt16LittleEndian(Take(2)); var b = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            if (id == 0 || a != b) throw new InvalidDataException("Unvalidated member ID/server pair.");
            var uuid = Uuid(); var token = Convert.ToHexString(Take(8)); var name = Name();
            return new(id, name, uuid, token);
        }
        public Identity Member()
        {
            var identity = MemberCore();
            Take(78); // Existing complete single-member / join boundary, opaque gameplay fields.
            return identity;
        }
        public Identity RosterMember(int slot)
        {
            var identity = MemberCore(slot);
            Take(56);
            if (slot == 1 && Byte() != 0x3F) throw new InvalidDataException("Unvalidated two-member roster suffix layout.");
            Take(21); // Opaque complete suffix; second entry has no extra layout byte.
            return identity;
        }
        public void End() { if (position != bytes.Length) throw new InvalidDataException("Unconsumed party structure."); }
    }
}
