using System.ComponentModel;
using System.Diagnostics;

namespace Aion2Meter.App;

public sealed record RunningProduct(int ProcessId, string? Executable);

/// <summary>One product process per Windows session, including older builds without a mutex.</summary>
public sealed class ProductInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;
    private ProductInstanceGuard(Mutex mutex) => this.mutex = mutex;

    public static ProductInstanceGuard? TryAcquire(out string? warning,
        string name = @"Local\Aion2Meter.ProductOverlay", Func<IReadOnlyList<RunningProduct>>? inspectLegacy = null)
    {
        // Atomic creation, rather than WaitOne, also rejects same-thread reentrant launches.
        var mutex = new Mutex(true, name, out var created);
        if (!created)
        {
            mutex.Dispose(); warning = "Aion2Meter is already running. Use its system tray to show the overlay, or exit it before switching builds.";
            return null;
        }
        var lease = new ProductInstanceGuard(mutex);
        try
        {
            var legacy = (inspectLegacy ?? InspectLegacy)();
            if (legacy.Count > 0)
            {
                warning = "Another Aion2Meter build is already running. Exit it from its tray/Close button before starting this build.\n\n" +
                    string.Join("\n", legacy.Take(8).Select(p => $"PID {p.ProcessId}: {p.Executable ?? "executable path unavailable"}"));
                lease.Dispose(); return null;
            }
            warning = null; return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static IReadOnlyList<RunningProduct> InspectLegacy()
    {
        using var current = Process.GetCurrentProcess(); var found = new List<RunningProduct>();
        foreach (var process in Process.GetProcessesByName("Aion2Meter.App"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId || process.HasExited) continue;
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch (Win32Exception) { path = null; } // An elevated older process still blocks a duplicate.
                    found.Add(new(process.Id, path));
                }
                catch (InvalidOperationException) { } // Exited during discovery.
            }
        }
        return found;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        mutex.ReleaseMutex(); mutex.Dispose();
    }
}
