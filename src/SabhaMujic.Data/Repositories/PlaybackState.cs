using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Internal;

namespace SabhaMujic.Data.Repositories;

/// <summary>Persisted playback settings (restored after a service restart).</summary>
public sealed record PlaybackSettings(float Volume, bool Muted, LoopMode LoopMode, double CrossfadeSeconds)
{
    public const double MaxCrossfadeSeconds = 12;

    /// <summary>The values a fresh database starts with.</summary>
    public static PlaybackSettings Default { get; } = new(0.8f, false, LoopMode.Off, 6.0);
}

/// <summary>Persisted queue. <see cref="CurrentIndex"/> is -1 when nothing is selected.</summary>
public sealed record PersistedQueue(IReadOnlyList<QueueItemDto> Items, int CurrentIndex);

public interface IPlaybackStateRepository
{
    /// <summary>
    /// Replaces the stored queue atomically. Items whose track no longer exists are dropped (a track may be deleted
    /// concurrently); the current item is remembered by id so the index stays right after such drops.
    /// </summary>
    Task SaveQueueAsync(IReadOnlyList<QueueItemDto> items, int currentIndex, CancellationToken ct = default);

    Task<PersistedQueue> LoadQueueAsync(CancellationToken ct = default);

    Task SaveSettingsAsync(PlaybackSettings settings, CancellationToken ct = default);

    Task<PlaybackSettings> LoadSettingsAsync(CancellationToken ct = default);
}

public sealed class SqlitePlaybackStateRepository(SqliteDatabase database) : SqliteRepository(database), IPlaybackStateRepository
{
    public const int MaxQueueItems = 5_000;

    public async Task SaveQueueAsync(IReadOnlyList<QueueItemDto> items, int currentIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaxQueueItems)
        {
            throw DataRules.Invalid($"The queue is limited to {MaxQueueItems} items.");
        }

        if (currentIndex < -1 || currentIndex >= items.Count)
        {
            throw DataRules.Invalid("The current index is outside the queue.");
        }

        if (items.Select(i => i.QueueItemId).Distinct().Count() != items.Count)
        {
            throw DataRules.Invalid("Queue item ids must be unique.");
        }

        Guid? currentItemId = currentIndex >= 0 ? items[currentIndex].QueueItemId : null;
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: false);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM playback_queue;", transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        var position = 0;
        var storedIndex = -1;
        foreach (var item in items)
        {
            var inserted = await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO playback_queue (position, queue_item_id, track_id)
                SELECT @Position, @QueueItemId, @TrackId WHERE EXISTS (SELECT 1 FROM tracks WHERE id = @TrackId);
                """,
                new { Position = position, item.QueueItemId, item.TrackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (inserted == 1)
            {
                if (item.QueueItemId == currentItemId)
                {
                    storedIndex = position;
                }

                position++;
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE playback_state SET current_index = @Index, current_queue_item_id = @ItemId, updated_at = @Now WHERE id = 1;",
            new { Index = storedIndex, ItemId = storedIndex >= 0 ? currentItemId : null, Now = Now() }, tx, cancellationToken: ct)).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task<PersistedQueue> LoadQueueAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction(deferred: true);
        var items = (await conn.QueryAsync<QueueRow>(new CommandDefinition(
            "SELECT queue_item_id AS QueueItemId, track_id AS TrackId FROM playback_queue ORDER BY position;",
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false))
            .Select(r => new QueueItemDto(r.QueueItemId, r.TrackId)).ToArray();
        var state = await conn.QuerySingleAsync<CurrentRow>(new CommandDefinition(
            "SELECT current_index AS CurrentIndex, current_queue_item_id AS CurrentQueueItemId FROM playback_state WHERE id = 1;",
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        tx.Commit();

        // Prefer the remembered item id: a track deleted after saving removes its queue rows (FK cascade) and shifts positions.
        var index = -1;
        if (state.CurrentQueueItemId is { } currentId)
        {
            index = Array.FindIndex(items, i => i.QueueItemId == currentId);
            if (index < 0 && items.Length > 0)
            {
                index = Math.Clamp(state.CurrentIndex, 0, items.Length - 1);
            }
        }

        return new PersistedQueue(items, index);
    }

    public async Task SaveSettingsAsync(PlaybackSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DataRules.RequireFinite(settings.Volume, "Volume", 0, 1);
        DataRules.RequireFinite(settings.CrossfadeSeconds, "Crossfade", 0, PlaybackSettings.MaxCrossfadeSeconds);
        if (!Enum.IsDefined(settings.LoopMode))
        {
            throw DataRules.Invalid("Unknown loop mode.");
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE playback_state SET volume = @Volume, muted = @Muted, loop_mode = @LoopMode,
                                      crossfade_seconds = @CrossfadeSeconds, updated_at = @Now
            WHERE id = 1;
            """,
            new { Volume = (double)settings.Volume, Muted = settings.Muted ? 1 : 0, settings.LoopMode, settings.CrossfadeSeconds, Now = Now() },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<PlaybackSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition(
            "SELECT volume AS Volume, muted AS Muted, loop_mode AS LoopMode, crossfade_seconds AS CrossfadeSeconds FROM playback_state WHERE id = 1;",
            cancellationToken: ct)).ConfigureAwait(false);
        return row is null
            ? PlaybackSettings.Default
            : new PlaybackSettings((float)row.Volume, row.Muted != 0, row.LoopMode, row.CrossfadeSeconds);
    }

    private sealed class QueueRow
    {
        public Guid QueueItemId { get; set; }

        public Guid TrackId { get; set; }
    }

    private sealed class CurrentRow
    {
        public int CurrentIndex { get; set; }

        public Guid? CurrentQueueItemId { get; set; }
    }

    private sealed class SettingsRow
    {
        public double Volume { get; set; }

        public long Muted { get; set; }

        public LoopMode LoopMode { get; set; }

        public double CrossfadeSeconds { get; set; }
    }
}
