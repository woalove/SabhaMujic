using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SabhaMujic.Service.Logging;

/// <summary>
/// Minimal rolling file logger: one line per entry to <c>logs\sabhamujic-yyyyMMdd.log</c>, a new file per day or when
/// the current one exceeds <see cref="MaxFileBytes"/>, keeping the newest <see cref="MaxFiles"/>. Writes happen on a
/// background task, so logging never blocks callers (entries are dropped if 10 000 are pending).
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFiles = 14;

    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    public RollingFileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private void Enqueue(string line) => _lines.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? path = null;
        try
        {
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var line))
                {
                    var wanted = CurrentPath(path, writer);
                    if (wanted != path)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        path = wanted;
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                        Prune();
                    }

                    await writer!.WriteLineAsync(line).ConfigureAwait(false);
                }

                if (writer is not null)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // Logging must never take the service down.
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string CurrentPath(string? path, StreamWriter? writer)
    {
        var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var basePath = Path.Combine(_directory, $"sabhamujic-{day}.log");
        if (path is not null && path.StartsWith(basePath[..^4], StringComparison.Ordinal) && writer is not null && writer.BaseStream.Length < MaxFileBytes)
        {
            return path;
        }

        for (var n = 0; ; n++)
        {
            var candidate = n == 0 ? basePath : Path.Combine(_directory, $"sabhamujic-{day}-{n}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < MaxFileBytes)
            {
                return candidate;
            }
        }
    }

    private void Prune()
    {
        foreach (var old in new DirectoryInfo(_directory).GetFiles("sabhamujic-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(' ').Append(logLevel.ToString().ToUpperInvariant()[..4])
                .Append(' ').Append(category)
                .Append(": ").Append(formatter(state, exception));
            if (exception is not null)
            {
                line.Append(Environment.NewLine).Append(exception);
            }

            owner.Enqueue(line.ToString());
        }
    }
}
