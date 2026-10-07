using Aion2Meter.Core;

namespace Aion2Meter.Replay;

public static class LiveDiagnosticRunner
{
    /// <summary>Serializes ingestion and periodic snapshots; the source owns native shutdown in iterator finally.</summary>
    public static async Task RunAsync(IPacketSource source, LivePacketPipeline pipeline,
        Action<IReadOnlyList<LiveEpochSnapshot>> report, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (interval < TimeSpan.FromMilliseconds(100)) throw new ArgumentOutOfRangeException(nameof(interval));
        try
        {
            await using var packets = source.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            var pending = packets.MoveNextAsync().AsTask();
            var tick = Task.Delay(interval, cancellationToken);
            while (true)
            {
                await Task.WhenAny(pending, tick).ConfigureAwait(false);
                if (tick.IsCompleted && !cancellationToken.IsCancellationRequested)
                {
                    report(pipeline.Snapshot());
                    tick = Task.Delay(interval, cancellationToken);
                }
                else
                {
                    // On cancellation, wait for the pending read before disposing the enumerator.
                    if (!await pending.ConfigureAwait(false)) break;
                    pipeline.Ingest(packets.Current);
                    pending = packets.MoveNextAsync().AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            pipeline.FaultAll("Source/processing failure; live session is not trustworthy. Restart capture.");
            throw;
        }
        finally { report(pipeline.Complete()); }
    }
}
