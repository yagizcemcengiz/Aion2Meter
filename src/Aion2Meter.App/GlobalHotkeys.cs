using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Aion2Meter.Presentation;

namespace Aion2Meter.App;

public interface IHotkeyRegistrar
{
    bool Register(int id, uint modifiers, uint key);
    void Unregister(int id);
}

/// <summary>Registration ownership is independently testable without reserving real desktop shortcuts.</summary>
public sealed class HotkeyBindings(IHotkeyRegistrar registrar, Action toggle, Action reset) : IDisposable
{
    private readonly HashSet<int> registered = [];
    private bool disposed;
    public bool CanToggle => !disposed && registered.Contains(1);
    public string Apply(string hideText, string resetText)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!HotkeyGesture.TryParse(hideText, out var hide) || !HotkeyGesture.TryParse(resetText, out var current))
            return "Use Ctrl/Alt/Win + a letter, number or F1–F11 (Shift is optional).";
        if (hide == current) return "Hide/show and reset shortcuts must be different.";
        Release();
        var warnings = new List<string>();
        Bind(1, hide, "Hide/show"); Bind(2, current, "Reset");
        return string.Join(" ", warnings);
        void Bind(int id, HotkeyGesture gesture, string label)
        {
            if (registrar.Register(id, gesture.Modifiers | 0x4000, gesture.VirtualKey)) registered.Add(id);
            else warnings.Add($"{label} shortcut ({gesture.Text}) is unavailable. Choose another combination; the tray and settings buttons still work.");
        }
    }
    public void Dispatch(int id)
    {
        if (disposed || !registered.Contains(id)) return;
        if (id == 1) toggle(); else if (id == 2) reset();
    }
    private void Release() { foreach (var id in registered) registrar.Unregister(id); registered.Clear(); }
    public void Dispose() { if (disposed) return; disposed = true; Release(); }
}

public sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource source;
    private readonly HotkeyBindings bindings;
    private bool disposed;
    public GlobalHotkeys(Window owner, Action toggle, Action reset)
    {
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        source = HwndSource.FromHwnd(handle)!;
        bindings = new(new Registrar(handle), toggle, reset);
        source.AddHook(Hook);
    }
    public string Apply(OverlaySettings settings) => bindings.Apply(settings.HideHotkey, settings.ResetHotkey);
    public bool CanToggle => bindings.CanToggle;
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312) { bindings.Dispatch((int)wParam); handled = true; }
        return 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; bindings.Dispose(); source.RemoveHook(Hook);
    }
    private sealed class Registrar(nint handle) : IHotkeyRegistrar
    {
        public bool Register(int id, uint modifiers, uint key) => RegisterHotKey(handle, id, modifiers, key);
        public void Unregister(int id) => UnregisterHotKey(handle, id);
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
