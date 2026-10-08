using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Aion2Meter.Presentation;
using Microsoft.Win32;

namespace Aion2Meter.App;

public partial class OverlayWindow : Window
{
    private readonly Func<OverlaySnapshot> readLatest;
    private readonly Action openSettings;
    private readonly Func<Task> stop;
    private readonly Action reset;
    private readonly Action hide;
    private readonly DispatcherTimer refresh = new() { Interval = LiveOverlaySession.RefreshInterval };
    private OverlaySettings settings;
    private bool closing, allowClose, loaded;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ClosedTask => closed.Task;
    public OverlayViewModel ViewModel { get; } = new();
    public event Action<OverlaySettings>? SettingsChanged;
    public event Action<string>? Diagnostic;
    public event Action? HiddenByButton;

    public OverlayWindow(OverlaySettings settings, Func<OverlaySnapshot> readLatest, Action openSettings, Func<Task> stop, Action? reset = null, Action? hide = null)
    {
        this.settings = settings.Validated(); this.readLatest = readLatest; this.openSettings = openSettings; this.stop = stop;
        this.reset = reset ?? (() => {});
        this.hide = hide ?? Hide;
        InitializeComponent(); DataContext = ViewModel; ApplySettings(this.settings);
        refresh.Tick += (_, _) => ViewModel.Apply(readLatest());
        DpiChanged += (_, _) => QueuePlacement();
        SizeChanged += (_, _) => { if (IsLoaded) QueuePlacement(); };
    }

    public void ApplySettings(OverlaySettings value)
    {
        settings = value.Validated(); Opacity = settings.Opacity;
        ViewModel.SetSelfClassOverride(settings.SelfClassOverride);
        Panel.LayoutTransform = new ScaleTransform(settings.Scale, settings.Scale);
        Panel.Width = Math.Min(settings.Width, SystemParameters.WorkArea.Width / settings.Scale);
        Panel.MinHeight = Math.Min(settings.Height, SystemParameters.WorkArea.Height / settings.Scale);
        RowsScroll.MaxHeight = Math.Max(80, SystemParameters.WorkArea.Height / settings.Scale - 120);
        ResizeGrip.IsEnabled = !settings.Locked;
        if (IsLoaded) QueuePlacement();
    }

    private void OverlayLoaded(object sender, RoutedEventArgs e)
    {
        if (closing || loaded) return;
        loaded = true;
        try { NativeOverlayPlacement.Restore(this, settings.Position); }
        catch (System.ComponentModel.Win32Exception ex) { Diagnostic?.Invoke(ex.Message); }
        ViewModel.Apply(readLatest()); refresh.Start();
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
    }
    private void DisplaysChanged(object? sender, EventArgs e) => QueuePlacement();
    private void QueuePlacement()
    {
        if (closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!closing && IsLoaded)
                try { NativeOverlayPlacement.Restore(this, NativeOverlayPlacement.Read(this)); }
                catch (System.ComponentModel.Win32Exception ex) { Diagnostic?.Invoke(ex.Message); }
        });
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (settings.Locked || e.ChangedButton != MouseButton.Left) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null;
             source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; } // Button was released before the drag started.
        try { NativeOverlayPlacement.Restore(this, NativeOverlayPlacement.Read(this)); SavePosition(); }
        catch (System.ComponentModel.Win32Exception ex) { Diagnostic?.Invoke(ex.Message); }
    }
    private void SavePosition()
    {
        try { settings = settings with { Position = NativeOverlayPlacement.Read(this) }; }
        catch (System.ComponentModel.Win32Exception ex) { Diagnostic?.Invoke(ex.Message); }
        SettingsChanged?.Invoke(settings);
    }
    private void ResizeDragged(object sender, DragDeltaEventArgs e)
    {
        if (settings.Locked) return;
        ApplySettings(settings with { Width = settings.Width + e.HorizontalChange / settings.Scale,
            Height = Math.Max(settings.Height, ActualHeight / settings.Scale) + e.VerticalChange / settings.Scale });
    }
    private void ResizeCompleted(object sender, DragCompletedEventArgs e) => SavePosition();
    public void ToggleVisibility() { if (IsVisible) Hide(); else Show(); }
    private void HideClicked(object sender, RoutedEventArgs e) { hide(); HiddenByButton?.Invoke(); }
    private void ResetClicked(object sender, RoutedEventArgs e) => reset();
    private void SettingsClicked(object sender, RoutedEventArgs e) => openSettings();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private async void OverlayClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing) return;
        closing = true; refresh.Stop(); SavePosition();
        // Even an already-stopped session must let WPF finish its current Closing event.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try { await stop(); }
        catch (Exception ex) { Diagnostic?.Invoke("Shutdown: " + ex.Message); }
        finally { allowClose = true; Close(); }
    }
    private void OverlayClosed(object? sender, EventArgs e)
    {
        refresh.Stop(); SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        closed.TrySetResult();
    }
}
