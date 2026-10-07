using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public sealed record ContainerLocation(int ContainerRecordId, int InnerOffset);

// Nested records inherit the outer TCP offset/timestamp; inner offsets are in separate byte spaces.
public sealed record RawProtocolRecord(int RecordId, string SourceCapture, TrafficDirection Direction,
    long StreamOffset, int OuterFrameId, long OuterFrameOffset, DateTimeOffset TimestampUtc,
    long PacketIndex, long CompletionPacketIndex, DateTimeOffset CompletionUtc,
    int PrefixLength, int FrameLength, byte[] RawBytes, string OpcodeCandidate,
    IReadOnlyList<ContainerLocation> ContainerPath, string DecodeStatus, IReadOnlyList<string> DecodeWarnings)
{
    public int ContainerDepth => ContainerPath.Count;
    public int? InnerOffset => ContainerPath.Count == 0 ? null : ContainerPath[^1].InnerOffset;
}

public sealed record RawCombatCandidate(RawProtocolRecord RawRecord)
{
    public string Status { get; init; } = "Unresolved";
    public string Confidence { get; init; } = "Unresolved layout; no gameplay attribution";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ulong? TargetIdCandidate { get; init; }
    public ulong? CategoryOrSwitch { get; init; }
    public ulong? Unknown0 { get; init; }
    public ulong? ActorIdCandidate { get; init; }
    public uint? RawSkillCodeCandidate { get; init; }
    public byte? UnknownAfterSkill { get; init; }
    public ulong? TypeCandidate { get; init; }
    public byte? ModifierCandidate { get; init; }
    public byte? DirectionCandidate { get; init; }
    public IReadOnlyDictionary<string, byte[]> UnknownRegions { get; init; } = new Dictionary<string, byte[]>();
    public ulong? ZeroUnknownCandidate { get; init; }
    public ulong? PreValueCandidate { get; init; }
    public ulong? AggregateAmount { get; init; }
    public IReadOnlyList<ulong> OptionalComponents { get; init; } = [];
    public ulong? DerivedBaseAmount { get; init; }
    public byte[] TerminalBytes { get; init; } = [];
}

public sealed record ProtocolDecodeLimits
{
    public const int DefaultMaximumDecompressedSize = 2 * 1024 * 1024;
    public int MaximumDecompressedSize { get; init; } = DefaultMaximumDecompressedSize;
    public int MaximumContainerDepth { get; init; } = 4;
    public long MaximumDecompressedBytesPerOuter { get; init; } = 8 * 1024 * 1024;
    public long MaximumDecompressedBytesPerCapture { get; init; } = 128 * 1024 * 1024;
    public int MaximumFramesPerContainer { get; init; } = 100_000;
    public int MaximumRecordsPerCapture { get; init; } = 500_000;
    public void Validate()
    {
        if (MaximumDecompressedSize is < 1 or > 16 * 1024 * 1024 || MaximumContainerDepth is < 1 or > 16 ||
            MaximumDecompressedBytesPerOuter < 1 || MaximumDecompressedBytesPerCapture < 1 ||
            MaximumFramesPerContainer < 1 || MaximumRecordsPerCapture < 1)
            throw new ArgumentException("Invalid protocol decode safety limits.");
    }
}

public sealed record ContainerAudit(int RecordId, int OuterFrameId, int Depth, uint? DeclaredSize,
    bool ValidDecompression, int DecompressedBytes, int InnerFrameCount, int FullyConsumedBytes,
    int TrailingUnparsedBytes, int MalformedInnerRecords, string? Error);

public sealed record ProtocolDecodeResult(IReadOnlyList<RawProtocolRecord> Records,
    IReadOnlyList<RawCombatCandidate> CombatCandidates, IReadOnlyList<ContainerAudit> Containers);
