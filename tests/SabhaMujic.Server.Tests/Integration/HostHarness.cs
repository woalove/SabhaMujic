using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Output;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Hosting;

namespace SabhaMujic.Server.Tests.Integration;

/// <summary>
/// A real <see cref="SabhaHost"/> (real engine, WAV decoder, accelerated <see cref="NullRealtimeOutput"/>) on a temp data
/// root and a unique pipe, with every pipe client reported as an elevated administrator (for first-run setup).
/// </summary>
public sealed class HostHarness : IAsyncDisposable
{
    private readonly List<PipeSabhaClient> _clients = [];

    private HostHarness(double speed)
    {
        Speed = speed;
        Root = Path.Combine(Path.GetTempPath(), "sabhamujic-host-tests", Guid.NewGuid().ToString("N"));
        PipeName = "sm-" + Guid.NewGuid().ToString("N")[..12];
    }

    public string Root { get; }

    public string PipeName { get; }

    /// <summary>Real-time multiplier of the null output (10 = ten seconds of audio per second).</summary>
    public double Speed { get; }

    public SabhaHost Host { get; private set; } = null!;

    public ServerApplication App => Host.App;

    public static async Task<HostHarness> StartAsync(double speed = 10)
    {
        var harness = new HostHarness(speed);
        await harness.StartHostAsync();
        return harness;
    }

    public SabhaHostOptions Options() => new()
    {
        DataRoot = Root,
        PipeName = PipeName,
        OutputFactory = () => new NullRealtimeOutput(Speed),
        CallerIdentity = new FixedCallerIdentity("PC\\owner", elevated: true),
        Server = new ServerOptions { SecretHashIterations = 1_000 },
        Engine = new PlaybackEngineOptions(),
        StateHeartbeat = TimeSpan.FromMilliseconds(300),
        PersistDebounce = TimeSpan.FromMilliseconds(100),
    };

    public async Task StartHostAsync() => Host = await SabhaHost.StartAsync(Options());

    /// <summary>Stops the host (graceful) and starts a new one on the same data root and pipe name.</summary>
    public async Task RestartAsync()
    {
        await DisposeClientsAsync();
        await Host.StopAsync();
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
            await client.Auth.LoginAsync(user, secret!);
        }

        return client;
    }

    public static async Task<ImportResultDto> ImportAsync(PipeSabhaClient admin, string fileName, byte[] bytes, string title, Guid? categoryId = null)
    {
        var id = await admin.Admin.BeginImportAsync(fileName, bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
        {
            await admin.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)]);
        }

        return await admin.Admin.CompleteImportAsync(id, new ImportMetadata(title, null, categoryId));
    }

    public int StoredFileCount() =>
        Directory.EnumerateFiles(Host.Store.RootDirectory, "*", SearchOption.AllDirectories).Count(f => !f.Contains(".staging"));

    public async Task DisposeClientsAsync()
    {
        PipeSabhaClient[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
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
    }
}
