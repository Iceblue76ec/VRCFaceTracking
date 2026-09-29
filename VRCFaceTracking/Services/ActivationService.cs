using System.Reflection;
using Microsoft.Extensions.Logging;

using VRCFaceTracking.Contracts.Services;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Services;

public class ActivationService(
    OscQueryService parameterOutputService,
    IMainService mainService,
    IModuleDataService moduleDataService,
    ModuleInstaller moduleInstaller,
    ILibManager libManager,
    ILogger<ActivationService> logger,
    OpenVRService openVrService)
    : IActivationService
{
    public async Task ActivateAsync(object activationArgs)
    {
        // Execute tasks before activation.
        await InitializeAsync();


        // Handle activation via ActivationHandlers.
        await HandleActivationAsync(activationArgs);


        // Execute tasks after activation.
        await StartupAsync();
    }

    private async Task HandleActivationAsync(object activationArgs)
    {
        
    }

    private async Task InitializeAsync()
    {

        await Task.CompletedTask;
    }

    private async Task StartupAsync()
    {
        logger.LogInformation("VRCFT Version {version} initializing...", Assembly.GetExecutingAssembly().GetName().Version);
        
        logger.LogInformation("Initializing OSC...");
        await parameterOutputService.InitializeAsync();

        logger.LogInformation("Initializing main service...");
        await mainService.InitializeAsync();
        
        logger.LogInformation("Initializing OpenVR...");
        if (!openVrService.Initialize())
        {
            logger.LogWarning("Failed to initialize OpenVR during ActivationService startup. Skipping.");
        }
        else
        {
            // Manifest registration does not require a permanent OpenVR session.
            // Keep VRCFT independent of the SteamVR process lifetime.
            openVrService.Disconnect();
        }

        logger.LogInformation("Checking for updates for installed modules...");
        var localModules = moduleDataService.GetInstalledModules().Where(m => m.ModuleId != Guid.Empty);
        try
        {
            var catalog = await moduleDataService.RefreshModuleCatalogAsync();
            if (catalog.FromCache)
            {
                logger.LogInformation("Module registry is unavailable; skipping update checks and loading installed modules");
            }
            else
            {
                var outdatedModules = catalog.Modules.Where(rm => localModules.Any(lm =>
                {
                    if (rm.ModuleId != lm.ModuleId || lm.IsLocal)
                        return false;

                    return ModuleVersion.IsNewer(rm.Version, lm.Version);
                }));

                using var updateBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                foreach (var outdatedModule in outdatedModules)
                {
                    try
                    {
                        logger.LogInformation("Updating {module} to {version}", outdatedModule.ModuleName, outdatedModule.Version);
                        await moduleInstaller.InstallRemoteModule(outdatedModule, cancellationToken: updateBudget.Token);
                    }
                    catch (OperationCanceledException) when (updateBudget.IsCancellationRequested)
                    {
                        logger.LogWarning("Module update time budget expired; loading installed modules");
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Skipping update for {module}; installed version remains available", outdatedModule.ModuleName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Module update check failed; loading installed modules");
        }
        finally
        {
            logger.LogInformation("Initializing modules...");
            try
            {
                await libManager.Initialize();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Module initialization failed");
            }
        }
        
        await Task.CompletedTask;
    }
}
