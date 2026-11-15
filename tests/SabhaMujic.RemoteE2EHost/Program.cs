// E2E host for the SabhaMujic web remote. Starts the real application layer and RemoteHost (HTTPS + bootstrap) on
// loopback with a temp database/store and a scripted playback backend, then speaks a line protocol:
//   stdout (JSON lines): {"event":"ready",…}, {"event":"code",…}, {"event":"restarted"}, {"event":"state",…}
//   stdin:  "code Admin" | "code Volunteer" | "restart" | "state" | "quit"
// Usage: dotnet SabhaMujic.RemoteE2EHost.dll [--https-port 0] [--bootstrap-port 0]
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Data;
using SabhaMujic.Data.Storage;
using SabhaMujic.RemoteE2EHost;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Remote;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Http;
using SabhaMujic.Server.Remote.Network;

var httpsPort = IntArg(args, "--https-port", 0);
var bootstrapPort = IntArg(args, "--bootstrap-port", 0);
var root = Path.Combine(Path.GetTempPath(), "sabhamujic-e2e", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var json = RemoteJson.Options;
var stdout = TextWriter.Synchronized(Console.Out);
void Emit(object value) => stdout.WriteLine(JsonSerializer.Serialize(value, json));

using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new StderrLoggerProvider()).SetMinimumLevel(LogLevel.Warning));
var database = new SqliteDatabase(Path.Combine(root, "db", "e2e.db"));
await database.MigrateAsync();
var durations = new ConcurrentDictionary<Guid, double>();
using var backend = new ScriptedPlaybackBackend(id => durations.TryGetValue(id, out var d) ? d : 180);
var network = new SystemNetworkAddressProvider();
var urls = new RemoteUrlProvider(network, bootstrapPort, hostOverride: "127.0.0.1");
using var app = new ServerApplication(new ServerDependencies
{
    Database = database,
    Store = new ProtectedStore(Path.Combine(root, "Library"), NoOpStoreAcl.Instance),
    Uploads = new UploadStaging(Path.Combine(root, "Uploads"), NoOpStoreAcl.Instance),
    Playback = backend,
    AudioProbe = new WavProbe(),
    AnalysisQueue = new NoAnalysis(),
    RemoteUrls = urls,
    Options = new ServerOptions { SecretHashIterations = 1_000 },
    LoggerFactory = loggerFactory,
});

// Track durations for uploaded files come from the library (the scripted backend only needs a number).
app.Events.Subscribe(new DurationSink(app, durations), new SabhaMujic.Core.Security.CallerContext(ClientKind.Desktop, Role.Admin, null, null, "e2e", "e2e"));

await app.Auth.EnsureInitialAdminAsync("admin", "admin-pass");
await Seed(app, durations);

var certDir = Path.Combine(root, "certs");
using var ca = LocalCertificateAuthority.LoadOrCreate(certDir, UnprotectedSecretProtector.Instance, "E2E-HOST");
using var certificates = new ServerCertificateManager(ca, network, UnprotectedSecretProtector.Instance, certDir);
var remote = new RemoteHost(app, certificates, new RemoteHostOptions
{
    HttpsPort = httpsPort,
    BootstrapPort = bootstrapPort,
    BindAddress = IPAddress.Loopback,
}, urls, network);
await remote.StartAsync();

var admin = app.CreateSession(ClientKind.Desktop, "e2e-host");
await admin.LoginAsync("admin", "admin-pass");

Emit(new { @event = "ready", httpsPort = remote.HttpsPort, bootstrapPort = remote.BootstrapPort, fingerprint = ca.Sha256Fingerprint });

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
    {
        continue;
    }

    try
    {
        switch (parts[0])
        {
            case "code":
                var role = parts.Length > 1 && Enum.TryParse<Role>(parts[1], true, out var r) ? r : Role.Volunteer;
                var pairing = await admin.CreatePairingCodeAsync(role);
                Emit(new { @event = "code", role, pairing.Code, pairing.PairingUrl, pairing.BootstrapUrl });
                break;
            case "restart":
                await remote.StopAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
                await remote.StartAsync();
                Emit(new { @event = "restarted" });
                break;
            case "state":
                var state = backend.GetState();
                Emit(new { @event = "state", state.Status, state.CurrentTrackId, queueCount = backend.GetQueue().Items.Count, connections = remote.ConnectionCount });
                break;
            case "quit":
                goto done;
            default:
                Emit(new { @event = "error", message = "unknown command " + parts[0] });
                break;
        }
    }
    catch (Exception ex)
    {
        Emit(new { @event = "error", message = ex.Message });
    }
}

