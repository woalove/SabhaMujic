using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Remote;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Http;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Tests.Remote;

/// <summary>A controllable LAN identity (IP set, primary address, host name).</summary>
public sealed class FakeNetwork : INetworkAddressProvider
{
    public List<IPAddress> Addresses { get; set; } = [IPAddress.Parse("192.168.1.20")];

    public string HostName { get; set; } = "sabha-pc";

    public IReadOnlyList<IPAddress> GetPrivateIPv4Addresses() => Addresses.ToList();

    public IPAddress? GetPrimaryIPv4() => Addresses.FirstOrDefault();

    public string GetHostName() => HostName;
}

/// <summary>Protector that really transforms the bytes (XOR) and counts calls, to prove keys go through it.</summary>
public sealed class RecordingProtector : ISecretProtector
{
    public int ProtectCalls;
    public int UnprotectCalls;

    public byte[] Protect(byte[] plaintext)
    {
        Interlocked.Increment(ref ProtectCalls);
        return [0x42, .. plaintext.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        Interlocked.Increment(ref UnprotectCalls);
        if (protectedData.Length == 0 || protectedData[0] != 0x42)
        {
            throw new CryptographicException("not protected by RecordingProtector");
        }

        return protectedData[1..].Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}

/// <summary>A paired remote device as a test client sees it.</summary>
public sealed record PairedPhone(string Token, string Name, Role Role, Guid DeviceId);

/// <summary>
/// The full server (<see cref="TestServer"/>: temp DB/store/uploads, fake backend) plus a real <see cref="RemoteHost"/> on
/// loopback (HTTPS + bootstrap on port 0) with a local CA in a temp folder. Clients trust exactly that CA.
/// Requests may carry <c>X-Test-Peer</c> to simulate another peer address (internal test hook).
/// </summary>
public sealed class RemoteTestHost : IAsyncDisposable
{
    public const string PeerHeader = "X-Test-Peer";

    private readonly List<HubConnection> _hubs = [];
    private readonly List<HttpClient> _clients = [];

    private RemoteTestHost(TestServer server, LocalCertificateAuthority ca, ServerCertificateManager certificates, RemoteHost remote,
        FakeNetwork network, string certDirectory)
    {
        Server = server;
        Ca = ca;
        Certificates = certificates;
        Remote = remote;
        Network = network;
        CertDirectory = certDirectory;
    }

    public TestServer Server { get; }

    public LocalCertificateAuthority Ca { get; }

    public ServerCertificateManager Certificates { get; }

    public RemoteHost Remote { get; }

    public FakeNetwork Network { get; }

    public string CertDirectory { get; }

    public Uri BaseUri => new($"https://localhost:{Remote.HttpsPort}/");

    public Uri BootstrapUri => new($"http://127.0.0.1:{Remote.BootstrapPort}/");

    public string Origin => $"https://localhost:{Remote.HttpsPort}";

    public static async Task<RemoteTestHost> StartAsync()
    {
        var server = await TestServer.StartAsync();
        var network = new FakeNetwork();
        var certDirectory = Path.Combine(server.Root, "certs");
        var ca = LocalCertificateAuthority.LoadOrCreate(certDirectory, UnprotectedSecretProtector.Instance, "TEST-PC", server.Clock);
        var certificates = new ServerCertificateManager(ca, network, UnprotectedSecretProtector.Instance, certDirectory, server.Clock);
        var remote = new RemoteHost(server.App, certificates, new RemoteHostOptions
        {
            HttpsPort = 0,
            BootstrapPort = 0,
            BindAddress = IPAddress.Loopback,
            PeerAddressOverride = context =>
                context.Request.Headers.TryGetValue(PeerHeader, out var peer) && IPAddress.TryParse(peer.ToString(), out var address) ? address : null,
        }, network: network);
        await remote.StartAsync();
        return new RemoteTestHost(server, ca, certificates, remote, network, certDirectory);
    }

    /// <summary>Accepts only certificates that chain to this host's CA (host name still checked by .NET).</summary>
    public bool Validate(X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Ca.Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Server.Clock.GetUtcNow().UtcDateTime;
        return chain.Build(certificate);
    }

    public HttpClientHandler NewHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) => Validate(cert, errors),
    };

    /// <summary>HTTPS client; with a token it authenticates with <c>Authorization: Bearer</c>.</summary>
    public HttpClient Http(string? token = null)
    {
        var client = new HttpClient(NewHandler()) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        lock (_clients)
        {
            _clients.Add(client);
        }

        return client;
    }

