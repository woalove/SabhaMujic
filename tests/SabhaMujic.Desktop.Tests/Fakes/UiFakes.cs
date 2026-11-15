using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Tests.Fakes;

public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Deterministic scheduler: time only moves when the test calls <see cref="Advance"/>.</summary>
public sealed class ManualScheduler : IScheduler
{
    private readonly List<Entry> _entries = [];
    private long _sequence;

    public ManualScheduler(DateTimeOffset? start = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; private set; }

    public int ActiveCount => _entries.Count(e => !e.Disposed);

    public IDisposable Schedule(TimeSpan dueTime, Action action) => Add(dueTime, null, action);

    public IDisposable SchedulePeriodic(TimeSpan period, Action action) => Add(period, period, action);

    /// <summary>Moves time forward, running due callbacks in order (periodic ones repeatedly).</summary>
    public void Advance(TimeSpan by)
    {
        var target = UtcNow + by;
        while (true)
        {
            var next = _entries
                .Where(e => !e.Disposed && e.Due <= target)
                .OrderBy(e => e.Due)
                .ThenBy(e => e.Sequence)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            UtcNow = next.Due;
            if (next.Period is { } period)
            {
                next.Due += period;
            }
            else
            {
                next.Disposed = true;
            }

            next.Action();
        }

        _entries.RemoveAll(e => e.Disposed);
        UtcNow = target;
    }

    private Entry Add(TimeSpan due, TimeSpan? period, Action action)
    {
        var entry = new Entry { Due = UtcNow + due, Period = period, Action = action, Sequence = _sequence++ };
        _entries.Add(entry);
        return entry;
    }

    private sealed class Entry : IDisposable
    {
        public DateTimeOffset Due { get; set; }

        public TimeSpan? Period { get; init; }

        public required Action Action { get; init; }

        public long Sequence { get; init; }

        public bool Disposed { get; set; }

        public void Dispose() => Disposed = true;
    }
}

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public Queue<string?> PromptResults { get; } = new();

    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Asked.Add($"confirm:{title}");
        return Task.FromResult(ConfirmResult);
    }

    public Task<string?> PromptTextAsync(string title, string label, string initialValue = "")
    {
        Asked.Add($"prompt:{title}");
        return Task.FromResult(PromptResults.Count > 0 ? PromptResults.Dequeue() : null);
    }

    public Task<string?> PromptSecretAsync(string title, string label)
    {
        Asked.Add($"secret:{title}");
        return Task.FromResult(PromptResults.Count > 0 ? PromptResults.Dequeue() : null);
    }
}

public sealed class FakeFilePicker : IFilePicker
{
    public List<string> Paths { get; } = [];

    public Task<IReadOnlyList<string>> PickAudioFilesAsync() => Task.FromResult<IReadOnlyList<string>>(Paths.ToArray());
}

public sealed class InMemoryFileSystem : IFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long GetLength(string path) =>
        Files.TryGetValue(path, out var data) ? data.Length : throw new FileNotFoundException(path);

    public Stream OpenRead(string path) =>
        Files.TryGetValue(path, out var data) ? new MemoryStream(data, writable: false) : throw new FileNotFoundException(path);

    /// <summary>Adds a file whose byte at position i is (byte)i, so chunk contents are checkable.</summary>
    public string Add(string path, int length)
    {
        Files[path] = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        return path;
    }
}

public sealed class TestUi
{
    public InlineDispatcher Dispatcher { get; } = new();

    public ManualScheduler Scheduler { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public FakeFilePicker FilePicker { get; } = new();

    public InMemoryFileSystem FileSystem { get; } = new();

    public UiServices Services => new(Dispatcher, Scheduler, Dialogs, FilePicker, FileSystem);
}
