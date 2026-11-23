using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Remote;
using SabhaMujic.Server.Remote.Http;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Tests.Remote;

/// <summary>Transport security of the remote: peer filter, headers, sign-in cookie, CSRF, rate limits, revocation.</summary>
public sealed class RemoteSecurityTests : IAsyncLifetime
{
    private RemoteTestHost _host = null!;

    public async Task InitializeAsync() => _host = await RemoteTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Peer_rules_allow_only_loopback_private_and_link_local(string address, bool allowed) =>
        Assert.Equal(allowed, AddressRules.IsAllowedPeer(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::7")]
    public async Task Public_peers_get_403_on_every_endpoint_of_both_sites(string peer)
    {
        var phone = await _host.PhoneAsync(Role.Admin);
        foreach (var path in new[] { "/", "/api/me", "/js/app.js", "/hub/negotiate?negotiateVersion=1" })
        {
            using var request = new HttpRequestMessage(path.StartsWith("/hub", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, path);
            request.Headers.Add(RemoteTestHost.PeerHeader, peer);
            request.Headers.Authorization = new("Bearer", phone.Token);
            using var response = await _host.Http().SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var bootstrap = new HttpRequestMessage(HttpMethod.Get, "/sabhamujic-ca.mobileconfig");
        bootstrap.Headers.Add(RemoteTestHost.PeerHeader, peer);
        using var refused = await _host.Bootstrap().SendAsync(bootstrap);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var lan = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        lan.Headers.Add(RemoteTestHost.PeerHeader, "192.168.1.50");
        lan.Headers.Authorization = new("Bearer", phone.Token);
        using var ok = await _host.Http().SendAsync(lan);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        foreach (var path in new[] { "/", "/pair", "/js/app.js", "/api/me", "/nope" })
        {
            using var response = await _host.Http().GetAsync(path);
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Equal(RemoteHost.ContentSecurityPolicy, csp);
            Assert.Contains("default-src 'self'", csp);
            Assert.Contains("connect-src 'self' wss:", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.DoesNotContain("unsafe", csp);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.False(response.Headers.Contains("Server"));
        }
    }

    [Fact]
    public async Task Web_app_is_served_from_embedded_assets()
    {
        var http = _host.Http();
        using var index = await http.GetAsync("/");
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        var html = await index.Content.ReadAsStringAsync();
        Assert.Contains("/vendor/signalr/signalr.min.js", html);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);

        using var login = await http.GetAsync("/login");
        Assert.Equal(html, await login.Content.ReadAsStringAsync());

        foreach (var (path, type) in new[]
        {
            ("/js/app.js", "text/javascript"), ("/styles.css", "text/css"), ("/vendor/signalr/signalr.min.js", "text/javascript"),
            ("/manifest.webmanifest", "application/manifest+json"), ("/icons/apple-touch-icon.png", "image/png"), ("/icons/icon.svg", "image/svg+xml"),
        })
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
            var etag = response.Headers.ETag!;
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.IfNoneMatch.Add(etag);
            using var notModified = await http.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        }

        // No JavaScript asset assigns innerHTML/outerHTML or uses eval-like sinks.
        foreach (var path in new WebAssets().Paths.Where(p => p.StartsWith("/js/", StringComparison.Ordinal)))
        {
            var js = await http.GetStringAsync(path);
            Assert.DoesNotMatch(@"\.(inner|outer)HTML\b|insertAdjacentHTML|document\.write|\beval\(|new Function", js);
        }
    }

    private static HttpRequestMessage Login(string userName, string password, bool remember, string? deviceName = "Sevak phone", string? peer = null)
    {
        var request = RemoteTestHost.Api(HttpMethod.Post, "/api/login", new { userName, password, rememberDevice = remember, deviceName });
        if (peer is not null)
        {
            request.Headers.Add(RemoteTestHost.PeerHeader, peer);
        }

        return request;
    }

    private static List<string> CookieAttributes(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(RemoteAuth.CookieName + "=", StringComparison.Ordinal))
            .Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();

    [Fact]
    public async Task Remembered_sign_in_sets_a_persistent_httponly_secure_samesite_strict_cookie_and_lists_the_device()
    {
        using var response = await _host.Http().SendAsync(Login(TestServer.AdminName, TestServer.AdminSecret, remember: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var attributes = CookieAttributes(response);
        Assert.StartsWith("__host-", attributes[0]);
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain", StringComparison.Ordinal));
        Assert.Contains(attributes, a => a.StartsWith("max-age=", StringComparison.Ordinal) && long.Parse(a[8..]) >= 170 * 86400);

        var token = RemoteTestHost.CookieToken(response)!;
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, body);
        var me = (await response.Content.ReadFromJsonAsync<MeResponse>(RemoteJson.Options))!;
        Assert.Equal((TestServer.AdminName, Role.Admin), (me.Name, me.Role));
        var device = Assert.Single(await _host.Server.App.Devices.ListAsync());
        Assert.Equal(("Sevak phone", Role.Admin), (device.Name, device.Role));

        // The cookie authenticates (as a browser would send it), and /api/me slides the browser-side expiry.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", $"{RemoteAuth.CookieName}={token}");
        using var meResponse = await _host.Http().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.Contains(CookieAttributes(meResponse), a => a.StartsWith("max-age=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sign_in_without_remember_sets_a_session_cookie_and_lists_no_device()
    {
        using var response = await _host.Http().SendAsync(Login(TestServer.AdminName, TestServer.AdminSecret, remember: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var attributes = CookieAttributes(response);
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("max-age", StringComparison.Ordinal) || a.StartsWith("expires", StringComparison.Ordinal));
        Assert.Empty(await _host.Server.App.Devices.ListAsync());

        var token = RemoteTestHost.CookieToken(response)!;
        using var me = await _host.Http(token).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False(me.Headers.Contains("Set-Cookie")); // nothing to slide: it ends 12 h after sign-in

        _host.Server.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        using var expired = await _host.Http(token).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_and_volunteer_accounts_get_no_cookie()
    {
        using var wrong = await _host.Http().SendAsync(Login(TestServer.AdminName, "not-the-password", remember: true));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));

        using var volunteer = await _host.Http().SendAsync(Login(TestServer.VolunteerName, TestServer.VolunteerSecret, remember: true));
        Assert.Equal(HttpStatusCode.Forbidden, volunteer.StatusCode);
        var error = await RemoteErrors.FromHttpAsync(volunteer);
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Contains("administrators", error.Message);
        Assert.False(volunteer.Headers.Contains("Set-Cookie"));
        Assert.Empty(await _host.Server.App.Devices.ListAsync());

        // A phone paired as a volunteer before v2 is refused everywhere but stays listed for the admin to forget.
        var legacy = await _host.PhoneAsync(Role.Volunteer);
        using var me = await _host.Http(legacy.Token).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        var hub = _host.Hub(legacy.Token);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(legacy.DeviceId, (await _host.Server.App.Devices.ListAsync()).Select(d => d.Id));
    }

    [Fact]
    public async Task Sign_in_is_rate_limited()
    {
        var http = _host.Http();
        for (var i = 0; i < _host.Server.App.Options.LoginMaxFailuresPerUser; i++)
        {
            using var failed = await http.SendAsync(Login(TestServer.AdminName, "wrong-pass-" + i, remember: true, peer: "10.9.9.9"));
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Even the right password is refused while limited, from any phone.
        using var limited = await http.SendAsync(Login(TestServer.AdminName, TestServer.AdminSecret, remember: true, peer: "10.9.9.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(ErrorCode.RateLimited, (await RemoteErrors.FromHttpAsync(limited)).Code);
        Assert.False(limited.Headers.Contains("Set-Cookie"));
        Assert.Empty(await _host.Server.App.Devices.ListAsync());

        _host.Server.Clock.Advance(TimeSpan.FromMinutes(5));
        using var later = await http.SendAsync(Login(TestServer.AdminName, TestServer.AdminSecret, remember: true, peer: "10.9.9.10"));
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task Pairing_endpoints_no_longer_exist()
    {
        var http = _host.Http();
        using var pair = await http.SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/pair", new { code = "123456", deviceName = "x" }));
        Assert.Equal(HttpStatusCode.NotFound, pair.StatusCode);
        using var pairPage = await http.GetAsync("/pair");
        Assert.Equal(HttpStatusCode.NotFound, pairPage.StatusCode);
        Assert.DoesNotContain(RemoteApiEndpoints.All, e => e.Route.Contains("pair", StringComparison.OrdinalIgnoreCase));
        using var loginPage = await http.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        Assert.Empty(await _host.Server.App.Devices.ListAsync());
    }

    [Theory]
    [InlineData(null, false)]                          // no CSRF header
    [InlineData("https://evil.example", true)]         // foreign origin
    [InlineData("http://localhost", true)]             // wrong scheme/port
    [InlineData("null", true)]                         // opaque origin
    public async Task State_changing_requests_need_the_csrf_header_and_a_same_origin(string? origin, bool withHeader)
    {
        var admin = await _host.PhoneAsync(Role.Admin);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/uploads")
        {
            Content = JsonContent.Create(new { fileName = "a.wav", totalBytes = 10 }),
        };
        if (withHeader)
        {
            request.Headers.Add(RemoteAuth.CsrfHeader, "1");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        using var response = await _host.Http(admin.Token).SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _host.Server.Uploads.ActiveCount);

        using var pair = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = JsonContent.Create(new { code = "1", deviceName = "x" }) };
        using var pairResponse = await _host.Http().SendAsync(pair);
        Assert.Equal(HttpStatusCode.Forbidden, pairResponse.StatusCode);

        // Same origin + header: accepted.
        var good = RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", new { fileName = "a.wav", totalBytes = 10 });
        good.Headers.Add("Origin", _host.Origin);
        using var accepted = await _host.Http(admin.Token).SendAsync(good);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task Hub_rejects_cross_origin_and_tokenless_connections()
    {
        var phone = await _host.PhoneAsync(Role.Admin);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/hub/negotiate?negotiateVersion=1");
        crossOrigin.Headers.Add("Origin", "https://evil.example");
        crossOrigin.Headers.Authorization = new("Bearer", phone.Token);
        using var refused = await _host.Http().SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var anonymous = await _host.Http().PostAsync("/hub/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var hub = _host.Hub("not-a-valid-token");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Logout_forgets_the_remembered_phone_and_clears_the_cookie()
    {
        var phone = await _host.PhoneAsync(Role.Admin);
        var other = await _host.PhoneAsync(Role.Admin);
        var hub = await _host.ConnectHubAsync(phone.Token);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string>(nameof(IClientEvents.SessionEnded), reason => ended.TrySetResult(reason));
        var admin = _host.Server.App.CreateSession(ClientKind.Desktop, "test-admin");
        await admin.LoginAsync(TestServer.AdminName, TestServer.AdminSecret);
        var adminEvents = new System.Collections.Concurrent.ConcurrentQueue<LibraryChangedDto>();
        await using var adminSub = _host.Server.App.Events.Subscribe(new LibraryRecorder(adminEvents), admin.Caller);

        using var response = await _host.Http(phone.Token).SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/logout"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cleared = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(RemoteAuth.CookieName + "=;", cleared);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);

        // The device itself is revoked (no admin involved): its token is dead and it is no longer listed.
        using var me = await _host.Http(phone.Token).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.True((await _host.Server.App.Devices.GetAsync(phone.DeviceId))!.IsRevoked);
        Assert.DoesNotContain(phone.DeviceId, (await admin.GetDevicesAsync()).Select(d => d.Id));
        Assert.Contains(other.DeviceId, (await admin.GetDevicesAsync()).Select(d => d.Id));
        Assert.Contains("signed out", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertEx.Eventually(() => adminEvents.Any(e => e.Kind == LibraryChangeKind.DevicesChanged && e.EntityId == phone.DeviceId),
            "admins learn that the device list changed");
        Assert.Contains(await _host.Server.App.AuditLog.QueryRecentAsync(), a => a.Action == "device.logout");

        // Logging out again (or with a dead token) still succeeds and just clears the cookie.
        using var again = await _host.Http(phone.Token).SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/logout"));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using var anonymous = await _host.Http().SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/logout"));
        Assert.Equal(HttpStatusCode.NoContent, anonymous.StatusCode);
    }

    private sealed class LibraryRecorder(System.Collections.Concurrent.ConcurrentQueue<LibraryChangedDto> sink) : Application.IEventSink
    {
        public ValueTask DeliverAsync(Application.ServerEvent serverEvent, CancellationToken ct)
        {
            if (serverEvent is Application.LibraryEvent e)
            {
                sink.Enqueue(e.Change);
            }

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Revoking_a_device_ends_its_live_hub_connection_and_later_requests_get_401()
    {
        var phone = await _host.PhoneAsync(Role.Admin);
        var hub = await _host.ConnectHubAsync(phone.Token);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string>(nameof(IClientEvents.SessionEnded), reason => ended.TrySetResult(reason));
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.InvokeAsync<PlaybackStateDto>("GetState");

        var admin = _host.Server.App.CreateSession(ClientKind.Desktop, "test");
        await admin.LoginAsync(TestServer.AdminName, TestServer.AdminSecret);
        await admin.RevokeDeviceAsync(phone.DeviceId);

        Assert.Contains("removed", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertEx.Eventually(() => _host.Remote.ConnectionCount == 0, "server dropped the connection");

        using var me = await _host.Http(phone.Token).GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var upload = await _host.Http(phone.Token).SendAsync(RemoteTestHost.Api(HttpMethod.Post, "/api/uploads", new { fileName = "a.wav", totalBytes = 1 }));
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        var again = _host.Hub(phone.Token);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => again.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}
