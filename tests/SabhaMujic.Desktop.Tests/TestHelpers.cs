using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Tests;

internal static class TestHelpers
{
    public static (ShellViewModel Shell, FakeSabhaClient Client, TestUi Ui) ConnectedShell(Role? signedInAs = null)
    {
        var client = new FakeSabhaClient();
        var ui = new TestUi();
        var shell = new ShellViewModel(client, ui.Services);
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);
        if (signedInAs is { } role)
        {
            client.SetSession(FakeSabhaClient.MakeSession(role));
        }

        return (shell, client, ui);
    }

    public static PlaybackStateDto State(
        long revision,
        PlaybackStatus status = PlaybackStatus.Playing,
        double position = 0,
        double duration = 300,
        DateTimeOffset? timestamp = null,
        Guid? trackId = null,
        float volume = 1,
        double crossfade = 6,
        LoopMode loop = LoopMode.Off,
        string? error = null,
        Guid? queueItemId = null) =>
        new(status, trackId ?? TrackA, queueItemId, null, position, duration, volume, false, loop, crossfade, error, revision,
            timestamp ?? new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

    public static readonly Guid TrackA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public static QueueDto Queue(long revision, int currentIndex, params Guid[] queueItemIds) =>
        new(queueItemIds.Select(id => new QueueItemDto(id, TrackA)).ToArray(), currentIndex, revision);

    public static Guid Id(int n) => new(n, 0, 0, new byte[8]);
}
