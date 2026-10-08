using System.Windows;
using System.Windows.Threading;
using Aion2Meter.Presentation;

namespace Aion2Meter.App;

public partial class MainWindow
{
    private readonly OverlaySettingsStore overlayStore = new(OverlaySettingsStore.DefaultPath);
    private readonly DispatcherTimer overlaySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private OverlaySettings overlaySettings = new();
    private LiveOverlaySession? liveSession;
    private OverlayWindow? overlay;
    private Task settingsWriteTask = Task.CompletedTask;
    private bool settingsLoaded, updatingOverlayControls;
    private string? lastLiveError;
    public string LiveStatus => liveSession?.Latest.Status ?? "Meter stopped";

    private void InitializeLiveControls()
    {
        overlaySaveTimer.Tick += (_, _) =>
        {
            overlaySaveTimer.Stop();
            if (!settingsWriteTask.IsCompleted) { overlaySaveTimer.Start(); return; }
            settingsWriteTask = PersistOverlaySettingsAsync(overlaySettings);
        };
    }
    private async Task LoadOverlaySettingsAsync()
    {
        var loaded = await Task.Run(overlayStore.Load);
        if (closing) return;
        overlaySettings = loaded.Settings; settingsLoaded = true; ApplyOverlayControls();
        if (loaded.Warning is not null) AddLog("Warning", loaded.Warning);
    }
    private async Task PersistOverlaySettingsAsync(OverlaySettings value)
    {
        try { await overlayStore.SaveAsync(value); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { if (!allowClose) AddLog("Warning", "Overlay preferences could not be saved: " + ex.Message); }
    }
    private void SaveOverlaySettingsSoon()
    {
        if (closing) return;
        overlaySaveTimer.Stop(); overlaySaveTimer.Start();
    }
    private void ApplyOverlayControls()
    {
        updatingOverlayControls = true;
        try { OverlayLock.IsChecked = overlaySettings.Locked; OverlayOpacity.Value = overlaySettings.Opacity; OverlayScale.Value = overlaySettings.Scale; }
        finally { updatingOverlayControls = false; }
    }
    private void OverlaySettingsChanged(OverlaySettings settings)
    {
        overlaySettings = settings; ApplyOverlayControls(); SaveOverlaySettingsSoon();
    }
    private void OverlayLockChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsLoaded || updatingOverlayControls) return;
        overlaySettings = overlaySettings with { Locked = OverlayLock.IsChecked == true };
        overlay?.ApplySettings(overlaySettings); SaveOverlaySettingsSoon();
    }
    private void OverlayStyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!settingsLoaded || updatingOverlayControls) return;
        overlaySettings = overlaySettings with { Opacity = OverlayOpacity.Value, Scale = OverlayScale.Value };
        overlay?.ApplySettings(overlaySettings); SaveOverlaySettingsSoon();
    }
    private async void StartLiveClicked(object sender, RoutedEventArgs e)
    {
        if (productDiagnostics || busy || closing || engine.IsRunning) return;
        if (SelectedAdapter is not { } adapter || !adapter.IPv4Addresses.Concat(adapter.IPv6Addresses).Any())
        { StatusMessage = "Select an available Ethernet or Wi-Fi capture adapter with a local IP address."; return; }
        busy = true; UpdateControls();
        try
        {
            await StopLiveAsync();
            if (closing) return;
            overlaySettings = overlaySettings with { AdapterIdentifier = adapter.Identifier };
            SaveOverlaySettingsSoon(); lastLiveError = null;
            liveSession = LiveOverlaySession.Create(adapter);
            ShowOverlay(); StatusMessage = "Live meter started. You can minimize this window; the overlay remains visible.";
        }
        catch (Exception ex) { await StopLiveAsync(); ShowError(ex); }
        finally { busy = false; Notify(nameof(LiveStatus)); UpdateControls(); }
    }
    private void ShowOverlayClicked(object sender, RoutedEventArgs e) => ShowOverlay();
    private void ShowOverlay()
    {
        if (productDiagnostics || closing) return;
        if (overlay is not null) { overlay.Show(); return; }
        var window = new OverlayWindow(overlaySettings, () => liveSession?.Latest ?? OverlaySnapshot.Stopped, () =>
        { WindowState = WindowState.Normal; Show(); Activate(); }, StopLiveAsync, () => liveSession?.ResetCurrent());
        overlay = window;
        window.HiddenByButton += () => { WindowState = WindowState.Normal; Show(); Activate(); UpdateControls(); };
        window.SettingsChanged += OverlaySettingsChanged;
        window.Diagnostic += message => AddLog("Warning", message);
        window.Closed += (_, _) => { window.SettingsChanged -= OverlaySettingsChanged; if (ReferenceEquals(overlay, window)) overlay = null; UpdateControls(); };
        window.Show();
    }
    private async void StopLiveClicked(object sender, RoutedEventArgs e)
    {
        busy = true; UpdateControls();
        try { await StopLiveAsync(); }
        finally { busy = false; UpdateControls(); }
    }
    private async Task StopLiveAsync()
    {
        if (liveSession is { } session)
        {
            await session.DisposeAsync();
            if (ReferenceEquals(liveSession, session)) liveSession = null;
        }
        Notify(nameof(LiveStatus)); UpdateControls();
    }
    private void UpdateLiveControls()
    {
        if (!IsInitialized) return;
        var running = liveSession?.IsRunning == true;
        StartLiveButton.IsEnabled = !productDiagnostics && !busy && !closing && !engine.IsRunning && !running && npcapReady &&
            SelectedAdapter is { } adapter && adapter.IPv4Addresses.Concat(adapter.IPv6Addresses).Any();
        StopLiveButton.IsEnabled = !busy && !closing && running;
        ShowOverlayButton.IsEnabled = !productDiagnostics && !closing && liveSession is not null && (overlay is null || !overlay.IsVisible);
        Notify(nameof(LiveStatus));
        if (liveSession?.Error is { } error && error != lastLiveError)
        {
            lastLiveError = error; AddLog("Error", error);
            StatusMessage = "Live data unavailable. Check the adapter, then start the live meter again.";
        }
    }
}
