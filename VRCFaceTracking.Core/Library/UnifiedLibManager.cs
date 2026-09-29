using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Sandboxing;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Core.Library;

public partial class UnifiedLibManager : ILibManager
{
    private readonly ILogger<UnifiedLibManager> _logger;
    private readonly ILogger _moduleLogger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDispatcherService _dispatcherService;
    private readonly IModuleDataService _moduleDataService;
    private readonly SendCoordinator _sendCoordinator;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);

    public ObservableCollection<ModuleMetadataInternal> LoadedModulesMetadata { get; set; }

    public static ModuleState EyeStatus { get; private set; }
    public static ModuleState ExpressionStatus { get; private set; }

    // Sandbox stuff
    private readonly string _sandboxProcessPath;
    private readonly List<ModuleRuntimeInfo> AvailableSandboxModules = new();
    private readonly List<ModuleRuntimeInfo> _moduleThreads = new();
    private bool _acceptingHandshakes;
    private static VrcftSandboxServer _sandboxServer;

    public UnifiedLibManager(ILoggerFactory factory, IDispatcherService dispatcherService, IModuleDataService moduleDataService, SendCoordinator sendCoordinator)
    {
        _loggerFactory = factory;
        _logger = factory.CreateLogger<UnifiedLibManager>();
        _moduleLogger = factory.CreateLogger("\0VRCFT\0");
        _dispatcherService = dispatcherService;
        _moduleDataService = moduleDataService;
        _sendCoordinator = sendCoordinator;

        LoadedModulesMetadata = new ObservableCollection<ModuleMetadataInternal>();
        _sandboxProcessPath = Path.Combine(AppContext.BaseDirectory,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "VRCFaceTracking.ModuleProcess.exe" : "VRCFaceTracking.ModuleProcess");
        if ( !File.Exists(_sandboxProcessPath) )
        {
            // @TODO: Better error handling
            throw new FileNotFoundException($"Failed to find sandbox process at \"{_sandboxProcessPath}\"!");
        }

        // @TODO: Kill any lingering sub-modules to eliminate any conflicts
    }

    public async Task Initialize()
    {
        await _initializeLock.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                await TeardownAllModulesCore();
                await InitializeCore();
            });
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task ChangeModules(Action change, Action? rollback = null,
        CancellationToken cancellationToken = default)
    {
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TeardownAllModulesCore();
                try
                {
                    change();
                    await InitializeCore();
                }
                catch (Exception changeError)
                {
                    await TeardownAllModulesCore();
                    try
                    {
                        rollback?.Invoke();
                        await InitializeCore();
                    }
                    catch (Exception recoveryError)
                    {
                        throw new AggregateException("Module change and recovery failed.", changeError, recoveryError);
                    }
                    throw;
                }
            });
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task InitializeCore()
    {
        _dispatcherService.Run(() =>
        {
            LoadedModulesMetadata.Clear();
            LoadedModulesMetadata.Add(new ModuleMetadataInternal
            {
                Active = false,
                Name = "Initializing Modules...",
                IsPlaceholder = true
            });
        });
        _logger.LogInformation("Starting initialization tracking");

        if (_sandboxServer == null)
        {
            var reservedPorts = new[] { 9000, 9001 };
            _sandboxServer = new VrcftSandboxServer(_loggerFactory, reservedPorts);
            _sandboxServer.OnPacketReceived += OnSandboxPacketReceived;
        }

        var modules = _moduleDataService.GetInstalledModules().Concat(_moduleDataService.GetLegacyModules()).ToArray();
        var modulePaths = new List<string>();
        foreach (var module in modules)
        {
            if (await _moduleDataService.IsModuleEnabledAsync(module))
                modulePaths.Add(module.AssemblyLoadPath);
        }
        lock (AvailableSandboxModules)
        {
            _acceptingHandshakes = true;
        }
        InitialiseSandboxesBaseOnPaths(modulePaths);
        lock (AvailableSandboxModules)
        {
            if (AvailableSandboxModules.Count > 0)
            {
                _logger.LogDebug("Initializing requested runtimes...");
                return;
            }
        }

        // Installed but enabled modules may also fail to start; do not label them disabled.
        var allDisabled = modules.Length > 0 && modulePaths.Count == 0;
        _dispatcherService.Run(() =>
        {
            LoadedModulesMetadata.Clear();
            LoadedModulesMetadata.Add(new ModuleMetadataInternal
            {
                Active = false,
                Name = allDisabled ? "No Modules Enabled" : "No Modules Loaded",
                IsPlaceholder = true
            });
        });
        if (allDisabled)
            _logger.LogInformation("All installed modules are disabled.");
        else
            _logger.LogWarning("No modules loaded.");
    }

    // Signal all active modules to gracefully shut down their respective runtimes.
    public async Task TeardownAllModules()
    {
        await _initializeLock.WaitAsync();
        try
        {
            await TeardownAllModulesCore();
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task TeardownAllModulesCore()
    {
        _logger.LogInformation("Tearing down all modules...");

        List<ModuleRuntimeInfo> modules;
        lock (AvailableSandboxModules)
        {
            _acceptingHandshakes = false;
            modules = AvailableSandboxModules.Concat(_moduleThreads).Distinct().ToList();
            AvailableSandboxModules.Clear();
            _moduleThreads.Clear();
        }

        foreach (var module in modules)
        {
            try
            {
                await TryTeardownModule(module);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop module {Module}; continuing shutdown", module.ModuleClassName);
            }
        }

        EyeStatus = ModuleState.Uninitialized;
        ExpressionStatus = ModuleState.Uninitialized;
    }

    private async Task TryTeardownModule(ModuleRuntimeInfo module)
    {
        if (module == null)
        {
            return;
        }

        var success = false;
        try
        {
            if (module.SandboxProcessPort > 0)
                _sendCoordinator.UnregisterModule(module.SandboxProcessPort);
            module.UpdateCancellationToken?.Cancel();
            if (module.Process?.HasExited ?? true)
            {
                success = true;
            }
            else
            {
                success = await TeardownModuleSandboxed(module);
            }
        }
        finally
        {
            if (!success)
            {
                var moduleName = module.ModuleInformation?.Name ?? module.ModuleClassName ?? "Unknown";
                _logger.LogWarning("Module {Module} failed to shut down cleanly", moduleName);
            }
            if (module.UpdateThread?.IsAlive ?? false)
                module.UpdateThread.Join(500);
            module.UpdateCancellationToken?.Dispose();
            module.Process?.Dispose();
        }
    }
}
