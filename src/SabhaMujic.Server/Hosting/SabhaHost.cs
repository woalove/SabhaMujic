using Microsoft.Extensions.Logging;
using SabhaMujic.Audio;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;
using SabhaMujic.Data;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Pipe;
using SabhaMujic.Server.Playback;

namespace SabhaMujic.Server.Hosting;

/// <summary>
/// Composes and runs the whole service, cross-platform: database (migrated), protected store, upload staging, decoder,
/// audio output, <see cref="PlaybackEngine"/> + adapter, analysis worker, <see cref="ServerApplication"/>, playback-state
/// persistence, maintenance, the desktop pipe, and any <see cref="IHostedComponent"/>s (e.g. the M5 remote).
/// The Windows service and the dev host differ only in the injected platform pieces.
/// </summary>
public sealed class SabhaHost : IAsyncDisposable
{
    private readonly SabhaHostOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IHostedComponent> _started = [];
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private Task _maintenance = Task.CompletedTask;
    private PlaybackPersistence? _persistence;
    private bool _stopped;

    private SabhaHost(SabhaHostOptions options)
    {
        _options = options;
        _logger = options.LoggerFactory.CreateLogger<SabhaHost>();
        Paths = new SabhaDataPaths(Path.GetFullPath(options.DataRoot));
    }

    public SabhaDataPaths Paths { get; }

    public SqliteDatabase Database { get; private set; } = null!;

    public ProtectedStore Store { get; private set; } = null!;

    public UploadStaging Uploads { get; private set; } = null!;

    public IAudioDecoderFactory Decoders { get; private set; } = null!;

    public IAudioOutput Output { get; private set; } = null!;

    public PlaybackEngine Engine { get; private set; } = null!;

    public EnginePlaybackBackend Playback { get; private set; } = null!;

    public StoreTrackResolver Resolver { get; private set; } = null!;

    public AnalysisWorker Analysis { get; private set; } = null!;

    /// <summary>The application layer: services, repositories, events and <c>CreateSession</c> for transports.</summary>
    public ServerApplication App { get; private set; } = null!;

    public PipeRpcServer? Pipe { get; private set; }

    public bool IsRunning => !_stopped;

