using System.Windows;
using System.Windows.Controls;
using Aion2Meter.App;
using Xunit;
using static Aion2Meter.App.Tests.OverlayWindowTests;

namespace Aion2Meter.App.Tests;

[Collection("WPF")]
public sealed class DiagnosticExportWindowTests
{
    [Fact]
    public Task ExportIsAvailableFromSettingsWithoutStartingAnotherCapture() => Sta(() =>
    {
        var window = new OverlaySettingsWindow(new()); var requests = 0; var starts = 0;
        window.ExportDiagnosticsRequested += () => requests++;
        window.StartRequested += _ => starts++;
        var button = (Button)window.FindName("ExportDiagnosticsButton");
        Assert.True(button.IsEnabled); Assert.Equal("Export diagnostics", button.Content);
        window.UpdateSession(false, false, false, "Data unavailable", null);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, requests); Assert.Equal(0, starts); window.Finish();
    });
}
