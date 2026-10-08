using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Aion2Meter.Core;
using Aion2Meter.Presentation;

namespace Aion2Meter.App;

public partial class OverlaySettingsWindow : Window
{
    private OverlaySettings settings;
    private bool ready, exit;
    public event Action<OverlaySettings>? PreferencesChanged;
    public event Action<NetworkAdapter>? StartRequested;
    public event Action? ExportDiagnosticsRequested;
    public event Action? StopRequested, RefreshRequested, ShowRequested, ResetRequested, AdvancedRequested;
    public OverlaySettingsWindow(OverlaySettings settings)
    {
        this.settings = settings.Validated(); InitializeComponent();
        MyClass.ItemsSource = Enum.GetValues<PlayerClass>().Select(c => new ClassChoice(c,
            c == PlayerClass.Unknown ? "Automatic / Unknown" : c.ToString())).ToArray();
        Synchronize(this.settings); ready = true;
        SettingsScroll.MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 60);
    }
    public void Synchronize(OverlaySettings value)
    {
        var prior = ready; ready = false; value = value.Validated(); settings = value;
        OpacitySlider.Value = value.Opacity; ScaleSlider.Value = value.Scale; LockDragging.IsChecked = value.Locked;
        HideShortcut.Text = value.HideHotkey; ResetShortcut.Text = value.ResetHotkey;
        MyClass.SelectedValue = value.SelfClassOverride ?? PlayerClass.Unknown;
        Labels(); ready = prior;
    }
    private void Labels()
    {
        OpacityValue.Text = settings.Opacity.ToString("0%", CultureInfo.InvariantCulture);
        ScaleValue.Text = settings.Scale.ToString("0%", CultureInfo.InvariantCulture);
    }
    public void Adapters(IEnumerable<NetworkAdapter> adapters)
    {
        Adapter.ItemsSource = adapters;
        Adapter.SelectedItem = OverlaySettings.SelectAdapter(adapters, settings.AdapterIdentifier);
    }
    public void UpdateSession(bool running, bool busy, bool advanced, string status, string? error)
    {
        SessionStatus.Text = status; ErrorText.Text = error ?? "";
        StartButton.IsEnabled = !running && !busy && !advanced;
        StopButton.IsEnabled = running && !busy;
        Adapter.IsEnabled = RefreshButton.IsEnabled = !running && !busy && !advanced;
        AdvancedButton.IsEnabled = !busy;
    }
    public void ShortcutWarning(string message) => HotkeyStatus.Text = message;
    private void Publish() { Labels(); PreferencesChanged?.Invoke(settings.Validated()); }
    private sealed record ClassChoice(PlayerClass Value, string Label);
    private void ClassChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || MyClass.SelectedValue is not PlayerClass selected) return;
        settings = settings with { SelfClassOverride = selected == PlayerClass.Unknown ? null : selected }; Publish();
    }
    private void StyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        settings = settings with { Opacity = OpacitySlider.Value, Scale = ScaleSlider.Value }; Publish();
    }
    private void LockChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings = settings with { Locked = LockDragging.IsChecked == true }; Publish();
    }
    private void ApplyShortcuts(object sender, RoutedEventArgs e)
    {
        if (!HotkeyGesture.TryParse(HideShortcut.Text, out var hide) || !HotkeyGesture.TryParse(ResetShortcut.Text, out var reset))
        { ShortcutWarning("Invalid shortcut. Use Ctrl/Alt/Win + a letter, digit or F1–F11."); return; }
        if (hide == reset) { ShortcutWarning("Use different shortcuts for hide/show and reset."); return; }
        settings = settings with { HideHotkey = hide.Text, ResetHotkey = reset.Text }; Publish();
    }
    private void StartClicked(object sender, RoutedEventArgs e)
    {
        if (Adapter.SelectedItem is NetworkAdapter adapter) StartRequested?.Invoke(adapter);
        else ErrorText.Text = "Select your active Ethernet or Wi-Fi adapter first.";
    }
    private void StopClicked(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void RefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void ShowClicked(object sender, RoutedEventArgs e) => ShowRequested?.Invoke();
    private void ResetClicked(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();
    private void ExportDiagnosticsClicked(object sender, RoutedEventArgs e) => ExportDiagnosticsRequested?.Invoke();
    private void AdvancedClicked(object sender, RoutedEventArgs e) => AdvancedRequested?.Invoke();
    private void DoneClicked(object sender, RoutedEventArgs e) { ShowRequested?.Invoke(); Hide(); }
    private void SettingsClosing(object? sender, CancelEventArgs e) { if (!exit) { e.Cancel = true; ShowRequested?.Invoke(); Hide(); } }
    public void Finish() { exit = true; Close(); }
}
