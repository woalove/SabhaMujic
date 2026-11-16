using System.Collections.Concurrent;
using System.Text;
using Dapper;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Playback;
using SabhaMujic.Data;
using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Server.Tests;

public sealed class FakeClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>Playback engine stand-in: records every command and keeps a real <see cref="PlaybackQueue"/>.</summary>
public sealed class FakePlaybackBackend : IPlaybackBackend
{
    private readonly Lock _gate = new();
    private readonly PlaybackQueue _queue = new();
    private PlaybackStatus _status = PlaybackStatus.Idle;
    private float _volume = 0.8f;
    private long _revision;

    public ConcurrentQueue<string> Commands { get; } = new();

    public Exception? ThrowOnPause { get; set; }

    public event Action<PlaybackStateDto>? StateChanged;

    public event Action<QueueDto>? QueueChanged;

    public PlaybackStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public void SetPlaying()
    {
        lock (_gate)
        {
            _status = PlaybackStatus.Playing;
        }
    }

    public void RaiseState() => StateChanged?.Invoke(GetState());

    public PlaybackStateDto GetState()
    {
        lock (_gate)
        {
            return new PlaybackStateDto(_status, _queue.Current?.TrackId, _queue.Current?.QueueItemId, null, 0, 0, _volume, false,
                LoopMode.Off, 6, null, ++_revision, DateTimeOffset.UnixEpoch);
        }
    }

    public QueueDto GetQueue()
    {
        lock (_gate)
        {
            return _queue.Snapshot();
        }
    }

    public Task PlayTrackAsync(Guid trackId, CancellationToken ct) => Command($"PlayTrack {trackId}", () => _status = PlaybackStatus.Playing);

    public Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_queue.JumpTo(queueItemId))
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "No such queue item.");
            }
        }

        return Command("PlayQueueItem", () => _status = PlaybackStatus.Playing);
    }

    public Task PauseAsync(CancellationToken ct)
    {
        if (ThrowOnPause is { } ex)
        {
            throw ex;
        }

        return Command("Pause", () => _status = PlaybackStatus.Paused);
    }

    public Task ResumeAsync(CancellationToken ct) => Command("Resume", () => _status = PlaybackStatus.Playing);

    public Task StopAsync(CancellationToken ct) => Command("Stop", () => _status = PlaybackStatus.Stopped);

    public Task NextAsync(CancellationToken ct) => Command("Next", () => { });

    public Task PreviousAsync(CancellationToken ct) => Command("Previous", () => { });

    public Task SeekAsync(double positionSeconds, CancellationToken ct) => Command("Seek", () => { });

    public Task SetVolumeAsync(float volume, CancellationToken ct) => Command("SetVolume", () => _volume = volume);

    public Task SetMutedAsync(bool muted, CancellationToken ct) => Command("SetMuted", () => { });

    public Task SetLoopModeAsync(LoopMode mode, CancellationToken ct) => Command("SetLoopMode", () => { });

    public Task SetCrossfadeAsync(double seconds, CancellationToken ct) => Command("SetCrossfade", () => { });

    public Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct) =>
        QueueCommand("Enqueue", q => q.Enqueue(trackIds, playNext));

    public Task<QueueDto> ReplaceQueueAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct) =>
        QueueCommand("ReplaceQueue", q => q.ReplaceAll(trackIds));

    public Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct) =>
        QueueCommand("RemoveFromQueue", q =>
        {
            if (!q.Remove(queueItemId))
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "No such queue item.");
            }
        });

    public Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct) =>
        QueueCommand("MoveQueueItem", q =>
        {
            if (!q.Move(queueItemId, newIndex))
            {
                throw new SabhaMujicException(ErrorCode.NotFound, "No such queue item.");
            }
        });

    public Task<QueueDto> ClearQueueAsync(CancellationToken ct) => QueueCommand("ClearQueue", q => q.ClearUpcomingAndHistory());

    public Task OnTrackChangedAsync(Guid trackId, LibraryChangeKind kind, CancellationToken ct) => Command($"TrackChanged {kind}", () => { });

    private Task Command(string name, Action apply)
    {
        lock (_gate)
        {
            apply();
        }

        Commands.Enqueue(name);
        StateChanged?.Invoke(GetState());
        return Task.CompletedTask;
    }

    private Task<QueueDto> QueueCommand(string name, Action<PlaybackQueue> apply)
    {
        QueueDto snapshot;
        lock (_gate)
        {
            apply(_queue);
            snapshot = _queue.Snapshot();
        }

        Commands.Enqueue(name);
        QueueChanged?.Invoke(snapshot);
        return Task.FromResult(snapshot);
    }
}

