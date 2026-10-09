using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Aion2Meter.App;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.App.Tests.OverlayWindowTests;

namespace Aion2Meter.App.Tests;

[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollection { }

[Collection("WPF")]
public sealed class OverlayRefinementWindowTests
{
    private sealed class Tray : IOverlayTray
    {
        private Action<TrayAction>? requested;
        public event Action<TrayAction>? Requested { add { requested += value; } remove { requested -= value; } }
        public int Subscribers => requested?.GetInvocationList().Length ?? 0;
        public bool OverlayVisible; public int Disposed;
        public void Send(TrayAction action) => requested?.Invoke(action);
        public void UpdateVisibility(bool visible) => OverlayVisible = visible;
        public void Dispose() => Disposed++;
    }
    private sealed class RejectedKeys : IHotkeyRegistrar
    {
        public bool Register(int id, uint modifiers, uint key) => false;
        public void Unregister(int id) { }
    }
    private sealed class AcceptedKeys : IHotkeyRegistrar
    {
        public bool Register(int id, uint modifiers, uint key) => true;
        public void Unregister(int id) { }
    }
    private sealed class RunningSource : IPacketSource
    {
        public string SourceId => "tray-lifecycle-test";
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public async IAsyncEnumerable<SourcePacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { Reading.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); yield break; }
            finally { Disposed = true; }
        }
    }

    [Fact]
    public Task HeaderTrayAndHotkeysShareCommandsAndKeepSessionUntilOrderlyExit() => Sta(() =>
    {
        var tray = new Tray(); var reset = 0; var stop = 0; var settingsCalls = 0;
        var source = new RunningSource(); var session = new LiveOverlaySession(source, [IPAddress.Parse("192.0.2.5")]);
        source.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        OverlayRecovery? recovery = null; var preferences = new OverlaySettingsWindow(new());
        var encounter = new OverlaySnapshot(OverlayState.InCombat, "In combat", "", 10, "Coverage: Partial", "",
            Array.AsReadOnly(new[] { new OverlayRow("test/self", 1, "Local", true, 5830, 583, 100, 6) }));
        var window = new OverlayWindow(new(), () => encounter,
            () => recovery!.Settings(), async () => { stop++; recovery!.Dispose(); await session.DisposeAsync(); preferences.Finish(); },
            () => recovery!.Reset(), () => recovery!.Hide());
        recovery = new(tray, () => window.IsVisible, window.Show, window.Hide, () => reset++,
            () => { settingsCalls++; preferences.Show(); }, window.Close);
        preferences.PreferencesChanged += window.ApplySettings;
        window.Show(); Layout(window);
        var handle = new WindowInteropHelper(window).Handle; var vm = window.ViewModel; var row = Assert.Single(vm.Rows);
        var size = window.RenderSize; var position = new Point(window.Left, window.Top);
        ((ComboBox)preferences.FindName("MyClass")).SelectedValue = PlayerClass.Cleric;
        Assert.Equal(0, reset); Assert.Equal(0, stop); Assert.True(session.IsRunning); Assert.False(source.Disposed);
        ((Button)window.FindName("HideButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(window.IsVisible); Assert.Equal(0, stop); Assert.False(tray.OverlayVisible);
        Assert.True(session.IsRunning); Assert.False(source.Disposed);
        tray.Send(TrayAction.Toggle); Assert.True(window.IsVisible); Assert.True(tray.OverlayVisible);
        tray.Send(TrayAction.Toggle); Assert.False(window.IsVisible);
        tray.Send(TrayAction.Settings); Assert.True(preferences.IsVisible); Assert.Equal(1, settingsCalls); Assert.False(window.IsVisible);
        using var hotkeys = new HotkeyBindings(new AcceptedKeys(), recovery.Toggle, recovery.Reset);
        hotkeys.Apply("Ctrl+Shift+H", "Ctrl+Shift+R"); hotkeys.Dispatch(1); Assert.True(window.IsVisible);
        hotkeys.Dispatch(1); Assert.False(window.IsVisible); hotkeys.Dispatch(1); Assert.True(window.IsVisible);
        tray.Send(TrayAction.Reset); hotkeys.Dispatch(2);
        ((Button)window.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Assert.Equal(3, reset);
        for (var i = 0; i < 10; i++) { recovery.Hide(); recovery.Show(); }
        Assert.Same(vm, window.ViewModel); Assert.Same(row, Assert.Single(vm.Rows)); Assert.Equal("5.83K", row.Damage);
        Assert.Equal(handle, new WindowInteropHelper(window).Handle); Assert.Equal(size, window.RenderSize);
        Assert.Equal(position, new Point(window.Left, window.Top)); Assert.Equal(PlayerClass.Cleric, row.Class);
        var roots = PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).ToArray();
        Assert.Same(window, Assert.Single(roots.OfType<OverlayWindow>())); Assert.Empty(roots.OfType<MainWindow>());
        Assert.Equal(1, tray.Subscribers); Assert.Equal(0, tray.Disposed); Assert.Equal(0, stop);
        Assert.True(session.IsRunning); Assert.False(source.Disposed);
        tray.Send(TrayAction.Exit); Pump(() => window.ClosedTask.IsCompleted);
        Assert.Equal(1, stop); Assert.Equal(0, tray.Subscribers); Assert.Equal(1, tray.Disposed);
        Assert.True(source.Disposed); Assert.False(session.IsRunning);
        recovery.Dispose(); recovery.Exit(); recovery.Toggle(); Assert.Equal(1, stop); Assert.Equal(1, tray.Disposed);
        tray.Send(TrayAction.Reset); Assert.Equal(3, reset);
    });

    [Fact]
    public Task HotkeyConflictStillLeavesTrayRecoveryAndSettingsReachable() => Sta(() =>
    {
        var tray = new Tray(); var visible = true; var settings = 0;
        using var recovery = new OverlayRecovery(tray, () => visible, () => visible = true, () => visible = false,
            () => {}, () => settings++, () => {});
        using var keys = new HotkeyBindings(new RejectedKeys(), recovery.Toggle, recovery.Reset);
        var warning = keys.Apply("Ctrl+Shift+H", "Ctrl+Shift+R"); Assert.Contains("tray", warning);
        Assert.False(keys.CanToggle); recovery.Hide(); keys.Dispatch(1); Assert.False(visible);
        tray.Send(TrayAction.Toggle); Assert.True(visible); recovery.Hide(); tray.Send(TrayAction.Settings); Assert.Equal(1, settings);
    });

    [Fact]
    public Task NativeBadgesCoverKnownUnknownAndInvalidClassesWithoutImageResources() => Sta(() =>
    {
        var expected = new Dictionary<PlayerClass, string> {
            [PlayerClass.Gladiator] = "GL", [PlayerClass.Templar] = "TE", [PlayerClass.Ranger] = "RA",
            [PlayerClass.Assassin] = "AS", [PlayerClass.Spiritmaster] = "SP", [PlayerClass.Sorcerer] = "SO",
            [PlayerClass.Cleric] = "CL", [PlayerClass.Chanter] = "CH", [PlayerClass.Unknown] = "?" };
        var converter = new ClassBadgeConverter();
        foreach (var item in expected)
            Assert.Equal(item.Value, converter.Convert(item.Key, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("?", ClassBadgeConverter.Label((PlayerClass)999));
        Assert.Equal("?", converter.Convert("invalid", typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        using var stream = typeof(OverlayWindow).Assembly.GetManifestResourceStream("Aion2Meter.App.g.resources")!;
        using var resources = new System.Resources.ResourceReader(stream);
        foreach (System.Collections.DictionaryEntry resource in resources)
            Assert.DoesNotContain("assets/classicons/", (string)resource.Key, StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(PlayerClass.Unknown)] [InlineData(PlayerClass.Gladiator)]
    public Task CompactRowHasCorrectHierarchyBarAndNativeClassBadge(PlayerClass playerClass) => Sta(() =>
    {
        var snapshot = new OverlaySnapshot(OverlayState.InCombat, "In combat", "", 10, "Coverage: Partial", "",
            Array.AsReadOnly(new[] { new OverlayRow("self", 2, "Local", true, 1400000, 65744, 31.2m, 10, playerClass) }));
        var window = new OverlayWindow(new(), () => snapshot, () => {}, () => Task.CompletedTask);
        window.ViewModel.Apply(snapshot); Layout(window);
        var tree = Descendants((DependencyObject)window.Content).ToArray(); var texts = tree.OfType<TextBlock>().ToArray();
        var dps = Assert.Single(texts, t => t.Text == "65.7K"); var total = Assert.Single(texts, t => t.Text == "1.40M");
        Assert.True(total.FontSize > dps.FontSize); Assert.Equal(FontWeights.Bold, total.FontWeight);
        Assert.Contains(texts, t => t.Text == "TOTAL"); Assert.Contains(texts, t => t.Text == "DPS");
        Assert.DoesNotContain(texts, t => t.Text == "ME");
        Assert.Equal(Grid.GetRow(dps), Grid.GetRow(total)); Assert.Contains(texts, t => t.Text == "31.2%");
        Assert.Contains(texts, t => t.Text == "YOU"); Assert.Equal(2, window.ViewModel.Rows[0].Rank);
        var bar = Assert.Single(tree.OfType<ProgressBar>()); Assert.Equal(31.2, bar.Value);
        var track = bar.Template.FindName("PART_Track", bar) as FrameworkElement;
        var indicator = bar.Template.FindName("PART_Indicator", bar) as FrameworkElement;
        Assert.NotNull(track); Assert.NotNull(indicator); Assert.InRange(indicator.ActualWidth / track.ActualWidth, .30, .32);
        var badge = Assert.Single(texts, t => t.Name == "ClassBadge");
        Assert.Equal(playerClass == PlayerClass.Unknown ? "?" : "GL", badge.Text);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Empty(tree.OfType<Image>());
        Assert.InRange(bar.ActualHeight, 40, 48);
        Assert.Equal(20, badge.Width); Assert.Equal(20, badge.Height);
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });

    [Theory]
    [InlineData(360, .75)] [InlineData(360, 1)] [InlineData(360, 1.5)]
    [InlineData(420, .75)] [InlineData(420, 1)] [InlineData(420, 1.5)]
    [InlineData(640, .75)] [InlineData(640, 1)] [InlineData(640, 1.5)]
    public Task MultirowLayoutRendersAtAllWidthsAndScales(double width, double scale) => Sta(() =>
    {
        // Presentation fixtures, never a claim that a real actor's class has been resolved.
        var snapshot = new OverlaySnapshot(OverlayState.InCombat, "In combat", "", 14.5, "Coverage: Partial", "",
            Array.AsReadOnly(new[] {
                new OverlayRow("a", 1, "Party Member Alpha", false, 657440, 45340m, 35, 10, PlayerClass.Templar),
                new OverlayRow("b", 2, "Member Beta", false, 563520, 38863m, 30, 10, PlayerClass.Ranger),
                new OverlayRow("c", 3, "Local", true, 375680, 25909m, 20, 10),
                new OverlayRow("d", 4, "Party Member Delta", false, 281760, 19431m, 15, 10)
            }), NetworkRttMilliseconds: 85);
        var window = new OverlayWindow(new(Width: width, Scale: scale), () => snapshot, () => {}, () => Task.CompletedTask);
        window.ViewModel.Apply(snapshot); Layout(window); var content = (FrameworkElement)window.Content;
        Assert.InRange(content.DesiredSize.Width, width * scale - 1, width * scale + 2);
        var tree = Descendants(content).ToArray(); Assert.Equal(4, tree.OfType<ProgressBar>().Count());
        Assert.Empty(tree.OfType<Image>());
        Assert.Equal(4, tree.OfType<TextBlock>().Count(t => t.Name == "ClassBadge"));
        Assert.Equal(2, tree.OfType<TextBlock>().Count(t => t.Name == "ClassBadge" && t.Text != "?"));
        foreach (var text in tree.OfType<TextBlock>().Where(t => t.Name is "RowDps" or "RowTotal" or "RowContribution"))
            Assert.True(text.ActualWidth >= text.DesiredSize.Width);
        if (Environment.GetEnvironmentVariable("AION2METER_UI_PREVIEWS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width), (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); using var file = File.Create(Path.Combine(directory, FormattableString.Invariant($"compact-{width:0}-{scale * 100:0}.png")));
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(file);
        }
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });
}
