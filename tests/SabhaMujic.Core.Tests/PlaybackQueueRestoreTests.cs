using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;

namespace SabhaMujic.Core.Tests;

public class PlaybackQueueRestoreTests
{
    private static QueueItemDto Item() => new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Restore_keeps_ids_and_selection()
    {
        var queue = new PlaybackQueue();
        queue.Enqueue([Guid.NewGuid()], playNext: false);
        var revision = queue.Revision;
        QueueItemDto[] items = [Item(), Item(), Item()];

        queue.Restore(items, 2);

        Assert.Equal(items, queue.Snapshot().Items);
        Assert.Equal(2, queue.CurrentIndex);
        Assert.Equal(items[2], queue.Current);
        Assert.True(queue.Revision > revision);

        queue.Restore([], -1);
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Restore_validates_its_input()
    {
        var queue = new PlaybackQueue();
        var item = Item();
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SabhaMujicException>(() => queue.Restore([item, item], 0)).Code);
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SabhaMujicException>(() => queue.Restore([item], 1)).Code);
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SabhaMujicException>(() => queue.Restore([item], -2)).Code);
        var tooMany = Enumerable.Range(0, PlaybackQueue.MaxItems + 1).Select(_ => Item()).ToArray();
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SabhaMujicException>(() => queue.Restore(tooMany, 0)).Code);
        Assert.Equal(0, queue.Count);
    }
}
