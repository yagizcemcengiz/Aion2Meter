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
    PartyMembershipEvidence? TerminationEvidence = null, string? MemberUuid = null, string? OpaqueToken = null);
public sealed record PartyRosterSnapshot(string EpochId, IReadOnlyList<PartyMemberIdentity> ActiveMembers,
    IReadOnlyList<PartyMemberIdentity> RecentIntervals, string? Diagnostic);

/// <summary>Independently written, bounded party evidence. Combat activity never grants membership.</summary>
public sealed class PartyRosterResolver(string epochId)
{
    private sealed record Identity(ulong Id, string Name, string Uuid, string Token);
    private sealed record Replacement(Identity[] Members, PartyMembershipEvidence Evidence);
    private readonly Dictionary<ulong, (string? Name, PartyMembershipEvidence Evidence)> names = [];
    private readonly Dictionary<ulong, (Identity Value, PartyMembershipEvidence Evidence)> invitations = [];
    private readonly Dictionary<ulong, PartyMemberIdentity> active = [];
    private readonly Queue<PartyMemberIdentity> closed = [];
    private string? diagnostic;
    private bool exhausted;
    private Replacement? pending;
    public int RetainedIdentityCount => names.Count;
    public int RetainedInvitationCount => invitations.Count;
    public int RetainedReplacementMemberCount => pending?.Members.Length ?? 0;
    public PartyRosterSnapshot Snapshot() => new(epochId, Array.AsReadOnly(active.Values.ToArray()),
        Array.AsReadOnly(closed.ToArray()), diagnostic);
    public PartyMemberIdentity? Eligible(ulong id, DateTimeOffset firstByte, DateTimeOffset complete) =>
        active.TryGetValue(id, out var member) && firstByte >= member.ValidFrom && complete >= member.ValidFrom ? member : null;

    public void ObserveBinding(ulong? selfId, string? selfName, DateTimeOffset? bindingFrom)
    {
        TryReplace(selfId, selfName, bindingFrom);
    }

