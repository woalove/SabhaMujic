using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Remote.Http;

namespace SabhaMujic.Server.Tests.Remote;

/// <summary>Hub behaviour (live events, errors, continuity) and the admin upload over HTTP.</summary>
public sealed class RemoteHubTests : IAsyncLifetime
{
    private RemoteTestHost _host = null!;

    public async Task InitializeAsync() => _host = await RemoteTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Two_hub_clients_both_receive_a_state_change()
    {
        var volunteer = await _host.PairAsync(Role.Volunteer);
        var admin = await _host.PairAsync(Role.Admin);
        var states = new ConcurrentDictionary<string, ConcurrentQueue<PlaybackStateDto>>();
        var hubs = new List<HubConnection>();
        foreach (var (name, phone, transport) in new[]
        {
            ("volunteer", volunteer, HttpTransportType.WebSockets),
            ("admin", admin, HttpTransportType.LongPolling),
        })
        {
            var queue = states.GetOrAdd(name, _ => new ConcurrentQueue<PlaybackStateDto>());
            var hub = _host.Hub(phone.Token, transport);
            hub.On<PlaybackStateDto>(nameof(IClientEvents.PlaybackStateChanged), s => queue.Enqueue(s));
            await hub.StartAsync();
            hubs.Add(hub);
        }

        await hubs[0].InvokeAsync("Pause");
        await AssertEx.Eventually(() => states.Values.All(q => q.Any(s => s.Status == PlaybackStatus.Paused)), "both clients see Paused");
        Assert.All(states.Values, q => Assert.Equal(_host.Server.App.ServerEpoch, q.Last().ServerEpoch));

        var queues = new ConcurrentQueue<QueueDto>();
        hubs[0].On<QueueDto>(nameof(IClientEvents.QueueChanged), queues.Enqueue);
        var track = await _host.Server.App.Tracks.AddAsync(new Data.Repositories.NewTrack(new string('a', 64), new string('a', 64) + ".mp3", "Song", null, null, 10, 1));
        var returned = await hubs[1].InvokeAsync<QueueDto>("Enqueue", new[] { track.Track.Id }, false);
        Assert.Single(returned.Items);
        await AssertEx.Eventually(() => queues.Any(q => q.Items.Count == 1), "queue change pushed to the other client");
    }

    [Fact]
    public async Task Hub_errors_carry_code_and_message_without_internal_details()
    {
        var volunteer = await _host.PairAsync(Role.Volunteer);
        var hub = await _host.ConnectHubAsync(volunteer.Token);

        var forbidden = await RemoteErrors.HubThrows(() => hub.InvokeAsync<CategoryDto>("CreateCategory", "New"));
        Assert.Equal(ErrorCode.Forbidden, forbidden.Code);

        var invalid = await RemoteErrors.HubThrows(() => hub.InvokeAsync("SetVolume", 3f));
        Assert.Equal(ErrorCode.InvalidArgument, invalid.Code);

        var notFound = await RemoteErrors.HubThrows(() => hub.InvokeAsync<TrackDto>("GetTrack", Guid.NewGuid()));
        Assert.Equal(ErrorCode.NotFound, notFound.Code);

        _host.Server.Backend.ThrowOnPause = new InvalidOperationException("secret path C:\\ProgramData\\SabhaMujic\\Library");
        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => hub.InvokeAsync("Pause"));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("ProgramData", ex.Message);
        Assert.Equal(ErrorCode.Internal, RemoteErrors.FromHub(ex).Code);

