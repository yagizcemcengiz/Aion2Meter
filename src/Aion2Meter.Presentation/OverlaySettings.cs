using System.Text.Json;
using Aion2Meter.Core;

namespace Aion2Meter.Presentation;

public sealed record OverlayPosition(int LeftPixels, int TopPixels, string? Monitor);
public sealed record OverlaySettings(string? AdapterIdentifier = null, bool Locked = false, double Opacity = 0.92,
    double Scale = 1, OverlayPosition? Position = null, double Width = 420, double Height = 150,
    string HideHotkey = "Ctrl+Shift+H", string ResetHotkey = "Ctrl+Shift+R", PlayerClass? SelfClassOverride = null)
{
    public OverlaySettings Validated() => this with
    {
        AdapterIdentifier = AdapterIdentifier is { Length: > 0 and <= 512 } ? AdapterIdentifier : null,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.5, 1) : 0.92,
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.75, 1.5) : 1,
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 360, 1000) : 420,
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 150, 1000) : 150,
        HideHotkey = HotkeyGesture.TryParse(HideHotkey, out var hide) ? hide.Text : "Ctrl+Shift+H",
        ResetHotkey = HotkeyGesture.TryParse(ResetHotkey, out var reset) ? reset.Text : "Ctrl+Shift+R",
        SelfClassOverride = SelfClassOverride is { } selected && Enum.IsDefined(selected) && selected != PlayerClass.Unknown ? selected : null,
        Position = Position?.Monitor is { Length: > 512 } ? Position with { Monitor = null } : Position
    };

    public static NetworkAdapter? SelectAdapter(IEnumerable<NetworkAdapter> adapters, string? identifier) =>
        adapters.FirstOrDefault(a => a.Identifier == identifier && !a.Identifier.StartsWith("rpcap", StringComparison.OrdinalIgnoreCase) &&
            a.IPv4Addresses.Concat(a.IPv6Addresses).Any());
}

public sealed class OverlaySettingsStore(string path)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2Meter", "overlay-settings.json");
    public (OverlaySettings Settings, string? Warning) Load()
    {
        try
        {
            if (!File.Exists(path)) return (new(), null);
            if (new FileInfo(path).Length > 16 * 1024) return (new(), "Overlay settings were too large; using defaults.");
            return ((JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path)) ?? new()).Validated(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return (new(), "Overlay settings could not be read; using defaults."); }
    }
    public async Task SaveAsync(OverlaySettings settings)
    {
        await writes.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(directory);
                var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try { File.WriteAllText(temp, JsonSerializer.Serialize(settings.Validated())); File.Move(temp, path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
}
