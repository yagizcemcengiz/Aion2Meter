using System.Threading.Channels;
using Aion2Meter.Core;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2Meter.Capture;

public sealed class PassiveCaptureEngine : ICaptureEngine
{
    private const int SnapshotLength = 262_144;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object faultGate = new();
    private readonly PacketStatistics statistics = new();
    private readonly IPacketMetadataReader metadataReader = new PacketMetadataReader();
    private LibPcapLiveDevice? device;
    private CaptureFileWriterDevice? writer;
    private Channel<CapturedPacket>? queue;
    private Task? processing;
    private long queueDropped, metadataErrors;
    private int running, stopping;
    private CaptureSession? faultedSession;
    private bool disposed;

    public event Action<DiagnosticMessage>? Diagnostic;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public CaptureSession? Session { get; private set; }
    public StatisticsSnapshot Statistics => statistics.Snapshot();
    public long QueueDroppedPackets => Interlocked.Read(ref queueDropped);
    public long MetadataErrors => Interlocked.Read(ref metadataErrors);

    public async Task StartAsync(NetworkAdapter adapter, string directory)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        await lifecycle.WaitAsync().ConfigureAwait(false);
        var startAttempted = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsRunning) throw new InvalidOperationException("Capture is already running.");
            startAttempted = true;
            statistics.Reset();
            Interlocked.Exchange(ref queueDropped, 0);
            Interlocked.Exchange(ref metadataErrors, 0);
            Volatile.Write(ref stopping, 0);
            Session = null;
            await Task.Run(() => StartCore(adapter, directory)).ConfigureAwait(false);
            Log("Info", $"Capture started: {Session!.FilePath}. Filter: ip or ip6; promiscuous mode disabled.");
        }
        catch (Exception ex) when (startAttempted)
        {
            try { await StopCoreAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { Log("Error", $"Capture cleanup: {cleanup.Message}"); }
            if (AdapterDiscovery.IsNativeLoadFailure(ex))
                throw new InvalidOperationException(AdapterDiscovery.MissingNpcapMessage, ex);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private void StartCore(NetworkAdapter adapter, string directory)
    {
        if (adapter.Identifier.StartsWith("rpcap", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only local capture adapters are supported.");
        device = LibPcapLiveDeviceList.New().FirstOrDefault(d => d.Name == adapter.Identifier)
            ?? throw new InvalidOperationException("The selected adapter is no longer available. Refresh adapters.");
        device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.None, ReadTimeout = 500, Snaplen = SnapshotLength,
            TimestampResolution = TimestampResolution.Microsecond
        });
        device.Filter = "ip or ip6";
        Session = CaptureSessionFactory.Create(directory, adapter.Identifier, DateTimeOffset.UtcNow);
        writer = new CaptureFileWriterDevice(Session.FilePath, FileMode.Open);
        writer.Open(new DeviceConfiguration
        {
            LinkLayerType = device.LinkType, Snaplen = SnapshotLength,
            TimestampResolution = TimestampResolution.Microsecond
        });
        queue = Channel.CreateBounded<CapturedPacket>(new BoundedChannelOptions(4096)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var session = Session;
        var activeQueue = queue;
        var activeWriter = writer;
        processing = Task.Run(() => ProcessAsync(activeQueue.Reader, activeWriter));
        _ = processing.ContinueWith(t => RequestFaultStop(session, t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        device.OnPacketArrival += OnPacketArrival;
        device.OnCaptureStopped += OnCaptureStopped;
        Volatile.Write(ref running, 1);
        device.StartCapture();
    }

    private void OnPacketArrival(object sender, PacketCapture packet)
    {
        try
        {
            var timestamp = DateTimeOffset.UnixEpoch.AddTicks(checked((long)(packet.Header.Timeval.Value * TimeSpan.TicksPerSecond)));
            var header = packet.Header as PcapHeader ?? throw new InvalidOperationException("Expected a libpcap packet header.");
            var captured = new CapturedPacket(timestamp, checked((int)header.PacketLength),
                (uint)packet.Device.LinkType, packet.Data.ToArray());
            if (queue?.Writer.TryWrite(captured) != true) Interlocked.Increment(ref queueDropped);
        }
        catch (Exception ex) { RequestFaultStop(Session, $"Packet receive failed: {ex.Message}"); }
    }

    private async Task ProcessAsync(ChannelReader<CapturedPacket> reader, CaptureFileWriterDevice activeWriter)
    {
        await foreach (var packet in reader.ReadAllAsync().ConfigureAwait(false))
        {
            var epochTicks = packet.TimestampUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            var header = new PcapHeader(checked((uint)(epochTicks / TimeSpan.TicksPerSecond)),
                (uint)(epochTicks % TimeSpan.TicksPerSecond / 10), (uint)packet.OriginalLength, (uint)packet.Data.Length);
            activeWriter.Write(packet.Data, ref header);
            PacketMetadata metadata;
            try { metadata = metadataReader.Read(packet); }
            catch (Exception)
            {
                Interlocked.Increment(ref metadataErrors);
                metadata = new(packet.TimestampUtc, packet.Data.Length, packet.OriginalLength);
            }
            statistics.Add(metadata);
        }
    }

    private void OnCaptureStopped(object sender, CaptureStoppedEventStatus status)
    {
        if (Volatile.Read(ref stopping) == 0)
            RequestFaultStop(Session, $"Capture ended unexpectedly ({status}).");
    }

    private void RequestFaultStop(CaptureSession? session, string error)
    {
        lock (faultGate)
        {
            if (!ReferenceEquals(Session, session) || ReferenceEquals(faultedSession, session)) return;
            faultedSession = session;
        }
        Log("Error", error);
        _ = Task.Run(async () =>
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(Session, session) && !disposed)
                    await StopCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Log("Error", $"Could not finish capture shutdown: {ex.Message}"); }
            finally { lifecycle.Release(); }
        });
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var hadResources = device is not null || writer is not null;
        var errors = new List<Exception>();
        Volatile.Write(ref stopping, 1);
        if (device is { } activeDevice)
        {
            try { await Task.Run(activeDevice.StopCapture).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
            activeDevice.OnPacketArrival -= OnPacketArrival;
            activeDevice.OnCaptureStopped -= OnCaptureStopped;
            try { await Task.Run(activeDevice.Dispose).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
            device = null;
        }
        queue?.Writer.TryComplete();
        if (processing is { } task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (writer is { } activeWriter)
        {
            try { await Task.Run(activeWriter.Dispose).ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
            writer = null;
        }
        queue = null;
        processing = null;
        Volatile.Write(ref running, 0);
        if (hadResources && errors.Count == 0)
            Log("Info", $"Capture stopped; file closed. Saved={Statistics.TotalPackets}, queue dropped={QueueDroppedPackets}, metadata errors={MetadataErrors}.");
        if (errors.Count > 0) throw new AggregateException("Capture shutdown encountered an error; check the capture file.", errors);
    }

    private void Log(string level, string message) => Diagnostic?.Invoke(new(DateTimeOffset.UtcNow, level, message));

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
}
