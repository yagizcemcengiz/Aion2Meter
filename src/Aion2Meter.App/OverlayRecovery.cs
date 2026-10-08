namespace Aion2Meter.App;

public enum TrayAction { Toggle, Reset, Settings, Exit }
public interface IOverlayTray : IDisposable
{
    event Action<TrayAction>? Requested;
    void UpdateVisibility(bool visible);
}

/// <summary>One owner and the same commands for tray, header, settings and hotkeys; never touches capture.</summary>
public sealed class OverlayRecovery : IDisposable
{
    private readonly IOverlayTray tray;
    private readonly Func<bool> visible;
    private readonly Action show, hide, reset, settings, exit;
    private bool disposed;
    public OverlayRecovery(IOverlayTray tray, Func<bool> visible, Action show, Action hide, Action reset, Action settings, Action exit)
    {
        this.tray = tray; this.visible = visible; this.show = show; this.hide = hide;
        this.reset = reset; this.settings = settings; this.exit = exit;
        tray.Requested += Dispatch; Update();
    }
    public void Show() { if (disposed) return; show(); Update(); }
    public void Hide() { if (disposed) return; hide(); Update(); }
    public void Toggle() { if (visible()) Hide(); else Show(); }
    public void Reset() { if (!disposed) reset(); }
    public void Settings() { if (!disposed) settings(); }
    public void Exit() { if (!disposed) exit(); }
    public void Update() { if (!disposed) tray.UpdateVisibility(visible()); }
    private void Dispatch(TrayAction action)
    {
        switch (action) { case TrayAction.Toggle: Toggle(); break; case TrayAction.Reset: Reset(); break;
            case TrayAction.Settings: Settings(); break; case TrayAction.Exit: Exit(); break; }
    }
    public void Dispose() { if (disposed) return; disposed = true; tray.Requested -= Dispatch; tray.Dispose(); }
}
