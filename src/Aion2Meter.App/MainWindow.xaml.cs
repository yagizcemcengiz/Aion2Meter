using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Aion2Meter.Capture;
using Aion2Meter.Core;

namespace Aion2Meter.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ICaptureEngine engine = new PassiveCaptureEngine();
    private readonly IAdapterDiscovery discovery = new AdapterDiscovery();
    private readonly ProcessNetworkDiscovery processDiscovery = new();
    private ProcessNetworkSnapshot processSnapshot = new([], [], []);
    private NetworkProcess? selectedProcess;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(333) };
    private readonly Stopwatch rateClock = Stopwatch.StartNew();
    private long previousPackets;
    private double previousSeconds;
    private bool busy, closing, allowClose, npcapReady;
    private long lastDropped, lastMetadataErrors;
    private NetworkAdapter? selectedAdapter;
    private string npcapStatus = "Checking", captureStatus = "Stopped", statusMessage = "", currentFile = "—";
    private StatisticsSnapshot snapshot = new(0, 0, 0, 0, 0, null, null);
    private CancellationTokenSource? cueCancellation;
    private Task cueTask = Task.CompletedTask;
    private string countdownStatus = "Visual cue only. Perform the game action manually; cue time is not cast time.";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        CaptureDirectory = Path.Combine(FindProjectRoot(), "captures");
        engine.Diagnostic += OnDiagnostic;
        timer.Tick += (_, _) => UpdateStatistics();
        timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<NetworkAdapter> Adapters { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<NetworkProcess> Processes { get; } = [];
    public ObservableCollection<ProcessNetworkEndpoint> Connections { get; } = [];
    public NetworkProcess? SelectedProcess
    {
        get => selectedProcess;
        set { selectedProcess = value; Notify(); UpdateConnections(); }
    }
    public string CaptureDirectory { get; }
    public string NpcapStatus { get => npcapStatus; private set { npcapStatus = value; Notify(); } }
    public string CaptureStatus { get => captureStatus; private set { captureStatus = value; Notify(); } }
    public string StatusMessage { get => statusMessage; private set { statusMessage = value; Notify(); } }
    public string CurrentFile { get => currentFile; private set { currentFile = value; Notify(); } }
    public string CountdownStatus { get => countdownStatus; private set { countdownStatus = value; Notify(); } }
    public double PacketsPerSecond { get; private set; }
    public long TotalPackets => snapshot.TotalPackets;
    public long TotalBytes => snapshot.TotalBytes;
    public long TcpCount => snapshot.TcpCount;
    public long UdpCount => snapshot.UdpCount;
    public long OtherCount => snapshot.OtherCount;
    public string DropSummary => $"Queue dropped: {engine.QueueDroppedPackets:N0} • Metadata errors: {engine.MetadataErrors:N0}";
    public NetworkAdapter? SelectedAdapter
    {
        get => selectedAdapter;
        set { selectedAdapter = value; Notify(); UpdateControls(); }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (!closing) await RefreshProcessesAsync();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ProcessRefreshClicked(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();
    private async void ConnectionsRefreshClicked(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        busy = true;
        UpdateControls();
        try { ApplyProcessSnapshot(await Task.Run(processDiscovery.Refresh)); }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; UpdateControls(); }
    }

    private void ApplyProcessSnapshot(ProcessNetworkSnapshot result)
    {
        var pid = SelectedProcess?.Pid;
        processSnapshot = result;
        Processes.Clear();
        foreach (var process in result.Processes) Processes.Add(process);
        SelectedProcess = Processes.FirstOrDefault(p => p.Pid == pid);
        foreach (var warning in result.Warnings) AddLog("Warning", warning);
        StatusMessage = pid is not null && SelectedProcess is null ? "The selected process has no current endpoints or has exited. Select a process again."
            : $"Found {Processes.Count} network processes. Select a process manually; no game process is auto-selected.";
    }

    private void UpdateConnections()
    {
        Connections.Clear();
        if (SelectedProcess is { } process)
            foreach (var endpoint in processSnapshot.ForPid(process.Pid)) Connections.Add(endpoint);
    }

    private async Task RefreshAsync()
    {
        busy = true;
        UpdateControls();
        try
        {
            var identifier = SelectedAdapter?.Identifier;
            var result = await Task.Run(discovery.Discover);
            npcapReady = result.NpcapReady;
            NpcapStatus = result.NpcapReady ? "Ready" : "Missing";
            Adapters.Clear();
            foreach (var adapter in result.Adapters) Adapters.Add(adapter);
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Identifier == identifier);
            StatusMessage = result.Error ?? (Adapters.Count == 0 ? "No capture-capable adapters found." : "Select your active Ethernet or Wi-Fi adapter.");
            AddLog(result.Error is null ? "Info" : "Error", result.Error ?? $"Found {Adapters.Count} capture adapters.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; UpdateControls(); }
    }

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedAdapter is not { } adapter) { StatusMessage = "Select a network adapter first."; return; }
        busy = true;
        UpdateControls();
        try
        {
            var mode = CaptureModeCombo.SelectedIndex == 1 ? CaptureMode.SelectedProcessTraffic : CaptureMode.AllTraffic;
            var label = SessionLabelBox.Text;
            var notes = UserNotesBox.Text;
            NetworkProcess? process = null;
            IReadOnlyList<ProcessNetworkEndpoint> endpoints = [];
            if (mode == CaptureMode.SelectedProcessTraffic)
            {
                var pid = SelectedProcess?.Pid ?? throw new InvalidOperationException("Select a network process first.");
                // Refresh at start, outside the packet callback and off the UI thread.
                var result = await Task.Run(processDiscovery.Refresh);
                ApplyProcessSnapshot(result);
                process = result.Processes.FirstOrDefault(p => p.Pid == pid)
                    ?? throw new InvalidOperationException("The selected process exited or has no endpoints. Refresh processes.");
                endpoints = result.ForPid(pid);
            }
            await engine.StartAsync(adapter, CaptureDirectory, new(mode, label, process?.Pid, process?.ProcessName, endpoints, notes));
            previousPackets = 0;
            previousSeconds = rateClock.Elapsed.TotalSeconds;
            lastDropped = lastMetadataErrors = 0;
            StatusMessage = mode == CaptureMode.AllTraffic ? "Capturing all IP traffic on the selected adapter. Capture files contain private network data."
                : "Capturing a fixed IP/port/protocol snapshot, not PID-isolated traffic. Shared endpoints may match other processes.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; UpdateStatistics(); UpdateControls(); }
    }

    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        cueCancellation?.Cancel();
        busy = true;
        UpdateControls();
        try { await engine.StopAsync(); StatusMessage = "Capture stopped. File closed and ready for offline replay."; }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; UpdateStatistics(); UpdateControls(); }
    }

    private void CountdownClicked(object sender, RoutedEventArgs e)
    {
        if (closing || busy || cueCancellation is not null || !engine.IsRunning || engine.Session is not { } session) return;
        cueTask = RunCountdownAsync(session);
    }

    private async Task RunCountdownAsync(CaptureSession session)
    {
        using var cancellation = new CancellationTokenSource();
        cueCancellation = cancellation;
        UpdateControls();
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var scheduled = started.AddSeconds(10);
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!engine.IsRunning || engine.Session?.SessionId != session.SessionId) throw new OperationCanceledException();
                CountdownStatus = $"Manual action cue in {Math.Max(0, 10 - clock.Elapsed.TotalSeconds):F1}s";
                var remaining = TimeSpan.FromSeconds(10) - clock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!engine.IsRunning || engine.Session?.SessionId != session.SessionId) throw new OperationCanceledException();
            var timestamp = DateTimeOffset.UtcNow;
            var delay = Math.Max(0, clock.Elapsed.TotalMilliseconds - 10_000);
            CountdownStatus = "CUE NOW — perform your action manually. Saving UserActionCue…";
            await engine.RecordUserActionCueAsync(session.SessionId, scheduled, timestamp, delay);
            CountdownStatus = $"UserActionCue saved at {timestamp:HH:mm:ss.fff} UTC (scheduler delay {delay:F1}ms). This is not the actual cast timestamp.";
            AddLog("Info", $"UserActionCue saved; relative time {(timestamp - session.StartedUtc).TotalSeconds:F6}s.");
        }
        catch (OperationCanceledException) { CountdownStatus = "Countdown cancelled; no cue recorded."; }
        catch (Exception ex) { CountdownStatus = "Cue metadata was not saved. Check the diagnostic error."; ShowError(ex); }
        finally { cueCancellation = null; UpdateControls(); }
    }

    private void UpdateStatistics()
    {
        if (!engine.IsRunning) cueCancellation?.Cancel();
        snapshot = engine.Statistics;
        var now = rateClock.Elapsed.TotalSeconds;
        var elapsed = now - previousSeconds;
        PacketsPerSecond = engine.IsRunning && elapsed > 0 ? Math.Max(0, snapshot.TotalPackets - previousPackets) / elapsed : 0;
        previousPackets = snapshot.TotalPackets;
        previousSeconds = now;
        CaptureStatus = engine.IsRunning ? "Running" : "Stopped";
        CurrentFile = engine.Session?.FilePath ?? "—";
        foreach (var property in new[] { nameof(PacketsPerSecond), nameof(TotalPackets), nameof(TotalBytes), nameof(TcpCount), nameof(UdpCount), nameof(OtherCount), nameof(DropSummary) }) Notify(property);
        if (engine.QueueDroppedPackets > lastDropped)
        {
            lastDropped = engine.QueueDroppedPackets;
            AddLog("Warning", $"Capture queue full: {lastDropped} packets dropped in this session.");
        }
        if (engine.MetadataErrors > lastMetadataErrors)
        {
            lastMetadataErrors = engine.MetadataErrors;
            AddLog("Warning", $"Metadata could not be read for {lastMetadataErrors} packets; raw packets saved and counted as Other.");
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!IsInitialized) return;
        var editable = !busy && !closing && !engine.IsRunning;
        RefreshButton.IsEnabled = AdapterCombo.IsEnabled = editable;
        ProcessRefreshButton.IsEnabled = ConnectionsRefreshButton.IsEnabled = ProcessCombo.IsEnabled = editable;
        CaptureModeCombo.IsEnabled = SessionLabelBox.IsEnabled = UserNotesBox.IsEnabled = editable;
        StartButton.IsEnabled = !busy && !closing && !engine.IsRunning && npcapReady && SelectedAdapter is not null;
        StopButton.IsEnabled = !busy && !closing && engine.IsRunning;
        CountdownButton.IsEnabled = !busy && !closing && engine.IsRunning && cueCancellation is null;
    }

    private void OnDiagnostic(DiagnosticMessage message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            AddLog(message.Level, message.Message, message.TimestampUtc);
            if (message.Level == "Error") StatusMessage = message.Message;
        });
    }

    private void AddLog(string level, string message, DateTimeOffset? timestamp = null)
    {
        Logs.Add($"{(timestamp ?? DateTimeOffset.UtcNow).UtcDateTime:HH:mm:ss} UTC [{level}] {message}");
        while (Logs.Count > 100) Logs.RemoveAt(0);
    }

    // The bounded log may scroll internally; its visibility requests must not scroll the page.
    private void DiagnosticLogRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    private void ShowError(Exception error)
    {
        StatusMessage = error.Message;
        if (AdapterDiscovery.IsNativeLoadFailure(error))
        {
            npcapReady = false;
            NpcapStatus = "Missing";
            StatusMessage = AdapterDiscovery.MissingNpcapMessage;
        }
        AddLog("Error", StatusMessage);
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        cueCancellation?.Cancel();
        UpdateControls();
        timer.Stop();
        while (busy) await Task.Delay(50);
        await cueTask;
        try { await engine.DisposeAsync(); }
        catch (Exception ex)
        {
            ShowError(ex);
            MessageBox.Show(this, ex.Message, "Capture shutdown error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        engine.Diagnostic -= OnDiagnostic;
        allowClose = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Aion2Meter.sln"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
