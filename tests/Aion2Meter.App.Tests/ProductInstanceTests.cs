using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Aion2Meter.App;
using Xunit;
using static Aion2Meter.App.Tests.OverlayWindowTests;

namespace Aion2Meter.App.Tests;

[Collection("WPF")]
public sealed class ProductInstanceTests
{
    [Fact]
    public void MutexRejectsSameThreadAndOtherThreadDuplicateAndReleasesOnExit()
    {
        var name = @"Local\Aion2Meter.Test." + Guid.NewGuid().ToString("N");
        var first = ProductInstanceGuard.TryAcquire(out var warning, name, () => []);
        Assert.NotNull(first); Assert.Null(warning);
        try
        {
            Assert.Null(ProductInstanceGuard.TryAcquire(out warning, name, () => throw new Exception("Must not inspect or start another product")));
            Assert.Contains("already running", warning);
            ProductInstanceGuard? duplicate = null;
            var probe = new Thread(() => duplicate = ProductInstanceGuard.TryAcquire(out _, name, () => []));
            probe.Start(); Assert.True(probe.Join(TimeSpan.FromSeconds(5))); Assert.Null(duplicate);
        }
        finally { first.Dispose(); first.Dispose(); }
        using var next = ProductInstanceGuard.TryAcquire(out warning, name, () => []);
        Assert.NotNull(next); Assert.Null(warning);
    }

    [Fact]
    public void OlderBuildWithoutMutexBlocksBeforeProductCreationAndCanExitNormally()
    {
        var name = @"Local\Aion2Meter.Test." + Guid.NewGuid().ToString("N");
        Assert.Null(ProductInstanceGuard.TryAcquire(out var warning, name, () => [new(12, @"C:\old\Aion2Meter.App.exe"), new(13, null)]));
        Assert.Contains("PID 12", warning); Assert.Contains(@"C:\old\Aion2Meter.App.exe", warning);
        Assert.Contains("PID 13", warning); Assert.Contains("path unavailable", warning);
        using var afterExit = ProductInstanceGuard.TryAcquire(out warning, name, () => []);
        Assert.NotNull(afterExit); Assert.Null(warning);
    }

    [Fact]
    public void FailedLegacyInspectionReleasesMutexWithoutStartingProduct()
    {
        var name = @"Local\Aion2Meter.Test." + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => ProductInstanceGuard.TryAcquire(out _, name, () => throw new InvalidOperationException()));
        using var next = ProductInstanceGuard.TryAcquire(out _, name, () => []); Assert.NotNull(next);
    }

    [Fact]
    public Task AdvancedHandlersCannotCreateASecondOverlayEvenWhenInvokedDirectly() => Sta(() =>
    {
        var advanced = new MainWindow(productDiagnostics: true);
        ((Button)advanced.FindName("ShowOverlayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ((Button)advanced.FindName("StartLiveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("Meter stopped", advanced.LiveStatus);
        Assert.False(advanced.IsVisible); Assert.False(((Button)advanced.FindName("ShowOverlayButton")).IsEnabled);
        var closed = false; advanced.Closed += (_, _) => closed = true; advanced.Close(); Pump(() => closed);
    });
}
