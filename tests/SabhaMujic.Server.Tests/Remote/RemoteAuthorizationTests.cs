using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Server.Remote.Http;
using SabhaMujic.Server.Remote.Hub;

namespace SabhaMujic.Server.Tests.Remote;

public sealed class RemoteAuthorizationFixture : IAsyncLifetime
{
    public RemoteTestHost Host { get; private set; } = null!;

    public PairedPhone Volunteer { get; private set; } = null!;

    public PairedPhone Admin { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await RemoteTestHost.StartAsync();
        Volunteer = await Host.PairAsync(Role.Volunteer, "matrix volunteer");
        Admin = await Host.PairAsync(Role.Admin, "matrix admin");
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>
/// Exhaustive remote authorization: every hub method and every HTTP endpoint as anonymous, Volunteer and Admin, checked
/// against <see cref="Authorizer"/>. Denials must leave no trace (DB, store, uploads, backend, analysis).
/// </summary>
public sealed class RemoteAuthorizationTests(RemoteAuthorizationFixture fixture) : IClassFixture<RemoteAuthorizationFixture>
{
    public enum As
    {
        Anonymous,
        Volunteer,
        Admin,
    }

    private static readonly Type[] HubApis = [typeof(IPlaybackApi), typeof(ILibraryApi), typeof(IPlaylistApi), typeof(IAdminApi)];

    private static MethodInfo[] HubMethods() =>
        typeof(RemoteHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name is not (nameof(RemoteHub.OnConnectedAsync) or nameof(RemoteHub.OnDisconnectedAsync)) && !m.IsSpecialName)
            .ToArray();

    /// <summary>The contract operation a hub method forwards to (hub name + "Async").</summary>
    private static (Type Api, MethodInfo Contract) ContractFor(MethodInfo hubMethod) =>
        HubApis.Select(api => (api, api.GetMethod(hubMethod.Name + "Async")!)).Single(x => x.Item2 is not null);

    public static TheoryData<string, As> HubCases()
    {
        var data = new TheoryData<string, As>();
        foreach (var method in HubMethods())
        {
            foreach (var role in Enum.GetValues<As>())
            {
                data.Add(method.Name, role);
            }
        }

        return data;
    }

    public static TheoryData<string, string, As> HttpCases()
    {
        var data = new TheoryData<string, string, As>();
        foreach (var (method, route) in RemoteApiEndpoints.All)
        {
            foreach (var role in Enum.GetValues<As>())
            {
                data.Add(method, route, role);
            }
        }

        return data;
    }

    [Fact]
    public void Hub_mirrors_the_playback_library_and_playlist_contracts_and_every_method_maps_to_an_operation()
    {
        var hubNames = HubMethods().Select(m => m.Name).ToHashSet();
        foreach (var api in new[] { typeof(IPlaybackApi), typeof(ILibraryApi), typeof(IPlaylistApi) })
        {
            foreach (var method in api.GetMethods())
            {
                Assert.Contains(method.Name[..^"Async".Length], hubNames);
            }
        }

        foreach (var method in HubMethods())
        {
            var (api, contract) = ContractFor(method);
            Assert.Equal(contract.GetParameters().Count(p => p.ParameterType != typeof(CancellationToken)), method.GetParameters().Length);
            _ = OperationMap.For(api.Name, contract.Name);
        }

        Assert.Contains("CreateCategory", hubNames);
        Assert.Equal(HubMethods().Length * 3, HubCases().Count);
    }

    [Theory]
    [MemberData(nameof(HubCases))]
    public async Task Hub_method_is_authorized_exactly_per_the_matrix(string methodName, As role)
    {
        var host = fixture.Host;
        var hubMethod = HubMethods().Single(m => m.Name == methodName);
        var (api, contract) = ContractFor(hubMethod);
        var level = Authorizer.RequiredLevel(OperationMap.For(api.Name, contract.Name));
        var args = hubMethod.GetParameters().Select(ArgumentFor).ToArray();

        var before = await host.Server.SnapshotAsync();
        if (role == As.Anonymous)
        {
            // Anonymous callers never get a hub connection, so no hub method is reachable.
            var hub = host.Hub(token: null);
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal(before, await host.Server.SnapshotAsync());
            return;
        }

        var phone = role == As.Admin ? fixture.Admin : fixture.Volunteer;
        await using var connection = await host.ConnectHubAsync(phone.Token);
        before = await host.Server.SnapshotAsync();
        ErrorInfo? error = null;
        try
        {
            await connection.InvokeCoreAsync(methodName, typeof(object), args);
        }
        catch (HubException ex)
        {
            error = RemoteErrors.FromHub(ex);
        }

        if (role == As.Volunteer && level == AccessLevel.Admin)
        {
            Assert.NotNull(error);
            Assert.Equal(ErrorCode.Forbidden, error.Code);
            await Task.Delay(20);
            Assert.Equal(before, await host.Server.SnapshotAsync());
        }
        else if (error is not null)
        {
            Assert.NotEqual(ErrorCode.Forbidden, error.Code);
            Assert.NotEqual(ErrorCode.Unauthenticated, error.Code);
            Assert.NotEqual(ErrorCode.Internal, error.Code);
        }
    }

    [Theory]
    [MemberData(nameof(HttpCases))]
    public async Task Http_endpoint_is_authorized_exactly_per_the_matrix(string method, string route, As role)
    {
        var host = fixture.Host;
        // Logout unpairs the calling device, so it gets a phone of its own instead of the shared fixture phones.
        var phone = (role, route) switch
        {
            (As.Admin, "/api/logout") => await host.PairAsync(Role.Admin),
            (As.Volunteer, "/api/logout") => await host.PairAsync(Role.Volunteer),
            (As.Admin, _) => fixture.Admin,
            (As.Volunteer, _) => fixture.Volunteer,
            _ => null,
        };
        // Pair and logout are anonymous operations; /api/me needs any session; uploads need an admin.
        var required = route switch
        {
            "/api/pair" or "/api/logout" => AccessLevel.Anonymous,
            "/api/me" => AccessLevel.Authenticated,
            _ => Authorizer.RequiredLevel(Operation.ImportTracks),
        };
        Assert.Equal(AccessLevel.Admin, Authorizer.RequiredLevel(Operation.ImportTracks));

        var path = route.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(method), path + (method == "PUT" ? "?offset=0" : ""));
        request.Headers.Add(RemoteAuth.CsrfHeader, "1");
        request.Content = route switch
        {
            "/api/pair" => JsonBody(new { code = "000000", deviceName = "probe" }),
            "/api/uploads" => JsonBody(new { fileName = "probe.wav", totalBytes = 10 }),
            "/api/uploads/{id}/complete" => JsonBody(new { title = "probe" }),
            "/api/uploads/{id}" when method == "PUT" => new ByteArrayContent([1, 2, 3]),
            _ => null,
        };

        var before = await host.Server.SnapshotAsync();
        using var response = await host.Http(phone?.Token).SendAsync(request);

        var expected = (role, required) switch
        {
            (As.Anonymous, not AccessLevel.Anonymous) => HttpStatusCode.Unauthorized,
            (As.Volunteer, AccessLevel.Admin) => HttpStatusCode.Forbidden,
            _ => (HttpStatusCode?)null,
        };
        if (expected is { } denial)
        {
            Assert.Equal(denial, response.StatusCode);
            Assert.Equal(denial == HttpStatusCode.Unauthorized ? ErrorCode.Unauthenticated : ErrorCode.Forbidden,
                (await RemoteErrors.FromHttpAsync(response)).Code);
            await Task.Delay(20);
            Assert.Equal(before, await host.Server.SnapshotAsync());
        }
        else
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            if (required != AccessLevel.Anonymous)
            {
                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Volunteer_upload_is_refused_with_nothing_staged()
    {
        var host = fixture.Host;
        var http = host.Http(fixture.Volunteer.Token);
        var activeBefore = host.Server.Uploads.ActiveCount;
        var stagedBefore = Directory.EnumerateFiles(host.Server.Uploads.StagingDirectory, "*", SearchOption.AllDirectories).Order().ToList();
        using var begin = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", new { fileName = "song.wav", totalBytes = 4 }));
        Assert.Equal(HttpStatusCode.Forbidden, begin.StatusCode);
        Assert.Equal(activeBefore, host.Server.Uploads.ActiveCount);
        Assert.Equal(stagedBefore, Directory.EnumerateFiles(host.Server.Uploads.StagingDirectory, "*", SearchOption.AllDirectories).Order().ToList());

        // Even with a real admin upload id, a volunteer cannot write into or complete it.
        using var adminBegin = await host.Http(fixture.Admin.Token)
            .SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", new { fileName = "song.wav", totalBytes = 4 }));
        var id = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<BeginUploadResponse>(adminBegin.Content, RemoteJson.Options))!.UploadId;
        using var chunk = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Put, $"/api/uploads/{id}?offset=0", content: new ByteArrayContent([1, 2, 3, 4])));
        Assert.Equal(HttpStatusCode.Forbidden, chunk.StatusCode);
        using var complete = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, $"/api/uploads/{id}/complete", new { title = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
        Assert.Equal(0, host.Server.StoredFileCount());
        using var cancel = await host.Http(fixture.Admin.Token).SendAsync(RemoteTestHost.Api(HttpMethod.Delete, $"/api/uploads/{id}"));
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
    }

    private static HttpContent JsonBody(object value) => System.Net.Http.Json.JsonContent.Create(value, options: RemoteJson.Options);

    private static object? ArgumentFor(ParameterInfo p)
    {
        var t = p.ParameterType;
        return t switch
        {
            _ when t == typeof(string) => "probe-" + Guid.NewGuid().ToString("N")[..8],
            _ when t == typeof(int) => 0,
            _ when t == typeof(Guid) => Guid.NewGuid(),
            _ when t == typeof(bool) => false,
            _ when t == typeof(float) => 0.5f,
            _ when t == typeof(double) => 1.0,
            _ when t == typeof(IReadOnlyList<Guid>) => Array.Empty<Guid>(),
            _ when t == typeof(TrackQuery) => new TrackQuery(),
            _ when t == typeof(LoopMode) => LoopMode.Off,
            _ => throw new NotSupportedException($"No test argument for {t} ({p.Member.Name}.{p.Name}). Add one."),
        };
    }
}