done:
await remote.DisposeAsync();
database.ClearPool();
try
{
    Directory.Delete(root, recursive: true);
}
catch (IOException)
{
}

return 0;

static int IntArg(string[] args, string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var value) ? value : fallback;
}

static async Task Seed(ServerApplication app, ConcurrentDictionary<Guid, double> durations)
{
    var bhajan = await app.Categories.CreateAsync("Bhajan");
    var kirtan = await app.Categories.CreateAsync("Kirtan");
    var dhun = await app.Categories.CreateAsync("Dhun");
    var catalog = new (string Title, string Artist, Guid Category, double Seconds)[]
    {
        ("Morning Aarti", "Sabha Choir", bhajan.Id, 240),
        ("Shanti Path", "Sabha Choir", bhajan.Id, 200),
        ("Evening Prayer", "Youth Group", bhajan.Id, 300),
        ("Jay Jay Kirtan", "Kirtan Mandal", kirtan.Id, 260),
        ("Prem Kirtan", "Kirtan Mandal", kirtan.Id, 280),
        ("Swagat Geet", "Youth Group", kirtan.Id, 150),
        ("Mahamantra Dhun", "Sabha Choir", dhun.Id, 600),
        ("Akhand Dhun", "Sabha Choir", dhun.Id, 900),
    };
    var ids = new List<Guid>();
    foreach (var (title, artist, category, seconds) in catalog)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title))).ToLowerInvariant();
        var added = await app.Tracks.AddAsync(new SabhaMujic.Data.Repositories.NewTrack(hash, hash + ".mp3", title, artist, category, seconds, 1_000_000,
            title + ".mp3"));
        durations[added.Track.Id] = seconds;
        ids.Add(added.Track.Id);
    }

    await app.Playlists.CreateAsync("Sunday Sabha", [ids[0], ids[3], ids[6]]);
    await app.Playlists.CreateAsync("Quiet evening", [ids[1], ids[2]]);
}

/// <summary>Decodes only RIFF/WAVE headers (enough for e2e uploads): duration = data bytes / byte rate.</summary>
internal sealed class WavProbe : IAudioProbe
{
    public async Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        var header = new byte[4096];
        int read;
        await using (var file = File.OpenRead(path))
        {
            read = await file.ReadAsync(header, ct);
        }

        if (read < 12 || header.AsSpan(0, 4).SequenceEqual("RIFF"u8) is false || header.AsSpan(8, 4).SequenceEqual("WAVE"u8) is false)
        {
            return AudioProbeResult.Unsupported("not a WAV file");
        }

        uint byteRate = 0;
        for (var at = 12; at + 8 <= read;)
        {
            var id = header.AsSpan(at, 4);
            var size = BitConverter.ToInt32(header, at + 4);
            if (id.SequenceEqual("fmt "u8) && at + 16 <= read)
            {
                byteRate = BitConverter.ToUInt32(header, at + 8 + 8);
            }
            else if (id.SequenceEqual("data"u8))
            {
                return byteRate == 0 ? AudioProbeResult.Unsupported("no fmt chunk") : AudioProbeResult.Supported((double)(uint)size / byteRate);
            }

            at += 8 + size + (size & 1);
        }

        return AudioProbeResult.Unsupported("no data chunk");
    }
}

internal sealed class NoAnalysis : IAnalysisQueue
{
    public int AnalysisVersion => 1;

    public void Enqueue(Guid trackId)
    {
    }
}

/// <summary>Learns durations of newly imported tracks so the scripted backend can simulate them.</summary>
internal sealed class DurationSink(ServerApplication app, ConcurrentDictionary<Guid, double> durations) : IEventSink
{
    public async ValueTask DeliverAsync(ServerEvent serverEvent, CancellationToken ct)
    {
        if (serverEvent is LibraryEvent { Change: { Kind: LibraryChangeKind.TrackAdded, EntityId: { } id } } &&
            await app.Tracks.GetAsync(id, ct) is { } track)
        {
            durations[id] = Math.Max(track.DurationSeconds, 1);
        }
    }
}

internal sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class StderrLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Console.Error.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception.GetType().Name + ": " + exception.Message)}");
            }
        }
    }
}
