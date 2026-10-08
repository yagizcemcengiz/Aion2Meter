using System.Net;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Replay;

namespace Aion2Meter.Presentation;

/// <summary>Background owner of the existing live stack. UI polls one latest compact snapshot; no Dispatcher queue.</summary>
public sealed class LiveOverlaySession : IAsyncDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private readonly Task worker;
    private bool disposed;
    private OverlaySnapshot latest = OverlaySnapshot.Waiting;
    private string? error;
    private long snapshots;
    private long resetTicks;
    private readonly Func<DateTimeOffset> readClock;
    private readonly LiveDiagnosticBuffer diagnostics;
    public string ExportDiagnostics() => diagnostics.ToJson(readClock());
    /// <summary>Queues an explicit user reset; all meter mutation remains on the capture consumer.</summary>
    public void ResetCurrent() => Interlocked.Exchange(ref resetTicks, readClock().UtcTicks);
    public OverlaySnapshot Latest => Volatile.Read(ref latest);
    public string? Error => Volatile.Read(ref error);
    public long SnapshotCount => Interlocked.Read(ref snapshots);
    public bool IsRunning => !worker.IsCompleted;

    public LiveOverlaySession(IPacketSource source, IEnumerable<IPAddress> localAddresses, ushort port = 13328,
        TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        var feed = new LiveCombatFeed(); var meter = new LiveCombatMeter(feed);
        diagnostics = meter.Diagnostics;
        var pipeline = new LivePacketPipeline(localAddresses, port, combatFeed: feed);
        var rtt = new PassiveTcpRtt();
        var period = interval ?? RefreshInterval;
        if (period < TimeSpan.FromMilliseconds(100)) throw new ArgumentOutOfRangeException(nameof(interval));
        clock ??= () => DateTimeOffset.UtcNow;
        readClock = clock;
        worker = Task.Run(async () =>
        {
            try
            {
                void ApplyReset()
                {
                    var ticks = Interlocked.Exchange(ref resetTicks, 0);
                    if (ticks != 0) meter.ResetCurrent(new DateTimeOffset(ticks, TimeSpan.Zero));
                }
                await LiveDiagnosticRunner.RunAsync(source, pipeline, epochs =>
                {
                    ApplyReset();
                    var active = epochs.Where(e => e.Lifecycle == "Active" &&
                        e.BindingStatus == CurrentPlayerBindingStatus.Resolved).ToArray();
                    rtt.Select(active.Length == 1 ? active[0].EpochId : null, active.Length == 1 ? active[0].Connection : null);
                    var now = clock();
                    Volatile.Write(ref latest, OverlaySnapshot.FromMeter(meter.Snapshot(now)) with { NetworkRttMilliseconds = rtt.Milliseconds(now) });
                    Interlocked.Increment(ref snapshots);
                }, period, cancellation.Token, input => { ApplyReset(); rtt.Observe(input); }).ConfigureAwait(false);
                Volatile.Write(ref latest, OverlaySnapshot.Stopped);
            }
            catch (Exception ex)
            {
                diagnostics.SessionFailure(readClock(), ex.Message);
                Volatile.Write(ref error, ex.Message);
                Volatile.Write(ref latest, OverlaySnapshot.Unavailable);
                Interlocked.Increment(ref snapshots);
            }
        });
    }
    public static LiveOverlaySession Create(NetworkAdapter adapter, ushort port = 13328) =>
        new(new NpcapLiveSource(adapter, port), adapter.IPv4Addresses.Concat(adapter.IPv6Addresses), port);

    public async Task StopAsync()
    {
        lock (gate) { if (!disposed) cancellation.Cancel(); }
        await worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (gate)
        {
            if (disposed) return;
            disposed = true; cancellation.Dispose();
        }
    }
}
