using System.Text;
using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Core.Services;

public sealed class LogFileWriter : IDisposable
{
    private const long MaxLogFileSize = 10 * 1024 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    private readonly Timer _flushTimer;
    private long _estimatedSize;
    private bool _dirty;
    private bool _disposed;

    public LogFileWriter(StreamWriter writer)
    {
        _writer = writer;
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public void Write(string line)
    {
        lock (_sync)
        {
            if (_disposed) return;

            _writer.WriteLine(line);
            _dirty = true;
            _estimatedSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (_estimatedSize >= MaxLogFileSize)
            {
                _writer.Flush();
                _writer.BaseStream.SetLength(0);
                _writer.BaseStream.Position = 0;
                _estimatedSize = 0;
                _dirty = false;
            }
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed || !_dirty) return;
            try
            {
                _writer.Flush();
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File logging is best-effort; a later flush can retry.
            }
            catch (ObjectDisposedException)
            {
                _disposed = true;
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { _writer.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public sealed class LogFileLogger(string categoryName, LogFileWriter writer) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            writer.Write(LogMessageFormatter.Format(categoryName, logLevel, state, exception, formatter));
        }
        catch (Exception)
        {
            // Logging must not terminate the application when storage fails.
        }
    }
}
