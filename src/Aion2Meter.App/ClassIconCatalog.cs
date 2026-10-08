using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Meter.Core;

namespace Aion2Meter.App;

/// <summary>User-provided development assets, replaceable independently of protocol and accounting.</summary>
public sealed class ClassIconCatalog
{
    private readonly Dictionary<PlayerClass, ImageSource?> cache = [];
    private readonly Func<Uri, ImageSource?> load;
    public ClassIconCatalog(Func<Uri, ImageSource?>? load = null) => this.load = load ?? Load;
    public static Uri? ResourceUri(PlayerClass playerClass) => playerClass == PlayerClass.Unknown || !Enum.IsDefined(playerClass)
        ? null : new($"pack://application:,,,/Aion2Meter.App;component/Assets/ClassIcons/{playerClass.ToString().ToLowerInvariant()}.png");
    public ImageSource? Get(PlayerClass playerClass)
    {
        var uri = ResourceUri(playerClass); if (uri is null) return null;
        if (cache.TryGetValue(playerClass, out var icon)) return icon;
        try { icon = load(uri); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { icon = null; }
        cache.Add(playerClass, icon); return icon;
    }
    private static ImageSource Load(Uri uri)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = uri; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}

public sealed class ClassIconConverter : IValueConverter
{
    private static readonly ClassIconCatalog Icons = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PlayerClass playerClass ? Icons.Get(playerClass) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
