namespace VRCFaceTracking.Strings;

public static class MainPageStrings
{
    public static string WaitingForVrchat => Get(nameof(WaitingForVrchat), "Main_WaitingForVrchat");
    public static string AvatarPreview => Get(nameof(AvatarPreview), "Main_AvatarPreview");
    public static string AvatarFaceTrackingHint => Get(nameof(AvatarFaceTrackingHint), "Main_AvatarFaceTrackingHint");
    public static string TrackingOn => Get(nameof(TrackingOn), "Main_Tracking_On");
    public static string TrackingPaused => Get(nameof(TrackingPaused), "Main_Tracking_Paused");

    private static string Get(string member, string key) =>
        Resources.ResourceManager.GetString(key, Resources.Culture) ?? member;
}
