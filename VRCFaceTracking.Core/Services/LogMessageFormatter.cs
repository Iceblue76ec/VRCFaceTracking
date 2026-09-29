using Microsoft.Extensions.Logging;

namespace VRCFaceTracking.Core.Services;

public static class LogMessageFormatter
{
    public static string Format<TState>(string category, LogLevel level, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception != null)
        {
            var detail = exception.ToString();
            // Some modules embed the full exception for compatibility with older hosts.
            if (!message.Contains(detail, StringComparison.Ordinal))
                message += Environment.NewLine + detail;
        }
        return category == "\0VRCFT\0" ? message : $"[{category}] {level}: {message}";
    }
}
