using System.Globalization;
using Avalonia.Data.Converters;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Helpers;

public class LogLevelMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level && parameter is string name && level.ToString() == name;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
