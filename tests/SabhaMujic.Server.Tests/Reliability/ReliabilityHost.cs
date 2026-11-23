using System.IO.Pipes;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Output;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Reliability;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>
/// A real <see cref="SabhaHost"/> (engine, WAV decoder, accelerated <see cref="NullRealtimeOutput"/>, SQLite, store, pipe) on a
/// temp data root with a capturing logger, for fault-injection and stress tests. Every pipe client counts as an elevated
/// administrator (first-run setup), then signs in as whichever user the test asks for.
/// </summary>
public sealed class ReliabilityHost : IAsyncDisposable
{
    public const string AdminUser = "admin";
    public const string AdminSecret = "admin-pin";

    private readonly List<PipeSabhaClient> _clients = [];
    private readonly Func<SabhaHostOptions, SabhaHostOptions>? _customize;

    private ReliabilityHost(double speed, string? root, Func<SabhaHostOptions, SabhaHostOptions>? customize)
    {
        Speed = speed;
        _customize = customize;
        Root = root ?? Path.Combine(Path.GetTempPath(), "sabhamujic-reliability", Guid.NewGuid().ToString("N"));
        PipeName = "sm-rel-" + Guid.NewGuid().ToString("N")[..12];
    }

    public string Root { get; }

    public string PipeName { get; }

    public double Speed { get; }

    public CapturingLoggerFactory Logs { get; } = new();

    public SabhaHost Host { get; private set; } = null!;

    public ServerApplication App => Host.App;

    public NullRealtimeOutput Output => (NullRealtimeOutput)Host.Output;

    public PipeSabhaClient Admin { get; private set; } = null!;

    /// <param name="root">Data root (default: a fresh temp folder).</param>
    /// <param name="customize">Adjusts the host options (e.g. persistence debounce, server options).</param>
    public static async Task<ReliabilityHost> StartAsync(double speed = 20, string? root = null, Func<SabhaHostOptions, SabhaHostOptions>? customize = null,
        bool setup = true)
    {
        var h = new ReliabilityHost(speed, root, customize);
        await h.StartHostAsync();
        if (setup)
        {
            h.Admin = await h.ConnectAsync();
            await h.Admin.Auth.CompleteInitialSetupAsync(AdminUser, AdminSecret);
        }

        return h;
    }

    public SabhaHostOptions Options()
    {
        var options = new SabhaHostOptions
        {
            DataRoot = Root,
            PipeName = PipeName,
            OutputFactory = () => new NullRealtimeOutput(Speed),
            CallerIdentity = new ElevatedIdentity(),
            Server = new ServerOptions { SecretHashIterations = 1_000 },
            Engine = new PlaybackEngineOptions(),
            StateHeartbeat = TimeSpan.FromMilliseconds(500),
            PersistDebounce = TimeSpan.FromMilliseconds(100),
            LoggerFactory = Logs,
        };
        return _customize?.Invoke(options) ?? options;
    }

    public async Task StartHostAsync() => Host = await SabhaHost.StartAsync(Options());

    /// <summary>Graceful stop and a new host on the same data root and pipe name (clients reconnect on their own).</summary>
    public async Task RestartAsync()
    {
        await Host.StopAsync();
        await Host.DisposeAsync();
        await StartHostAsync();
    }

    public async Task<PipeSabhaClient> ConnectAsync(string? user = null, string? secret = null)
    {
        var client = new PipeSabhaClient(new PipeClientOptions
        {
            PipeName = PipeName,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            InitialBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(250),
        });
        lock (_clients)
        {
            _clients.Add(client);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(timeout.Token);
        if (user is not null)
        {
            await client.Auth.LoginAsync(user, secret!, timeout.Token);
        }

        return client;
    }

    /// <summary>
    /// Creates volunteer accounts <c>vol0..vol{n-1}</c> (secret <c>vol-pin-i</c>). Since contract v2 the API only creates
    /// administrators, so these are written directly, like accounts left over from before v2 (still able to sign in on
    /// the desktop, with volunteer rights).
    /// </summary>
    public async Task CreateVolunteersAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await App.Users.CreateAsync($"vol{i}", Role.Volunteer, App.Auth.HashSecret($"vol-pin-{i}"));
        }
    }

    public Task<PipeSabhaClient> ConnectVolunteerAsync(int i) => ConnectAsync($"vol{i}", $"vol-pin-{i}");

    public async Task<Guid> ImportAsync(byte[] wav, string title, PipeSabhaClient? admin = null)
    {
        admin ??= Admin;
        var id = await admin.Admin.BeginImportAsync(title.Replace(' ', '_') + ".wav", wav.Length);
        for (var offset = 0; offset < wav.Length; offset += AdminLimits.MaxChunkBytes)
        {
            await admin.Admin.UploadChunkAsync(id, offset, wav[offset..Math.Min(wav.Length, offset + AdminLimits.MaxChunkBytes)]);
        }

        return (await admin.Admin.CompleteImportAsync(id, new ImportMetadata(title, null, null))).TrackId;
    }

    /// <summary>Waits until no analysis is queued or running.</summary>
    public Task WaitForAnalysisAsync(int timeoutMs = 30_000) =>
        AssertEx.Eventually(() => Host.Analysis.PendingCount == 0, "analysis finished", timeoutMs);

    public async Task<string> StoredPathAsync(Guid trackId) =>
        Host.Store.GetPhysicalPath((await App.Tracks.GetFileInfoAsync(trackId))!.FileKey);

    public IReadOnlyList<CapturedLog> UnexpectedErrors(params string[] allowedMessagePrefixes) =>
        Logs.Entries.Where(l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Error
                                && !allowedMessagePrefixes.Any(p => l.Message.StartsWith(p, StringComparison.Ordinal))).ToList();

    public async Task DisposeClientsAsync()
    {
        PipeSabhaClient[] clients;
        lock (_clients)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientsAsync();
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ElevatedIdentity : ICallerIdentity
    {
        public PipeCallerInfo Describe(NamedPipeServerStream pipe) => new("PC\\operator", true);
    }
}

/// <summary>Records every state a client is pushed, for "eventually saw X" assertions.</summary>
public sealed class StateLog
{
    private readonly List<PlaybackStateDto> _states = [];

    public StateLog(PipeSabhaClient client) => client.Events.PlaybackStateReceived += Add;

    public PlaybackStateDto? Last
    {
        get
        {
            lock (_states)
            {
                return _states.Count == 0 ? null : _states[^1];
            }
        }
    }

    public bool Any(Func<PlaybackStateDto, bool> predicate)
    {
        lock (_states)
        {
            return _states.Any(predicate);
        }
    }

    private void Add(PlaybackStateDto state)
    {
        lock (_states)
        {
            _states.Add(state);
        }
    }
}
