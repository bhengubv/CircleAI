// CircleLoggerProvider.cs
//
// ILogger now writes somewhere: through CircleLog to whatever sinks the host registered.
// The category becomes the area, so per-area levels apply to it too. Code that defaulted
// to NullLogger on the device path - AIService, the component base, the catalogue's
// bootstrap and housekeeping - now defaults to CircleLog.LoggerFor/Logger instead, and a
// host with a DI container adds this provider to its logging builder.

using Microsoft.Extensions.Logging;

namespace CircleAI.Core.Logging;

/// <summary>An <see cref="ILoggerProvider"/> that writes through <see cref="CircleLog"/>.</summary>
public sealed class CircleLoggerProvider : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CircleLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>Maps an <see cref="LogLevel"/> to a <see cref="CircleLogLevel"/>.</summary>
    public static CircleLogLevel Map(LogLevel level) => level switch
    {
        LogLevel.Trace => CircleLogLevel.Verbose,
        LogLevel.Debug => CircleLogLevel.Debug,
        LogLevel.Information => CircleLogLevel.Info,
        LogLevel.Warning => CircleLogLevel.Warn,
        LogLevel.Error or LogLevel.Critical => CircleLogLevel.Error,
        _ => CircleLogLevel.Off,
    };

    internal class CircleLogger(string area) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => CircleLog.IsEnabled(area, Map(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = Map(logLevel);
            if (!CircleLog.IsEnabled(area, level)) return;
            CircleLog.Write(level, area, formatter(state, exception), exception);
        }
    }

    internal sealed class CircleLogger<T>() : CircleLogger(typeof(T).FullName ?? typeof(T).Name), ILogger<T>
    {
    }
}
