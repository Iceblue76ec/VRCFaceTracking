using Microsoft.Extensions.Logging;
using Valve.VR;

namespace VRCFaceTracking.Services;

public class OpenVRService(ILogger<OpenVRService> logger)
{
    private const string ApplicationKey = "benaclejames.vrcft";
    private readonly object _sync = new();

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { return OpenVR.IsRuntimeInstalled(); }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                return false;
            }
        }
    }

    public bool Initialize(bool allowStartingSteamVr = false)
    {
        if (!IsAvailable)
        {
            return false;
        }

        lock (_sync)
        {
            if (IsInitialized) return true;
            try
            {
                EVRInitError error = EVRInitError.None;
                OpenVR.Init(ref error, allowStartingSteamVr
                    ? EVRApplicationType.VRApplication_Utility
                    : EVRApplicationType.VRApplication_Background);

                if (error != EVRInitError.None)
                {
                    logger.LogWarning("Failed to initialize OpenVR: {Error}", error);
                    return false;
                }

                var fullManifestPath = Path.Combine(AppContext.BaseDirectory, "app.vrmanifest");
                var manifestRegisterResult = OpenVR.Applications.AddApplicationManifest(fullManifestPath, false);
                if (manifestRegisterResult != EVRApplicationError.None)
                {
                    logger.LogWarning("Failed to register SteamVR manifest: {Error}", manifestRegisterResult);
                    OpenVR.Shutdown();
                    return false;
                }

                logger.LogInformation("Successfully initialized OpenVR");
                IsInitialized = true;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
            {
                logger.LogWarning("OpenVR native library not available: {Message}", ex.Message);
                return false;
            }
        }
    }

    public void InitIfNotAlready()
    {
        if (!IsInitialized)
            Initialize();
    }

    public bool IsInitialized { get; private set; }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (!IsInitialized) return;
            try
            {
                OpenVR.Shutdown();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to disconnect from OpenVR");
            }
            finally
            {
                IsInitialized = false;
            }
        }
    }

    public bool AutoStart
    {
        get
        {
            try
            {
                lock (_sync)
                    return IsInitialized && OpenVR.Applications.GetApplicationAutoLaunch(ApplicationKey);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryGetAutoStart(out bool value)
    {
        value = false;
        lock (_sync)
        {
            // Background initialization does not start SteamVR; unavailable sessions use the local cache.
            if (!Initialize()) return false;
            try
            {
                value = OpenVR.Applications.GetApplicationAutoLaunch(ApplicationKey);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read SteamVR auto launch");
                return false;
            }
            finally
            {
                Disconnect();
            }
        }
    }

    public bool TrySetAutoStart(bool value, out string error)
    {
        error = string.Empty;
        lock (_sync)
        {
            if (!Initialize(allowStartingSteamVr: true))
            {
                error = "OpenVR initialization failed";
                return false;
            }
            try
            {
                var result = OpenVR.Applications.SetApplicationAutoLaunch(ApplicationKey, value);
                if (result != EVRApplicationError.None)
                {
                    error = result.ToString();
                    logger.LogError("Failed to set SteamVR auto launch: {Error}", result);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogError(ex, "Exception setting SteamVR auto launch");
                return false;
            }
            finally
            {
                Disconnect();
            }
        }
    }
}
