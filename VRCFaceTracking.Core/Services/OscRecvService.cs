using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.OSC;

namespace VRCFaceTracking.Core.Services;

public class OscRecvService : BackgroundService
{
    private readonly ILogger<OscRecvService> _logger;
    private readonly IOscTarget _oscTarget;
    private readonly ILocalSettingsService _settingsService;

    private readonly object _bindingLock = new();
    private Socket? _recvSocket;
    private bool _usesOscQueryPort;
    private bool _disposed;
    private readonly byte[] _recvBuffer = new byte[4096];

    private CancellationTokenSource _cts, _linkedToken;
    private CancellationToken _stoppingToken;

    public Action<OscMessage> OnMessageReceived = _ => { };

    public OscRecvService(
        ILogger<OscRecvService> logger,
        IOscTarget oscTarget,
        ILocalSettingsService settingsService
    )
    {
        _logger = logger;
        _cts = new CancellationTokenSource();

        _oscTarget = oscTarget;
        _settingsService = settingsService;

        _oscTarget.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(IOscTarget.InPort)) return;
            lock (_bindingLock)
            {
                // Legacy preferences must not replace the port already advertised by OSCQuery.
                if (_usesOscQueryPort || _oscTarget.InPort is < 1 or > 65535) return;
                UpdateTarget(new IPEndPoint(IPAddress.Loopback, _oscTarget.InPort));
            }
        };
    }

    public async override Task StartAsync(CancellationToken cancellationToken)
    {
        await _settingsService.Load(_oscTarget);

        await base.StartAsync(cancellationToken);
    }

    public IPEndPoint? UpdateTarget(IPEndPoint endpoint, bool negotiated = false)
    {
        if (!Equals(endpoint.Address, IPAddress.Loopback))
        {
            _logger.LogError("Cannot bind to non-loopback IP");
            return null;
        }

        lock (_bindingLock)
        {
            if (_disposed) return null;
            _cts.Cancel();
            _recvSocket?.Dispose();
            _linkedToken?.Dispose();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken, _cts.Token);
            _oscTarget.BoundInPort = null;
            _oscTarget.IsReceiving = false;
            _recvSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _recvSocket.Bind(endpoint);
                var bound = (IPEndPoint)_recvSocket.LocalEndPoint!;
                _usesOscQueryPort = negotiated;
                _oscTarget.BoundInPort = bound.Port;
                _oscTarget.IsReceiving = true;
                _logger.LogInformation("OSC receiver bound to {Endpoint}", bound);
                return bound;
            }
            catch (Exception ex)
            {
                _recvSocket.Dispose();
                _recvSocket = null;
                _logger.LogWarning(ex, "Could not bind OSC receiver to {Endpoint}", endpoint);
                return null;
            }
        }
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var nextErrorReport = DateTime.MinValue;

        lock (_bindingLock)
        {
            if (_disposed) return;
            _linkedToken?.Dispose();
            _linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken, _cts.Token);
        }

        while (!_stoppingToken.IsCancellationRequested)
        {
            Socket? socket;
            CancellationToken receiveToken;
            bool bound;
            lock (_bindingLock)
            {
                if (_disposed) return;
                socket = _recvSocket;
                receiveToken = _linkedToken.Token;
                bound = socket is { IsBound: true };
            }
            if (receiveToken.IsCancellationRequested || !bound)
            {
                await Task.Delay(10, _stoppingToken);
                continue;
            }

            try
            {
                var bytesReceived =
                    await socket!.ReceiveAsync(_recvBuffer, SocketFlags.None, receiveToken);
                var offset = 0;
                var newMsg = OscMessage.TryParseOsc(_recvBuffer, bytesReceived, ref offset);
                if (newMsg == null)
                {
                    continue;
                }

                OnMessageReceived(newMsg);
            }
            catch (Exception) when (receiveToken.IsCancellationRequested || _stoppingToken.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception e)
            {
                if (DateTime.UtcNow >= nextErrorReport)
                {
                    _logger.LogError(e, "Error encountered in OSC Receive thread");
                    SentrySdk.CaptureException(e, scope => scope.SetExtra("recvBuffer", _recvBuffer));
                    nextErrorReport = DateTime.UtcNow.AddSeconds(30);
                }

                try
                {
                    await Task.Delay(250, _stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public override void Dispose()
    {
        lock (_bindingLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _recvSocket?.Dispose();
            _linkedToken?.Dispose();
            _cts.Dispose();
        }
        base.Dispose();
    }
}
