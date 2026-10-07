namespace Aion2Meter.Replay.Research;

// Optional presentation metadata. Raw protocol decoding never depends on a lookup result.
public sealed record SkillMetadata(uint RawSkillCode, string? DisplayName = null, string? Class = null,
    string? Rank = null, uint? ParentSkill = null, string? Region = null, string? GameBuild = null,
    string? Language = null, string? Source = null, string? License = null, string? VerificationStatus = null);

public interface ISkillMetadataProvider
{
    bool TryGetSkillMetadata(uint rawSkillCode, out SkillMetadata? metadata);
}

public sealed class EmptySkillMetadataProvider : ISkillMetadataProvider
{
    public bool TryGetSkillMetadata(uint rawSkillCode, out SkillMetadata? metadata)
    {
        metadata = null;
        return false;
    }
}