        // The connection survives errors.
        Assert.Equal(HubConnectionState.Connected, hub.State);
        await hub.InvokeAsync<PlaybackStateDto>("GetState");
    }

    [Fact]
    public async Task Killing_every_hub_connection_and_stopping_the_remote_never_touches_playback()
    {
        var phones = new[] { await _host.PairAsync(Role.Volunteer), await _host.PairAsync(Role.Admin), await _host.PairAsync(Role.Volunteer) };
        foreach (var phone in phones)
        {
            await _host.ConnectHubAsync(phone.Token);
        }

        var longPolling = _host.Hub(phones[0].Token, HttpTransportType.LongPolling);
        await longPolling.StartAsync();
        await AssertEx.Eventually(() => _host.Remote.ConnectionCount == 4, "all connected");

        _host.Server.Backend.SetPlaying();
        var commandsBefore = _host.Server.Backend.Commands.Count;

        _host.Remote.AbortAllConnections();
        await AssertEx.Eventually(() => _host.Remote.ConnectionCount == 0, "server dropped every connection");
        await _host.Remote.StopAsync();

        await Task.Delay(200);
        Assert.Equal(commandsBefore, _host.Server.Backend.Commands.Count);
        Assert.Equal(PlaybackStatus.Playing, _host.Server.Backend.Status);
        Assert.Equal(0, _host.Server.App.Events.SubscriberCount - (_host.Server.Pipe?.ConnectionCount ?? 0));

        // The remote can come back on the same port and clients reconnect.
        await _host.Remote.StartAsync();
        var again = await _host.ConnectHubAsync(phones[1].Token);
        Assert.Equal(PlaybackStatus.Playing, (await again.InvokeAsync<PlaybackStateDto>("GetState")).Status);
    }

    [Fact]
    public async Task Admin_upload_happy_path_over_http_with_progress_on_the_same_device_hub()
    {
        var admin = await _host.PairAsync(Role.Admin);
        var hub = await _host.ConnectHubAsync(admin.Token);
        var progress = new ConcurrentQueue<ImportProgressDto>();
        hub.On<ImportProgressDto>(nameof(IClientEvents.ImportProgress), progress.Enqueue);
        var category = await hub.InvokeAsync<CategoryDto>("CreateCategory", "Bhajan");

        var http = _host.Http(admin.Token);
        var content = new byte[AdminLimits.MaxChunkBytes + 1000];
        Random.Shared.NextBytes(content);
        using var begin = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", new { fileName = "aarti.wav", totalBytes = content.Length }));
        Assert.Equal(HttpStatusCode.Created, begin.StatusCode);
        var id = (await begin.Content.ReadFromJsonAsync<BeginUploadResponse>(RemoteJson.Options))!.UploadId;

        // A chunk above the limit is refused (413) and changes nothing.
        // (Expect: 100-continue so the client waits for the verdict instead of streaming the oversized body.)
        var tooBigRequest = RemoteTestHost.Api(HttpMethod.Put, $"/api/uploads/{id}?offset=0", content: new ByteArrayContent(content));
        tooBigRequest.Headers.ExpectContinue = true;
        using var tooBig = await http.SendAsync(tooBigRequest);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal(ErrorCode.TooLarge, (await RemoteErrors.FromHttpAsync(tooBig)).Code);

        for (var offset = 0; offset < content.Length; offset += AdminLimits.MaxChunkBytes)
        {
            var length = Math.Min(AdminLimits.MaxChunkBytes, content.Length - offset);
            using var chunk = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Put, $"/api/uploads/{id}?offset={offset}",
                content: new ByteArrayContent(content, offset, length)));
            Assert.Equal(HttpStatusCode.NoContent, chunk.StatusCode);
        }

        using var complete = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, $"/api/uploads/{id}/complete",
            new { title = "Aarti", artist = "Choir", categoryId = category.Id }));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var result = (await complete.Content.ReadFromJsonAsync<ImportResultDto>(RemoteJson.Options))!;
        Assert.False(result.WasDuplicate);

        var track = (await _host.Server.App.Tracks.GetAsync(result.TrackId))!;
        Assert.Equal("Aarti", track.Title);
        Assert.Equal(category.Id, track.CategoryId);
        Assert.Equal(1, _host.Server.StoredFileCount());
        Assert.Single(_host.Server.Probe.Probed);
        Assert.Contains(result.TrackId, _host.Server.AnalysisQueue.Queued);
        await AssertEx.Eventually(() => progress.Any(p => p.UploadId == id && p.Stage == ImportStages.Analyzing), "progress reaches the device's hub (imports end at analyzing; the analysis worker sends done)");
        Assert.Contains(progress, p => p.Stage == ImportStages.Uploading && p.BytesReceived == AdminLimits.MaxChunkBytes);

        // Same content again → duplicate; cancel works for an abandoned upload.
        using var again = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", new { fileName = "copy.wav", totalBytes = 4 }));
        var second = (await again.Content.ReadFromJsonAsync<BeginUploadResponse>(RemoteJson.Options))!.UploadId;
        using var cancel = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Delete, $"/api/uploads/{second}"));
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(0, _host.Server.Uploads.ActiveCount);
    }

    [Fact]
    public async Task Malformed_api_requests_are_rejected_cleanly()
    {
        var admin = await _host.PairAsync(Role.Admin);
        var http = _host.Http(admin.Token);

        using var notJson = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", content: new StringContent("fileName=a.wav")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notJson.StatusCode);

        using var badJson = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads",
            content: new StringContent("{nope", System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        using var noOffset = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Put, $"/api/uploads/{Guid.NewGuid()}", content: new ByteArrayContent([1])));
        Assert.Equal(HttpStatusCode.BadRequest, noOffset.StatusCode);

        using var huge = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/pair",
            content: new StringContent(new string(' ', RemoteApiEndpoints.MaxJsonBytes + 10), System.Text.Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        using var unknown = await http.GetAsync("/api/nothing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
