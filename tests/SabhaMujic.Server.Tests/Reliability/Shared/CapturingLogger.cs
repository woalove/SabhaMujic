using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SabhaMujic.Reliability;

/// <summary>One captured log entry (warnings and above, plus anything carrying an exception).</summary>
public sealed record CapturedLog(DateTimeOffset At, LogLevel Level, string Category, string Message, string? ExceptionType, string? ExceptionMessage);

/// <summary>
/// An <see cref="ILoggerFactory"/> that records every warning/error (and every entry with an exception) so soak and
/// fault tests can assert "no unexpected exceptions in the logs". Optionally echoes to a sink.
/// </summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private readonly Action<string>? _echo;
    private readonly LogLevel _echoLevel;

    public CapturingLoggerFactory(Action<string>? echo = null, LogLevel echoLevel = LogLevel.Warning)
    {
        _echo = echo;
        _echoLevel = echoLevel;
    }

    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    public int Count => _entries.Count;

    public IReadOnlyList<CapturedLog> Since(int index) => _entries.Skip(index).ToList();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Record(LogLevel level, string category, string message, Exception? exception)
    {
        if (level >= LogLevel.Warning || exception is not null)
        {
            var root = exception;
            while (root is AggregateException { InnerException: { } inner })
            {
                root = inner;
            }

            _entries.Enqueue(new CapturedLog(DateTimeOffset.UtcNow, level, category, message, root?.GetType().FullName, root?.Message));
        }

        if (_echo is not null && level >= _echoLevel)
        {
            _echo($"[{level}] {category}: {message}{(exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}");
        }
    }

    private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Record(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
