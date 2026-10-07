namespace Aion2Meter.Core;

public sealed record TestMarker(Guid MarkerId, string MarkerType, DateTimeOffset ScheduledUtc, DateTimeOffset TimestampUtc,
    double RelativeSeconds, double SchedulingDelayMilliseconds)
{
    public static TestMarker UserActionCue(DateTimeOffset sessionStart, DateTimeOffset scheduledUtc, DateTimeOffset timestampUtc, double delayMilliseconds)
    {
        if (scheduledUtc < sessionStart || timestampUtc == default || !double.IsFinite(delayMilliseconds) || delayMilliseconds < 0)
            throw new ArgumentException("Invalid cue scheduling information.");
        return new(Guid.NewGuid(), "UserActionCue", scheduledUtc.ToUniversalTime(), timestampUtc.ToUniversalTime(),
            (timestampUtc - sessionStart).TotalSeconds, delayMilliseconds);
    }
}
