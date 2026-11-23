using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Server.Hosting;

/// <summary>Folder layout under the data root (Windows service: <c>%ProgramData%\SabhaMujic</c>).</summary>
public sealed record SabhaDataPaths(string Root)
{
    /// <summary>SQLite database folder (holds secret hashes and token hashes; ACL-protected like the library).</summary>
    public string Data => Path.Combine(Root, "data");

    public string DatabaseFile => Path.Combine(Data, "sabhamujic.db");

    /// <summary>Protected audio store.</summary>
    public string Library => Path.Combine(Root, "library");

    /// <summary>Chunked upload staging (protected too: it holds audio).</summary>
    public string Uploads => Path.Combine(Root, "uploads");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Remote CA and TLS certificate (keys protected with the secret protector; ACL-protected folder).</summary>
    public string Certs => Path.Combine(Root, "certs");
}

/// <summary>
/// A component started with the host after the core (database, engine, application, pipe) is up and stopped before
/// the core shuts down. This is the extension point for the HTTPS remote (M5): it gets <see cref="SabhaHost.App"/>
/// and creates its own <c>ApiSession</c>s.
/// </summary>
public interface IHostedComponent
{
    Task StartAsync(SabhaHost host, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

public sealed class SabhaHostOptions
{
    /// <summary>Root folder for data, library, uploads and logs.</summary>
    public required string DataRoot { get; init; }

    public string PipeName { get; init; } = Endpoints.PipeName;

    /// <summary>Platform decoder (MediaFoundation on Windows). Default: WAV only.</summary>
    public IAudioDecoderFactory? Decoders { get; init; }

    /// <summary>Creates the audio output (WASAPI on Windows). Default: a real-time <see cref="NullRealtimeOutput"/>.</summary>
    public Func<IAudioOutput>? OutputFactory { get; init; }

    /// <summary>
    /// Runs first: creates the data root and its folders, refuses junctions/symlinks, and (Windows service) takes ownership
    /// and applies the folder DACLs. Default: the cross-platform checks only.
    /// </summary>
    public DataRootGuard RootGuard { get; init; } = new();

    /// <summary>Applied to the data, library and uploads folders. Default: no-op (development only).</summary>
    public IStoreAcl StoreAcl { get; init; } = NoOpStoreAcl.Instance;

    public IPipeStreamFactory? PipeStreams { get; init; }

    public ICallerIdentity? CallerIdentity { get; init; }

    /// <summary>
    /// Overrides the phone-setup URLs. Default: the remote's <c>RemoteUrlProvider</c> when <see cref="Remote"/> is set, else
    /// <see cref="LocalRemoteUrlProvider"/>.
    /// </summary>
    public IRemoteUrlProvider? RemoteUrls { get; init; }

    /// <summary>Runs the HTTPS remote + bootstrap site in the host (as the first <see cref="IHostedComponent"/>); null disables it.</summary>
    public SabhaRemoteOptions? Remote { get; init; }

    public ServerOptions Server { get; init; } = new();

    public int PipeMaxConnections { get; init; } = 32;

    /// <summary>Per OS account cap on pipe connections (<see cref="Pipe.PipeServerOptions.MaxConnectionsPerPrincipal"/>); the Windows service sets 8.</summary>
    public int? PipeMaxConnectionsPerPrincipal { get; init; }

    /// <summary>Handshake deadline for pipe connections (<see cref="Pipe.PipeServerOptions.HelloTimeout"/>).</summary>
    public TimeSpan? PipeHelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Sign-in deadline for pipe connections (<see cref="Pipe.PipeServerOptions.AuthenticationTimeout"/>); off by default (open desktop use).</summary>
    public TimeSpan? PipeAuthenticationTimeout { get; init; }

    public PlaybackEngineOptions? Engine { get; init; }

    /// <summary>Clock for the database, sessions and timers. Default: system clock.</summary>
    public TimeProvider? Clock { get; init; }

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PersistDebounce { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan StateHeartbeat { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Fade-out applied to whatever is playing when the host stops.</summary>
    public TimeSpan StopFade { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Start the desktop pipe listener (tests of the core alone may turn it off).</summary>
    public bool StartPipe { get; init; } = true;

    /// <summary>Components (e.g. the M5 remote) started after the core; see <see cref="IHostedComponent"/>.</summary>
    public IReadOnlyList<IHostedComponent> Components { get; init; } = [];

    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
}

/// <summary>Phone-setup URLs on this machine's host name, used when the remote is not enabled (informational only).</summary>
public sealed class LocalRemoteUrlProvider(string? host = null) : IRemoteUrlProvider
{
    private readonly string _host = host ?? Environment.MachineName.ToLowerInvariant() + ".local";

    public string GetBootstrapUrl() => $"http://{_host}:{Endpoints.BootstrapHttpPort}/";

    public string GetRemoteUrl() => $"https://{_host}:{Endpoints.RemoteHttpsPort}/";
}