    public void Observe(RawProtocolRecord r, ulong? selfId, string? selfName, DateTimeOffset? bindingFrom)
    {
        if (r.SourceCapture != epochId || r.Direction != TrafficDirection.ServerToClient) return;
        if (r.OpcodeCandidate == "4536")
        {
            foreach (var i in IdentityRecordDecoder.Decode(r).Identities.Where(i => i.EvidenceType == IdentityEvidenceType.NameEnvelope4536))
            {
                if (!names.ContainsKey(i.EntityId) && names.Count >= 4096)
                { exhausted = true; Clear(r, "Party identity bound reached; fresh epoch required."); break; }
                if (names.TryGetValue(i.EntityId, out var previous) && previous.Name != i.Name)
                { names[i.EntityId] = (null, Evidence(r)); Clear(r, "Conflicting remote identity; party hidden."); }
                else if (!names.ContainsKey(i.EntityId)) names.Add(i.EntityId, (i.Name, Evidence(r)));
            }
            TryReplace(selfId, selfName, bindingFrom);
            return;
        }
        if (r.OpcodeCandidate is not ("0892" or "0D92" or "0092" or "1392" or "2192" or "2F92")) return;
        try
        {
            var c = new Cursor(r);
            if (r.OpcodeCandidate == "0892")
            {
                c.Take(2); var uuid = c.Uuid(); var token = Convert.ToHexString(c.Take(8)); var id = c.Varint();
                c.Take(12); var name = c.Name(); c.Take(8); c.End();
                if (!invitations.ContainsKey(id) && invitations.Count >= 5) invitations.Clear();
                invitations[id] = (new(id, name, uuid, token), Evidence(r)); return; // Never active at invite time.
            }
            if (r.OpcodeCandidate == "0D92")
            {
                pending = null; // A subsequent transition supersedes an earlier initialization roster.
                var identity = c.Member(); c.End();
                if (exhausted || selfId is null || selfName is null || bindingFrom is null ||
                    r.TimestampUtc < bindingFrom || r.CompletionUtc < bindingFrom || identity.Id == selfId) return;
                if (!names.TryGetValue(identity.Id, out var name) || name.Name != identity.Name ||
                    !invitations.TryGetValue(identity.Id, out var invite) || invite.Value != identity)
                { Clear(r, "Join lacks independent matching identity/invite; party hidden."); return; }
                if (active.TryGetValue(identity.Id, out var member))
                {
                    if (member.CharacterName != identity.Name) Clear(r, "Conflicting active party identity.");
                    return; // Repeat announcements do not rewrite ValidFrom.
                }
                if (active.Count >= 5) { Clear(r, "Party capacity bound reached."); return; }
                active.Add(identity.Id, new(identity.Id, identity.Name, r.CompletionUtc, null, epochId, Evidence(r), name.Evidence, invite.Evidence,
                    MemberUuid: identity.Uuid, OpaqueToken: identity.Token));
                diagnostic = null; return;
            }
            if (r.OpcodeCandidate == "0092")
            {
                pending = null;
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
                { Clear(r, "Roster awaits independent same-epoch identity."); invitations.Clear(); }
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
            Clear(r, "Unvalidated party transition; fresh membership evidence required.");
            invitations.Clear(); pending = null;
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException)
        { Clear(r, e.Message); invitations.Clear(); pending = null; }
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
            if (!names.TryGetValue(identity.Id, out var known)) return;
            if (known.Name != identity.Name)
            { RejectReplacement(replacement.Evidence, "Roster conflicts with independent remote identity."); return; }
            if (active.TryGetValue(identity.Id, out var current) &&
                (current.CharacterName != identity.Name || current.MemberUuid != identity.Uuid || current.OpaqueToken != identity.Token))
            { RejectReplacement(replacement.Evidence, "Roster conflicts with active member identity."); return; }
        }
        var from = replacement.Evidence.CompletedAt > bindingFrom ? replacement.Evidence.CompletedAt : bindingFrom.Value;
        foreach (var identity in remote)
            if (names[identity.Id].Evidence.CompletedAt > from) from = names[identity.Id].Evidence.CompletedAt;
        var desired = remote.Select(m => m.Id).ToHashSet();
        foreach (var absent in active.Keys.Where(id => !desired.Contains(id)).ToArray())
        {
            Close(active[absent], replacement.Evidence, from);
            active.Remove(absent);
        }
        foreach (var identity in remote)
        {
            if (active.ContainsKey(identity.Id))
            {
                continue; // All identity fields matched above. Preserve the original interval.
            }
            active[identity.Id] = new(identity.Id, identity.Name, from, null, epochId, replacement.Evidence,
                names[identity.Id].Evidence, MemberUuid: identity.Uuid, OpaqueToken: identity.Token);
        }
        invitations.Clear(); pending = null; diagnostic = null;
    }

    private void RejectReplacement(PartyMembershipEvidence evidence, string reason)
    { pending = null; invitations.Clear(); Clear(evidence, reason); }

    private void Close(PartyMemberIdentity member, PartyMembershipEvidence evidence, DateTimeOffset until)
    {
        closed.Enqueue(member with { ValidUntil = until, TerminationEvidence = evidence });
        while (closed.Count > 16) closed.Dequeue();
    }

    private void Clear(RawProtocolRecord r, string? reason)
        => Clear(Evidence(r), reason);

    private void Clear(PartyMembershipEvidence evidence, string? reason)
    {
        foreach (var member in active.Values)
        {
            Close(member, evidence, evidence.CompletedAt);
        }
        active.Clear(); diagnostic = reason;
    }
    public void EndEpoch() { active.Clear(); invitations.Clear(); names.Clear(); closed.Clear(); pending = null; }
    private PartyMembershipEvidence Evidence(RawProtocolRecord r) => new(epochId, r.OpcodeCandidate,
        r.OuterFrameOffset, r.StreamOffset, r.CompletionPacketIndex, r.CompletionUtc,
        Convert.ToHexString(SHA256.HashData(r.RawBytes)));

    private sealed class Cursor
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly byte[] bytes;
        private int position;
        public Cursor(RawProtocolRecord r)
        {
            bytes = r.RawBytes;
            var f = ApplicationFraming.Read(bytes);
            if (r.DecodeStatus == "Suppressed" || !f.Success || f.TotalLength != bytes.Length ||
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
            return s;
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
