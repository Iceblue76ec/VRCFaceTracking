using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Models.ParameterDefinition.FileBased;

namespace VRCFaceTracking.Core.Services;

/// <summary>Recognizes VRCFaceTracking expression inputs without changing the active avatar mapping.</summary>
public static class AvatarTrackingSummary
{
    private static readonly Lazy<HashSet<string>> CurrentNames = new(() =>
        UnifiedTracking.AllParameters_v2.SelectMany(parameter => parameter.GetParamNames())
            .Select(parameter => parameter.paramName)
            .Where(IsTrackingName)
            .ToHashSet(StringComparer.Ordinal));

    private static readonly Lazy<HashSet<string>> LegacyNames = new(() =>
        UnifiedTracking.AllParameters_v1.SelectMany(parameter => parameter.GetParamNames())
            .Select(parameter => parameter.paramName)
            .Where(IsTrackingName)
            .ToHashSet(StringComparer.Ordinal));

    public static (int Count, int LegacyCount, bool PossiblyUnsupported) Analyze(IAvatarInfo avatar)
    {
        var count = 0;
        var legacyCount = 0;
        foreach (var parameter in avatar.Parameters ?? [])
        {
            if (parameter is AvatarConfigFileParameter { input: null })
                continue;
            var address = parameter?.Address;
            if (string.IsNullOrWhiteSpace(address))
                continue;

            // Binary parameters append a power-of-two number to the expression name.
            var name = address.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (Matches(name, CurrentNames.Value))
                count++;
            else if (Matches(name, LegacyNames.Value))
            {
                count++;
                legacyCount++;
            }
        }

        return (count, legacyCount, !avatar.FullFaceTracking && count == 0);
    }

    private static bool Matches(string address, HashSet<string> names)
    {
        if (names.Contains(address))
            return true;

        for (var separator = address.IndexOf('/'); separator >= 0;
             separator = address.IndexOf('/', separator + 1))
        {
            if (names.Contains(address[(separator + 1)..]))
                return true;
        }

        return false;
    }

    private static bool IsTrackingName(string name) =>
        name is not ("EyeTrackingActive" or "ExpressionTrackingActive" or "LipTrackingActive");
}
