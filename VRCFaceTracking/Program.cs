using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using ReactiveUI.Avalonia;

namespace VRCFaceTracking;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, "VRCFaceTracking.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance || (OperatingSystem.IsWindows() && IsLegacyInstanceRunning())) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool IsLegacyInstanceRunning()
    {
        using var current = Process.GetCurrentProcess();
        var found = false;
        try
        {
            foreach (var process in Process.GetProcessesByName("VRCFaceTracking"))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != current.Id && process.SessionId == current.SessionId && !process.HasExited)
                            found = true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                        // A process can exit while it is being inspected.
                    }
                }
            }
        }
        catch (Win32Exception)
        {
            // Keep the named mutex check if process enumeration is unavailable.
        }

        return found;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();
}
