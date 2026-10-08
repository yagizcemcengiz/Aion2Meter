using System.Windows;
using System.Windows.Threading;
using Aion2Meter.Capture;
using Aion2Meter.Core;
using Aion2Meter.Presentation;

namespace Aion2Meter.App;

/// <summary>Owns the product windows and exactly one live session. Advanced research is mutually exclusive.</summary>
public sealed class OverlayApplication
{
    private readonly OverlaySettingsStore store = new(OverlaySettingsStore.DefaultPath);
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private OverlaySettings preferences = new();
    private OverlayWindow overlay = null!;
    private OverlaySettingsWindow settings = null!;
    private GlobalHotkeys hotkeys = null!;
    private OverlayRecovery? recovery;
    private LiveOverlaySession? session;
    private MainWindow? advanced;
    private IReadOnlyList<NetworkAdapter> adapters = [];
    private bool busy, closing, started;
    private string? error;
    private string? lastDiagnostics;
    private Task write = Task.CompletedTask;

    public async Task StartAsync()
    {
        if (started) return; started = true;
        var loaded = await Task.Run(store.Load); preferences = loaded.Settings; error = loaded.Warning;
        settings = new(preferences);
        overlay = new(preferences, () => session?.Latest ?? OverlaySnapshot.Stopped,
            () => recovery?.Settings(), FinishAsync, () => recovery?.Reset(), () => recovery?.Hide());
        Application.Current.MainWindow = overlay;
        overlay.Closed += (_, _) => { DisposeRecovery(); settings.Finish(); Application.Current.Shutdown(); };
        recovery = new(new WindowsOverlayTray(), () => overlay.IsVisible, overlay.Show, overlay.Hide, Reset, OpenSettings, overlay.Close);
        overlay.IsVisibleChanged += (_, _) => recovery.Update();
        overlay.SettingsChanged += ChangePreferences;
        overlay.Diagnostic += message => { error = message; Update(); };
        settings.PreferencesChanged += ChangePreferences;
        settings.ShowRequested += recovery.Show;
        settings.ResetRequested += recovery.Reset;
        settings.StartRequested += async adapter => await StartMeterAsync(adapter);
        settings.StopRequested += async () => await StopMeterAsync();
        settings.RefreshRequested += async () => await DiscoverAsync();
        settings.ExportDiagnosticsRequested += async () => await ExportDiagnosticsAsync();
        settings.AdvancedRequested += async () => await OpenAdvancedAsync();
        overlay.Show();
        hotkeys = new(overlay, recovery.Toggle, recovery.Reset);
        settings.ShortcutWarning(hotkeys.Apply(preferences));
        pulse.Tick += (_, _) => Update(); pulse.Start();
        save.Tick += async (_, _) =>
        {
            save.Stop();
            if (!write.IsCompleted) { save.Start(); return; }
            write = PersistAsync(preferences); await write;
        };
        await DiscoverAsync();
        if (closing) return;
        var selected = OverlaySettings.SelectAdapter(adapters, preferences.AdapterIdentifier);
        if (selected is not null) await StartMeterAsync(selected);
        else OpenSettings();
    }
    private void OpenSettings() { if (closing) return; settings.Show(); settings.Activate(); }
    private void ChangePreferences(OverlaySettings value)
    {
        if (closing) return;
        var keysChanged = preferences.HideHotkey != value.HideHotkey || preferences.ResetHotkey != value.ResetHotkey;
        preferences = value.Validated(); overlay.ApplySettings(preferences); settings.Synchronize(preferences);
        if (keysChanged) settings.ShortcutWarning(hotkeys.Apply(preferences));
        save.Stop(); save.Start();
    }
    private async Task PersistAsync(OverlaySettings value)
    {
        try { await store.SaveAsync(value); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { error = "Preferences could not be saved: " + ex.Message; Update(); }
    }
    private void Update()
    {
        if (closing) return;
        if (session?.Error is { } failure) error = failure;
        settings.UpdateSession(session?.IsRunning == true, busy, advanced is not null,
            session?.Latest.Status ?? "Meter stopped", error);
    }
    private async Task DiscoverAsync()
    {
        if (busy || closing) return;
        busy = true; Update();
        try
        {
            var result = await Task.Run(() => new AdapterDiscovery().Discover());
            if (closing) return;
            adapters = result.Adapters.Where(a => OverlaySettings.SelectAdapter([a], a.Identifier) is not null).ToArray();
            settings.Adapters(adapters); error = result.Error ?? error;
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; Update(); }
    }
    private async Task StartMeterAsync(NetworkAdapter adapter)
    {
        if (busy || closing || advanced is not null || session?.IsRunning == true) return;
        if (OverlaySettings.SelectAdapter(adapters, adapter.Identifier) is null) { error = "The adapter is no longer available."; Update(); return; }
        busy = true; Update();
        try
        {
            await DisposeSessionAsync(); if (closing) return;
            error = null; ChangePreferences(preferences with { AdapterIdentifier = adapter.Identifier });
            session = LiveOverlaySession.Create(adapter); overlay.Show();
        }
        catch (Exception ex) { error = ex.Message; OpenSettings(); }
        finally { busy = false; Update(); }
    }
    private async Task DisposeSessionAsync()
    {
        var previous = session; session = null;
        if (previous is not null)
        {
            try { await previous.DisposeAsync(); }
            finally { lastDiagnostics = previous.ExportDiagnostics(); }
        }
    }
    private async Task ExportDiagnosticsAsync()
    {
        var json = session?.ExportDiagnostics() ?? lastDiagnostics;
        if (json is null) { error = "Start the meter once before exporting diagnostics."; Update(); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export Aion2Meter diagnostics", Filter = "JSON diagnostics (*.json)|*.json",
            FileName = "Aion2Meter-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(settings) != true) return;
        try { await System.IO.File.WriteAllTextAsync(dialog.FileName, json, new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { error = "Diagnostics export: " + ex.Message; Update(); }
    }

    private async Task StopMeterAsync()
    {
        if (busy || closing) return;
        busy = true; Update();
        try { await DisposeSessionAsync(); }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; Update(); }
    }
    private void Reset() { if (!closing && !busy) session?.ResetCurrent(); }
    private async Task OpenAdvancedAsync()
    {
        if (busy || closing) return;
        if (advanced is not null) { advanced.Show(); advanced.Activate(); return; }
        await StopMeterAsync(); if (closing) return;
        advanced = new MainWindow(productDiagnostics: true);
        advanced.Closed += (_, _) => { advanced = null; Update(); OpenSettings(); };
        advanced.Show(); Update();
    }
    private async Task FinishAsync()
    {
        closing = true; pulse.Stop(); save.Stop(); hotkeys?.Dispose(); DisposeRecovery();
        while (busy) await Task.Delay(25);
        try { await DisposeSessionAsync(); }
        catch (Exception ex) { error = "Meter shutdown: " + ex.Message; }
        if (advanced is { } diagnostics)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            diagnostics.Closed += (_, _) => done.TrySetResult(); diagnostics.Close(); await done.Task;
        }
        await write; await PersistAsync(preferences);
    }
    public void DisposeRecovery() => recovery?.Dispose();
}
