using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Aion2Meter.Presentation;

namespace Aion2Meter.App;

/// <summary>Only our overlay and OS monitor work areas. No game handles, process inspection or global hooks.</summary>
internal static class NativeOverlayPlacement
{
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public Rectangle Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rectangle rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rectangle rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private static IReadOnlyList<DisplayWorkArea> Monitors()
    {
        var monitors = new List<DisplayWorkArea>();
        if (!EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rectangle rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var work = info.Work;
            var area = new DisplayWorkArea(info.Device, work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
            if (info.Flags == 1) monitors.Insert(0, area); else monitors.Add(area);
            return true;
        }, 0) || monitors.Count == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Monitor work areas could not be read.");
        return monitors;
    }
    public static OverlayPosition Read(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        return new(bounds.Left, bounds.Top, GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) ? info.Device : null);
    }
    public static void Restore(Window window, OverlayPosition? saved)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var position = OverlayPlacement.Restore(saved, Monitors(), bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        // Preserve size/z-order, never activate during restore, DPI changes or monitor changes.
        if (!SetWindowPos(hwnd, 0, position.LeftPixels, position.TopPixels, 0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
