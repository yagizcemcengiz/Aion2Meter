namespace Aion2Meter.Core;

/// <summary>Session-local character presentation evidence; never grants combat eligibility.</summary>
public sealed record StableCharacterIdentity(string CharacterName, PlayerClass Class, ushort? ServerId,
    byte? FactionCode, string LayoutFingerprint)
{
    // Profile layout is provenance; changing a supported branch does not change these facts.
    public bool SameProfileFacts(StableCharacterIdentity other) =>
        CharacterName == other.CharacterName && Class == other.Class && ServerId == other.ServerId && FactionCode == other.FactionCode;
}
