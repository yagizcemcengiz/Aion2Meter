using Aion2Meter.Capture;
using Aion2Meter.Core;

namespace Aion2Meter.Presentation;

/// <summary>Bounded, passive outbound-data / inbound-ACK timing. Includes delayed ACK/server scheduling.
/// This is a network approximation, never authoritative game latency or a combat input.</summary>
public sealed class PassiveTcpRtt
{
    private readonly TcpResearchPacketReader reader = new();
    private readonly Queue<(uint End, DateTimeOffset Sent)> pending = new();
    private TcpConnectionSelection? connection;
    private string? epoch;
    private uint? highWater, blockedThrough;
    private DateTimeOffset? lastPacket, sampled;
    private double smooth;
    private int samples;
    public int PendingCount => pending.Count;
    public void Select(string? epochId, TcpConnectionSelection? selected)
    {
        if (epoch == epochId && connection == selected) return;
        epoch = epochId; connection = selected; Clear();
    }
    private void Clear()
    {
        pending.Clear(); highWater = blockedThrough = null; lastPacket = sampled = null; samples = 0; smooth = 0;
    }
    public void Observe(SourcePacket packet)
    {
        if (connection is null) return;
        try { if (reader.Read(packet.Packet, packet.PacketIndex) is { } segment) Observe(segment); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
    }
    public void Observe(TcpSegment segment)
    {
        var direction = connection?.Direction(segment);
        if (direction is null) return;
        if (lastPacket is { } prior && segment.TimestampUtc < prior) { Clear(); return; }
        lastPacket = segment.TimestampUtc;
        if ((segment.Flags & (TcpFlags.Syn | TcpFlags.Fin | TcpFlags.Rst)) != 0) { Clear(); return; }
        if (segment.IsTruncated) { pending.Clear(); blockedThrough = highWater; return; }
        while (pending.TryPeek(out var old) && segment.TimestampUtc - old.Sent > TimeSpan.FromSeconds(5)) pending.Dequeue();
        if (direction == TrafficDirection.ClientToServer && segment.DeclaredPayloadLength > 0)
        {
            var end = unchecked(segment.SequenceNumber + (uint)segment.DeclaredPayloadLength);
            // Karn: a retransmission/overlap makes the whole outstanding flight ambiguous.
            if (highWater is { } high && Before(segment.SequenceNumber, high))
            {
                pending.Clear(); blockedThrough = Before(high, end) ? end : high;
                if (Before(high, end)) highWater = end;
                return;
            }
            highWater = end;
            if (blockedThrough is null)
            {
                if (pending.Count == 128) pending.Dequeue();
                pending.Enqueue((end, segment.TimestampUtc));
            }
        }
        else if (direction == TrafficDirection.ServerToClient && segment.Flags.HasFlag(TcpFlags.Ack))
        {
            var ack = segment.AcknowledgmentNumber;
            if (highWater is null || Before(highWater.Value, ack)) return; // Unobserved data; cannot measure.
            if (blockedThrough is { } barrier)
            {
                if (!Before(ack, barrier)) blockedThrough = null;
                return;
            }
            (uint End, DateTimeOffset Sent)? candidate = null;
            while (pending.TryPeek(out var item) && !Before(ack, item.End))
            { candidate ??= item; pending.Dequeue(); }
            if (candidate is { } value)
            {
                var ms = (segment.TimestampUtc - value.Sent).TotalMilliseconds;
                if (ms is <= 0 or > 5000) return;
                smooth = samples == 0 ? ms : smooth * .875 + ms * .125;
                samples = Math.Min(samples + 1, 3); sampled = segment.TimestampUtc;
            }
        }
    }
    private static bool Before(uint a, uint b) => unchecked((int)(a - b)) < 0;
    public double? Milliseconds(DateTimeOffset now) => samples >= 3 && sampled is { } at && now >= at &&
        now - at <= TimeSpan.FromSeconds(10) ? smooth : null;
}
