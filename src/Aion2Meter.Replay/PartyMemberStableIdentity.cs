using Aion2Meter.Core;

namespace Aion2Meter.Replay;

/// <summary>Validated membership presentation, independent of an optional scene actor. Never grants combat eligibility.</summary>
public sealed record PartyMemberStableIdentity(string StableKey, string CharacterName, string? MemberUuid,
    string? OpaqueToken, PartyMembershipEvidence MembershipEvidence, PlayerClassEvidence? ClassEvidence,
    ushort? OriginServerId = null, byte? MemberSlot = null)
{
    public PlayerClass Class => ClassEvidence?.Class ?? PlayerClass.Unknown;
}
