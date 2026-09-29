using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;

namespace VRCFaceTracking.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrcft-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}

internal sealed class Settings : ILocalSettingsService
{
    public Task<T> ReadSettingAsync<T>(string key, T? defaultValue = default, bool forceLocal = false) => Task.FromResult(defaultValue!);
    public Task SaveSettingAsync<T>(string key, T value, bool forceLocal = false) => Task.CompletedTask;
    public Task Save(object target) => Task.CompletedTask;
    public Task Load(object target) => Task.CompletedTask;
}

internal sealed class Target : IOscTarget
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsConnected { get; set; }
    public bool IsReceiving { get; set; }
    public int? BoundInPort { get; set; }
    private int _inPort = 9001;
    public int InPort { get => _inPort; set { _inPort = value; PropertyChanged?.Invoke(this, new(nameof(InPort))); } }
    private string _address = "127.0.0.1";
    public string DestinationAddress { get => _address; set { _address = value; PropertyChanged?.Invoke(this, new(nameof(DestinationAddress))); } }
    public int OutPort { get; set; } = 9000;
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Events { get; } = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Events.Enqueue((level, exception, formatter(state, exception)));
}

internal sealed class Dispatcher : IDispatcherService
{
    public void Run(Action action) => action();
}

internal sealed class ModuleData : IModuleDataService
{
    public Func<IEnumerable<InstallableTrackingModule>> Installed { get; set; } = () => [];
    public Func<Task<ModuleCatalogResult>> Catalog { get; set; } = () => Task.FromResult(new ModuleCatalogResult([], false));
    public Func<InstallableTrackingModule, Task<bool>> Enabled { get; set; } = _ => Task.FromResult(true);
    public IEnumerable<InstallableTrackingModule> GetInstalledModules() => Installed();
    public IEnumerable<InstallableTrackingModule> GetLegacyModules() => [];
    public Task<ModuleCatalogResult> RefreshModuleCatalogAsync() => Catalog();
    public async Task<IEnumerable<InstallableTrackingModule>> GetRemoteModules() => (await Catalog()).Modules;
    public Task<bool> IsModuleEnabledAsync(InstallableTrackingModule module) => Enabled(module);
    public Task SetModuleEnabledAsync(InstallableTrackingModule module, bool enabled) => Task.CompletedTask;
    public Task<int?> GetMyRatingAsync(TrackingModuleMetadata metadata) => Task.FromResult<int?>(null);
    public Task SetMyRatingAsync(TrackingModuleMetadata metadata, int rating) => Task.CompletedTask;
    public Task IncrementDownloadsAsync(TrackingModuleMetadata metadata) => Task.CompletedTask;
}

internal sealed class ScriptedManager : ILibManager
{
    public ObservableCollection<ModuleMetadataInternal> LoadedModulesMetadata { get; set; } = new();
    public Action? BeforeChange { get; set; }
    public Action? AfterChange { get; set; }
    public Task Initialize() => Task.CompletedTask;
    public Task TeardownAllModules() => Task.CompletedTask;
    public Task ChangeModules(Action change, Action? rollback = null, CancellationToken cancellationToken = default)
    {
        BeforeChange?.Invoke();
        try { change(); AfterChange?.Invoke(); }
        catch { rollback?.Invoke(); throw; }
        return Task.CompletedTask;
    }
}