public sealed class FakeAudioProbe : IAudioProbe
{
    public AudioProbeResult Result { get; set; } = AudioProbeResult.Supported(123.5);

    public ConcurrentQueue<string> Probed { get; } = new();

    public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        Assert.True(File.Exists(path), "The probe must see the staged file.");
        Probed.Enqueue(path);
        return Task.FromResult(Result);
    }
}

public sealed class FakeAnalysisQueue : IAnalysisQueue
{
    public int AnalysisVersion { get; set; } = 1;

    public ConcurrentQueue<Guid> Queued { get; } = new();

    public ConcurrentQueue<ImportProgressTarget?> Progress { get; } = new();

    public void Enqueue(Guid trackId, ImportProgressTarget? progress = null)
    {
        Queued.Enqueue(trackId);
        Progress.Enqueue(progress);
    }
}

public sealed class FakeUrls : IRemoteUrlProvider
{
    public string GetPairingUrl(string code) => $"https://sabha.local:8443/pair#{code}";

    public string GetBootstrapUrl() => "http://sabha.local:8080/";
}

/// <summary>A full in-process server (temp DB + store + uploads) with a pipe listener on a unique name.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public const string AdminName = "admin";
    public const string AdminSecret = "admin-pass";
    public const string VolunteerName = "vol";
    public const string VolunteerSecret = "vol-pass";

    private readonly List<PipeSabhaClient> _clients = [];

    private TestServer(string root, PipeServerOptions pipeOptions, ServerOptions options)
    {
        Root = root;
        PipeOptions = pipeOptions;
        Database = new SqliteDatabase(Path.Combine(root, "db", "test.db"), Clock);
        Store = new ProtectedStore(Path.Combine(root, "Library"), NoOpStoreAcl.Instance);
        Uploads = new UploadStaging(Path.Combine(root, "Uploads"), NoOpStoreAcl.Instance, Clock);
        App = new ServerApplication(new ServerDependencies
        {
            Database = Database,
            Store = Store,
            Uploads = Uploads,
            Playback = Backend,
            AudioProbe = Probe,
            AnalysisQueue = AnalysisQueue,
            RemoteUrls = new FakeUrls(),
            Options = options,
        });
    }

    public FakeClock Clock { get; } = new();

    public string Root { get; }

    public PipeServerOptions PipeOptions { get; }

    public SqliteDatabase Database { get; }

    public ProtectedStore Store { get; }

    public UploadStaging Uploads { get; }

    public FakePlaybackBackend Backend { get; } = new();

    public FakeAudioProbe Probe { get; } = new();

    public FakeAnalysisQueue AnalysisQueue { get; } = new();

    public ServerApplication App { get; }

    public PipeRpcServer? Pipe { get; private set; }

    public Guid AdminId { get; private set; }

    public Guid VolunteerId { get; private set; }

    /// <summary>Pipe caller identity used by <see cref="StartPipe"/> (default: never elevated).</summary>
    public ICallerIdentity? Identity { get; set; }

    /// <param name="pipe">Builds the pipe options from the generated pipe name (overrides maxConnections/maxMessageBytes).</param>
    public static async Task<TestServer> StartAsync(int maxConnections = 32, int maxMessageBytes = 4 * 1024 * 1024, ServerOptions? options = null,
        bool seedUsers = true, ICallerIdentity? identity = null, Func<string, PipeServerOptions>? pipe = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sabhamujic-server-tests", Guid.NewGuid().ToString("N"));
        var pipeName = "sm-" + Guid.NewGuid().ToString("N")[..12];
        var server = new TestServer(root,
            pipe?.Invoke(pipeName) ?? new PipeServerOptions { PipeName = pipeName, MaxConnections = maxConnections, MaxMessageBytes = maxMessageBytes },
            options ?? new ServerOptions { SecretHashIterations = 1_000 });
        await server.Database.MigrateAsync();
        server.Identity = identity;
        if (!seedUsers)
        {
            server.StartPipe();
            return server;
        }

        Assert.True(await server.App.Auth.EnsureInitialAdminAsync(AdminName, AdminSecret));
        server.AdminId = (await server.App.Users.GetByNameAsync(AdminName))!.Id;
        var vol = await server.App.Users.CreateAsync(VolunteerName, Role.Volunteer, server.App.Auth.HashSecret(VolunteerSecret));
        server.VolunteerId = vol.Id;
        server.StartPipe();
        return server;
    }

    public void StartPipe()
    {
        Pipe = new PipeRpcServer(App, PipeOptions, identity: Identity);
        Pipe.Start();
    }

    public async Task StopPipeAsync()
    {
        if (Pipe is not null)
        {
            await Pipe.DisposeAsync();
            Pipe = null;
        }
    }

    public PipeSabhaClient NewClient(TimeSpan? quietReconnectAfter = null)
    {
        var client = new PipeSabhaClient(new PipeClientOptions
        {
            PipeName = PipeOptions.PipeName,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            InitialBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(250),
            QuietReconnectAfter = quietReconnectAfter ?? TimeSpan.FromSeconds(5),
        });
        lock (_clients)
        {
            _clients.Add(client);
        }

        return client;
    }

    public async Task<PipeSabhaClient> ConnectAsync(string? userName = null, string? secret = null)
    {
        var client = NewClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(timeout.Token);
        if (userName is not null)
        {
            await client.Auth.LoginAsync(userName, secret!);
        }

        return client;
    }

    public Task<PipeSabhaClient> ConnectAdminAsync() => ConnectAsync(AdminName, AdminSecret);

    public Task<PipeSabhaClient> ConnectVolunteerAsync() => ConnectAsync(VolunteerName, VolunteerSecret);

    /// <summary>Everything observable that a denied call must not change.</summary>
    public async Task<string> SnapshotAsync()
    {
        var sb = new StringBuilder();
        await using (var conn = await Database.OpenConnectionAsync())
        {
            var tables = await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
            foreach (var table in tables)
            {
                // Table names come from sqlite_master, not from input.
                foreach (var row in await conn.QueryAsync($"SELECT * FROM \"{table}\" ORDER BY 1;"))
                {
                    sb.Append(table).Append(": ");
                    foreach (var (key, value) in (IDictionary<string, object>)row)
                    {
                        sb.Append(key).Append('=').Append(value).Append(';');
                    }

                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine($"backend: {string.Join(',', Backend.Commands)}");
        sb.AppendLine($"probe: {Probe.Probed.Count} analysis: {AnalysisQueue.Queued.Count} uploads: {Uploads.ActiveCount}");
        sb.AppendLine($"store: {string.Join(',', Directory.EnumerateFiles(Store.RootDirectory, "*", SearchOption.AllDirectories).Order())}");
        return sb.ToString();
    }

    public int StoredFileCount() =>
        Directory.EnumerateFiles(Store.RootDirectory, "*", SearchOption.AllDirectories).Count(f => !f.Contains(ProtectedStore.StagingFolderName));

    public async ValueTask DisposeAsync()
    {
        PipeSabhaClient[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }

        await StopPipeAsync();
        App.Dispose();
        Database.ClearPool();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class AssertEx
{
    public static async Task<SabhaMujicException> ThrowsCode(ErrorCode code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    /// <summary>Polls until <paramref name="condition"/> holds (event delivery is asynchronous).</summary>
    public static async Task Eventually(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Timed out waiting: " + because);
            }

            await Task.Delay(20);
        }
    }
}

/// <summary>Reports every pipe client with a fixed identity (e.g. an elevated local Administrator).</summary>
public sealed class FixedCallerIdentity(string principal, bool elevated) : ICallerIdentity
{
    public PipeCallerInfo Describe(System.IO.Pipes.NamedPipeServerStream pipe) => new(principal, elevated);
}
