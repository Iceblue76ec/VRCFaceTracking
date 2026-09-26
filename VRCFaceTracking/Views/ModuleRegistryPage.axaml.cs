using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.DependencyInjection;
using VRCFaceTracking.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.Services;
using AppStrings = VRCFaceTracking.Strings.Resources;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public partial class ModuleRegistryPage : UserControl, INotifyNavigated
{
    private ModuleRegistryViewModel ViewModel => (ModuleRegistryViewModel)DataContext!;
    private readonly ModuleInstaller _moduleInstaller;
    private readonly ILibManager _libManager;

    public ModuleRegistryPage()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<ModuleRegistryViewModel>();
        _moduleInstaller = Ioc.Default.GetRequiredService<ModuleInstaller>();
        _libManager = Ioc.Default.GetRequiredService<ILibManager>();
    }

    public async void OnNavigatedTo() => await ViewModel.OnNavigatedTo();

    private async void ModuleSelection_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {   
        if (ViewModel.Selected is not InstallTrackedTrackingModule module) return;
        InstallButton.IsVisible = module.InstallationState != InstallState.Installed;
        UninstallButton.IsVisible = module.InstallationState == InstallState.Installed;
        InstallButton.Content = AppStrings.Registry_Install;
        InstallButton.IsEnabled = true;
        if (module.InstallationState != InstallState.AwaitingRestart)
        {
            UninstallButton.IsEnabled = true;
            UninstallButton.Content = AppStrings.Registry_Uninstall;
        }
    }
    private async void InstallButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel.Selected is not InstallTrackedTrackingModule module) return;
        InstallButton.IsEnabled = false;
        ModuleList.IsEnabled = false;
        InstallButton.Content = AppStrings.Registry_Installing;
        InstallProgress.IsVisible = true;
        InstallProgress.IsIndeterminate = true;
        InstallStatusText.IsVisible = true;
        InstallStatusText.Text = AppStrings.Registry_Preparing;

        try
        {
            var progress = new Progress<ModuleInstallProgress>(update =>
            {
                InstallStatusText.Text = update.Stage switch
                {
                    ModuleInstallStage.Downloading => AppStrings.Registry_Downloading,
                    ModuleInstallStage.Extracting => AppStrings.Registry_Extracting,
                    ModuleInstallStage.Installing => AppStrings.Registry_Applying,
                    ModuleInstallStage.Installed => AppStrings.Registry_Installed,
                    _ => AppStrings.Registry_Installing
                };
                InstallProgress.IsIndeterminate = update.Percent is null;
                if (update.Percent is { } percent)
                    InstallProgress.Value = percent;
            });
            var installedPath = await _moduleInstaller.InstallRemoteModule(module.TrackingModuleMetadata, progress);
            if (installedPath == null)
                throw new InvalidDataException(AppStrings.Registry_Package_Invalid);
            module.InstallationState = InstallState.Installed;
            InstallButton.Content = AppStrings.Registry_Installed;
            InstallButton.IsVisible = false;
            UninstallButton.IsVisible = true;
            InstallStatusText.Text = AppStrings.Registry_Installed_Success;
        }
        catch (OperationCanceledException)
        {
            InstallButton.Content = AppStrings.Registry_Retry;
            InstallStatusText.Text = AppStrings.Registry_Timeout;
            InstallProgress.IsVisible = false;
        }
        catch (Exception ex)
        {
            InstallButton.Content = AppStrings.Registry_Retry;
            InstallStatusText.Text = string.Format(AppStrings.Registry_Failure, ex.Message);
            InstallProgress.IsVisible = false;
        }
        finally
        {
            ModuleList.IsEnabled = true;
            InstallButton.IsEnabled = true;
        }
    }

    private async void UninstallButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel.Selected is not InstallTrackedTrackingModule module) return;

        UninstallButton.IsEnabled = false;
        await _moduleInstaller.UninstallModule(module.TrackingModuleMetadata);
        await ViewModel.OnNavigatedTo();
    }

    private async void OpenModulePage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel.Selected?.TrackingModuleMetadata.ModulePageUrl is not { Length: > 0 } url) return;
        
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher != null)
                await launcher.LaunchUriAsync(new Uri(url));
        }
        catch { }
    }

    private async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install from .zip",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Zip Files")
                {
                    Patterns = (IReadOnlyList<string>)
                    [
                        "*.zip"
                    ],
                    AppleUniformTypeIdentifiers = (IReadOnlyList<string>)
                    [
                        "public.zip"
                    ],
                    MimeTypes = (IReadOnlyList<string>)
                        [
                            "application/zip",
                            "application/x-zip",
                            "application/x-zip-compressed",
                            "application/zip-compressed",
                            "multipart/x-zip"
                        ]
                    
                }
            ]
        });

        try
        {
            foreach (var file in files)
            {
                await _moduleInstaller.InstallLocalModule(file.Path.LocalPath);
            }
        }
        finally
        {
            await _libManager.Initialize();
        }
    }
}
