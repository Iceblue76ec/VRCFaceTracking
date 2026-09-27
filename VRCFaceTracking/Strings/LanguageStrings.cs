namespace VRCFaceTracking.Strings;

public static class LanguageStrings
{
    public static string Header => Get("Settings_Language_Header");
    public static string Description => Get("Settings_Language_Description");
    public static string System => Get("Settings_Language_System");
    public static string SaveFailed => Get("Settings_Language_SaveFailed");

    private static string Get(string key) =>
        Resources.ResourceManager.GetString(key, Resources.Culture) ?? key;
}
