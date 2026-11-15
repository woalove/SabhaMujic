using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Playback;

/// <summary>
/// Ordered play queue with a current position. Not thread-safe: the engine serializes access.
/// <see cref="CurrentIndex"/> is -1 when nothing is selected.
/// </summary>
public sealed class PlaybackQueue
{
    public const int MaxItems = 5_000;

    private readonly List<QueueItemDto> _items = [];

    public int CurrentIndex { get; private set; } = -1;

    public long Revision { get; private set; }

    public int Count => _items.Count;

    public QueueItemDto? Current => CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

    public QueueDto Snapshot() => new(_items.ToArray(), CurrentIndex, Revision);

    /// <summary>Appends tracks, or inserts them right after the current item when <paramref name="playNext"/> is true.</summary>
    public IReadOnlyList<QueueItemDto> Enqueue(IReadOnlyList<Guid> trackIds, bool playNext)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (_items.Count + trackIds.Count > MaxItems)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, $"The queue is limited to {MaxItems} items.");
        }

        var added = trackIds.Select(id => new QueueItemDto(Guid.NewGuid(), id)).ToArray();
        if (added.Length == 0)
        {
            return added;
        }

        var insertAt = playNext && CurrentIndex >= 0 ? CurrentIndex + 1 : _items.Count;
        _items.InsertRange(insertAt, added);
        Touch();
        return added;
    }

    /// <summary>
    /// Restores a saved queue exactly (same queue item ids) with <paramref name="currentIndex"/> selected (-1 for none).
    /// Throws InvalidArgument for too many items, duplicate ids or an index outside the queue.
    /// </summary>
    public void Restore(IReadOnlyList<QueueItemDto> items, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaxItems)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, $"The queue is limited to {MaxItems} items.");
        }

        if (items.Select(i => i.QueueItemId).Distinct().Count() != items.Count)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Queue item ids must be unique.");
        }

        if (currentIndex < -1 || currentIndex >= items.Count)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The current index is outside the queue.");
        }

        _items.Clear();
        _items.AddRange(items);
        CurrentIndex = currentIndex;
        Touch();
    }

    public void ReplaceAll(IReadOnlyList<Guid> trackIds)
    {
        _items.Clear();
        CurrentIndex = -1;
        Enqueue(trackIds, playNext: false);
        Touch();
    }

    public bool Remove(Guid queueItemId)
    {
        var index = IndexOf(queueItemId);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        if (index < CurrentIndex)
        {
            CurrentIndex--;
        }
        else if (index == CurrentIndex)
        {
            // The current item was removed: keep pointing at the item that took its place, if any.
            if (CurrentIndex >= _items.Count)
            {
                CurrentIndex = _items.Count - 1;
            }
        }

        Touch();
        return true;
    }

    public bool Move(Guid queueItemId, int newIndex)
    {
        var from = IndexOf(queueItemId);
        if (from < 0)
        {
            return false;
        }

        newIndex = Math.Clamp(newIndex, 0, _items.Count - 1);
        if (from == newIndex)
        {
            return true;
        }

        var current = Current;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(newIndex, item);
        CurrentIndex = current is null ? -1 : IndexOf(current.QueueItemId);
        Touch();
        return true;
    }

    /// <summary>Removes everything except the current item.</summary>
    public void ClearUpcomingAndHistory()
    {
        var current = Current;
        _items.Clear();
        if (current is not null)
        {
            _items.Add(current);
            CurrentIndex = 0;
        }
        else
        {
            CurrentIndex = -1;
        }

        Touch();
    }

    public bool JumpTo(Guid queueItemId)
    {
        var index = IndexOf(queueItemId);
        if (index < 0)
        {
            return false;
        }

        CurrentIndex = index;
        Touch();
        return true;
    }

    /// <summary>The item that would play after the current one, honouring wrap-around.</summary>
    public QueueItemDto? PeekNext(bool wrap)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        var next = CurrentIndex + 1;
        if (next < _items.Count)
        {
            return _items[next];
        }

        return wrap ? _items[0] : null;
    }

    public QueueItemDto? MoveNext(bool wrap)
    {
        var next = PeekNext(wrap);
        if (next is null)
        {
            return null;
        }

        CurrentIndex = IndexOf(next.QueueItemId);
        Touch();
        return next;
    }

    public QueueItemDto? MovePrevious()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        CurrentIndex = Math.Max(0, CurrentIndex - 1);
        Touch();
        return _items[CurrentIndex];
    }

    private int IndexOf(Guid queueItemId) => _items.FindIndex(i => i.QueueItemId == queueItemId);

    private void Touch() => Revision++;
}
