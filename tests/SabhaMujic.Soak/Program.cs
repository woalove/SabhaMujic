using System.Globalization;
using SabhaMujic.Reliability;
using SabhaMujic.Soak;

// SabhaMujic soak and performance harness.
//
//   dotnet run -c Release --project tests/SabhaMujic.Soak -- soak --hours 8 --speed 50 --seed 1 --report artifacts/soak.json
//   dotnet run -c Release --project tests/SabhaMujic.Soak -- perf --report artifacts/perf.json [--cpu-seconds 60] [--tracks 1000] [--startup-only]
//
// soak options: --hours <simulated hours> --speed <null-output speed factor> --seed <n> --report <json path>
//               [--tracks 30] [--min-track 20] [--max-track 240] [--clients 4] [--phones 2] [--restart-at 0.5]
//               [--action-interval 20] [--max-wall-minutes 30] [--work-dir <dir>] [--quiet]
// Exit code: 0 = no invariant violations and no unexpected log entries; 1 = violations or unexpected logs; 2 = usage error.

var arguments = args.ToList();
var command = arguments.Count > 0 && !arguments[0].StartsWith("--", StringComparison.Ordinal) ? arguments[0] : "soak";
if (arguments.Count > 0 && arguments[0] == command)
{
    arguments.RemoveAt(0);
}

Dictionary<string, string> options;
try
{
    options = Parse(arguments);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var quiet = options.ContainsKey("quiet");
void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

switch (command)
{
    case "soak":
    {
        var soak = new SoakOptions
        {
            SimulatedHours = Number(options, "hours", 8),
            Speed = Number(options, "speed", 50),
            Seed = (int)Number(options, "seed", 1),
            ReportPath = options.GetValueOrDefault("report"),
            TrackCount = (int)Number(options, "tracks", 30),
            MinTrackSeconds = Number(options, "min-track", 20),
            MaxTrackSeconds = Number(options, "max-track", 240),
            PipeClients = (int)Number(options, "clients", 4),
            RemoteClients = (int)Number(options, "phones", 2),
            RestartAtFraction = Number(options, "restart-at", 0.5),
            MeanActionIntervalSimSeconds = Number(options, "action-interval", 20),
            MaxWallTime = TimeSpan.FromMinutes(Number(options, "max-wall-minutes", 30)),
            WorkDirectory = options.GetValueOrDefault("work-dir"),
            Log = quiet ? null : Log,
        };
        Log($"Soak: {soak.SimulatedHours} simulated hours at {soak.Speed}x (seed {soak.Seed}), report {soak.ReportPath ?? "(none)"}.");
        await using var runner = new SoakRunner(soak);
        var report = await runner.RunAsync();
        Console.WriteLine();
        Console.WriteLine(SoakReportWriter.Markdown(report).Split("## Samples")[0]);
        return report.Violations.Count == 0 && report.UnexpectedLogCount == 0 ? 0 : 1;
    }

    case "perf":
    {
        var perf = new PerfRunner(new PerfOptions
        {
            CpuSeconds = Number(options, "cpu-seconds", 60),
            LibraryTracks = (int)Number(options, "tracks", 1000),
            WorkDirectory = options.GetValueOrDefault("work-dir"),
            StartupOnly = options.ContainsKey("startup-only"),
            Log = Log,
        });
        var result = await perf.RunAsync();
        var json = SoakReportWriter.ToJson(result);
        if (options.GetValueOrDefault("report") is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, json);
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".md"), PerfRunner.Markdown(result));
        }

        Console.WriteLine(PerfRunner.Markdown(result));
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'soak' or 'perf'.");
        return 2;
}

static Dictionary<string, string> Parse(List<string> arguments)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < arguments.Count; i++)
    {
        var arg = arguments[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{arg}'.");
        }

        var name = arg[2..];
        if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[name] = arguments[++i];
        }
        else
        {
            result[name] = "true";
        }
    }

    return result;
}

static double Number(Dictionary<string, string> options, string name, double fallback)
{
    if (!options.TryGetValue(name, out var text))
    {
        return fallback;
    }

    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new ArgumentException($"--{name} needs a number, not '{text}'.");
}
