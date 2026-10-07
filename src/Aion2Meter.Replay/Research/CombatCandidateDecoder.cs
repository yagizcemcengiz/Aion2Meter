using System.Buffers.Binary;

namespace Aion2Meter.Replay.Research;

public static class CombatCandidateDecoder
{
    public const int MaximumComponentCount = 256;

    public static RawCombatCandidate Decode(RawProtocolRecord record)
    {
        var result = new RawCombatCandidate(record);
        var bytes = record.RawBytes;
        var position = record.PrefixLength;
        var regions = new Dictionary<string, byte[]>();
        try
        {
            if (!Take(2).AsSpan().SequenceEqual(new byte[] { 0x04, 0x38 })) return Fail("Unsupported", "Not the 0438 candidate family.");
            result = result with { TargetIdCandidate = Number(), CategoryOrSwitch = Number() };
            if (result.CategoryOrSwitch is not (0x06 or 0x26)) return Fail("Unsupported", "Unresolved category/switch layout; only 06/26 supported.");
            result = result with { Unknown0 = Number(), ActorIdCandidate = Number() };
            result = result with { RawSkillCodeCandidate = BinaryPrimitives.ReadUInt32LittleEndian(Take(4)), UnknownAfterSkill = Take(1)[0], TypeCandidate = Number() };
            if (result.TypeCandidate is not (2 or 3)) return Fail("Unsupported", "Unresolved type/layout variant.");
            var modifierRegion = Take(3);
            regions["ReservedAfterModifier"] = [modifierRegion[1]];
            regions["UnknownFourBytes"] = Take(4);
            regions["UnknownThreeBytes"] = Take(3);
            result = result with { ModifierCandidate = modifierRegion[0], DirectionCandidate = modifierRegion[2], UnknownRegions = regions, ZeroUnknownCandidate = Number() };
            if (modifierRegion[1] != 0 || !regions["UnknownThreeBytes"].AsSpan().SequenceEqual(new byte[] { 1, 0, 0 }) || result.ZeroUnknownCandidate != 0)
                return Fail("Unsupported", "Unresolved category-6 layout guards; no cursor realignment attempted.");
            result = result with { PreValueCandidate = Number(), AggregateAmount = Number() };
            var components = new List<ulong>();
            ulong sum = 0;
            if ((result.CategoryOrSwitch.Value & 0x20) != 0)
            {
                var count = Number();
                if (count is < 1 or > MaximumComponentCount) return Fail("Malformed", "Component count outside bounded supported list.");
                for (ulong i = 0; i < count; i++)
                {
                    var component = Number();
                    components.Add(component);
                    result = result with { OptionalComponents = components.ToArray() };
                    if (ulong.MaxValue - sum < component) return Fail("Malformed", "Component sum overflow; aggregate retained.");
                    sum += component;
                }
            }
            result = result with { TerminalBytes = Take(2) };
            if (!result.TerminalBytes.AsSpan().SequenceEqual(new byte[] { 1, 0 }) || position != bytes.Length)
                return Fail("Unsupported", "Unknown terminal/trailing bytes; raw record retained.");
            if (sum > result.AggregateAmount.Value) return Fail("Malformed", "Component sum exceeds aggregate; aggregate retained.");
            return result with { Status = "Supported", Confidence = "Validated category-6 numeric shape; identities, skills and flags remain candidates",
                DerivedBaseAmount = result.AggregateAmount.Value - sum };
        }
        catch (InvalidDataException ex) { return Fail("Malformed", ex.Message); }

        RawCombatCandidate Fail(string status, string reason) => result with { Status = status, UnknownRegions = regions, Warnings = [reason] };
        byte[] Take(int count)
        {
            if (count > bytes.Length - position) throw new InvalidDataException("Truncated category-6 candidate.");
            var value = bytes.AsSpan(position, count).ToArray(); position += count; return value;
        }
        ulong Number()
        {
            var value = UnsignedVarint.Read(bytes.AsSpan(position), requireCanonical: true);
            if (!value.Success) throw new InvalidDataException(value.Error);
            position += value.BytesConsumed; return value.Value;
        }
    }
}
