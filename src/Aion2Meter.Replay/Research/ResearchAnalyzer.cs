using Aion2Meter.Capture;
using Aion2Meter.Core;

namespace Aion2Meter.Replay.Research;

public sealed record ResearchPacket(TcpSegment Segment, TrafficDirection Direction, double RelativeSeconds);
public sealed record ResearchCapture(string Path, DateTimeOffset OriginUtc, string OriginSource,
    IReadOnlyList<ResearchPacket> Packets, int HeaderErrors, int UnsupportedPackets);
public sealed record ResearchSelection(double? From = null, double? To = null,
    IReadOnlySet<int>? FrameLengths = null, int? PayloadLength = null, TrafficDirection? Direction = null)
{
    public bool Matches(ResearchPacket packet) =>
        (From is null || packet.RelativeSeconds >= From) && (To is null || packet.RelativeSeconds <= To) &&
        (FrameLengths is null || FrameLengths.Contains(packet.Segment.CapturedFrameLength)) &&
        (PayloadLength is null || packet.Segment.DeclaredPayloadLength == PayloadLength) &&
        (Direction is null || packet.Direction == Direction);

    public void Validate()
    {
        if (From is { } from && (!double.IsFinite(from) || from < 0) || To is { } to && (!double.IsFinite(to) || to < 0) ||
            From is not null && To is not null && From > To || PayloadLength is < 0 || FrameLengths?.Any(n => n < 0) == true)
            throw new ArgumentException("Invalid research time/length window.");
    }
}

public sealed class ResearchAnalyzer(IOfflinePacketSource source, TcpResearchPacketReader reader)
{
    public ResearchAnalyzer() : this(new PcapPacketSource(), new TcpResearchPacketReader()) { }
    public ResearchCapture Read(string path, TcpConnectionSelection connection, DateTimeOffset? sessionStart = null,
        CancellationToken cancellationToken = default)
    {
        var packets = new List<(TcpSegment Segment, TrafficDirection Direction)>();
        DateTimeOffset? first = null;
        long index = 0, bytes = 0;
        var errors = 0;
        var unsupported = 0;
        foreach (var capture in source.Read(path, cancellationToken))
        {
            index++;
            if (first is null || capture.TimestampUtc < first) first = capture.TimestampUtc;
            TcpSegment? segment;
            try { segment = reader.Read(capture, index); }
            catch (InvalidDataException) { errors++; continue; }
            catch (NotSupportedException) { unsupported++; continue; }
            if (segment is null || connection.Direction(segment) is not { } direction) continue;
            bytes += segment.Payload.Length;
            if (bytes > 64 * 1024 * 1024 || packets.Count >= 250_000)
                throw new InvalidDataException("Research memory limit reached (64 MiB payload / 250000 selected packets). Use a smaller capture.");
            packets.Add((segment, direction));
        }
        var origin = (sessionStart ?? first ?? DateTimeOffset.UnixEpoch).ToUniversalTime();
        return new(path, origin, sessionStart is null ? "earliest packet in file (metadata unavailable)" : "session metadata StartedUtc",
            packets.OrderBy(p => p.Segment.TimestampUtc).ThenBy(p => p.Segment.PacketIndex)
                .Select(p => new ResearchPacket(p.Segment, p.Direction, (p.Segment.TimestampUtc - origin).TotalSeconds)).ToArray(), errors, unsupported);
    }

    public static IReadOnlyList<ResearchPacket> Select(ResearchCapture capture, ResearchSelection selection)
    {
        selection.Validate();
        return capture.Packets.Where(selection.Matches).ToArray();
    }
}

public sealed record SequenceMatch(TrafficDirection Direction, IReadOnlyList<ResearchPacket> Packets)
{
    public double RelativeSeconds => Packets[0].RelativeSeconds;
}

public static class FrameSequenceSearch
{
    // Contiguous in that direction's timeline. Reverse-direction packets do not break a match.
    // Byte/length presentation filters are applied after searching, to avoid manufacturing adjacency.
    public static IReadOnlyList<SequenceMatch> Find(IReadOnlyList<ResearchPacket> packets, IReadOnlyList<int> lengths, double windowMs)
    {
        if (lengths.Count == 0 || lengths.Count > 32 || lengths.Any(n => n < 0) || !double.IsFinite(windowMs) || windowMs < 0)
            throw new ArgumentException("Sequence requires 1-32 frame lengths and a finite nonnegative time window.");
        var matches = new List<SequenceMatch>();
        foreach (var direction in Enum.GetValues<TrafficDirection>())
        {
            var timeline = packets.Where(p => p.Direction == direction).OrderBy(p => p.Segment.TimestampUtc).ThenBy(p => p.Segment.PacketIndex).ToArray();
            for (var start = 0; start <= timeline.Length - lengths.Count; start++)
            {
                if ((timeline[start + lengths.Count - 1].Segment.TimestampUtc - timeline[start].Segment.TimestampUtc).TotalMilliseconds > windowMs) continue;
                if (Enumerable.Range(0, lengths.Count).All(i => timeline[start + i].Segment.CapturedFrameLength == lengths[i]))
                    matches.Add(new(direction, timeline.Skip(start).Take(lengths.Count).ToArray()));
            }
        }
        return matches.OrderBy(m => m.Packets[0].Segment.TimestampUtc).ThenBy(m => m.Packets[0].Segment.PacketIndex).ToArray();
    }
}
