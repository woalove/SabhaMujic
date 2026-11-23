namespace SabhaMujic.Server.Application;

/// <summary>
/// Sliding-window failure counter per key (thread-safe, in memory). A key is limited once it has
/// <c>maxFailures</c> failures inside the window. Used for sign-in and recovery attempts.
/// </summary>
public sealed class FailureRateLimiter
{
    private const int MaxTrackedKeys = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;

    public FailureRateLimiter(int maxFailures, TimeSpan window, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFailures, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _maxFailures = maxFailures;
        _window = window;
        _clock = clock;
    }

    public bool IsLimited(string key)
    {
        lock (_gate)
        {
            return Prune(key) >= _maxFailures;
        }
    }

    public void RecordFailure(string key)
    {
        lock (_gate)
        {
            Prune(key);
            if (!_failures.TryGetValue(key, out var queue))
            {
                if (_failures.Count >= MaxTrackedKeys)
                {
                    PruneAll();
                }

                queue = new Queue<DateTimeOffset>();
                _failures[key] = queue;
            }

            queue.Enqueue(_clock.GetUtcNow());
        }
    }

    public void Reset(string key)
    {
        lock (_gate)
        {
            _failures.Remove(key);
        }
    }

    private int Prune(string key)
    {
        if (!_failures.TryGetValue(key, out var queue))
        {
            return 0;
        }

        var cutoff = _clock.GetUtcNow() - _window;
        while (queue.Count > 0 && queue.Peek() <= cutoff)
        {
            queue.Dequeue();
        }

        if (queue.Count == 0)
        {
            _failures.Remove(key);
        }

        return queue.Count;
    }

    private void PruneAll()
    {
        foreach (var key in _failures.Keys.ToList())
        {
            Prune(key);
        }
    }
}
