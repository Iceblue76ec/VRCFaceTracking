using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Helpers;

public class LogLevelBrushConverter : IValueConverter
{
    public static readonly LogLevelBrushConverter Instance = new();

    private static readonly IBrush Error    = new SolidColorBrush(Color.Parse("#F44747"));
    private static readonly IBrush Critical = new SolidColorBrush(Color.Parse("#A70023"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch
            {
                LogLevel.Error => Error,
                LogLevel.Critical => Critical,
                _ => AvaloniaProperty.UnsetValue
            }
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
