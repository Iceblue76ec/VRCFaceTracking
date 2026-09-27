using System.Globalization;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Strings;

namespace VRCFaceTracking.Services;

public sealed class LanguageService(ILocalSettingsService settings)
{
    private const string SettingKey = "Language";
    public const string SystemOption = "System";

    private readonly CultureInfo _systemCulture = CultureInfo.CurrentUICulture;

    public string SelectedLanguage { get; private set; } = SystemOption;

    public async Task<string?> ReadPreferenceAsync()
    {
        try
        {
            return await settings.ReadSettingAsync<string>(SettingKey);
        }
        catch
        {
            // A corrupt preference must not prevent the app from starting.
            return null;
        }
    }

    public async Task SetLanguageAsync(string? language)
    {
        var selected = Normalize(language);
        if (selected == SelectedLanguage)
            return;

        await settings.SaveSettingAsync(SettingKey, selected);
        Apply(selected);
    }

    public void Apply(string? language)
    {
        SelectedLanguage = Normalize(language);
        var effective = SelectedLanguage == SystemOption
            ? MatchSystemLanguage(_systemCulture)
            : SelectedLanguage;
        var culture = CultureInfo.GetCultureInfo(effective);

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Resources.Culture = culture;
    }

    private static string Normalize(string? language) => language switch
    {
        "en" or "zh-CN" or "es-ES" or "ja-JP" or "pl-PL" => language,
        _ => SystemOption
    };

    private static string MatchSystemLanguage(CultureInfo culture)
    {
        if (culture.Name is "zh-CN" or "zh-SG" or "zh-Hans"
            || culture.Name.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";

        return culture.TwoLetterISOLanguageName switch
        {
            "es" => "es-ES",
            "ja" => "ja-JP",
            "pl" => "pl-PL",
            _ => "en"
        };
    }
}
