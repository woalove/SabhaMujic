namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>Runs the last triggered action once no new trigger arrived for <c>delay</c>.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly IScheduler _scheduler;
    private readonly TimeSpan _delay;
    private IDisposable? _pending;

    public Debouncer(IScheduler scheduler, TimeSpan delay)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _delay = delay;
    }

    public bool IsPending => _pending is not null;

    public void Trigger(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pending?.Dispose();
        IDisposable? handle = null;
        handle = _scheduler.Schedule(_delay, () =>
        {
            if (!ReferenceEquals(_pending, handle))
            {
                return;
            }

            _pending = null;
            action();
        });
        _pending = handle;
    }

    public void Cancel()
    {
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose() => Cancel();
}
