using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Helpers;
using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Services;

public class ModuleDataService : IModuleDataService
{
    private IReadOnlyList<InstallableTrackingModule>? _remoteModules;
    private readonly Dictionary<Guid, int> _ratingCache = new();

    private readonly IIdentityService _identityService;
    private readonly ILogger<ModuleDataService> _logger;
    private readonly ILocalSettingsService _settings;
    private readonly HttpClient _httpClient;

    private const string BaseUrl = "https://registry.vrcft.io/";
    private static readonly string CatalogCachePath = Path.Combine(Core.Utils.PersistentDataDirectory, "ModuleCatalogCache.json");

    public ModuleDataService(IIdentityService identityService, ILocalSettingsService settings, ILogger<ModuleDataService> logger)
    {
        _identityService = identityService;
        _settings = settings;
        _logger = logger;
        _httpClient = HappyEyeballsHttp.CreateHttpClient();
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
    }

    private async Task<(IReadOnlyList<InstallableTrackingModule> Modules, string Json)?> FetchModulesAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync("modules");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Module registry returned HTTP {statusCode}", response.StatusCode);
                return null;
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var modules = await Json.ToObjectAsync<List<InstallableTrackingModule>>(content);
            return modules == null ? null : (modules, content);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Exception trying to get modules from module registry: {e}", e.Message);
            return null;
        }
    }

    public async Task<IEnumerable<InstallableTrackingModule>> GetRemoteModules()
    {
        return (await RefreshModuleCatalogAsync()).Modules;
    }

    public async Task<ModuleCatalogResult> RefreshModuleCatalogAsync()
    {
        if (await FetchModulesAsync() is { } fresh)
        {
            _remoteModules = fresh.Modules;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CatalogCachePath)!);
                var tempPath = CatalogCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(tempPath, fresh.Json);
                    File.Move(tempPath, CatalogCachePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not cache module registry results");
            }
            return new ModuleCatalogResult(fresh.Modules.ToArray(), false);
        }

        if (_remoteModules == null)
        {
            try
            {
                if (File.Exists(CatalogCachePath))
                    _remoteModules = await Json.ToObjectAsync<List<InstallableTrackingModule>>(
                        await File.ReadAllTextAsync(CatalogCachePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException
                                           or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "Could not read cached module registry results");
            }
        }

        return new ModuleCatalogResult((_remoteModules ?? []).ToArray(), true);
    }

    public async Task<bool> IsModuleEnabledAsync(InstallableTrackingModule module)
    {
        try
        {
            return await _settings.ReadSettingAsync(ModuleEnabledKey(module), true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read startup preference for module {moduleId}; keeping it enabled", module.ModuleId);
            return true;
        }
    }

    public Task SetModuleEnabledAsync(InstallableTrackingModule module, bool enabled) =>
        _settings.SaveSettingAsync(ModuleEnabledKey(module), enabled);

    private static string ModuleEnabledKey(InstallableTrackingModule module) => module.ModuleId != Guid.Empty
        ? $"ModuleEnabled:{module.ModuleId:D}"
        : $"ModuleEnabled:legacy:{Path.GetFullPath(module.AssemblyLoadPath)}";

    public async Task IncrementDownloadsAsync(TrackingModuleMetadata moduleMetadata)
    {
        // Send a PATCH request to the downloads endpoint with the module ID in the body
        var rating = new RatingObject
            { UserId = _identityService.GetUniqueUserId(), ModuleId = moduleMetadata.ModuleId.ToString() };
        var content = new StringContent(JsonConvert.SerializeObject(rating), Encoding.UTF8, "application/json");
        var response = await _httpClient.PatchAsync("downloads", content);
        
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        _logger.LogError("Failed to increment downloads for {ModuleId}. Status code: {StatusCode}", moduleMetadata.ModuleId, response.StatusCode);
    }

    public IEnumerable<InstallableTrackingModule> GetLegacyModules()
    {
        if (!Directory.Exists(Core.Utils.CustomLibsDirectory))
        {
            Directory.CreateDirectory(Core.Utils.CustomLibsDirectory);
        }

        var moduleDlls = Directory.GetFiles(Core.Utils.CustomLibsDirectory, "*.dll");

        return moduleDlls.Select(moduleDll => new InstallableTrackingModule
        {
            AssemblyLoadPath = moduleDll,
            DllFileName = Path.GetFileName(moduleDll),
            ModuleId = Guid.Empty,
            ModuleName = Path.GetFileNameWithoutExtension(moduleDll),
            ModuleDescription = "Legacy module",
            AuthorName = "Local",
            ModulePageUrl = "file:///" + Path.GetDirectoryName(moduleDll)
        });
    }

    public async Task<int?> GetMyRatingAsync(TrackingModuleMetadata moduleMetadata)
    {
        try
        {
            if (_ratingCache.TryGetValue(moduleMetadata.ModuleId, out var async))
            {
                _logger.LogDebug("Rating for {ModuleId} was cached as {Rating}", moduleMetadata.ModuleId, async);
                return async;
            }

            var rating = new RatingObject
                { UserId = _identityService.GetUniqueUserId(), ModuleId = moduleMetadata.ModuleId.ToString() };
        
            var response = await _httpClient.SendAsync(new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri("rating", UriKind.Relative),
                Content = new StringContent(JsonConvert.SerializeObject(rating), Encoding.UTF8, "application/json"),
            });

            // Deserialize the input content but extract the Rating property. Be careful though, we might 404 if the user hasn't rated the module yet.
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogDebug("Failed to get user rating for module {ModuleId}", moduleMetadata.ModuleId);
                return null;
            }

            var ratingResponse = await Json.ToObjectAsync<RatingObject>(await response.Content.ReadAsStringAsync());
            
            _logger.LogDebug("Rating for {ModuleId} was {Rating}. Caching...", moduleMetadata.ModuleId, ratingResponse.Rating);
            _ratingCache[moduleMetadata.ModuleId] = ratingResponse.Rating;
            return ratingResponse.Rating;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to get user rating for module {ModuleId}. Exception: {Exception}", moduleMetadata.ModuleId, e.Message);
            return null;
        }
    }

    public async Task SetMyRatingAsync(TrackingModuleMetadata moduleMetadata, int rating)
    {
        try
        {
            // Same format as get but we PUT this time
            var ratingObject = new RatingObject
            {
                UserId = _identityService.GetUniqueUserId(), ModuleId = moduleMetadata.ModuleId.ToString(),
                Rating = rating
            };

            var content = new StringContent(JsonConvert.SerializeObject(ratingObject), Encoding.UTF8,
                "application/json");
            var response = await _httpClient.PutAsync("rating", content);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("Failed to set rating for {ModuleId} to {Rating}. Status code: {StatusCode}",
                    moduleMetadata.ModuleId, rating, response.StatusCode);
                return;
            }

            _logger.LogDebug("Rating for {ModuleId} was set to {Rating}. Caching...", moduleMetadata.ModuleId, rating);
            _ratingCache[moduleMetadata.ModuleId] = rating;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to set rating for module {ModuleId}. Exception: {Exception}", moduleMetadata.ModuleId, e.Message);
        }
    }

    public IEnumerable<InstallableTrackingModule> GetInstalledModules()
    {
        if (!Directory.Exists(Core.Utils.CustomLibsDirectory))
        {
            Directory.CreateDirectory(Core.Utils.CustomLibsDirectory);
        }

        // Check each folder in our CustomModulesDir folder and see if it has a module.json file.
        // If it does, deserialize it and add it to the list of installed modules.
        var installedModules = new List<InstallableTrackingModule>();
        var moduleFolders = Directory.GetDirectories(Core.Utils.CustomLibsDirectory);
        foreach (var moduleFolder in moduleFolders)
        {
            var moduleJsonPath = Path.Combine(moduleFolder, "module.json");
            if (!File.Exists(moduleJsonPath))
            {
                continue;
            }

            try
            {
                var moduleJson = File.ReadAllText(moduleJsonPath);
                var module = JsonConvert.DeserializeObject<InstallableTrackingModule>(moduleJson);
                if (module == null) continue;
                module.AssemblyLoadPath = Path.Combine(moduleFolder, module.DllFileName);
                installedModules.Add(module);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to deserialize module.json for {ModuleFolder}", moduleFolder);
            }
        }

        return installedModules;
    } 
}
