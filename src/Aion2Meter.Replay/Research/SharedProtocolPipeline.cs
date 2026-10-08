using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public sealed record SharedProtocolResult(IReadOnlyList<ReassembledStream> Streams, ProtocolDecodeResult Decoded);

/// <summary>One downstream path for finite replay epochs and bounded live epoch snapshots.</summary>
public static class SharedProtocolPipeline
{
    public static SharedProtocolResult Decode(ResearchCapture capture, ProtocolDecodeLimits? limits = null)
        => DecodeStreams(capture, Reassemble(capture), limits);

    internal static IReadOnlyList<ReassembledStream> Reassemble(ResearchCapture capture) =>
        Enum.GetValues<TrafficDirection>().Select(d => TcpStreamReassembler.Assemble(capture.Packets, d)).ToArray();

    internal static SharedProtocolResult DecodeStreams(ResearchCapture capture,
        IReadOnlyList<ReassembledStream> streams, ProtocolDecodeLimits? limits = null) =>
        new(streams, new ReplayProtocolDecoder(limits).Decode(capture.Path, streams, capture.OriginUtc));
}
