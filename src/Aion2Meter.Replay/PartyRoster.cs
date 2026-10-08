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
    PartyMembershipEvidence? TerminationEvidence = null);
public sealed record PartyRosterSnapshot(string EpochId, IReadOnlyList<PartyMemberIdentity> ActiveMembers,
    IReadOnlyList<PartyMemberIdentity> RecentIntervals, string? Diagnostic);

/// <summary>Independently written, bounded party evidence. Combat activity never grants membership.</summary>
public sealed class PartyRosterResolver(string epochId)
{
    private sealed record Identity(ulong Id, string Name, string Uuid, string Token);
    private readonly Dictionary<ulong, (string? Name, PartyMembershipEvidence Evidence)> names = [];
    private readonly Dictionary<ulong, (Identity Value, PartyMembershipEvidence Evidence)> invitations = [];
    private readonly Dictionary<ulong, PartyMemberIdentity> active = [];
    private readonly Queue<PartyMemberIdentity> closed = [];
    private string? diagnostic;
    private bool exhausted;
    public int RetainedIdentityCount => names.Count;
    public int RetainedInvitationCount => invitations.Count;
    public PartyRosterSnapshot Snapshot() => new(epochId, Array.AsReadOnly(active.Values.ToArray()),
        Array.AsReadOnly(closed.ToArray()), diagnostic);
    public PartyMemberIdentity? Eligible(ulong id, DateTimeOffset firstByte, DateTimeOffset complete) =>
        active.TryGetValue(id, out var member) && firstByte >= member.ValidFrom && complete >= member.ValidFrom ? member : null;

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
                active.Add(identity.Id, new(identity.Id, identity.Name, r.CompletionUtc, null, epochId, Evidence(r), name.Evidence, invite.Evidence));
                diagnostic = null; return;
            }
            if (r.OpcodeCandidate == "0092")
            {
                // Only fully consumed Self-only forms are authoritative in our corpus. The opaque
                // optional branch consists of five zero lanes (24 bytes each), then the member count.
                // Larger/changed rosters withdraw remote eligibility, never fabricate additions.
                var mask = c.Byte(); c.Take(24);
                if (mask == 8)
                {
                    if (c.Varint() != 5 || c.Take(120).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Unvalidated roster optional branch.");
                }
                else if (mask != 0) throw new InvalidDataException("Unvalidated roster mask.");
                if (c.Varint() != 1) throw new InvalidDataException("Non-Self-only roster requires further validation.");
                var member = c.Member(); c.Take(1); c.End();
                if (member.Id != selfId || member.Name != selfName) throw new InvalidDataException("Roster is not independently bound Self-only.");
                Clear(r, null); return;
            }
            if (r.OpcodeCandidate == "1392")
            {
                if (!c.Take(2).SequenceEqual(new byte[] { 0, 0 })) throw new InvalidDataException("Unvalidated party-end form.");
                c.End(); Clear(r, null); invitations.Clear(); return;
            }
            // External-only removal candidates: conservatively withdraw eligibility. No field or
            // member-removal semantics are claimed until those layouts occur in our own captures.
            Clear(r, "Unvalidated party transition; fresh membership evidence required.");
            invitations.Clear();
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException)
        { Clear(r, e.Message); invitations.Clear(); }
    }

    private void Clear(RawProtocolRecord r, string? reason)
    {
        foreach (var member in active.Values)
        {
            closed.Enqueue(member with { ValidUntil = r.CompletionUtc, TerminationEvidence = Evidence(r) });
            while (closed.Count > 16) closed.Dequeue();
        }
        active.Clear(); diagnostic = reason;
    }
    public void EndEpoch() { active.Clear(); invitations.Clear(); names.Clear(); closed.Clear(); }
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
        public Identity Member()
        {
            if (Byte() != 0 || Byte() == 0) throw new InvalidDataException("Unvalidated member mask/slot.");
            var id = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            var a = BinaryPrimitives.ReadUInt16LittleEndian(Take(2)); var b = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            if (id == 0 || a != b) throw new InvalidDataException("Unvalidated member ID/server pair.");
            var uuid = Uuid(); var token = Convert.ToHexString(Take(8)); var name = Name();
            Take(78); // Retained as an opaque layout boundary, never interpreted as class/role/stats.
            return new(id, name, uuid, token);
        }
        public void End() { if (position != bytes.Length) throw new InvalidDataException("Unconsumed party structure."); }
    }
}
