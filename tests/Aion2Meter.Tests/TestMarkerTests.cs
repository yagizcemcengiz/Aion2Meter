using Aion2Meter.Capture;
using Aion2Meter.Core;
using Xunit;

namespace Aion2Meter.Tests;

public sealed class TestMarkerTests
{
    [Fact]
    public void CueStoresScheduledAndActualTimeWithDelayAndSurvivesSessionFinish()
    {
        var start = DateTimeOffset.UnixEpoch;
        var marker = TestMarker.UserActionCue(start, start.AddSeconds(15), start.AddMilliseconds(15012), 12);
        var metadata = SessionMetadataTests.Metadata() with { TestMarkers = [marker] };
        metadata = metadata.Finish(start.AddSeconds(20), new(0, 0, 0, 0, 0, null, null), 0, 0, "Completed");
        var read = Assert.Single(SessionMetadataStore.Deserialize(SessionMetadataStore.Serialize(metadata)).TestMarkers);
        Assert.Equal("UserActionCue", read.MarkerType);
        Assert.Equal(15.012, read.RelativeSeconds, 6);
        Assert.Equal(12, read.SchedulingDelayMilliseconds);
        Assert.Equal(start.AddSeconds(15), read.ScheduledUtc);
        Assert.Equal(start.AddMilliseconds(15012), read.TimestampUtc);
        Assert.NotEqual(Guid.Empty, read.MarkerId);
    }

    [Fact]
    public void OldMetadataWithoutMarkersRemainsReadable()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(SessionMetadataStore.Serialize(SessionMetadataTests.Metadata()))!;
        json.AsObject().Remove("TestMarkers");
        Assert.Empty(SessionMetadataStore.Deserialize(json.ToJsonString()).TestMarkers);
    }

    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidSchedulerDelay(double delay) => Assert.Throws<ArgumentException>(() =>
        TestMarker.UserActionCue(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(10), DateTimeOffset.UnixEpoch.AddSeconds(10), delay));

    [Fact]
    public async Task StoppedCaptureRejectsMarkerWithoutCreatingSessionOrFiles()
    {
        await using var engine = new PassiveCaptureEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecordUserActionCueAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0));
        Assert.Null(engine.Session);
    }
}
