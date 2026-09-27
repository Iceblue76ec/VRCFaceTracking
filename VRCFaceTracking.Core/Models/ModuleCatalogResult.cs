namespace VRCFaceTracking.Core.Models;

public sealed record ModuleCatalogResult(
    IReadOnlyList<InstallableTrackingModule> Modules,
    bool FromCache);
