using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Meter.App;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.App.Tests.OverlayWindowTests;

namespace Aion2Meter.App.Tests;

[Collection("WPF")]
public sealed class OverlayRowPolishTests
{
    private static OverlaySnapshot Snapshot(int count) => new(OverlayState.InCombat, "In combat", "", 15,
        "Coverage: Partial", "", Array.AsReadOnly(Enumerable.Range(1, count).Select(i =>
            new OverlayRow($"scope/{i}", i, i == count ? "Local With A Long Name" : $"Remote Participant {i}",
                i == count, 1240000000 - i * 10000, 65744 - i * 100, count == 1 ? 100 : 100m / count, 2)).ToArray()), 85);

    [Fact]
    public Task MyClassSelectionImmediatelyUpdatesTheSameSelfIconWithoutResetOrStop() => Sta(() =>
    {
        var snapshot = Snapshot(2); var reset = 0; var stop = 0; var published = 0;
        var window = new OverlayWindow(new(), () => snapshot, () => {}, () => { stop++; return Task.CompletedTask; }, () => reset++);
        window.ViewModel.Apply(snapshot); Layout(window);
        var preferences = new OverlaySettingsWindow(new());
        preferences.PreferencesChanged += value => { published++; window.ApplySettings(value); preferences.Synchronize(value); };
        var combo = (ComboBox)preferences.FindName("MyClass"); Assert.Equal(9, combo.Items.Count);
        var self = window.ViewModel.Rows[1]; var remote = window.ViewModel.Rows[0];
        var image = Assert.Single(Descendants((DependencyObject)window.Content).OfType<Image>(), i => ReferenceEquals(i.DataContext, self));
        var catalog = new ClassIconCatalog();
        foreach (var playerClass in Enum.GetValues<PlayerClass>().Where(c => c != PlayerClass.Unknown))
        {
            combo.SelectedValue = playerClass;
            Assert.Equal(playerClass, self.Class); Assert.Equal(PlayerClass.Unknown, remote.Class);
            Assert.Same(self, window.ViewModel.Rows[1]); Assert.Equal("1.24B", self.Damage);
            Layout(window); Assert.Equal(Visibility.Visible, image.Visibility);
            Assert.Same(image, Assert.Single(Descendants((DependencyObject)window.Content).OfType<Image>(), i => ReferenceEquals(i.DataContext, self)));
            Assert.Equal(ClassIconCatalog.ResourceUri(playerClass), Assert.IsType<BitmapImage>(image.Source).UriSource);
            Assert.True(image.Source.IsFrozen); Assert.Equal(96, Assert.IsType<BitmapImage>(catalog.Get(playerClass)).PixelWidth);
            Assert.Equal(20, image.Width); Assert.Equal(20, image.Height);
            Assert.Equal(0, reset); Assert.Equal(0, stop);
        }
        Assert.Equal(8, published);
        combo.SelectedValue = PlayerClass.Unknown; Layout(window); Assert.Null(image.Source); Assert.Equal(Visibility.Collapsed, image.Visibility);
        Assert.Equal(9, published); preferences.Synchronize(new(SelfClassOverride: PlayerClass.Cleric)); Assert.Equal(9, published);
        Assert.Equal(PlayerClass.Cleric, combo.SelectedValue);
        Assert.All(snapshot.Rows, r => Assert.Equal(PlayerClass.Unknown, r.Class));
        preferences.Finish(); window.Close(); Pump(() => window.ClosedTask.IsCompleted); Assert.Equal(1, stop);
    });

