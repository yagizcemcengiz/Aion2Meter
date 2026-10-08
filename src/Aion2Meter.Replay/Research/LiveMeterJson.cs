using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2Meter.Replay.Research;

public sealed record LiveMeterPerformance(double DiagnosticTickMilliseconds, double LastRecomputeMilliseconds,
    long RecomputeCount, long PublishedEvents, int RetainedEventIdentities, int SelectedPackets, long RetainedPayloadBytes);
public sealed record LiveMeterReport(string Kind, DateTimeOffset TimestampUtc, LiveMeterSnapshot Meter,
    LiveMeterPerformance Performance, LiveDiagnosticSnapshot Diagnostics);

public static class LiveMeterJson
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public static LiveMeterPerformance Performance(LivePacketPipeline pipeline, LiveCombatFeed feed) =>
        new(pipeline.LastSnapshotMilliseconds, pipeline.LastRecomputeMilliseconds, pipeline.RecomputeCount,
            feed.PublishedCount, feed.RetainedIdentities, pipeline.SelectedPacketCount, pipeline.RetainedPayloadBytes);
    public static string Serialize(LivePacketPipeline pipeline, LiveCombatFeed feed,
        IReadOnlyList<LiveEpochSnapshot> epochs, LiveMeterSnapshot meter, DateTimeOffset timestampUtc) =>
        JsonSerializer.Serialize(new LiveMeterReport("meter", timestampUtc, meter, Performance(pipeline, feed),
            LiveDiagnosticJson.CreateSnapshot(pipeline, epochs, timestampUtc)), Options);
}
