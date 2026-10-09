using System.Globalization;
using System.Windows.Data;
using Aion2Meter.Core;

namespace Aion2Meter.App;

/// <summary>Native text presentation only; class authority remains in the protocol/profile layer.</summary>
public sealed class ClassBadgeConverter : IValueConverter
{
    public static string Label(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Gladiator => "GL", PlayerClass.Templar => "TE", PlayerClass.Ranger => "RA",
        PlayerClass.Assassin => "AS", PlayerClass.Spiritmaster => "SP", PlayerClass.Sorcerer => "SO",
        PlayerClass.Cleric => "CL", PlayerClass.Chanter => "CH", _ => "?"
    };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Label(value is PlayerClass playerClass ? playerClass : PlayerClass.Unknown);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
