using System.Buffers.Binary;
using System.Text;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

public sealed record PartyLayoutDiagnostic(string Direction, int BodyLength, string Layout,
    int? Cardinality, byte? Slot, bool HasName, bool HasOriginServer, bool HasUuidToken,
    bool HasRuntimeActor, string? RejectInvariant, string AuthorityDecision, byte? MemberMask = null, byte? RosterMask = null,
    int? ConsumedBodyBytes = null, int? RemainingBodyBytes = null, string? MutationEffect = null);
internal sealed record PartyProfileClaim(ulong Actor, string Name, string Uuid, string Token,
    ushort OriginServer, ushort SceneServer, byte Slot, byte Mask);
internal sealed record PartyProfileLayout(PartyProfileClaim[] Members, bool Replacement);

// Count-directed, complete-frame parsing. Suffix alternatives are bounded wire shapes,
// independently checked against local captures and public wire fixtures. No row scanning,
// resynchronization, foreign parser source, map rules or combat heuristics.
internal static class PartyRosterLayoutDecoder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static PartyProfileLayout? Decode(RawProtocolRecord r)
    {
        if (r.Direction != TrafficDirection.ServerToClient || r.DecodeStatus != "Unknown" || r.DecodeWarnings.Count != 0) return null;
        var f = ApplicationFraming.Read(r.RawBytes);
        if (!f.Success || f.TotalLength != r.RawBytes.Length || f.PrefixLength != r.PrefixLength || r.FrameLength != r.RawBytes.Length) return null;
        var b = r.RawBytes.AsSpan(r.PrefixLength).ToArray();
        if (b.Length < 2 || Convert.ToHexString(b.AsSpan(0, 2)) != r.OpcodeCandidate) return null;
        try
        {
            var replacement = r.OpcodeCandidate == "0092";
            var p = 2; var count = 1;
            if (replacement)
            {
                // Observed compact roster envelope. The older mask-08 envelope retains
                // its independently frozen parser; unobserved optional branches fail closed.
                if (ReadByte(b, ref p) != 0) return null;
                Skip(b, ref p, 24); count = checked((int)Varint(b, ref p));
                if (count is < 1 or > 5) return null;
            }
            else if (r.OpcodeCandidate != "0D92") return null;
            var solutions = new List<PartyProfileClaim[]>();
            Walk(b, p, count, replacement, [], solutions);
            if (solutions.Count != 1) return null;
            var rows = solutions[0];
            if (rows.Select(x => x.Slot).Distinct().Count() != count ||
                rows.Select(x => x.Uuid).Distinct().Count() != count ||
                rows.Select(x => x.Token).Distinct().Count() != count ||
                rows.Where(x => x.Actor != 0).Select(x => x.Actor).Distinct().Count() != rows.Count(x => x.Actor != 0)) return null;
            return new(rows, replacement);
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException) { return null; }
    }
    private static void Walk(byte[] b, int p, int left, bool replacement, List<PartyProfileClaim> rows, List<PartyProfileClaim[]> solutions)
    {
        if (solutions.Count > 1) return;
        if (left == 0)
        {
            if (p + (replacement ? 1 : 0) == b.Length) if (!solutions.Any(x => x.SequenceEqual(rows))) solutions.Add(rows.ToArray());
            return;
        }
        try
        {
            var mask = ReadByte(b, ref p); var slot = ReadByte(b, ref p);
            if (mask is not (0 or 3 or 7) || slot is < 1 or > 5) return;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(Take(b, ref p, 4));
            var origin = BinaryPrimitives.ReadUInt16LittleEndian(Take(b, ref p, 2));
            var scene = BinaryPrimitives.ReadUInt16LittleEndian(Take(b, ref p, 2));
            if (origin == 0 || scene == 0 || ReadByte(b, ref p) != 36) return;
            var uuidText = Encoding.ASCII.GetString(Take(b, ref p, 36));
            if (!Guid.TryParseExact(uuidText, "D", out _)) return;
            var tokenBytes = Take(b, ref p, 8);
            // This repeated origin field is in the stable token envelope, not the
            // scene-server field. Cross-server scene values may legitimately differ.
            if (BinaryPrimitives.ReadUInt16LittleEndian(tokenBytes[6..]) != origin) return;
            var token = Convert.ToHexString(tokenBytes); var name = Text(b, ref p);
            var claim = new PartyProfileClaim(id, name, uuidText.ToUpperInvariant(), token, origin, scene, slot, mask);
            Skip(b, ref p, 4); Varint(b, ref p); Varint(b, ref p); Skip(b, ref p, 48);
            // Opaque numeric suffix, including variable-width control bytes. No
            // gameplay meaning is assigned. Complete next-row/end alignment selects
            // exactly one shape; ambiguous layouts grant no authority.
            for (var prefix = 0; prefix <= 1; prefix++)
            {
                var q = p;
                try
                {
                    Skip(b, ref q, prefix);
                    if ((mask & 3) == 3) { Text(b, ref q); Skip(b, ref q, 4); }
                    var widths = mask == 7 ? new[] { 26, 27, 28 } : new[] { 20, 21 };
                    foreach (var width in widths)
                    {
                        var end = q + width;
                        if (end > b.Length) continue;
                        rows.Add(claim); Walk(b, end, left - 1, replacement, rows, solutions); rows.RemoveAt(rows.Count - 1);
                    }
                }
                catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException) { }
            }
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException) { }
    }
    private static ReadOnlySpan<byte> Take(byte[] b, ref int p, int n)
    {
        if (p > b.Length - n) throw new InvalidDataException("Incomplete party profile.");
        var value = b.AsSpan(p, n); p += n; return value;
    }
    private static void Skip(byte[] b, ref int p, int n) => Take(b, ref p, n);
    private static byte ReadByte(byte[] b, ref int p) => Take(b, ref p, 1)[0];
    private static string Text(byte[] b, ref int p)
    {
        var n = ReadByte(b, ref p); if (n is < 1 or > 64) throw new InvalidDataException("Invalid party text length.");
        var s = Utf8.GetString(Take(b, ref p, n));
        if (s.Any(char.IsControl)) throw new InvalidDataException("Invalid party text."); return s;
    }
    private static ulong Varint(byte[] b, ref int p)
    {
        var v = UnsignedVarint.Read(b.AsSpan(p), requireCanonical: true);
        if (!v.Success) throw new InvalidDataException("Invalid party profile varint."); p += v.BytesConsumed; return v.Value;
    }
}