    [Theory]
    [InlineData(0)] [InlineData(10)] [InlineData(50)] [InlineData(100)]
    public Task ThickFillHasExactZeroAndFullWidthBoundaries(int percent) => Sta(() =>
    {
        var snapshot = Snapshot(1) with { Rows = Array.AsReadOnly(new[] { Snapshot(1).Rows[0] with { ContributionPercent = percent } }) };
        var window = new OverlayWindow(new(), () => snapshot, () => {}, () => Task.CompletedTask);
        window.ViewModel.Apply(snapshot); Layout(window);
        var bar = Assert.Single(Descendants((DependencyObject)window.Content).OfType<ProgressBar>());
        var track = (FrameworkElement)bar.Template.FindName("PART_Track", bar);
        var indicator = (Border)bar.Template.FindName("PART_Indicator", bar);
        Assert.InRange(bar.ActualHeight, 40, 48); Assert.Equal(8, indicator.CornerRadius.TopLeft);
        Assert.InRange(Math.Abs(indicator.ActualWidth - track.ActualWidth * percent / 100d), 0, 1);
        Assert.Equal((Color)ColorConverter.ConvertFromString(window.ViewModel.Rows[0].Accent), ((SolidColorBrush)indicator.Background).Color);
        Assert.InRange(indicator.Opacity, .5, 1);
        if (Environment.GetEnvironmentVariable("AION2METER_UI_PREVIEWS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory); var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width), (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); using var file = File.Create(Path.Combine(directory, $"fill-{percent}.png"));
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(file);
        }
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });

    [Fact]
    public Task TallWindowKeepsFixedRowsAtTopAndOverflowScrolls() => Sta(() =>
    {
        var snapshot = Snapshot(2);
        var window = new OverlayWindow(new(Height: 600), () => snapshot, () => {}, () => Task.CompletedTask);
        window.ViewModel.Apply(snapshot); Layout(window);
        var scroll = (ScrollViewer)window.FindName("RowsScroll");
        var bars = Descendants((DependencyObject)window.Content).OfType<ProgressBar>().ToArray();
        Assert.All(bars, bar => Assert.Equal(40, bar.ActualHeight));
        Assert.InRange(bars[0].TransformToAncestor(scroll).Transform(new Point()).Y, 0, 1);
        Assert.True(bars[1].TransformToAncestor(scroll).Transform(new Point()).Y < 50);
        window.ViewModel.Apply(Snapshot(40)); Layout(window);
        Assert.True(scroll.ScrollableHeight > 0); Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        Assert.All(Descendants((DependencyObject)window.Content).OfType<ProgressBar>(), bar => Assert.Equal(40, bar.ActualHeight));
        scroll.ScrollToEnd(); Layout(window); Assert.True(scroll.VerticalOffset > 0);
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });

    [Theory]
    [InlineData(1, 360, .75)] [InlineData(1, 420, 1)] [InlineData(1, 640, 1.5)]
    [InlineData(2, 360, 1.5)] [InlineData(2, 420, .75)] [InlineData(2, 640, 1)]
    [InlineData(8, 360, 1)] [InlineData(8, 420, 1.5)] [InlineData(8, 640, .75)]
    public Task SoloAndManyRowsKeepNumbersAndSelfBadgeReadable(int count, double width, double scale) => Sta(() =>
    {
        var snapshot = Snapshot(count);
        var window = new OverlayWindow(new(Width: width, Scale: scale, SelfClassOverride: PlayerClass.Cleric),
            () => snapshot, () => {}, () => Task.CompletedTask);
        window.ViewModel.Apply(snapshot); Layout(window); var content = (FrameworkElement)window.Content;
        var tree = Descendants(content).ToArray();
        Assert.Equal(count, tree.OfType<ProgressBar>().Count());
        Assert.Single(tree.OfType<Image>(), i => i.Visibility == Visibility.Visible);
        var badge = Assert.Single(tree.OfType<TextBlock>(), t => t.Text == "YOU" && ((FrameworkElement)t.Parent).Visibility == Visibility.Visible);
        Assert.True(Assert.IsType<OverlayRowViewModel>(badge.DataContext).IsSelf);
        Assert.Equal(count, window.ViewModel.Rows.Single(r => r.IsSelf).Rank);
        foreach (var text in tree.OfType<TextBlock>().Where(t => t.Name is "RowDps" or "RowTotal" or "RowContribution" || t == badge))
        {
            var measured = new FormattedText(text.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Assert.True(text.ActualWidth + 1 >= measured.WidthIncludingTrailingWhitespace, $"Clipped metric {text.Name}: {text.Text}");
            var bounds = text.TransformToAncestor(content).TransformBounds(new Rect(text.RenderSize));
            Assert.InRange(bounds.Right, 0, content.ActualWidth + 1);
        }
        // Explicitly compare metric bounds to catch overlap rather than relying on clipped DesiredSize.
        foreach (var row in window.ViewModel.Rows)
        {
            var metrics = tree.OfType<TextBlock>().Where(t => ReferenceEquals(t.DataContext, row) &&
                t.Name is "RowDps" or "RowTotal" or "RowContribution").Select(t =>
                    t.TransformToAncestor(content).TransformBounds(new Rect(t.RenderSize))).OrderBy(r => r.Left).ToArray();
            Assert.Equal(3, metrics.Length); Assert.True(metrics[0].Right <= metrics[1].Left); Assert.True(metrics[1].Right <= metrics[2].Left);
        }
        if (Environment.GetEnvironmentVariable("AION2METER_UI_PREVIEWS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width), (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); using var file = File.Create(Path.Combine(directory, FormattableString.Invariant($"rows-{count}-{width:0}-{scale * 100:0}.png")));
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(file);
        }
        window.Close(); Pump(() => window.ClosedTask.IsCompleted);
    });
}
