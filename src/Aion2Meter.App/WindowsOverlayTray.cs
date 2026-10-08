using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Aion2Meter.App;

/// <summary>First-party .NET notification-area component, created/disposed on the WPF UI thread.</summary>
public sealed class WindowsOverlayTray : IOverlayTray
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem toggle = new("Hide Overlay");
    private readonly Icon image;
    private bool disposed;
    public event Action<TrayAction>? Requested;
    public WindowsOverlayTray()
    {
        image = CreateIcon();
        icon = new() { Icon = image, Text = "Aion2Meter — click: Show/Hide; right-click: menu", ContextMenuStrip = menu };
        toggle.Click += (_, _) => Requested?.Invoke(TrayAction.Toggle); menu.Items.Add(toggle);
        Item("Reset Current", TrayAction.Reset); Item("Settings", TrayAction.Settings);
        menu.Items.Add(new Forms.ToolStripSeparator()); Item("Exit Aion2Meter", TrayAction.Exit);
        icon.MouseClick += Click;
        try { icon.Visible = true; }
        catch { Dispose(); throw; }
    }
    private void Item(string text, TrayAction action) => menu.Items.Add(text, null, (_, _) => Requested?.Invoke(action));
    private void Click(object? sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Requested?.Invoke(TrayAction.Toggle); }
    public void UpdateVisibility(bool visible) { if (!disposed) toggle.Text = visible ? "Hide Overlay" : "Show Overlay"; }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Requested = null;
        icon.MouseClick -= Click; icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose();
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32); using var graphics = Graphics.FromImage(bitmap);
        using var background = new SolidBrush(Color.FromArgb(24, 35, 53)); using var accent = new SolidBrush(Color.FromArgb(113, 225, 193));
        graphics.FillEllipse(background, 0, 0, 31, 31);
        graphics.FillRectangle(accent, 7, 18, 4, 7); graphics.FillRectangle(accent, 14, 12, 4, 13); graphics.FillRectangle(accent, 21, 7, 4, 18);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
