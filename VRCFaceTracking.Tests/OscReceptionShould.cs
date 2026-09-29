using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Tests;

[TestClass, DoNotParallelize]
public class OscReceptionShould
{
    [TestMethod]
    public void KeepNegotiatedPortWhenLegacyPortOrDestinationChanges()
    {
        var target = new Target();
        using var receiver = new OscRecvService(new RecordingLogger<OscRecvService>(), target, new Settings());
        var endpoint = receiver.UpdateTarget(new IPEndPoint(IPAddress.Loopback, 0), negotiated: true)!;
        target.InPort = 65535;
        target.DestinationAddress = "192.0.2.1";
        Assert.AreEqual(endpoint.Port, target.BoundInPort);
        Assert.IsTrue(target.IsReceiving);
    }

    [TestMethod]
    public void BindLegacyReceiverToLoopbackIndependentlyOfDestination()
    {
        var target = new Target { DestinationAddress = "192.0.2.1" };
        using var freePort = new UdpClient(0);
        var port = ((IPEndPoint)freePort.Client.LocalEndPoint!).Port;
        freePort.Dispose();
        using var receiver = new OscRecvService(new RecordingLogger<OscRecvService>(), target, new Settings());
        target.InPort = port;
        Assert.AreEqual(port, target.BoundInPort);
        Assert.IsTrue(target.IsReceiving);
    }

    [TestMethod]
    public async Task RebindAndStopWithoutReportingCancellationAsAnError()
    {
        var logger = new RecordingLogger<OscRecvService>();
        using var receiver = new OscRecvService(logger, new Target(), new Settings());
        receiver.UpdateTarget(new IPEndPoint(IPAddress.Loopback, 0));
        await receiver.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(30);
            for (var i = 0; i < 12; i++)
            {
                receiver.UpdateTarget(new IPEndPoint(IPAddress.Loopback, 0));
                await Task.Delay(20);
            }
        }
        finally { await receiver.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.AreEqual(0, logger.Events.Count(e => e.Level >= LogLevel.Error));
        receiver.Dispose(); // DI can register this instance as both service and hosted service.
    }

    [TestMethod]
    public void AcceptTheFullUdpPortRangeInValidation()
    {
        var target = new OscTarget(new Settings()) { InPort = 65535, OutPort = 65535, DestinationAddress = "127.0.0.1" };
        Assert.IsTrue(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(target,
            new System.ComponentModel.DataAnnotations.ValidationContext(target), [], true));
    }
}
