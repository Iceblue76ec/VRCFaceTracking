using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.DependencyInjection;
using FluentAvalonia.Styling;
using VRCFaceTracking.ViewModels;
using VRCFaceTracking.Services;
using VRCFaceTracking.Strings;
using AppStrings = VRCFaceTracking.Strings.Resources;

namespace VRCFaceTracking.Views;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;
    private readonly LanguageService _languageService;
    private bool _initializingLanguage = true;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<SettingsViewModel>();
        _languageService = Ioc.Default.GetRequiredService<LanguageService>();

        var selected = _languageService.SelectedLanguage;
        LanguageCombo.SelectedIndex = LanguageCombo.Items.OfType<ComboBoxItem>()
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry => entry.item.Tag?.ToString() == selected).index;
        _initializingLanguage = false;

        // Show current version
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = string.Format(AppStrings.Settings_Version, version?.ToString(3) ?? AppStrings.Settings_UnknownVersion);

        var faTheme = Application.Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (faTheme != null)
        {
            ThemeCombo.SelectedIndex = Application.Current?.RequestedThemeVariant?.Key?.ToString() switch
            {
                "Light" => 0,
                "Dark" => 1,
                _ => 2
            };
        }
    }

    private async void LanguageCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializingLanguage || LanguageCombo.SelectedItem is not ComboBoxItem item)
            return;
        var selected = item.Tag?.ToString();
        if (selected == _languageService.SelectedLanguage)
            return;

        try
        {
            await _languageService.SetLanguageAsync(selected);
            if (TopLevel.GetTopLevel(this) is MainWindow window)
                window.RefreshLanguage();
        }
        catch
        {
            LanguageStatus.Text = LanguageStrings.SaveFailed;
            _initializingLanguage = true;
            LanguageCombo.SelectedIndex = LanguageCombo.Items.OfType<ComboBoxItem>()
                .Select((option, index) => (option, index))
                .First(entry => entry.option.Tag?.ToString() == _languageService.SelectedLanguage).index;
            _initializingLanguage = false;
        }
    }

    private void ThemeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;

        Application.Current!.RequestedThemeVariant = item.Tag?.ToString() switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void ForceReInit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.RiskySettings.ForceReInit();
    }

    private void ResetVRCFT_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.RiskySettings.ResetVRCFT();
    }

    private void ResetAvatarConfig_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.RiskySettings.ResetAvatarOscManifests();
    }

    private async void ContributorButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrEmpty(url))
        {
            try
            {
                var launcher = TopLevel.GetTopLevel(this)?.Launcher;
                if (launcher != null)
                    await launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        }
    }
}
