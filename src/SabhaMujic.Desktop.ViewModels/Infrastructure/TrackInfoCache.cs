using SabhaMujic.Client;
using SabhaMujic.Contracts;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>
/// Track metadata by id, shared by the queue, playlists and now-playing views (which only receive ids).
/// Used from the UI thread only. Entries are invalidated on <see cref="LibraryChangedDto"/>.
/// </summary>
public sealed class TrackInfoCache
{
    private readonly ISabhaClient _client;
    private readonly Dictionary<Guid, TrackDto?> _known = [];
    private readonly Dictionary<Guid, Task<TrackDto?>> _inFlight = [];

    public TrackInfoCache(ISabhaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Raised (on the UI thread) when a track's cached info was replaced or removed.</summary>
    public event Action<Guid>? Invalidated;

    public void Put(TrackDto track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _known[track.Id] = track;
    }

    public bool TryGet(Guid id, out TrackDto? track) => _known.TryGetValue(id, out track);

    /// <summary>Returns the track, or null if the service says it no longer exists.</summary>
    public Task<TrackDto?> GetAsync(Guid id)
    {
        if (_known.TryGetValue(id, out var cached))
        {
            return Task.FromResult(cached);
        }

        if (_inFlight.TryGetValue(id, out var pending))
        {
            return pending;
        }

        var task = FetchAsync(id);
        if (!task.IsCompleted)
        {
            _inFlight[id] = task;
        }

        return task;
    }

    /// <summary>
    /// Many tracks at once (queue and playlist rows): ids not cached yet are fetched with
    /// <see cref="ILibraryApi.GetTracksByIdsAsync"/> in batches of at most <see cref="LibraryLimits.MaxBatchLookup"/>;
    /// lookups already in flight are shared. Missing tracks map to null.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, TrackDto?>> GetManyAsync(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Dictionary<Guid, TrackDto?>();
        var pending = new List<(Guid Id, Task<TrackDto?> Task)>();
        var missing = new List<Guid>();
        foreach (var id in ids.Distinct())
        {
            if (_known.TryGetValue(id, out var cached))
            {
                result[id] = cached;
            }
            else if (_inFlight.TryGetValue(id, out var inFlight))
            {
                pending.Add((id, inFlight));
            }
            else
            {
                missing.Add(id);
            }
        }

        foreach (var chunk in missing.Chunk(LibraryLimits.MaxBatchLookup))
        {
            var batch = FetchBatchAsync(chunk);
            foreach (var id in chunk)
            {
                var one = PickAsync(batch, id);
                if (!one.IsCompleted)
                {
                    _inFlight[id] = one;
                }

                pending.Add((id, one));
            }
        }

        foreach (var (id, task) in pending)
        {
            result[id] = await task.ConfigureAwait(true);
        }

        return result;
    }

    public void Invalidate(Guid id)
    {
        _known.Remove(id);
        Invalidated?.Invoke(id);
    }

    public void Clear()
    {
        var ids = _known.Keys.ToArray();
        _known.Clear();
        foreach (var id in ids)
        {
            Invalidated?.Invoke(id);
        }
    }

    private async Task<Dictionary<Guid, TrackDto?>> FetchBatchAsync(Guid[] ids)
    {
        try
        {
            var found = (await _client.Library.GetTracksByIdsAsync(ids).ConfigureAwait(true)).ToDictionary(t => t.Id);
            var map = new Dictionary<Guid, TrackDto?>();
            foreach (var id in ids)
            {
                map[id] = found.GetValueOrDefault(id);
                _known[id] = map[id];
            }

            return map;
        }
        finally
        {
            foreach (var id in ids)
            {
                _inFlight.Remove(id);
            }
        }
    }

    private static async Task<TrackDto?> PickAsync(Task<Dictionary<Guid, TrackDto?>> batch, Guid id) =>
        (await batch.ConfigureAwait(true)).GetValueOrDefault(id);

    private async Task<TrackDto?> FetchAsync(Guid id)
    {
        try
        {
            var track = await _client.Library.GetTrackAsync(id).ConfigureAwait(true);
            _known[id] = track;
            return track;
        }
        catch (SabhaMujicException ex) when (ex.Code == ErrorCode.NotFound)
        {
            _known[id] = null;
            return null;
        }
        finally
        {
            _inFlight.Remove(id);
        }
    }
}
