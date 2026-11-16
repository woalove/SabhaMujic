using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SabhaMujic.Audio.Windows;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Service.Security;

namespace SabhaMujic.Service;

/// <summary>Windows composition of <see cref="SabhaHost"/>.</summary>
public static class WindowsHostComposition
{
    /// <summary><c>%ProgramData%\SabhaMujic</c>.</summary>
    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ServiceSid.ServiceName);

    /// <summary>
    /// Media Foundation decoding, WASAPI output, protected folders (<see cref="WindowsStoreAcl"/>), the ACL'd pipe and
    /// token-based caller identity. With <paramref name="remote"/> the iPhone remote runs too, its CA/TLS keys protected
    /// by DPAPI (machine scope) in the ACL'd <c>certs</c> folder.
    /// </summary>
    public static SabhaHostOptions CreateOptions(string dataRoot, ILoggerFactory loggerFactory, string? pipeName = null,
        SabhaRemoteOptions? remote = null) => new()
    {
        DataRoot = dataRoot,
        PipeName = pipeName ?? Endpoints.PipeName,
        Decoders = new MediaFoundationDecoderFactory(),
        OutputFactory = () => new WasapiOutput(),
        RootGuard = new WindowsDataRootGuard(),
        StoreAcl = new WindowsStoreAcl(),
        PipeStreams = new WindowsPipeStreamFactory(),
        CallerIdentity = new WindowsCallerIdentity(),
        PipeMaxConnectionsPerPrincipal = 8, // any local account can open the pipe; one account cannot fill it
        Remote = remote,
        LoggerFactory = loggerFactory,
    };

    /// <summary>The service's options from its configuration (see <see cref="ServiceSettings"/>).</summary>
    public static SabhaHostOptions CreateOptions(ServiceSettings settings, ILoggerFactory loggerFactory) =>
        CreateOptions(settings.DataRoot, loggerFactory, settings.PipeName, settings.RemoteEnabled
            ? new SabhaRemoteOptions
            {
                SecretProtector = new DpapiSecretProtector(),
                HttpsPort = settings.HttpsPort,
                BootstrapPort = settings.BootstrapPort,
            }
            : null);
}

/// <summary>Runs <see cref="SabhaHost"/> for the lifetime of the Windows service (or console run).</summary>
public sealed class SabhaServiceHost(SabhaHostOptions options, ILogger<SabhaServiceHost> logger) : IHostedService, IAsyncDisposable
{
    private SabhaHost? _host;

    public SabhaHost? Host => _host;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting SabhaMujic with data root {Root}.", options.DataRoot);
        _host = await SabhaHost.StartAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Service stop: graceful (pipe closed, state saved, fade out, devices released).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_host is not null)
        {
            await _host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
