namespace VRCFaceTracking.Strings;

public static class ModuleRegistryStrings
{
    public static string Refresh => Get("Registry_RefreshCatalog");
    public static string Loading => Get("Registry_CatalogLoading");
    public static string UsingCache => Get("Registry_UsingCache");
    public static string NoCache => Get("Registry_NoCache");
    public static string Disabled => Get("Registry_ModuleDisabled");
    public static string EnableAction => Get("Registry_EnableAction");
    public static string DisableAction => Get("Registry_DisableAction");
    public static string ActivationFailed => Get("Registry_ActivationFailed");

    private static string Get(string key) =>
        Resources.ResourceManager.GetString(key, Resources.Culture) ?? key;
}
