using System.Buffers.Binary;
using System.Security.Cryptography;
using Aion2Meter.Core;
using Aion2Meter.Replay.Research;

namespace Aion2Meter.Replay;

/// <summary>Bounded scalars at the known stream cursor. No bytes or speculative resynchronization.</summary>
public sealed record LiveCheckpointDiagnostic(string Direction, long CheckpointOffset, long FrameOffset,
    long UnpublishedBytes, string State, string PrefixState, int ExpectedPrefixBytes, int AvailablePrefixBytes,
    int ExpectedFrameBytes, int AvailableFrameBytes, string ContainerState, string Reason,
    bool ResolvedWaiting = false, string? IrreversibleInvariant = null,
    string? OuterFrameSha256 = null, string? CompressionDiscriminator = null,
    uint? DeclaredDecompressedSize = null, bool? ApplicationAuthoritative = null, string? Action = null)
{
    internal static LiveCheckpointDiagnostic ApplicationFrame(RawProtocolRecord outer, long cursor, long unpublished,
        string state, string reason, string? invariant = null)
    {
        var body = outer.RawBytes.Length >= outer.PrefixLength ? outer.RawBytes.AsSpan(outer.PrefixLength) : [];
        var marker = body.Length >= 2 && body[0] == 255 && body[1] == 255;
        var authoritative = outer.Direction == TrafficDirection.ServerToClient;
        return new(outer.Direction.ToString(), cursor, outer.OuterFrameOffset, unpublished, state,
            "Complete outer frame", outer.PrefixLength, outer.PrefixLength, outer.FrameLength, outer.FrameLength,
            outer.DecodeStatus, reason, IrreversibleInvariant: invariant,
            OuterFrameSha256: outer.DecodeStatus == "Suppressed" ? null : Convert.ToHexString(SHA256.HashData(outer.RawBytes)),
            CompressionDiscriminator: marker ? authoritative ? "InboundFFFF" : "OutboundFFFFUnclassified" : "NoFFFF",
            DeclaredDecompressedSize: marker && body.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(body[2..6]) : null,
            ApplicationAuthoritative: authoritative, Action: state == "Invalid" ? "Fault" : "IgnoreNonAuthoritative");
    }
}
