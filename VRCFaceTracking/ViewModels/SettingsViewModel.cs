using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Models;
using VRCFaceTracking.Services;
using VRCFaceTracking.Strings;

namespace VRCFaceTracking.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    [ObservableProperty] private List<GithubContributor> _contributors = [];

    public IOscTarget OscTarget { get; }
    public RiskySettingsViewModel RiskySettings { get; }

    private readonly OpenVRService _openVRService;
    private readonly ILocalSettingsService _settings;
    private bool _autoStart;
    private bool _settingAutoStart;
    private bool _loadingAutoStart;
    private bool _userChangedAutoStart;

    [ObservableProperty] private string _autoStartStatus = string.Empty;

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (_autoStart == value) return;
            _autoStart = value;
            OnPropertyChanged();
            if (_loadingAutoStart) return;
            _userChangedAutoStart = true;
            _ = ApplyAutoStartAsync(value);
        }
    }

    public bool IsOpenVREnabled => _openVRService.IsAvailable && !_settingAutoStart;

    public SettingsViewModel(
        GithubService githubService,
        OpenVRService openVRService,
        ILocalSettingsService settings,
        IOscTarget oscTarget,
        RiskySettingsViewModel riskySettingsViewModel)
    {
        _openVRService = openVRService;
        _settings = settings;
        OscTarget = oscTarget;
        RiskySettings = riskySettingsViewModel;

        _ = LoadAutoStartAsync();
        LoadContributors(githubService);
    }

    private async Task LoadAutoStartAsync()
    {
        var saved = false;
        try
        {
            saved = await _settings.ReadSettingAsync("SteamVrAutoStart", false);
        }
        catch
        {
            // A missing or corrupt local preference must not block the settings page.
        }
        var actual = await Task.Run(() => _openVRService.TryGetAutoStart(out var value) ? (bool?)value : null);
        if (_userChangedAutoStart) return;
        _loadingAutoStart = true;
        AutoStart = actual ?? saved;
        _loadingAutoStart = false;
    }

    private async Task ApplyAutoStartAsync(bool value)
    {
        _settingAutoStart = true;
        OnPropertyChanged(nameof(IsOpenVREnabled));
        AutoStartStatus = string.Empty;
        try
        {
            var result = await Task.Run(() =>
            {
                var applied = _openVRService.TrySetAutoStart(value, out var error);
                return (applied, error);
            });
            if (!result.applied)
            {
                _autoStart = !value;
                OnPropertyChanged(nameof(AutoStart));
                AutoStartStatus = string.Format(Resources.ResourceManager.GetString("AutoStartSettings_Failed") ??
                    "SteamVR could not apply this setting ({0}). Restart SteamVR and try again.", result.error);
                return;
            }

            try
            {
                await _settings.SaveSettingAsync("SteamVrAutoStart", value);
            }
            catch (Exception ex)
            {
                AutoStartStatus = string.Format(Resources.ResourceManager.GetString("AutoStartSettings_SaveFailed") ??
                    "SteamVR was updated, but this app could not save the preference ({0}).", ex.Message);
                return;
            }
            AutoStartStatus = Resources.ResourceManager.GetString("AutoStartSettings_Saved") ??
                "SteamVR auto start has been updated. It will take effect on the next SteamVR launch.";
        }
        catch (Exception ex)
        {
            AutoStartStatus = string.Format(Resources.ResourceManager.GetString("AutoStartSettings_Failed") ??
                "SteamVR could not apply this setting ({0}). Restart SteamVR and try again.", ex.Message);
        }
        finally
        {
            _settingAutoStart = false;
            OnPropertyChanged(nameof(IsOpenVREnabled));
        }
    }

    private async void LoadContributors(GithubService githubService)
    {
        try
        {
            Contributors = await githubService.GetContributors("benaclejames/VRCFaceTracking");
        }
        catch
        {
        }
    }
}
