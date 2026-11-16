using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace SabhaMujic.Windows.Tests;

/// <summary>Writes a service's log into the test output, so a failed CI run shows why the service stopped.</summary>
internal sealed class TestOutputLoggerProvider(ITestOutputHelper output, LogLevel minimum = LogLevel.Information) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(output, categoryName, minimum);

    public void Dispose()
    {
    }

    private sealed class Logger(ITestOutputHelper output, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
            }
            catch (InvalidOperationException)
            {
                // The test already finished.
            }
        }
    }
}
