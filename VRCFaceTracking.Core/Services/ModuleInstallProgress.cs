namespace VRCFaceTracking.Core.Services;

public enum ModuleInstallStage
{
    Downloading,
    Extracting,
    Installing,
    Installed
}

public record ModuleInstallProgress(ModuleInstallStage Stage, double? Percent = null);
