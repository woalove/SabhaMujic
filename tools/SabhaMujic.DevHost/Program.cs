using System.Globalization;
using Microsoft.Extensions.Logging;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Output;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Hosting;

// Development host (Linux: dotnet run -f net10.0 --project tools/SabhaMujic.DevHost -- --data ./devdata).
// The real service (engine, database, protected store, pipe) with a silent real-time output.
//
//   SabhaMujic.DevHost --data <dir> [--pipe <name>] [--seed-admin <name:pin>] [--speed <factor>] [--verbose]
//
// --seed-admin creates the first administrator on an empty database (dev only: on a real install the first admin is
// created from the desktop app started with "Run as administrator").
// Stop with Ctrl+C (graceful: saves the queue and settings).

string dataRoot = Path.Combine(Environment.CurrentDirectory, "devdata");
string pipeName = Endpoints.PipeName;
string? seedAdmin = null;
double speed = 1.0;
bool verbose = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length:
            dataRoot = Path.GetFullPath(args[++i]);
            break;
        case "--pipe" when i + 1 < args.Length:
            pipeName = args[++i];
            break;
        case "--seed-admin" when i + 1 < args.Length:
            seedAdmin = args[++i];
            break;
        case "--speed" when i + 1 < args.Length:
            speed = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--verbose":
            verbose = true;
            break;
        case "-h" or "--help":
            Console.WriteLine("SabhaMujic.DevHost --data <dir> [--pipe <name>] [--seed-admin <name:pin>] [--speed <factor>] [--verbose]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Use --help.");
            return 2;
    }
}

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));
var logger = loggerFactory.CreateLogger("DevHost");

IAudioDecoderFactory decoders = new WaveFileDecoderFactory();
#if WINDOWS
decoders = new SabhaMujic.Audio.Windows.MediaFoundationDecoderFactory();
#endif

var host = await SabhaHost.StartAsync(new SabhaHostOptions
{
    DataRoot = dataRoot,
    PipeName = pipeName,
    Decoders = decoders,
    OutputFactory = () => new NullRealtimeOutput(speed),
    LoggerFactory = loggerFactory,
});

if (seedAdmin is not null)
{
    var parts = seedAdmin.Split(':', 2);
    if (parts.Length != 2)
    {
        Console.Error.WriteLine("--seed-admin expects name:pin");
        await host.StopAsync();
        return 2;
    }

    Console.WriteLine("WARNING: --seed-admin is for development only. Real installs create the first administrator from the desktop app.");
    var created = await host.App.Auth.EnsureInitialAdminAsync(parts[0], parts[1]);
    logger.LogInformation(created ? "Seeded administrator '{Name}'." : "Accounts already exist; '{Name}' was not created.", parts[0]);
}

Console.WriteLine($"SabhaMujic dev host running. Data: {dataRoot}  Pipe: {pipeName}  Output: silent x{speed.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
// Ctrl+C / SIGINT and SIGTERM (e.g. a test harness stopping the process) both shut down gracefully.
using var onInterrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, Graceful);
using var onTerminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, Graceful);

void Graceful(System.Runtime.InteropServices.PosixSignalContext context)
{
    context.Cancel = true;
    stop.TrySetResult();
}

await stop.Task;
await host.StopAsync();
await host.DisposeAsync();
return 0;
