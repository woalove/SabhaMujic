using System.Net;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Remote;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Hosting;

/// <summary>Turns on the HTTPS remote (and its HTTP bootstrap site) inside <see cref="SabhaHost"/>.</summary>
public sealed class SabhaRemoteOptions
{
    /// <summary>
    /// Protects the CA and TLS keys at rest: DPAPI (machine scope) in the Windows service;
    /// <see cref="UnprotectedSecretProtector"/> only for development and tests.
    /// </summary>
    public required ISecretProtector SecretProtector { get; init; }

    /// <summary>HTTPS port (app, API, hub). 0 picks a free port.</summary>
    public int HttpsPort { get; init; } = Endpoints.RemoteHttpsPort;

    /// <summary>Plain-HTTP bootstrap port (CA profile + instructions only). 0 picks a free port; null disables it.</summary>
    public int? BootstrapPort { get; init; } = Endpoints.BootstrapHttpPort;

    /// <summary>Address to listen on; null listens on every interface (the remote still refuses non-private peers).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>LAN identity (addresses for the certificate and URLs). Default: the machine's network interfaces.</summary>
    public INetworkAddressProvider? Network { get; init; }

    /// <summary>Fixed host for the phone setup URLs (tests); null uses the primary private IPv4.</summary>
    public string? UrlHostOverride { get; init; }

    /// <summary>Machine name used in the CA subject. Default: <see cref="Environment.MachineName"/>.</summary>
    public string? MachineName { get; init; }
}

/// <summary>
/// The <see cref="RemoteHost"/> as an <see cref="IHostedComponent"/>. The CA and server certificate are loaded (or created)
/// in <c>&lt;DataRoot&gt;\certs</c>, which is secured with the host's <see cref="IStoreAcl"/>, before the application is
/// built, so the "Set up a phone" panel shows the CA fingerprint and the bootstrap site from the start.
/// </summary>
public sealed class RemoteComponent : IHostedComponent, IDisposable
{
    private readonly SabhaRemoteOptions _options;
    private readonly INetworkAddressProvider _network;
    private readonly LocalCertificateAuthority _ca;
    private readonly ServerCertificateManager _certificates;

    private RemoteComponent(SabhaRemoteOptions options, INetworkAddressProvider network, LocalCertificateAuthority ca,
        ServerCertificateManager certificates, RemoteUrlProvider urls)
    {
        _options = options;
        _network = network;
        _ca = ca;
        _certificates = certificates;
        Urls = urls;
    }

    /// <summary>Phone setup URLs and the CA fingerprint for the application layer.</summary>
    public RemoteUrlProvider Urls { get; }

    public LocalCertificateAuthority Authority => _ca;

    public ServerCertificateManager Certificates => _certificates;

    /// <summary>The running remote (null before start and after stop).</summary>
    public RemoteHost? Host { get; private set; }

    /// <summary>Loads or creates the CA and server certificate in <paramref name="certsDirectory"/> (secured first).</summary>
    public static RemoteComponent Create(SabhaRemoteOptions options, string certsDirectory, IStoreAcl acl, TimeProvider clock, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(certsDirectory);
        acl.Secure(certsDirectory);
        var network = options.Network ?? new SystemNetworkAddressProvider();
        var logger = loggers.CreateLogger<RemoteComponent>();
        var ca = LocalCertificateAuthority.LoadOrCreate(certsDirectory, options.SecretProtector, options.MachineName ?? Environment.MachineName,
            clock, logger);
        try
        {
            var certificates = new ServerCertificateManager(ca, network, options.SecretProtector, certsDirectory, clock, logger);
            var urls = new RemoteUrlProvider(network, options.BootstrapPort ?? Endpoints.BootstrapHttpPort, options.UrlHostOverride)
            {
                CaFingerprint = ca.Sha256Fingerprint,
            };
            return new RemoteComponent(options, network, ca, certificates, urls);
        }
        catch
        {
            ca.Dispose();
            throw;
        }
    }

    public async Task StartAsync(SabhaHost host, CancellationToken ct)
    {
        var remote = new RemoteHost(host.App, _certificates, new RemoteHostOptions
        {
            HttpsPort = _options.HttpsPort,
            BootstrapPort = _options.BootstrapPort,
            BindAddress = _options.BindAddress,
        }, Urls, _network);
        await remote.StartAsync(ct).ConfigureAwait(false);
        Host = remote;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (Host is { } remote)
        {
            Host = null;
            await remote.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _certificates.Dispose();
        _ca.Dispose();
    }
}
