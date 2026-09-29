using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.ModuleProcess;
using VRCFaceTracking.Services.Logging;

namespace VRCFaceTracking.Tests;

[TestClass, DoNotParallelize]
public class LoggingShould
{
    private static Exception Failure()
    {
        try { throw new InvalidOperationException("exception-sentinel"); }
        catch (Exception ex) { return ex; }
    }

    [TestMethod]
    public void PreserveExceptionTypeMessageAndStackWithoutDuplicatingEmbeddedDetail()
    {
        var failure = Failure();
        var formatted = LogMessageFormatter.Format("Review", LogLevel.Error, "failed", failure, (text, _) => text);
        StringAssert.Contains(formatted, "InvalidOperationException");
        StringAssert.Contains(formatted, "exception-sentinel");
        StringAssert.Contains(formatted, nameof(Failure));
        var embedded = "failed" + Environment.NewLine + failure;
        Assert.AreEqual(embedded, LogMessageFormatter.Format("\0VRCFT\0", LogLevel.Error, embedded, failure, (text, _) => text));
    }

    [TestMethod]
    public void PreserveExceptionsInFileBufferAndSandboxLoggers()
    {
        var failure = Failure();
        using var bytes = new MemoryStream();
        using (var writer = new LogFileWriter(new StreamWriter(bytes, Encoding.UTF8, 1024, leaveOpen: true)))
            new LogFileLogger("Review", writer).LogError(failure, "failed");
        StringAssert.Contains(Encoding.UTF8.GetString(bytes.ToArray()), nameof(Failure));
        using var buffer = new LogBufferProvider();
        buffer.CreateLogger("Review").LogError(failure, "failed");
        StringAssert.Contains(buffer.Snapshot(), "exception-sentinel");
        var previous = ProxyLogger.OnLog;
        string? proxy = null;
        try
        {
            ProxyLogger.OnLog = (_, message) => proxy = message;
            new ProxyLogger("Review").LogError(failure, "failed");
            StringAssert.Contains(proxy!, "InvalidOperationException");
            StringAssert.Contains(proxy!, nameof(Failure));
        }
        finally { ProxyLogger.OnLog = previous; }
    }

    [TestMethod]
    public async Task FlushOwnedFileProviderWhenHostIsDisposed()
    {
        using var temp = new TestDirectory();
        var path = System.IO.Path.Combine(temp.Path, "latest.log");
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(_ => new LogFileProvider(path));
        var host = builder.Build();
        try
        {
            await host.StartAsync();
            host.Services.GetRequiredService<ILogger<LoggingShould>>().LogError(Failure(), "last shutdown detail");
            await host.StopAsync();
        }
        finally { host.Dispose(); }
        var contents = await File.ReadAllTextAsync(path);
        StringAssert.Contains(contents, "last shutdown detail");
        StringAssert.Contains(contents, "exception-sentinel");
        StringAssert.Contains(contents, nameof(Failure));
    }
}
