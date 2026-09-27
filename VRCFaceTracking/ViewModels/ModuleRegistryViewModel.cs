using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Strings;

namespace VRCFaceTracking.ViewModels;

public partial class ModuleRegistryViewModel : ObservableRecipient
{
    private readonly IModuleDataService _moduleDataService;
    [ObservableProperty] private InstallTrackedTrackingModule? _selected;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _catalogStatus = string.Empty;
    private IReadOnlyList<InstallableTrackingModule> _catalog = [];
    private int _refreshVersion;

    public ObservableCollection<InstallTrackedTrackingModule> ModuleInfos { get; } = new();
    public ObservableCollection<InstallTrackedTrackingModule> FilteredModuleInfos { get; } = new();

    public ModuleRegistryViewModel(IModuleDataService moduleDataService)
    {
        _moduleDataService = moduleDataService;
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredModuleInfos.Clear();
        var query = SearchQuery?.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? ModuleInfos
            : ModuleInfos.Where(m =>
                (m.TrackingModuleMetadata.ModuleName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.TrackingModuleMetadata.AuthorName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        foreach (var m in filtered)
            FilteredModuleInfos.Add(m);
    }

    public async Task OnNavigatedTo()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        CatalogStatus = ModuleRegistryStrings.Loading;
        var result = await _moduleDataService.RefreshModuleCatalogAsync();
        if (version != Volatile.Read(ref _refreshVersion)) return;

        _catalog = result.Modules;
        CatalogStatus = result.FromCache
            ? result.Modules.Count > 0 ? ModuleRegistryStrings.UsingCache : ModuleRegistryStrings.NoCache
            : string.Empty;
        await RefreshInstalledAsync();
    }

    public async Task RefreshInstalledAsync()
    {
        var previous = Selected;
        var rows = _catalog.OrderByDescending(x => x.AuthorName == "VRCFT Team")
            .ThenBy(x => x.ModuleName)
            .Select(x => new InstallTrackedTrackingModule
            {
                TrackingModuleMetadata = x,
                InstallationState = InstallState.NotInstalled
            }).ToList();

        var installedModules = _moduleDataService.GetInstalledModules().Concat(_moduleDataService.GetLegacyModules());
        foreach (var installedModule in installedModules)
        {
            var remoteModule = installedModule.ModuleId == Guid.Empty ? null
                : rows.FirstOrDefault(x => x.TrackingModuleMetadata.ModuleId == installedModule.ModuleId);
            var enabled = await _moduleDataService.IsModuleEnabledAsync(installedModule);
            if (remoteModule == null)
            {
                rows.Insert(0, new InstallTrackedTrackingModule
                {
                    TrackingModuleMetadata = installedModule,
                    InstalledModule = installedModule,
                    InstallationState = InstallState.Installed,
                    IsEnabled = enabled
                });
            }
            else
            {
                remoteModule.InstallationState = IsRemoteNewer(remoteModule.TrackingModuleMetadata.Version, installedModule.Version)
                    ? InstallState.Outdated
                    : InstallState.Installed;
                remoteModule.InstalledModule = installedModule;
                remoteModule.IsEnabled = enabled;
                rows.Remove(remoteModule);
                rows.Insert(0, remoteModule);
            }
        }

        ModuleInfos.Clear();
        ModuleInfos.AddRange(rows);
        ApplyFilter();
        Selected = previous == null ? null : rows.FirstOrDefault(row =>
            row.TrackingModuleMetadata.ModuleId != Guid.Empty
                ? row.TrackingModuleMetadata.ModuleId == previous.TrackingModuleMetadata.ModuleId
                : row.InstalledModule?.AssemblyLoadPath == previous.InstalledModule?.AssemblyLoadPath);
    }

    private static bool IsRemoteNewer(string remote, string installed)
    {
        if (Version.TryParse(remote, out var remoteVersion) && Version.TryParse(installed, out var installedVersion))
            return remoteVersion.CompareTo(installedVersion) > 0;

        return string.CompareOrdinal(remote, installed) > 0;
    }
}
