using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading.Channels;
using Aion2Meter.Core;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2Meter.Capture;

/// <summary>Passive local capture only. No application decoding, file writes or packet injection.</summary>
public sealed class NpcapLiveSource(NetworkAdapter adapter, ushort servicePort = 13328) : IPacketSource
{
    private int started;
    public string SourceId { get; } = "npcap:" + Guid.NewGuid().ToString("N");

    public static AdapterDiscoveryResult EnumerateInterfaces() => new AdapterDiscovery().Discover();

    public async IAsyncEnumerable<SourcePacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Create a source per capture session.");
        cancellationToken.ThrowIfCancellationRequested();
        if (servicePort == 0 || adapter.Identifier.StartsWith("rpcap", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a local adapter and a nonzero TCP service port.");
        var queue = Channel.CreateBounded<SourcePacket>(new BoundedChannelOptions(512)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        LibPcapLiveDevice device;
        try
        {
            device = LibPcapLiveDeviceList.New().FirstOrDefault(d => d.Name == adapter.Identifier)
                ?? throw new InvalidOperationException("Adapter no longer available; enumerate interfaces again.");
        }
        catch (Exception ex) when (AdapterDiscovery.IsNativeLoadFailure(ex))
        { throw new InvalidOperationException(AdapterDiscovery.MissingNpcapMessage, ex); }
        long index = 0;
        var stopping = 0;
        var captureStarted = false;
        void Arrival(object sender, PacketCapture packet)
        {
            try
            {
                var header = packet.Header as PcapHeader ?? throw new InvalidDataException("Missing libpcap packet header.");
                var timestamp = DateTimeOffset.UnixEpoch.AddTicks(checked((long)(header.Timeval.Value * TimeSpan.TicksPerSecond)));
                var input = new SourcePacket(SourceId, adapter.Identifier, Interlocked.Increment(ref index),
                    new(timestamp, checked((int)header.PacketLength), (uint)packet.Device.LinkType, packet.Data.ToArray()));
                if (!queue.Writer.TryWrite(input))
                    queue.Writer.TryComplete(new IOException("Live queue overflow: capture lost packets; session is not trustworthy. Restart capture."));
            }
            catch (Exception ex) { queue.Writer.TryComplete(ex); }
        }
        void Stopped(object sender, CaptureStoppedEventStatus status)
        {
            if (Volatile.Read(ref stopping) == 0)
                queue.Writer.TryComplete(new IOException($"Npcap stopped unexpectedly: {status}."));
        }
        try
        {
            device.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.None, ReadTimeout = 500, Snaplen = 262_144,
                TimestampResolution = TimestampResolution.Microsecond
            });
            device.StopCaptureTimeout = TimeSpan.FromSeconds(2);
            device.Filter = $"tcp port {servicePort}";
            device.OnPacketArrival += Arrival;
            device.OnCaptureStopped += Stopped;
            device.StartCapture();
            captureStarted = true;
            var statisticsClock = Stopwatch.StartNew();
            await foreach (var input in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (statisticsClock.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    var statistics = device.Statistics;
                    if (statistics.DroppedPackets != 0 || statistics.InterfaceDroppedPackets != 0)
                        throw new IOException("Npcap reports dropped packets; live session is not trustworthy. Restart capture.");
                    statisticsClock.Restart();
                }
                yield return input;
            }
        }
        finally
        {
            Volatile.Write(ref stopping, 1);
            try { if (captureStarted) await Task.Run(device.StopCapture).ConfigureAwait(false); }
            finally
            {
                device.OnPacketArrival -= Arrival;
                device.OnCaptureStopped -= Stopped;
                queue.Writer.TryComplete();
                device.Dispose();
            }
        }
    }
}
