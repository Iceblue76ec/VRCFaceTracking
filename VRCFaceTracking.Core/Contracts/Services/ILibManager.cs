using System.Collections.ObjectModel;

namespace VRCFaceTracking.Core.Contracts.Services;

public interface ILibManager
{
    ObservableCollection<ModuleMetadataInternal> LoadedModulesMetadata { get; set; }
    Task Initialize();
    Task TeardownAllModules();
    // File changes and rollback share the same lifecycle lock as Initialize and teardown.
    Task ChangeModules(Action change, Action? rollback = null, CancellationToken cancellationToken = default);
}