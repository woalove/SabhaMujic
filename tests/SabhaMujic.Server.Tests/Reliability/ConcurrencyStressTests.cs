using System.Collections.Concurrent;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Reliability;
using Xunit.Abstractions;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>Many real pipe clients at once against the real engine: no deadlock, defined errors only, consistent end state.</summary>
public sealed class ConcurrencyStressTests(ITestOutputHelper output)
{
    private const int Clients = 20;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallWatchdog = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Twenty_clients_issuing_random_commands_for_ten_seconds_never_deadlock_and_end_consistent()
    {
        await using var h = await ReliabilityHost.StartAsync(speed: 20);
        var tracks = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            tracks.Add(await h.ImportAsync(SoakWav.Create((SoakTrackKind)(i % 3), 6 + i * 2, 900 + i), $"Stress {i}"));
        }

        var playlist = await h.Admin.Playlists.CreatePlaylistAsync("Stress set", tracks);
        await h.CreateVolunteersAsync(4);
        var clients = new List<(PipeSabhaClient Client, ClientView View)>();
        var violations = new ConcurrentQueue<string>();
        for (var i = 0; i < Clients; i++)
        {
            var client = i < 2 ? await h.ConnectAsync(ReliabilityHost.AdminUser, ReliabilityHost.AdminSecret) : await h.ConnectVolunteerAsync(i % 4);
            var view = new ClientView($"client{i}", (kind, detail) => violations.Enqueue($"{kind}: {detail}"), _ => { });
            client.Events.PlaybackStateReceived += view.OnState;
            client.Events.QueueReceived += view.OnQueue;
            view.Seed(await client.Playback.GetStateAsync(), await client.Playback.GetQueueAsync());
            clients.Add((client, view));
        }

        await h.Admin.Playback.EnqueueAsync(tracks, playNext: false);
        await h.Admin.Playback.ResumeAsync();
        var subscribersDuring = h.App.Events.SubscriberCount;
        var heapBefore = ProcessMetrics.Read().ManagedHeapMb;

        var outcomes = new ConcurrentDictionary<string, int>();
        var failures = new ConcurrentQueue<string>();
        var calls = 0;
        var end = DateTime.UtcNow + Duration;
        await Task.WhenAll(clients.Select((c, index) => Task.Run(async () =>
        {
            var random = new Random(4242 + index);
            while (DateTime.UtcNow < end)
            {
                var (name, call) = RandomCall(c.Client, random, tracks, playlist.Id, h, isAdmin: index < 2);
                using var timeout = new CancellationTokenSource(CallWatchdog);
                var task = call(timeout.Token);
                if (await Task.WhenAny(task, Task.Delay(CallWatchdog + TimeSpan.FromSeconds(5))) != task)
                {
                    failures.Enqueue($"{name} hung (watchdog)");
                    return;
                }

                Interlocked.Increment(ref calls);
                try
                {
                    await task;
                    outcomes.AddOrUpdate(name + ":ok", 1, (_, n) => n + 1);
                }
                catch (SabhaMujicException ex) when (Enum.IsDefined(ex.Code) && ex.Code != ErrorCode.Internal)
                {
                    outcomes.AddOrUpdate($"{name}:{ex.Code}", 1, (_, n) => n + 1);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{name}: {ex.GetType().Name} {(ex as SabhaMujicException)?.Code} {ex.Message}");
                }
            }
        })));

        output.WriteLine($"{calls} calls in {Duration.TotalSeconds} s by {Clients} clients");
        foreach (var (key, count) in outcomes.OrderBy(kv => kv.Key))
        {
            output.WriteLine($"  {key} × {count}");
        }

        Assert.Empty(failures);
        Assert.Empty(violations);
        Assert.True(calls > Clients * 10, $"only {calls} calls completed");

        // The engine still answers promptly, and every client converges on the server's state.
        var sync = h.Host.Engine.SyncAsync();
        Assert.Same(sync, await Task.WhenAny(sync, Task.Delay(5_000)));
        await AssertEx.Eventually(() =>
        {
            var server = h.Host.Engine.GetState();
            var queue = h.Host.Engine.GetQueue();
            return clients.All(c => c.View.Matches(h.App.ServerEpoch, server, queue, out _));
        }, "all clients converge", 10_000);
        var reference = await clients[0].Client.Playback.GetQueueAsync();
        foreach (var (client, _) in clients)
        {
            var queue = await client.Playback.GetQueueAsync();
            Assert.Equal(reference.Items, queue.Items);
        }

