using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Http;
using SabhaMujic.Server.Remote.Hub;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Remote;

public sealed class RemoteHostOptions
{
    /// <summary>HTTPS port (API, hub, web app). 0 picks a free port; a restart reuses the port picked first.</summary>
    public int HttpsPort { get; init; } = Endpoints.RemoteHttpsPort;

    /// <summary>Plain-HTTP bootstrap port. 0 picks a free port; null disables the bootstrap site.</summary>
    public int? BootstrapPort { get; init; } = Endpoints.BootstrapHttpPort;

    /// <summary>Address to listen on; null listens on every interface (IPv4 and IPv6). Tests use loopback.</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>How often the HTTPS certificate is checked for expiry and IP changes (also checked on network changes).</summary>
    public TimeSpan CertificateCheckInterval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Test hook: replaces the peer address before the private-network filter runs.</summary>
    internal Func<HttpContext, IPAddress?>? PeerAddressOverride { get; init; }
}

/// <summary>
/// The remote, hosted inside the service process: an HTTPS Kestrel server (local-CA leaf picked per handshake by
/// <see cref="ServerCertificateManager"/>, so rotation needs no restart) with the mobile web app, the HTTP API and the
/// SignalR hub, plus the plain-HTTP bootstrap site that only hands out the CA. Both refuse peers outside loopback and
/// private/link-local networks. Stopping or crashing the remote never touches playback.
/// </summary>
public sealed class RemoteHost : IAsyncDisposable
{
    /// <summary>Strict CSP for the HTTPS app: same-origin scripts/styles only, no inline code, no framing.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self' wss:; manifest-src 'self'; worker-src 'none'; object-src 'none'; base-uri 'none'; " +
        "form-action 'self'; frame-ancestors 'none'";

    private readonly ServerApplication _app;
    private readonly ServerCertificateManager _certificates;
    private readonly RemoteHostOptions _options;
    private readonly RemoteUrlProvider? _urls;
    private readonly INetworkAddressProvider _network;
    private readonly WebAssets _assets = new();
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _https;
    private WebApplication? _bootstrap;
    private CancellationTokenSource? _stop;
    private Task _certificateLoop = Task.CompletedTask;
    private int _httpsPort;
    private int? _bootstrapPort;

    public RemoteHost(ServerApplication app, ServerCertificateManager certificates, RemoteHostOptions? options = null,
        RemoteUrlProvider? urls = null, INetworkAddressProvider? network = null)
    {
        _app = app;
        _certificates = certificates;
        _options = options ?? new RemoteHostOptions();
        _urls = urls;
        _network = network ?? new SystemNetworkAddressProvider();
        _logger = app.LoggerFactory.CreateLogger<RemoteHost>();
        _httpsPort = _options.HttpsPort;
        _bootstrapPort = _options.BootstrapPort;
    }

    /// <summary>The HTTPS port actually bound (valid after <see cref="StartAsync"/>).</summary>
    public int HttpsPort => _httpsPort;

    /// <summary>The bootstrap port actually bound, or null when disabled.</summary>
    public int? BootstrapPort => _bootstrapPort;

    public bool IsRunning => _https is not null;