    /// <summary>Builds and starts everything. On failure, whatever was started is stopped again and the error is rethrown.</summary>
    public static async Task<SabhaHost> StartAsync(SabhaHostOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = new SabhaHost(options);
        try
        {
            await host.StartCoreAsync(ct).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var loggers = _options.LoggerFactory;
        var clock = _options.Clock ?? TimeProvider.System;
        foreach (var dir in new[] { Paths.Root, Paths.Data, Paths.Library, Paths.Uploads, Paths.Logs })
        {
            Directory.CreateDirectory(dir);
        }

        // The database holds secret and token hashes: protect its folder before creating the file.
        _options.StoreAcl.Secure(Paths.Data);
        Database = new SqliteDatabase(Paths.DatabaseFile, clock);
        var version = await Database.MigrateAsync(ct).ConfigureAwait(false);
        Store = new ProtectedStore(Paths.Library, _options.StoreAcl);
        Uploads = new UploadStaging(Paths.Uploads, _options.StoreAcl, clock);
        Decoders = _options.Decoders ?? new WaveFileDecoderFactory();

        Resolver = new StoreTrackResolver(new SqliteTrackRepository(Database), new SqliteTrackAnalysisRepository(Database), Store,
            loggers.CreateLogger<StoreTrackResolver>());
        Output = _options.OutputFactory?.Invoke() ?? new NullRealtimeOutput();
        Engine = new PlaybackEngine(Output, Decoders, Resolver, _options.Engine);
        Playback = new EnginePlaybackBackend(Engine, Resolver, loggers.CreateLogger<EnginePlaybackBackend>(), clock, _options.StateHeartbeat);

        var events = new EventBroadcaster(loggers.CreateLogger<EventBroadcaster>());
        Analysis = new AnalysisWorker(Decoders, new SqliteTrackRepository(Database), new SqliteTrackAnalysisRepository(Database), Store, Resolver,
            events, loggers.CreateLogger<AnalysisWorker>(), _options.Engine?.Analysis);
        Engine.TrackAnalyzed += (_, e) => Analysis.AcceptEngineResult(e.TrackId, e.Analysis);

        App = new ServerApplication(new ServerDependencies
        {
            Database = Database,
            Store = Store,
            Uploads = Uploads,
            Playback = Playback,
            AudioProbe = new DecoderAudioProbe(Decoders),
            AnalysisQueue = Analysis,
            RemoteUrls = _options.RemoteUrls ?? new LocalRemoteUrlProvider(),
            Options = _options.Server,
            LoggerFactory = loggers,
            Events = events,
        });

        _persistence = new PlaybackPersistence(App.PlaybackState, Playback, loggers.CreateLogger<PlaybackPersistence>(), clock, _options.PersistDebounce);
        await _persistence.RestoreAsync(Engine, ct).ConfigureAwait(false);
        await App.Maintenance.StartupAsync(ct).ConfigureAwait(false);
        var lifetime = _lifetime.Token;
        _maintenance = Task.Run(() => App.Maintenance.RunPeriodicallyAsync(_options.MaintenanceInterval, lifetime), CancellationToken.None);

        if (_options.StartPipe)
        {
            Pipe = new PipeRpcServer(App, new PipeServerOptions { PipeName = _options.PipeName, MaxConnections = _options.PipeMaxConnections },
                _options.PipeStreams, _options.CallerIdentity, loggers.CreateLogger<PipeRpcServer>());
            Pipe.Start();
        }

        foreach (var component in _options.Components)
        {
            await component.StartAsync(this, ct).ConfigureAwait(false);
            _started.Add(component);
        }

        _logger.LogInformation("SabhaMujic started (schema v{Version}, data root {Root}, pipe {Pipe}).", version, Paths.Root,
            _options.StartPipe ? _options.PipeName : "off");
    }

    /// <summary>
    /// Graceful stop: hosted components and the pipe stop first (clients see the service go away), the playback state is
    /// saved, whatever is playing fades out (~300 ms), then the engine, output, analysis and database are released.
    /// Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _stopGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            for (var i = _started.Count - 1; i >= 0; i--)
            {
                await Step("stop component", () => _started[i].StopAsync(ct)).ConfigureAwait(false);
            }

            if (Pipe is not null)
            {
                await Step("stop pipe", () => Pipe.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            await Step("stop maintenance", () => _maintenance).ConfigureAwait(false);

            if (_persistence is not null)
            {
                await Step("save playback state", () => _persistence.FlushAsync(CancellationToken.None)).ConfigureAwait(false);
                await Step("stop persistence", () => _persistence.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            if (Engine is not null)
            {
                await Step("fade out", FadeOutAsync).ConfigureAwait(false);
            }

            if (Playback is not null)
            {
                await Step("stop playback adapter", () => Playback.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            await Step("stop engine", () =>
            {
                Engine?.Dispose();
                Output?.Dispose();
                Analysis?.Dispose();
                App?.Dispose();
                Database?.ClearPool();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            _logger.LogInformation("SabhaMujic stopped.");
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task FadeOutAsync()
    {
        if (Engine.GetState().Status is not (PlaybackStatus.Playing or PlaybackStatus.Crossfading))
        {
            return;
        }

        // Straight to the mixer: the engine's own state (and therefore the saved settings) stays untouched.
        Engine.Mixer.Stop(AudioFormat.MillisecondsToFrames(_options.StopFade.TotalMilliseconds));
        await Task.Delay(_options.StopFade + TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
    }

    private async Task Step(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Shutdown step '{Step}' failed; continuing.", what);
        }
    }
}
