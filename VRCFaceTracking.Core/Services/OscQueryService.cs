using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.Models.ParameterDefinition;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.OSC.Query.mDNS;
using VRCFaceTracking.Core.Params;

namespace VRCFaceTracking.Core.Services;

public partial class OscQueryService(
    ILogger<OscQueryService> logger,
    OscQueryConfigParser oscQueryConfigParser,
    AvatarConfigParser avatarConfigParser,
    ParameterSenderService parameterSenderService,
    IOscTarget oscTarget,
    MulticastDnsService multicastDnsService,
    OscRecvService recvService,
    ILocalSettingsService settingsService,
    HttpHandler httpHandler,
    IDispatcherService dispatcherService)
    : ObservableObject
{
    [ObservableProperty] private IAvatarInfo _avatarInfo = new NullAvatarDef("Loading...", "Loading...");
    [ObservableProperty] private List<Parameter> _avatarParameters;
    [ObservableProperty] private IAvatarInfo _displayAvatarInfo = new NullAvatarDef("Waiting for VRChat", "");
    [ObservableProperty] private int _displayParameterCount;
    [ObservableProperty] private int _displayLegacyCount;
    [ObservableProperty] private bool _isAvatarPreview;
    [ObservableProperty] private bool _showFaceTrackingHint;
    private int _avatarLoadVersion;
    private readonly SemaphoreSlim _avatarMappingGate = new(1, 1);
    private readonly SemaphoreSlim _avatarPersistenceGate = new(1, 1);

    public async Task InitializeAsync()
    {
        logger.LogDebug("OSC Service Initializing");

        recvService.OnMessageReceived = HandleNewMessage;

        await settingsService.Load(oscTarget);
        VRChat.EnsureVRCOSCDirectory();
        await LoadAvatarPreviewAsync();

        (bool listenerSuccess, bool senderSuccess) result = (false, false);

        if (string.IsNullOrWhiteSpace(oscTarget.DestinationAddress))
        {
            return;  // Return both false as we cant bind to anything without an address
        }

        multicastDnsService.OnVrcClientDiscovered += FirstClientDiscovered;

        multicastDnsService.SendQuery("_oscjson._tcp.local");

        logger.LogDebug("OSC Service Initialized with result {0}", result);
        await Task.CompletedTask;
    }

    private void FirstClientDiscovered()
    {
        multicastDnsService.OnVrcClientDiscovered -= FirstClientDiscovered;

        logger.LogInformation($"OSCQuery detected at {multicastDnsService.VrchatClientEndpoint}. Setting port negotiation to autopilot.");

        httpHandler.OnHostInfoQueried += HandleNewAvatarWrapper;

        var recvEndpoint = recvService.UpdateTarget(new IPEndPoint(IPAddress.Parse(oscTarget.DestinationAddress), 0));
        if (recvEndpoint == null)
        {
            logger.LogWarning("Could not bind a random OSC receive port; trying the configured port.");
            recvEndpoint = recvService.UpdateTarget(
                new IPEndPoint(IPAddress.Parse(oscTarget.DestinationAddress), oscTarget.InPort));
            if (recvEndpoint == null)
            {
                logger.LogError("Could not bind an OSC receive port for OSCQuery.");
                return;
            }
        }

        var randomServiceSuffix = Utils.GetRandomChars(6);
        var httpPort = Utils.GetRandomFreePort();
        httpHandler.SetAppName("VRCFT-" + randomServiceSuffix);
        httpHandler.BindTo($"http://127.0.0.1:{httpPort}/", recvEndpoint.Port);

        // Advertise our OSC JSON and OSC endpoints (OSC JSON to display the silly lil popup in-game)
        multicastDnsService.Advertise("_oscjson._tcp", new AdvertisedService("VRCFT-"+randomServiceSuffix, httpPort, IPAddress.Loopback));
        multicastDnsService.Advertise("_osc._udp", new AdvertisedService("VRCFT-"+randomServiceSuffix, recvEndpoint.Port, IPAddress.Loopback));

        HandleNewAvatar();
    }

    private async Task LoadAvatarPreviewAsync()
    {
        var version = Volatile.Read(ref _avatarLoadVersion);
        try
        {
            var lastId = await settingsService.ReadSettingAsync<string>("LastAvatarId");
            var avatar = await avatarConfigParser.ReadAvatarConfigAsync(lastId)
                         ?? await avatarConfigParser.ReadMostRecentConfigAsync();
            if (avatar == null || version != Volatile.Read(ref _avatarLoadVersion))
                return;

            var summary = AvatarTrackingSummary.Analyze(avatar);
            dispatcherService.Run(() =>
            {
                if (version != Volatile.Read(ref _avatarLoadVersion)) return;
                DisplayAvatarInfo = avatar;
                DisplayParameterCount = summary.Count;
                DisplayLegacyCount = summary.LegacyCount;
                IsAvatarPreview = true;
                ShowFaceTrackingHint = false;
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the last known avatar preview.");
        }
    }

    private async void HandleNewAvatar(string newId = null)
    {
        var version = Interlocked.Increment(ref _avatarLoadVersion);
        (IAvatarInfo avatarInfo, List<Parameter> relevantParameters)? newAvatar;
        await _avatarMappingGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _avatarLoadVersion)) return;
            if (multicastDnsService.VrchatClientEndpoint != null)
            {
                newAvatar = await oscQueryConfigParser.ParseAvatar("");
            }
            else
            {
                newAvatar = await avatarConfigParser.ParseAvatar(newId);
                // VRChat can announce a new avatar before writing its OSC config.
                for (var attempt = 0; newAvatar == null && attempt < 3
                     && version == Volatile.Read(ref _avatarLoadVersion); attempt++)
                {
                    await Task.Delay(300);
                    newAvatar = await avatarConfigParser.ParseAvatar(newId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load avatar {avatarId}.", newId);
            return;
        }
        finally
        {
            _avatarMappingGate.Release();
        }

        if (!newAvatar.HasValue || version != Volatile.Read(ref _avatarLoadVersion))
        {
            return;
        }

        var summary = AvatarTrackingSummary.Analyze(newAvatar.Value.avatarInfo);
        httpHandler.OnHostInfoQueried -= HandleNewAvatarWrapper;
        dispatcherService.Run(() =>
        {
            if (version != Volatile.Read(ref _avatarLoadVersion)) return;
            AvatarInfo = newAvatar.Value.avatarInfo;
            AvatarParameters = newAvatar.Value.relevantParameters;
            DisplayAvatarInfo = newAvatar.Value.avatarInfo;
            DisplayParameterCount = newAvatar.Value.relevantParameters.Count;
            DisplayLegacyCount = newAvatar.Value.relevantParameters.Count(p => p.Deprecated);
            IsAvatarPreview = false;
            ShowFaceTrackingHint = summary.PossiblyUnsupported;
        });

        if (newAvatar.Value.avatarInfo.Id.StartsWith("avtr_", StringComparison.Ordinal))
        {
            await _avatarPersistenceGate.WaitAsync();
            try
            {
                if (version == Volatile.Read(ref _avatarLoadVersion))
                    await settingsService.SaveSettingAsync("LastAvatarId", newAvatar.Value.avatarInfo.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remember the last avatar.");
            }
            finally
            {
                _avatarPersistenceGate.Release();
            }
        }
    }

    private void HandleNewAvatarWrapper() => HandleNewAvatar(); // Helper func used in callbacks
    

    private void HandleNewMessage(OscMessage msg)
    {
        switch (msg.Address)
        {
            case "/avatar/change":
                HandleNewAvatar(msg.Value as string);
                break;
            case "/vrcft/settings/forceRelevant":   // Endpoint for external tools to force vrcft to send all parameters
                if (msg.Value is bool relevancy)
                {
                    parameterSenderService.AllParametersRelevant = relevancy;
                }

                break;
            /*
            case "/avatar/parameters/EyeTrackingActive":
                if (UnifiedLibManager.EyeStatus != ModuleState.Uninitialized)
                {
                    if (!msg.Value.BoolValue)
                        UnifiedLibManager.EyeStatus = ModuleState.Idle;
                    else UnifiedLibManager.EyeStatus = ModuleState.Active;
                }
                break;
            case "/avatar/parameters/LipTrackingActive":
            case "/avatar/parameters/ExpressionTrackingActive":
                {
                    if (!msg.Value.BoolValue)
                if (UnifiedLibManager.ExpressionStatus != ModuleState.Uninitialized)
                        UnifiedLibManager.ExpressionStatus = ModuleState.Idle;
                    else UnifiedLibManager.ExpressionStatus = ModuleState.Active;
                }
                break;
            */
        }
    }

    partial void OnAvatarParametersChanged(List<Parameter> value)
    {
        logger.LogDebug("Configuration loaded. Checking for native tracking parameters...");
        var hasLoadedNative = value.Any(p => p.GetParamNames().Any(t => t.paramName.StartsWith("/tracking/")));
        if (hasLoadedNative)
        {
            logger.LogWarning("Native tracking parameters detected.");
        }

        var deprecatedParams = value.Where(p => p.Deprecated).ToList();

        logger.LogInformation(value.Count + " parameters loaded.");
        if (deprecatedParams.Any())
        {
            logger.LogWarning(
                deprecatedParams.Count +
                " Legacy parameters detected. " +
                "Please consider updating the avatar to use the latest documented parameters.");
            logger.LogDebug($"Loaded deprecated parameters: {string.Join(", ", deprecatedParams.SelectMany(x => x.GetParamNames()).Select(y => y.paramName))}");
        }
    }
}
