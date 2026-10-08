using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Aion2Meter.App;
using Aion2Meter.Presentation;
using Xunit;

namespace Aion2Meter.App.Tests;

public sealed class OverlayWindowTests
{
    private static OverlaySnapshot Combat => new(OverlayState.InCombat, "In combat", "", 12.8,
        "Coverage: Partial", "PARTIAL - 06/26 supported; 0x36 pending",
        Array.AsReadOnly(new[] { new OverlayRow("test-scope", 1, "Test Player", true, 123456, 9645m, 100, 12) }));

    [Theory]
    [InlineData(OverlayState.Waiting)]
    [InlineData(OverlayState.Ready)]
    [InlineData(OverlayState.InCombat)]
    [InlineData(OverlayState.Unavailable)]
    public Task ActualXamlTemplatesInstantiateWithReadableTextAndNoBindingErrors(OverlayState state) => Sta(() =>
    {
        var snapshot = state == OverlayState.Waiting ? OverlaySnapshot.Waiting : state == OverlayState.Unavailable ? OverlaySnapshot.Unavailable :
            state == OverlayState.Ready ? Combat with { State = state, Status = "Ready", Rows = Array.AsReadOnly(new[] { Combat.Rows[0] with { TotalDamage = 0, Dps = null } }) } : Combat;
        using var diagnostics = new StringWriter(); using var listener = new TextWriterTraceListener(diagnostics);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            var window = new OverlayWindow(new(), () => snapshot, () => {}, () => Task.CompletedTask);
            window.ViewModel.Apply(snapshot); Layout(window);
            Assert.True(window.Topmost); Assert.False(window.ShowInTaskbar); Assert.False(window.ShowActivated);
            Assert.True(window.AllowsTransparency); Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            var tree = Descendants((DependencyObject)window.Content).ToArray();
            if (snapshot.Rows.Count > 0)
            {
                var bar = Assert.Single(tree.OfType<ProgressBar>()); Assert.Equal(100d, bar.Value);
                var name = Assert.Single(tree.OfType<TextBlock>(), t => t.Text == "Test Player");
                Assert.Equal(Color.FromRgb(0xE9, 0xED, 0xF5), ((SolidColorBrush)name.Foreground).Color);
                Assert.Contains(tree.OfType<TextBlock>(), t => t.Text == "ME");
            }
            else Assert.Empty(tree.OfType<ProgressBar>());
            Assert.Equal("", diagnostics.ToString());
            window.Close(); Pump(() => window.ClosedTask.IsCompleted);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    });

    [Fact]
    public Task RefreshReusesActualRowControlsAndWaitingRemovesThem() => Sta(() =>
    {
        var window = new OverlayWindow(new(), () => Combat, () => {}, () => Task.CompletedTask);
        window.ViewModel.Apply(Combat); Layout(window);
        var before = Assert.Single(Descendants((DependencyObject)window.Content).OfType<ProgressBar>());
        window.ViewModel.Apply(Combat with { Rows = Array.AsReadOnly(new[] { Combat.Rows[0] with { TotalDamage = 200000 } }) }); Layout(window);
        Assert.Same(before, Assert.Single(Descendants((DependencyObject)window.Content).OfType<ProgressBar>()));
        window.ViewModel.Apply(OverlaySnapshot.Waiting); Layout(window);
        Assert.Empty(Descendants((DependencyObject)window.Content).OfType<ProgressBar>());
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });

    [Fact]
    public Task LockRemainsRecoverableAndScaleAndOpacityApplyToWindow() => Sta(() =>
    {
        var window = new OverlayWindow(new(Locked: true, Opacity: .6, Scale: 1.5), () => OverlaySnapshot.Waiting, () => {}, () => Task.CompletedTask);
        var button = (Button)window.FindName("LockButton"); Assert.Equal("Locked", button.Content);
        Assert.True(button.IsEnabled); Assert.False(button.Focusable); Assert.Equal(.6, window.Opacity);
        Layout(window); Assert.InRange(((FrameworkElement)window.Content).DesiredSize.Width, 629, 632);
        OverlaySettings? saved = null; window.SettingsChanged += value => saved = value;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Equal("Unlocked", button.Content);
        Assert.NotNull(saved); Assert.False(saved.Locked);
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });

    [Fact]
    public Task RepeatedCloseWaitsForStopExactlyOnceWithoutReentrantWpfClose() => Sta(() =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var called = 0;
        var window = new OverlayWindow(new(), () => OverlaySnapshot.Waiting, () => {}, () => { called++; return gate.Task; });
        window.Close(); window.Close(); Pump(() => called > 0);
        Assert.Equal(1, called); Assert.False(window.ClosedTask.IsCompleted);
        gate.SetResult(); Pump(() => window.ClosedTask.IsCompleted); Assert.Equal(1, called);
    });

    [Fact]
    public Task LauncherCanCloseBeforePreferencesLoadWithoutOverwritingUserSettings() => Sta(() =>
    {
        var path = OverlaySettingsStore.DefaultPath;
        var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        // Never Show: no Loaded discovery, adapter open, preferences load or capture.
        var window = new MainWindow(); var closed = false;
        Assert.NotNull(window.FindName("StartLiveButton"));
        Assert.False(((Button)window.FindName("StartLiveButton")).IsEnabled);
        window.Closed += (_, _) => closed = true; window.Close(); Pump(() => closed);
        var after = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        Assert.Equal(before, after);
    });

    [Fact]
    public Task PartyRowsUseExistingTemplatesAndReuseBothControlsAcrossRefresh() => Sta(() =>
    {
        var snapshot = Combat with { Rows = Array.AsReadOnly(new[]
        {
            new OverlayRow("remote", 1, "Party Member", false, 700, 70m, 70m, 2),
            new OverlayRow("self", 2, "Local Player", true, 300, 30m, 30m, 1)
        }) };
        using var diagnostics = new StringWriter(); using var listener = new TextWriterTraceListener(diagnostics);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            var window = new OverlayWindow(new(), () => snapshot, () => {}, () => Task.CompletedTask);
            window.ViewModel.Apply(snapshot); Layout(window);
            var bars = Descendants((DependencyObject)window.Content).OfType<ProgressBar>()
                .ToDictionary(b => ((OverlayRowViewModel)b.DataContext).Key);
            Assert.Equal(2, bars.Count); Assert.Equal(70d, bars["remote"].Value); Assert.Equal(30d, bars["self"].Value);
            Assert.True(window.ViewModel.Rows[1].IsSelf); Assert.Equal(2, window.ViewModel.Rows[1].Rank);
            for (var i = 0; i < 5; i++)
            {
                window.ViewModel.Apply(snapshot with { Rows = Array.AsReadOnly(snapshot.Rows.Select(r => r with { TotalDamage = r.TotalDamage + i }).ToArray()) });
                Layout(window);
                var after = Descendants((DependencyObject)window.Content).OfType<ProgressBar>()
                    .ToDictionary(b => ((OverlayRowViewModel)b.DataContext).Key);
                Assert.Same(bars["remote"], after["remote"]); Assert.Same(bars["self"], after["self"]);
            }
            Assert.Equal("", diagnostics.ToString()); window.Close(); Pump(() => window.ClosedTask.IsCompleted);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    });

    private static void Layout(OverlayWindow window)
    {
        var content = (FrameworkElement)window.Content;
        for (var i = 0; i < 2; i++)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); content.Arrange(new Rect(content.DesiredSize)); content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.Render);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        { if (complete() || clock.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); Assert.True(complete(), "WPF close did not finish within five seconds."); }
        finally { timer.Stop(); }
    }
    private static Task Sta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