        // The broadcaster holds exactly one subscription per connection and lets go of all of them.
        Assert.Equal(h.Host.Pipe!.ConnectionCount, subscribersDuring);
        Assert.True(ProcessMetrics.Read().ManagedHeapMb - heapBefore < 64, "no runaway event backlog");
        await h.DisposeClientsAsync();
        await AssertEx.Eventually(() => h.App.Events.SubscriberCount == 0 && h.Host.Pipe!.ConnectionCount == 0, "subscriptions released");
        Assert.Empty(h.UnexpectedErrors());
    }

    private static (string Name, Func<CancellationToken, Task> Call) RandomCall(PipeSabhaClient c, Random random, List<Guid> tracks, Guid playlist,
        ReliabilityHost h, bool isAdmin)
    {
        var queue = h.Host.Engine.GetQueue().Items;
        var item = queue.Count > 0 ? queue[random.Next(queue.Count)].QueueItemId : Guid.NewGuid();
        var track = tracks[random.Next(tracks.Count)];
        if (isAdmin && random.Next(4) == 0)
        {
            return random.Next(3) switch
            {
                0 => ("UpdateTrack", ct => c.Admin.UpdateTrackAsync(new TrackUpdateDto(track, $"Renamed {random.Next(1000)}", null, []), ct)),
                1 => ("ReanalyzeTrack", ct => c.Admin.ReanalyzeTrackAsync(track, ct)),
                _ => ("SetLoopOverride", ct => c.Admin.SetLoopOverrideAsync(track, random.Next(2) == 0 ? null : new LoopRegionDto(0.5, 4.5, 0.1, 1, true), ct)),
            };
        }

        return random.Next(20) switch
        {
            0 => ("PlayQueueItem", ct => c.Playback.PlayQueueItemAsync(item, ct)),
            1 => ("PlayTrack", ct => c.Playback.PlayTrackAsync(track, ct)),
            2 => ("Pause", ct => c.Playback.PauseAsync(ct)),
            3 => ("Resume", ct => c.Playback.ResumeAsync(ct)),
            4 => ("Next", ct => c.Playback.NextAsync(ct)),
            5 => ("Previous", ct => c.Playback.PreviousAsync(ct)),
            6 => ("Seek", ct => c.Playback.SeekAsync(random.NextDouble() * 8, ct)),
            7 => ("SetLoopMode", ct => c.Playback.SetLoopModeAsync((LoopMode)random.Next(3), ct)),
            8 => ("SetCrossfade", ct => c.Playback.SetCrossfadeAsync(random.Next(0, 13), ct)),
            9 => ("SetVolume", ct => c.Playback.SetVolumeAsync((float)random.NextDouble(), ct)),
            10 => ("Enqueue", ct => c.Playback.EnqueueAsync([track, tracks[random.Next(tracks.Count)]], random.Next(2) == 0, ct)),
            11 when queue.Count > 4 => ("RemoveFromQueue", ct => c.Playback.RemoveFromQueueAsync(item, ct)),
            12 => ("MoveQueueItem", ct => c.Playback.MoveQueueItemAsync(item, random.Next(Math.Max(1, queue.Count)), ct)),
            13 when queue.Count > 20 => ("ClearQueue", ct => c.Playback.ClearQueueAsync(ct)),
            14 => ("EnqueuePlaylist", ct => c.Playback.EnqueuePlaylistAsync(playlist, random.Next(6) == 0, ct)),
            15 => ("Stop", ct => c.Playback.StopAsync(ct)),
            16 => ("CreatePlaylist", ct => c.Playlists.CreatePlaylistAsync($"P{random.Next(1_000_000)}", [track], ct)),
            17 => ("GetTracks", ct => c.Library.GetTracksAsync(new TrackQuery(Search: "Stress"), ct)),
            18 => ("GetQueue", ct => c.Playback.GetQueueAsync(ct)),
            _ => ("GetState", ct => c.Playback.GetStateAsync(ct)),
        };
    }
}