    /// <summary>Live hub connections.</summary>
    public int ConnectionCount => _https?.Services.GetService<RemoteConnections>()?.Count ?? 0;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_https is not null)
            {
                return;
            }

            _certificates.EnsureCurrent();
            var https = BuildHttps();
            await https.StartAsync(ct).ConfigureAwait(false);
            _https = https;
            _httpsPort = BoundPort(https);

            if (_bootstrapPort is { } bootstrapPort)
            {
                var bootstrap = BuildBootstrap(bootstrapPort);
                await bootstrap.StartAsync(ct).ConfigureAwait(false);
                _bootstrap = bootstrap;
                _bootstrapPort = BoundPort(bootstrap);
                if (_urls is not null)
                {
                    _urls.BootstrapPort = _bootstrapPort.Value;
                }
            }

            _stop = new CancellationTokenSource();
            _certificateLoop = Task.Run(() => CertificateLoopAsync(_stop.Token), CancellationToken.None);
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _logger.LogInformation("Remote listening on https port {HttpsPort} (bootstrap http port {BootstrapPort}).", _httpsPort, _bootstrapPort);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Aborts every live hub connection without stopping the server (tests, diagnostics).</summary>
    public void AbortAllConnections() => _https?.Services.GetService<RemoteConnections>()?.AbortAll();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async Task StopCoreAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        if (_stop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _certificateLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
            _stop = null;
        }

        if (_https is { } https)
        {
            _https = null;
            https.Services.GetService<RemoteConnections>()?.AbortAll();
            await StopAppAsync(https).ConfigureAwait(false);
        }

        if (_bootstrap is { } bootstrap)
        {
            _bootstrap = null;
            await StopAppAsync(bootstrap).ConfigureAwait(false);
        }
    }

    private async Task StopAppAsync(WebApplication web)
    {
        using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
        try
        {
            await web.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await web.DisposeAsync().ConfigureAwait(false);
    }

    private WebApplication BuildHttps()
    {
        var builder = CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            ConfigureLimits(kestrel);
            Listen(kestrel, _httpsPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ServerCertificateSelector = (_, _) => _certificates.Current;
                });
            });
        });
        builder.Services.AddRouting();
        builder.Services.AddSingleton(_app);
        builder.Services.AddSingleton<RemoteConnections>();
        builder.Services.AddSignalR(hub =>
        {
            hub.EnableDetailedErrors = false;
            hub.MaximumReceiveMessageSize = 128 * 1024;
            hub.KeepAliveInterval = TimeSpan.FromSeconds(10);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            hub.AddFilter<HubErrorFilter>();
        }).AddJsonProtocol(json => RemoteJson.Configure(json.PayloadSerializerOptions));

        var web = builder.Build();
        web.Use((context, next) => GuardAsync(context, next, ContentSecurityPolicy));
        web.Use(HandleErrorsAsync);
        web.Use(async (context, next) =>
        {
            if (!await _assets.TryServeAsync(context).ConfigureAwait(false))
            {
                await next(context).ConfigureAwait(false);
            }
        });
        web.Use(RequireSameOriginAsync);
        web.Use(AuthenticateHubAsync);
        web.UseRouting();
        RemoteApiEndpoints.Map(web, _app);
        web.MapHub<RemoteHub>(Endpoints.RemoteHubPath, hub =>
        {
            hub.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
            hub.ApplicationMaxBufferSize = 128 * 1024;
            hub.TransportMaxBufferSize = 128 * 1024;
        });
        web.UseEndpoints(_ => { });
        web.Run(context => RemoteJson.WriteErrorAsync(context, ErrorCode.NotFound, "Not found."));
        return web;
    }

    private WebApplication BuildBootstrap(int port)
    {
        var builder = CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            ConfigureLimits(kestrel);
            kestrel.Limits.MaxRequestBodySize = 0;
            Listen(kestrel, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        var site = new BootstrapSite(_certificates.Authority, () => _httpsPort,
            () => _network.GetPrimaryIPv4()?.ToString() ?? _network.GetHostName() + ".local");
        var web = builder.Build();
        web.Use((context, next) => GuardAsync(context, next, site.ContentSecurityPolicy));
        web.Run(site.HandleAsync);
        return web;
    }

    private WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = "SabhaMujic.Remote",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.Services.AddSingleton(_app.LoggerFactory);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = _options.ShutdownTimeout);
        return builder;
    }

    private static void ConfigureLimits(KestrelServerOptions kestrel)
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = AdminLimits.MaxChunkBytes;
        kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
        kestrel.Limits.MaxRequestLineSize = 8 * 1024;
        kestrel.Limits.MaxConcurrentConnections = 256;
        kestrel.Limits.MaxConcurrentUpgradedConnections = 128;
        kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    }

    private void Listen(KestrelServerOptions kestrel, int port, Action<ListenOptions> configure)
    {
        if (_options.BindAddress is { } address)
        {
            kestrel.Listen(address, port, configure);
        }
        else
        {
            kestrel.ListenAnyIP(port, configure);
        }
    }

    private static int BoundPort(WebApplication web)
    {
        var addresses = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            var colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.AsSpan(colon + 1).TrimEnd('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("Kestrel did not report a bound address.");
    }

    /// <summary>Test hook, security headers, then the private-network peer filter (403).</summary>
    private async Task GuardAsync(HttpContext context, RequestDelegate next, string contentSecurityPolicy)
    {
        if (_options.PeerAddressOverride is { } peerOverride && peerOverride(context) is { } fakePeer)
        {
            context.Connection.RemoteIpAddress = fakePeer;
        }

        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = contentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

        if (!AddressRules.IsAllowedPeer(context.Connection.RemoteIpAddress))
        {
            _logger.LogWarning("Refused a request from non-local address {Address}.", context.Connection.RemoteIpAddress);
            await RemoteJson.WriteErrorAsync(context, ErrorCode.Forbidden, "Only devices on the local network may use the remote.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private async Task HandleErrorsAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            var code = ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCode.TooLarge : ErrorCode.InvalidArgument;
            await RemoteJson.WriteErrorAsync(context, code, code == ErrorCode.TooLarge ? "The request body is too large." : "Bad request.")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
        {
            _logger.LogError(ex, "Unexpected failure handling {Path}.", context.Request.Path);
            await RemoteJson.WriteErrorAsync(context, ErrorCode.Internal, HubErrorFilter.InternalMessage).ConfigureAwait(false);
        }
    }

    /// <summary>CSRF defence for the API: state-changing requests need <c>X-SabhaMujic: 1</c>; any Origin must be our own.</summary>
    private static async Task RequireSameOriginAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments(Endpoints.RemoteHubPath))
        {
            if (!RemoteAuth.IsSameOriginOrNone(context))
            {
                await RemoteJson.WriteErrorAsync(context, ErrorCode.Forbidden, "Cross-origin requests are not allowed.").ConfigureAwait(false);
                return;
            }

            var method = context.Request.Method;
            var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
            if (path.StartsWithSegments("/api") && !safe && !RemoteAuth.HasCsrfHeader(context))
            {
                await RemoteJson.WriteErrorAsync(context, ErrorCode.Forbidden, $"The {RemoteAuth.CsrfHeader} header is required.").ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Hub requests (negotiate, WebSocket, long polling) need a valid device/session token (401 otherwise).</summary>
    private async Task AuthenticateHubAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments(Endpoints.RemoteHubPath))
        {
            try
            {
                var session = await RemoteAuth.OpenSessionAsync(_app, context).ConfigureAwait(false);
                if (!session.Caller.IsAuthenticated)
                {
                    throw new SabhaMujicException(ErrorCode.Unauthenticated, "Sign in is required.");
                }
            }
            catch (SabhaMujicException ex)
            {
                if (ex.Code == ErrorCode.Unauthenticated && RemoteAuth.GetToken(context) is not null)
                {
                    RemoteAuth.ClearCookie(context);
                }

                await RemoteJson.WriteErrorAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private async Task CertificateLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.CertificateCheckInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            CheckCertificate();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false); // let DHCP settle
        CheckCertificate();
    });

    private void CheckCertificate()
    {
        try
        {
            _certificates.EnsureCurrent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking the HTTPS certificate failed.");
        }
    }
}
