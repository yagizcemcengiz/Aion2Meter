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
public sealed record PlayerProfileInspection(PlayerClassEvidence? Profile, string LayoutType, string Decision,
    byte? Marker = null, ushort? ServerField = null, uint? ClassCode = null, byte? FactionCode = null);

/// <summary>Fixed-boundary metadata decoding. Authority requires separate fresh-epoch provenance validation.</summary>
public static class PlayerProfileDecoder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static PlayerClassEvidence? Decode(RawProtocolRecord r) => Inspect(r).Profile;
    public static PlayerProfileInspection Inspect(RawProtocolRecord r)
    {
        byte? marker = null, factionField = null;
        ushort? serverField = null;
        uint? classField = null;
        var layout = "Unsupported profile";
        PlayerProfileInspection Reject(string reason) => new(null, layout, reason, marker, serverField, classField, factionField);
        if (r.Direction != TrafficDirection.ServerToClient || r.DecodeStatus != "Unknown" ||
            r.DecodeWarnings.Count != 0 || r.OpcodeCandidate is not ("3336" or "4536")) return Reject("Wrong direction/tag or invalid/suppressed record state.");
        var b = r.RawBytes;
        var f = ApplicationFraming.Read(b);
        if (!f.Success || f.TotalLength != b.Length || r.FrameLength != b.Length || f.PrefixLength != r.PrefixLength ||
            b.Length < f.PrefixLength + 2 || Convert.ToHexString(b.AsSpan(f.PrefixLength, 2)) != r.OpcodeCandidate) return Reject("Incomplete or inconsistent application framing/tag.");
        var at = f.PrefixLength + 2;
        var id = UnsignedVarint.Read(b.AsSpan(at), requireCanonical: true);
        if (!id.Success || id.Value == 0) return Reject("Canonical nonzero actor ID required.");
        at += id.BytesConsumed;
        // Local 37, cross-server local 3F and remote 4536/07 share this fixed header.
        // 3F is independently checked against public wire fixtures, not a map or name rule.
        // No search for a name/class elsewhere in the remainder, including presence 17.
        if (at > b.Length - 6) return Reject("Truncated opaque-u32/marker/name-length boundary.");
        marker = b[at + 4];
        var crossServer = r.OpcodeCandidate == "3336" && marker == 0x3F;
        layout = (r.OpcodeCandidate, marker) switch
        {
            ("3336", 0x37) => "3336/37",
            ("3336", 0x3F) => "3336/3F",
            ("4536", 0x07) => "4536/07",
            _ => "Unsupported profile"
        };
        if (layout == "Unsupported profile") return Reject($"Unsupported branch marker 0x{marker:X2} after canonical ID + opaque-u32; no modeled profile layout.");
        if (crossServer && id.Value > uint.MaxValue) return Reject("Cross-server actor ID exceeds the validated uint32 domain.");
        at += 5;
        var size = b[at++];
        if (size is < 1 or > 64 || at > b.Length - size) return Reject("Invalid or truncated fixed name-u8 boundary.");
        string name;
        try { name = Utf8.GetString(b, at, size); }
        catch (DecoderFallbackException) { return Reject("Invalid fixed UTF-8 name."); }
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return Reject("Empty/control fixed name.");
        at += size;
        if (r.OpcodeCandidate == "3336")
        {
            if (at > b.Length - 2) return Reject("Truncated origin server-u16 field.");
            serverField = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
            at += 2;
        }
        if (at > b.Length - 5) return Reject("Truncated fixed class-u32/faction-u8 fields.");
        var code = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
        var faction = b[at + 4];
        classField = code; factionField = faction;
        if (faction is not (1 or 2)) return Reject("Unsupported fixed faction value; only validated values 1/2 accepted.");
        var variant = (byte)(code % 4);
        var kind = variant is 1 or 2 ? (code / 4) switch
        {
            1 => PlayerClass.Gladiator, 2 => PlayerClass.Templar, 3 => PlayerClass.Ranger,
            4 => PlayerClass.Assassin, 5 => PlayerClass.Spiritmaster, 6 => PlayerClass.Sorcerer,
            7 => PlayerClass.Cleric, 8 => PlayerClass.Chanter, _ => PlayerClass.Unknown
        } : PlayerClass.Unknown;
        // Fixed remote profile code 8 is independently corroborated by the live
        // 4536/07 observations and two public protocol mappings. Metadata only:
        // this does not grant party membership or local identity authority.
        if (r.OpcodeCandidate == "4536" && code == 8) kind = PlayerClass.Gladiator;
        // This new authority branch has no text-scanner fallback or unknown sentinel policy.
        if (crossServer && serverField == 0) return Reject("Cross-server profile requires a nonzero origin server field.");
        if (crossServer && kind == PlayerClass.Unknown) return Reject("Cross-server profile has an unsupported class code.");
        var profile = new PlayerClassEvidence(kind, r.OpcodeCandidate == "4536" ? PlayerClassSource.Profile4536 : PlayerClassSource.Character3336,
            r.SourceCapture, id.Value, name, r.CompletionUtc, code, variant, faction,
            RecordProvenance.From(r), Convert.ToHexString(SHA256.HashData(b)));
        return new(profile, layout, kind == PlayerClass.Unknown ? "Fixed layout with unsupported class code; cannot establish Self." :
            serverField == 0 ? "Fixed layout with zero local server field; cannot establish Self." :
            "Fixed layout recognized; independent transport/profile authority still required.", marker, serverField, classField, factionField);
    }

    internal static bool DeclaresCrossServerProfile(RawProtocolRecord r)
    {
        if (r.OpcodeCandidate != "3336" || r.RawBytes.Length <= r.PrefixLength + 2) return false;
        var id = UnsignedVarint.Read(r.RawBytes.AsSpan(r.PrefixLength + 2), requireCanonical: true);
        var markerAt = r.PrefixLength + 2 + id.BytesConsumed + 4;
        return id.Success && markerAt < r.RawBytes.Length && r.RawBytes[markerAt] == 0x3F;
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
        // Metadata lookup requires an independently bound Self; decoding alone grants no eligibility.
        if (self && bindingFrom is null) return null;
        return bindingFrom > value.ValidFrom ? value with { ValidFrom = bindingFrom.Value } : value;
    }
    public void Clear() { players.Clear(); exhausted = false; }
}
