using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public enum PlayerClassSource { Profile4536, Character3336 }
public sealed record PlayerClassEvidence(PlayerClass Class, PlayerClassSource Source, string EpochId,
    ulong EntityId, string CharacterName, DateTimeOffset ValidFrom, uint RawCode, byte Variant,
    byte FactionCode, RecordProvenance Provenance, string RawSha256, bool Conflict = false);

/// <summary>Own fixed-boundary profile decoding. Never grants binding, membership or damage eligibility.</summary>
public static class PlayerProfileDecoder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static PlayerClassEvidence? Decode(RawProtocolRecord r)
    {
        if (r.Direction != TrafficDirection.ServerToClient || r.DecodeStatus != "Unknown" ||
            r.DecodeWarnings.Count != 0 || r.OpcodeCandidate is not ("3336" or "4536")) return null;
        var b = r.RawBytes;
        var f = ApplicationFraming.Read(b);
        if (!f.Success || f.TotalLength != b.Length || r.FrameLength != b.Length || f.PrefixLength != r.PrefixLength ||
            b.Length < f.PrefixLength + 2 || Convert.ToHexString(b.AsSpan(f.PrefixLength, 2)) != r.OpcodeCandidate) return null;
        var at = f.PrefixLength + 2;
        var id = UnsignedVarint.Read(b.AsSpan(at), requireCanonical: true);
        if (!id.Success || id.Value == 0) return null;
        at += id.BytesConsumed;
        // Both observed forms have an opaque u32 then a distinct one-byte branch marker.
        // No search for a name/class elsewhere in the remainder, including presence 17.
        if (at > b.Length - 6 || b[at + 4] != (r.OpcodeCandidate == "4536" ? 7 : 0x37)) return null;
        at += 5;
        var size = b[at++];
        if (size is < 1 or > 64 || at > b.Length - size) return null;
        string name;
        try { name = Utf8.GetString(b, at, size); }
        catch (DecoderFallbackException) { return null; }
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return null;
        at += size;
        if (r.OpcodeCandidate == "3336") at += 2; // Local-only server pair before the class field.
        if (at > b.Length - 5) return null;
        var code = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
        var faction = b[at + 4];
        if (faction is not (1 or 2)) return null;
        var variant = (byte)(code % 4);
        var kind = variant is 1 or 2 ? (code / 4) switch
        {
            1 => PlayerClass.Gladiator, 2 => PlayerClass.Templar, 3 => PlayerClass.Ranger,
            4 => PlayerClass.Assassin, 5 => PlayerClass.Spiritmaster, 6 => PlayerClass.Sorcerer,
            7 => PlayerClass.Cleric, 8 => PlayerClass.Chanter, _ => PlayerClass.Unknown
        } : PlayerClass.Unknown;
        return new(kind, r.OpcodeCandidate == "4536" ? PlayerClassSource.Profile4536 : PlayerClassSource.Character3336,
            r.SourceCapture, id.Value, name, r.CompletionUtc, code, variant, faction,
            RecordProvenance.From(r), Convert.ToHexString(SHA256.HashData(b)));
    }
}

/// <summary>Compact epoch-scoped evidence, retained across checkpoints; no raw records or skill votes.</summary>
public sealed class PlayerProfileDirectory(string epochId)
{
    public const int Capacity = 4096;
    private readonly Dictionary<ulong, PlayerClassEvidence> players = [];
    private bool exhausted;
    public int Count => players.Count;
    public void Observe(RawProtocolRecord record)
    {
        if (exhausted || record.SourceCapture != epochId || PlayerProfileDecoder.Decode(record) is not { } next) return;
        if (players.TryGetValue(next.EntityId, out var old))
        {
            if (old.Conflict) return;
            if (old.CharacterName != next.CharacterName || old.Class != next.Class)
                players[next.EntityId] = next with { Class = PlayerClass.Unknown, Conflict = true };
            return; // Repeat evidence preserves original validity and source.
        }
        if (players.Count >= Capacity) { players.Clear(); exhausted = true; return; }
        players.Add(next.EntityId, next);
    }
    public PlayerClassEvidence? Get(ulong id, string name, bool self, DateTimeOffset? bindingFrom = null)
    {
        if (exhausted || !players.TryGetValue(id, out var value) || value.CharacterName != name ||
            !self && value.Source != PlayerClassSource.Profile4536) return null;
        // The local branch is usable only for independently bound Self, never to claim an actor.
        if (self && bindingFrom is null) return null;
        return bindingFrom > value.ValidFrom ? value with { ValidFrom = bindingFrom.Value } : value;
    }
    public void Clear() { players.Clear(); exhausted = false; }
}
