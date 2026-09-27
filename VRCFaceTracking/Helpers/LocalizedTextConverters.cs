using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using VRCFaceTracking.Core.Models;
using AppStrings = VRCFaceTracking.Strings.Resources;

namespace VRCFaceTracking.Helpers;

public sealed class InstallStateTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            InstallState.NotInstalled => AppStrings.Registry_NotInstalled,
            InstallState.Installed => AppStrings.Registry_Installed,
            InstallState.Outdated => AppStrings.Registry_Outdated,
#pragma warning disable CS0618
            InstallState.AwaitingRestart => AppStrings.Registry_AwaitingRestart,
#pragma warning restore CS0618
            _ => value?.ToString()
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MutationTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name)
            return value;

        var key = "Mutation_" + Regex.Replace(name, "[^A-Za-z0-9]+", "_").Trim('_');
        return AppStrings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? name;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PlaceholderTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            "Initializing Modules..." => AppStrings.Main_InitializingModules,
            "Loading..." => AppStrings.Main_Loading,
            "Waiting for VRChat" => Strings.MainPageStrings.WaitingForVrchat,
            _ => value
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
