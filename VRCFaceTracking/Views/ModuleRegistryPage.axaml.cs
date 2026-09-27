using System.Globalization;
using System.Net.Http;
using System.Security.Authentication;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.DependencyInjection;
using VRCFaceTracking.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Strings;
using AppStrings = VRCFaceTracking.Strings.Resources;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public partial class ModuleRegistryPage : UserControl, INotifyNavigated
{
    private ModuleRegistryViewModel ViewModel => (ModuleRegistryViewModel)DataContext!;
    private readonly ModuleInstaller _moduleInstaller;
    private readonly ILibManager _libManager;
    private readonly IModuleDataService _moduleDataService;

    public ModuleRegistryPage()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<ModuleRegistryViewModel>();
        _moduleInstaller = Ioc.Default.GetRequiredService<ModuleInstaller>();
        _libManager = Ioc.Default.GetRequiredService<ILibManager>();
        _moduleDataService = Ioc.Default.GetRequiredService<IModuleDataService>();
    }

    public async void OnNavigatedTo() => await ViewModel.OnNavigatedTo();

    private async void RefreshCatalog_Click(object? sender, RoutedEventArgs e)
    {
        RefreshCatalogButton.IsEnabled = false;
        try { await ViewModel.OnNavigatedTo(); }
        finally { RefreshCatalogButton.IsEnabled = true; }
    }

    private void ModuleSelection_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {   
        if (ViewModel.Selected is not InstallTrackedTrackingModule module) return;
        InstallButton.IsVisible = module.InstallationState != InstallState.Installed;
        UninstallButton.IsVisible = module.InstalledModule != null;
        InstallButton.Content = AppStrings.Registry_Install;
        InstallButton.IsEnabled = true;
        ModuleActivationStatus.Text = string.Empty;
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
        InstallProgress.Value = 0;
        InstallStatusText.IsVisible = true;
        InstallStatusText.Text = AppStrings.Registry_Preparing;

        var acceptProgress = true;
        try
        {
            var progress = new Progress<ModuleInstallProgress>(update =>
            {
                if (!acceptProgress) return;
                InstallStatusText.Text = update.Stage switch
                {
                    ModuleInstallStage.Connecting => AppStrings.Registry_Connecting,
                    ModuleInstallStage.Downloading when update.TotalBytes is { } total => string.Format(
                        CultureInfo.CurrentCulture, AppStrings.Registry_DownloadSizeKnown,
                        update.BytesReceived / 1_000_000.0, total / 1_000_000.0),
                    ModuleInstallStage.Downloading => string.Format(
                        CultureInfo.CurrentCulture, AppStrings.Registry_DownloadSizeUnknown,
                        update.BytesReceived / 1_000_000.0),
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
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            InstallStatusText.Text = AppStrings.Registry_Installed_Success;
            await ViewModel.RefreshInstalledAsync();
        }
        catch (OperationCanceledException)
        {
            InstallButton.Content = AppStrings.Registry_Retry;
            InstallStatusText.Text = AppStrings.Registry_Timeout;
            InstallProgress.IsVisible = false;
        }
        catch (HttpRequestException ex)
        {
            InstallButton.Content = AppStrings.Registry_Retry;
            var reason = ex.StatusCode is { } statusCode
                ? string.Format(CultureInfo.CurrentCulture, AppStrings.Registry_HttpError, (int)statusCode)
                : IsSecureConnectionError(ex)
                    ? string.Format(AppStrings.Registry_SslError, ex.GetBaseException().Message)
                    : string.Format(AppStrings.Registry_NetworkError, ex.GetBaseException().Message);
            InstallStatusText.Text = string.Format(AppStrings.Registry_Failure, reason);
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
            acceptProgress = false;
            ModuleList.IsEnabled = true;
            InstallButton.IsEnabled = true;
        }
    }

    private static bool IsSecureConnectionError(Exception error)
    {
        if (error is AuthenticationException ||
            error is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
            return true;

        if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsSecureConnectionError))
            return true;

        return error.InnerException is { } inner && IsSecureConnectionError(inner);
    }

    private async void UninstallButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel.Selected is not InstallTrackedTrackingModule module) return;

        UninstallButton.IsEnabled = false;
        await _moduleInstaller.UninstallModule(module.TrackingModuleMetadata);
        await ViewModel.RefreshInstalledAsync();
    }

    private async void ModuleStateButton_Click(object? sender, RoutedEventArgs e)
    {
        var module = ViewModel.Selected;
        if (module?.InstalledModule is not { } installed) return;
        var enabled = !module.IsEnabled;
        ModuleStateButtons.IsEnabled = false;
        ModuleList.IsEnabled = false;
        var previous = !enabled;
        try
        {
            previous = await _moduleDataService.IsModuleEnabledAsync(installed);
            if (previous == enabled)
            {
                module.IsEnabled = previous;
                return;
            }
            await _moduleDataService.SetModuleEnabledAsync(installed, enabled);
            module.IsEnabled = enabled;

            try
            {
                await _libManager.Initialize();
                ModuleActivationStatus.Text = string.Empty;
            }
            catch
            {
                ModuleActivationStatus.Text = ModuleRegistryStrings.ActivationFailed;
            }
        }
        catch
        {
            module.IsEnabled = previous;
            ModuleActivationStatus.Text = ModuleRegistryStrings.ActivationFailed;
        }
        finally
        {
            ModuleStateButtons.IsEnabled = true;
            ModuleList.IsEnabled = true;
        }
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
            Title = AppStrings.Registry_InstallZip_Tooltip,
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType(AppStrings.Registry_ZipFiles_Type)
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
            await ViewModel.RefreshInstalledAsync();
        }
    }
}
