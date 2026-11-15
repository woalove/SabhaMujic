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
