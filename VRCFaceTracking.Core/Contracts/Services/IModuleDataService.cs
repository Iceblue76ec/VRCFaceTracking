using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Core.Contracts.Services;

public interface IModuleDataService
{
    Task<IEnumerable<InstallableTrackingModule>> GetRemoteModules();
    Task<ModuleCatalogResult> RefreshModuleCatalogAsync();
    Task<int?> GetMyRatingAsync(TrackingModuleMetadata moduleMetadata);
    Task SetMyRatingAsync(TrackingModuleMetadata moduleMetadata, int rating);
    IEnumerable<InstallableTrackingModule> GetInstalledModules();
    Task<bool> IsModuleEnabledAsync(InstallableTrackingModule module);
    Task SetModuleEnabledAsync(InstallableTrackingModule module, bool enabled);
    Task IncrementDownloadsAsync(TrackingModuleMetadata moduleMetadata);
    IEnumerable<InstallableTrackingModule> GetLegacyModules();
}
