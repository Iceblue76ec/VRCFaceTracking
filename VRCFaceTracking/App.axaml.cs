using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Avalonia.Data.Core.Plugins;
using CommunityToolkit.Mvvm.DependencyInjection;
using FluentAvalonia.Styling;
using Microsoft.Extensions.Hosting;
using VRCFaceTracking.Contracts.Services;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Views;

namespace VRCFaceTracking
{
    public partial class App : Application
    {
        private ILogger? _logger;

        public static MainWindow? MainWindow { get; private set; }
        private IHost? _host;
        private bool _shutdownStarted;
        private bool _shutdownComplete;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // gsettings breaks gamescope on linux and system theme is unreliable on plasma anyways
            if (!OperatingSystem.IsLinux())
            {
                var faTheme = Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
                if (faTheme is not null)
                    faTheme.PreferSystemTheme = true;
            }

#if DEBUG
            var loggerFactory = LoggerFactory.Create(b => b
                .SetMinimumLevel(LogLevel.Information)
                .AddConsole());

            this.AttachDeveloperTools(o =>
            {
                o.AddMicrosoftLoggerObservable(loggerFactory);
            });

            _logger = loggerFactory.CreateLogger("App");
#endif
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Remove duplicate Avalonia/CommunityToolkit data validation
            BindingPlugins.DataValidators.RemoveAt(0);

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((_, services) =>services.AddCommonServices())
                .Build();
            Ioc.Default.ConfigureServices(_host.Services);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainWindow = new MainWindow();
                desktop.MainWindow = MainWindow;
                desktop.ShutdownRequested += (_, args) =>
                {
                    if (_shutdownComplete) return;
                    args.Cancel = true;
                    _ = ShutdownAsync(desktop);
                };
                MainWindow.Closing += (_, args) =>
                {
                    if (_shutdownComplete) return;
                    args.Cancel = true;
                    _ = ShutdownAsync(desktop);
                };
            }

            HandleResetFile();

            Core.Utils.KillAllProcessesOfName("VRCFaceTracking.ModuleProcess");

            _ = Task.Run(() => _host.StartAsync());
            Ioc.Default.GetRequiredService<IActivationService>().ActivateAsync(null);

            base.OnFrameworkInitializationCompleted();
        }

        private async Task ShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_shutdownStarted) return;
            _shutdownStarted = true;
            MainWindow?.Hide();

            try
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        await Ioc.Default.GetRequiredService<IMainService>().Teardown();
                    }
                    finally
                    {
                        if (_host != null)
                            await _host.StopAsync();
                    }
                }).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Shutdown cleanup did not finish within 15 seconds");
            }
            finally
            {
                _shutdownComplete = true;
                desktop.Shutdown();
            }
        }

        private void HandleResetFile()
        {
            var resetFile = Path.Combine(VRCFaceTracking.Core.Utils.PersistentDataDirectory, "reset");
            if (!File.Exists(resetFile)) return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             VRCFaceTracking.Core.Utils.PersistentDataDirectory, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clean directory during reset");
            }
            finally
            {
                try { File.Delete(resetFile); }
                catch {}
            }
        }
    }
}
