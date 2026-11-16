using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Server.Remote;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Http;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Reliability;

/// <summary>A fixed LAN identity (the soak binds the remote to loopback).</summary>
public sealed class SoakNetwork : INetworkAddressProvider
{
    public IReadOnlyList<IPAddress> GetPrivateIPv4Addresses() => [IPAddress.Parse("192.168.1.20")];

    public IPAddress? GetPrimaryIPv4() => IPAddress.Parse("192.168.1.20");

    public string GetHostName() => "soak-pc";
}

/// <summary>
/// Runs the real <see cref="RemoteHost"/> (HTTPS + SignalR on a loopback port, local CA in a folder of the data root) as an
/// <see cref="IHostedComponent"/> of <see cref="SabhaHost"/>, so it starts and stops with the host like the service does.
/// The CA persists across restarts (same folder), so paired device tokens keep working.
/// </summary>
public sealed class SoakRemoteComponent(string certificateDirectory) : IHostedComponent
{
    private readonly SoakNetwork _network = new();
    private LocalCertificateAuthority? _ca;
    private ServerCertificateManager? _certificates;

    public RemoteHost? Remote { get; private set; }

    public X509Certificate2? CaCertificate => _ca?.Certificate;

    public Uri BaseUri => new($"https://localhost:{Remote?.HttpsPort ?? 0}/");

    public async Task StartAsync(SabhaHost host, CancellationToken ct)
    {
        _ca = LocalCertificateAuthority.LoadOrCreate(certificateDirectory, UnprotectedSecretProtector.Instance, "SOAK-PC");
        _certificates = new ServerCertificateManager(_ca, _network, UnprotectedSecretProtector.Instance, certificateDirectory);
        Remote = new RemoteHost(host.App, _certificates, new RemoteHostOptions
        {
            HttpsPort = 0,
            BootstrapPort = null,
            BindAddress = IPAddress.Loopback,
        }, network: _network);
        await Remote.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (Remote is not null)
        {
            await Remote.DisposeAsync().ConfigureAwait(false);
        }

        _certificates?.Dispose();
        _ca?.Dispose();
        Remote = null;
        _certificates = null;
        _ca = null;
    }

    /// <summary>Accepts only certificates that chain to this component's CA.</summary>
    public bool Validate(X509Certificate2? certificate, SslPolicyErrors errors)
    {
        var ca = _ca?.Certificate;
        if (ca is null || certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    public HttpClient Http() => new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) => Validate(cert, errors),
    })
    { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Pairs a device with a one-time code (created through an admin <c>ApiSession</c>) and returns its token.</summary>
    public async Task<string> PairAsync(SabhaHost host, string adminUser, string adminSecret, Role role, string deviceName, CancellationToken ct)
    {
        var admin = host.App.CreateSession(ClientKind.Desktop, "soak-pairing");
        await admin.LoginAsync(adminUser, adminSecret, ct).ConfigureAwait(false);
        var code = (await admin.CreatePairingCodeAsync(role, ct).ConfigureAwait(false)).Code;
        await admin.LogoutAsync(ct).ConfigureAwait(false);

        using var http = Http();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair")
        {
            Content = JsonContent.Create(new { code, deviceName }, options: RemoteJson.Options),
        };
        request.Headers.Add(RemoteAuth.CsrfHeader, "1");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                if (value.StartsWith(RemoteAuth.CookieName + "=", StringComparison.Ordinal))
                {
                    var token = value[(RemoteAuth.CookieName.Length + 1)..].Split(';')[0];
                    if (token.Length > 0)
                    {
                        return token;
                    }
                }
            }
        }

        throw new InvalidOperationException("Pairing returned no device cookie.");
    }

    /// <summary>A SignalR connection to the hub, authenticated with a device token, trusting only this CA.</summary>
    public HubConnection Hub(string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(BaseUri, Endpoints.RemoteHubPath.TrimStart('/')), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
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
}