    public HttpClient Bootstrap()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = BootstrapUri };
        lock (_clients)
        {
            _clients.Add(client);
        }

        return client;
    }

    /// <summary>State-changing API request with the CSRF header.</summary>
    public static HttpRequestMessage Api(HttpMethod method, string path, object? json = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = json is null ? content : JsonContent.Create(json, options: RemoteJson.Options) };
        request.Headers.Add(RemoteAuth.CsrfHeader, "1");
        return request;
    }

    /// <summary>
    /// A phone with a session. <see cref="Role.Admin"/>: signs in through <c>POST /api/login</c> as the test admin
    /// (remembered unless <paramref name="remember"/> is false) and returns the token from the Set-Cookie header.
    /// <see cref="Role.Volunteer"/>: no volunteer can sign in on the remote any more, so this is a phone paired as a
    /// volunteer before contract v2 (device + long-lived session written directly), which the remote must refuse.
    /// </summary>
    public async Task<PairedPhone> PhoneAsync(Role role, string? name = null, bool remember = true)
    {
        name ??= $"{role} phone {Guid.NewGuid():N}"[..20];
        if (role == Role.Volunteer)
        {
            var device = await Server.App.Devices.CreateAsync(name, Role.Volunteer);
            var legacyToken = Core.Security.Tokens.NewToken();
            await Server.App.Sessions.CreateAsync(new Data.Repositories.NewSession(Core.Security.Tokens.HashToken(legacyToken), null, device.Id,
                Role.Volunteer, ClientKind.Remote, Server.Clock.GetUtcNow().AddDays(30)));
            return new PairedPhone(legacyToken, name, role, device.Id);
        }

        using var response = await Http().SendAsync(Api(HttpMethod.Post, "/api/login",
            new { userName = TestServer.AdminName, password = TestServer.AdminSecret, rememberDevice = remember, deviceName = name }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = CookieToken(response) ?? throw new InvalidOperationException("no session cookie");
        Guid? deviceId = remember ? (await Server.App.Devices.ListAsync()).Single(d => d.Name == name).Id : null;
        return new PairedPhone(token, name, role, deviceId ?? Guid.Empty);
    }

    public static string? CookieToken(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(RemoteAuth.CookieName + "=", StringComparison.Ordinal))
                .Select(v => v[(RemoteAuth.CookieName.Length + 1)..].Split(';')[0])
                .FirstOrDefault(v => v.Length > 0)
            : null;

    public HubConnection Hub(string? token, HttpTransportType transport = HttpTransportType.WebSockets)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(BaseUri, Endpoints.RemoteHubPath.TrimStart('/')), options =>
            {
                options.Transports = transport;
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }

                options.HttpMessageHandlerFactory = inner =>
                {
                    if (inner is HttpClientHandler handler)
                    {
                        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) => Validate(cert, errors);
                    }

                    return inner;
                };
                options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    Validate(cert as X509Certificate2 ?? (cert is null ? null : X509CertificateLoader.LoadCertificate(cert.GetRawCertData())), errors);
            })
            .AddJsonProtocol(json => RemoteJson.Configure(json.PayloadSerializerOptions))
            .Build();
        lock (_hubs)
        {
            _hubs.Add(connection);
        }

        return connection;
    }

    public async Task<HubConnection> ConnectHubAsync(string token)
    {
        var hub = Hub(token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await hub.StartAsync(timeout.Token);
        return hub;
    }

    public async ValueTask DisposeAsync()
    {
        HubConnection[] hubs;
        lock (_hubs)
        {
            hubs = _hubs.ToArray();
        }

        foreach (var hub in hubs)
        {
            await hub.DisposeAsync();
        }

        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        await Remote.DisposeAsync();
        Certificates.Dispose();
        Ca.Dispose();
        await Server.DisposeAsync();
    }
}

/// <summary>Decodes the remote's error formats.</summary>
public static class RemoteErrors
{
    /// <summary>The <c>{code, message}</c> carried in a hub error ("… HubException: {json}").</summary>
    public static ErrorInfo FromHub(HubException ex)
    {
        var marker = "HubException: ";
        var at = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "hub error carries a HubException payload: " + ex.Message);
        return JsonSerializer.Deserialize<ErrorInfo>(ex.Message[(at + marker.Length)..], RemoteJson.Options)!;
    }

    public static async Task<ErrorInfo> FromHttpAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorInfo>(RemoteJson.Options))!;

    public static async Task<ErrorInfo> HubThrows(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<HubException>(action);
        return FromHub(ex);
    }
}

public sealed class RemoteFixture : IAsyncLifetime
{
    public RemoteTestHost Host { get; private set; } = null!;

    public async Task InitializeAsync() => Host = await RemoteTestHost.StartAsync();

    public async Task DisposeAsync() => await Host.DisposeAsync();
}
