using System.ComponentModel;
using System.Diagnostics;
using Aion2Meter.Core;

namespace Aion2Meter.Capture;

public sealed class WindowsProcessNameResolver : IProcessNameResolver
{
    public string? Resolve(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        // Only the process name is requested. No executable path/modules or memory access.
        using var process = Process.GetProcessById(pid);
        return process.ProcessName;
    }
}

public sealed class ProcessNetworkDiscovery(IEndpointTableSource source, IProcessNameResolver names)
{
    public ProcessNetworkDiscovery() : this(new WindowsEndpointTableSource(), new WindowsProcessNameResolver()) { }

    public ProcessNetworkSnapshot Refresh()
    {
        var table = source.Read();
        var processes = new List<NetworkProcess>();
        foreach (var pid in table.Endpoints.Select(e => e.Pid).Where(p => p > 0).Distinct())
        {
            string? name = null;
            try { name = names.Resolve(pid); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException) { }
            processes.Add(new(pid, string.IsNullOrWhiteSpace(name) ? "Unavailable / exited" : name));
        }
        var byPid = processes.ToDictionary(p => p.Pid, p => p.ProcessName);
        return new(processes.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToArray(),
            table.Endpoints.Where(e => byPid.ContainsKey(e.Pid)).Select(e => e with { ProcessName = byPid[e.Pid] }).ToArray(), table.Warnings);
    }
}
