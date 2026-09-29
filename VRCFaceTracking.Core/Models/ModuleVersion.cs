namespace VRCFaceTracking.Core.Models;

public static class ModuleVersion
{
    public static bool IsNewer(string remote, string installed)
    {
        if (Version.TryParse(remote, out var remoteVersion) && Version.TryParse(installed, out var installedVersion))
            return Normalize(remoteVersion).CompareTo(Normalize(installedVersion)) > 0;
        return string.CompareOrdinal(remote, installed) > 0;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor,
        Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
