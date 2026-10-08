using System.Windows;

namespace Aion2Meter.App;

public partial class App : Application
{
    private OverlayApplication? product;
    private ProductInstanceGuard? instance;
    protected override void OnExit(ExitEventArgs e)
    {
        try { product?.DisposeRecovery(); }
        finally { instance?.Dispose(); base.OnExit(e); }
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            instance = ProductInstanceGuard.TryAcquire(out var warning);
            if (instance is null)
            {
                MessageBox.Show(warning, "Aion2Meter already running", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
            product = new(); await product.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Aion2Meter startup", MessageBoxButton.OK, MessageBoxImage.Error);
            if (MainWindow is OverlayWindow) MainWindow.Close();
            else Shutdown(1);
        }
    }
}
