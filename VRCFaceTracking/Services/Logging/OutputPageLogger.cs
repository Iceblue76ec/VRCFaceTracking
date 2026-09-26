using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Models;

namespace VRCFaceTracking.Services.Logging;

public class OutputPageLogger(string categoryName) : ILogger
{
    private const int MaxVisibleLines = 10_000;
    private const int MaxPendingLines = 2_000;
    private const int MaxLinesPerFlush = 200;
    public static readonly ObservableCollection<LogLine> AllLogs = new BoundedLogCollection();

    private static readonly ConcurrentQueue<LogLine> _pending = new();
    private static int _pendingCount;
    private static DispatcherTimer? _flushTimer;
    private static int _timerStarted;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = categoryName == "\0VRCFT\0"
            // Log events from sub-processes have the unique category name "\0VRCFT\0", so skip category name
            ? new LogLine($"{formatter(state, exception)}", logLevel)
            : new LogLine($"[{categoryName}] {logLevel}: {formatter(state, exception)}", logLevel);

        Interlocked.Increment(ref _pendingCount);
        _pending.Enqueue(line);
        if (Volatile.Read(ref _pendingCount) > MaxPendingLines && _pending.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
        EnsureFlushTimer();
    }

    private static void EnsureFlushTimer()
    {
        if (Interlocked.CompareExchange(ref _timerStarted, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _flushTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Background,
                Flush);
            _flushTimer.Start();
        });
    }

    private static void Flush(object? sender, EventArgs e)
    {
        for (var i = 0; i < MaxLinesPerFlush && _pending.TryDequeue(out var line); i++)
        {
            Interlocked.Decrement(ref _pendingCount);
            AllLogs.Add(line);
        }

        if (AllLogs.Count > MaxVisibleLines)
            ((BoundedLogCollection)AllLogs).TrimOldest(AllLogs.Count - MaxVisibleLines);
    }

    private sealed class BoundedLogCollection : ObservableCollection<LogLine>
    {
        public void TrimOldest(int count)
        {
            if (Items is List<LogLine> lines)
                lines.RemoveRange(0, count);
            else
                for (var i = 0; i < count; i++)
                    Items.RemoveAt(0);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
