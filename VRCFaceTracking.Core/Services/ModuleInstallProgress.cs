namespace VRCFaceTracking.Core.Services;

public enum ModuleInstallStage
{
    Connecting,
    Downloading,
    Extracting,
    Installing,
    Installed
}

public record ModuleInstallProgress(
    ModuleInstallStage Stage, double? Percent = null, long BytesReceived = 0, long? TotalBytes = null);
