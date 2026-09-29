using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Services.Logging;

[ProviderAlias("Debug")]
public class LogFileProvider : ILoggerProvider
{
    private readonly LogFileWriter? _writer;
    
    public LogFileProvider() : this(null) { }

    public LogFileProvider(string? logPath)
    {
        try
        {
            logPath ??= Path.Combine(Core.Utils.UserAccessibleDataDirectory, "latest.log");
            var directory = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
            Directory.CreateDirectory(directory);

            var file = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096);
            _writer = new LogFileWriter(new StreamWriter(file));
        }
        catch
        {

        }
    }
    
    private readonly ConcurrentDictionary<string, LogFileLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    public ILogger CreateLogger(string categoryName)
    {
        if (_writer != null)
        {
            return _loggers.GetOrAdd(categoryName, name => new LogFileLogger(name, _writer));
        }

        return NullLogger.Instance;
    }

    public void Flush() => _writer?.Flush();

    public void Dispose()
    {
        _writer?.Dispose();
        _loggers.Clear();
    }
}
