using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;

namespace SabhaMujic.Core.Tests;

public class PlaybackQueueTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    private static Guid[] Tracks(PlaybackQueue q) => q.Snapshot().Items.Select(i => i.TrackId).ToArray();

    [Fact]
    public void Enqueue_appends_and_playNext_inserts_after_current()
    {
        var q = new PlaybackQueue();
        q.Enqueue([A, B], playNext: false);
        q.MoveNext(wrap: false);
        Assert.Equal(A, q.Current!.TrackId);

        q.Enqueue([C], playNext: true);
        Assert.Equal([A, C, B], Tracks(q));
        Assert.Equal(C, q.PeekNext(wrap: false)!.TrackId);
    }

    [Fact]
    public void PlayNext_with_no_current_appends()
    {
        var q = new PlaybackQueue();
        q.Enqueue([A], playNext: true);
        q.Enqueue([B], playNext: true);
        Assert.Equal([A, B], Tracks(q));
        Assert.Equal(-1, q.CurrentIndex);
    }

    [Fact]
    public void Next_wraps_only_when_requested()
    {
        var q = new PlaybackQueue();
        q.Enqueue([A, B], false);
        q.MoveNext(false);
        q.MoveNext(false);
        Assert.Null(q.MoveNext(wrap: false));
        Assert.Equal(B, q.Current!.TrackId);
        Assert.Equal(A, q.MoveNext(wrap: true)!.TrackId);
    }

    [Fact]
    public void Previous_stops_at_first_item()
    {
        var q = new PlaybackQueue();
        q.Enqueue([A, B], false);
        q.MoveNext(false);
        Assert.Equal(A, q.MovePrevious()!.TrackId);
        Assert.Equal(A, q.MovePrevious()!.TrackId);
    }

    [Fact]
    public void Removing_items_keeps_current_stable()
    {
        var q = new PlaybackQueue();
        var items = q.Enqueue([A, B, C, D], false);
        q.JumpTo(items[2].QueueItemId); // C
        q.Remove(items[0].QueueItemId);
        Assert.Equal(C, q.Current!.TrackId);
        q.Remove(items[3].QueueItemId);
        Assert.Equal(C, q.Current!.TrackId);
    }

    [Fact]
    public void Removing_current_last_item_points_to_new_last()
    {
        var q = new PlaybackQueue();
        var items = q.Enqueue([A, B], false);
        q.JumpTo(items[1].QueueItemId);
        q.Remove(items[1].QueueItemId);
        Assert.Equal(A, q.Current!.TrackId);
        q.Remove(items[0].QueueItemId);
        Assert.Null(q.Current);
        Assert.Equal(-1, q.CurrentIndex);
    }

    [Fact]
    public void Move_reorders_and_tracks_current()
    {
        var q = new PlaybackQueue();
        var items = q.Enqueue([A, B, C], false);
        q.JumpTo(items[0].QueueItemId);
        q.Move(items[0].QueueItemId, 2);
        Assert.Equal([B, C, A], Tracks(q));
        Assert.Equal(2, q.CurrentIndex);
        q.Move(items[2].QueueItemId, 99); // clamped
        Assert.Equal([B, A, C], Tracks(q));
        Assert.Equal(A, q.Current!.TrackId);
    }

    [Fact]
    public void Clear_keeps_only_current()
    {
        var q = new PlaybackQueue();
        var items = q.Enqueue([A, B, C], false);
        q.JumpTo(items[1].QueueItemId);
        q.ClearUpcomingAndHistory();
        Assert.Equal([B], Tracks(q));
        Assert.Equal(0, q.CurrentIndex);
    }

    [Fact]
    public void Revision_increases_on_every_change()
    {
        var q = new PlaybackQueue();
        var r0 = q.Revision;
        var items = q.Enqueue([A, B], false);
        var r1 = q.Revision;
        q.Move(items[0].QueueItemId, 1);
        var r2 = q.Revision;
        Assert.True(r0 < r1 && r1 < r2);
    }

    [Fact]
    public void Queue_size_is_bounded()
    {
        var q = new PlaybackQueue();
        var ex = Assert.Throws<SabhaMujicException>(() =>
            q.Enqueue(Enumerable.Range(0, PlaybackQueue.MaxItems + 1).Select(_ => Guid.NewGuid()).ToArray(), false));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Unknown_ids_are_rejected_without_change()
    {
        var q = new PlaybackQueue();
        q.Enqueue([A], false);
        var rev = q.Revision;
        Assert.False(q.Remove(Guid.NewGuid()));
        Assert.False(q.Move(Guid.NewGuid(), 0));
        Assert.False(q.JumpTo(Guid.NewGuid()));
        Assert.Equal(rev, q.Revision);
    }
}
