using Microsoft.Extensions.Logging;

namespace Finlight.Tests.Support;

/// <summary>Collects every log line (all levels) so tests can search them.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<(string Category, LogLevel Level, string Text)> _lines = [];

    public IReadOnlyList<(string Category, LogLevel Level, string Text)> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception) + (exception is null ? string.Empty : " | " + exception);
            lock (owner._lines)
            {
                owner._lines.Add((category, logLevel, text));
            }
        }
    }
}
