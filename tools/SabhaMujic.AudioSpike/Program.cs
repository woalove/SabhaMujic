using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using SabhaMujic.Audio;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Windows;
using SabhaMujic.Contracts;

// Manual verification tool: plays files through the real PlaybackEngine on the default WASAPI device.
//   AudioSpike <file1> <file2> [more files] [--crossfade 6] [--smartloop] [--repeat] [--no-analysis] [--latency 100]

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("AudioSpike needs Windows (WASAPI + Media Foundation).");
    return 2;
}

var files = new List<string>();
double crossfade = 6;
bool smartLoop = false;
bool repeat = false;
bool analyze = true;
int latency = 100;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--crossfade" when i + 1 < args.Length:
            crossfade = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--latency" when i + 1 < args.Length:
            latency = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--smartloop":
            smartLoop = true;
            break;
        case "--repeat":
            repeat = true;
            break;
        case "--no-analysis":
            analyze = false;
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            files.Add(Path.GetFullPath(args[i]));
            break;
    }
}

if (files.Count == 0)
{
    PrintUsage();
    return 1;
}

var decoders = new MediaFoundationDecoderFactory();
var resolver = new SpikeResolver();
var names = new Dictionary<Guid, string>();
foreach (var file in files)
{
    TrackAnalysis? analysis = null;
    if (analyze)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            analysis = LoopAnalyzer.Analyze(decoders, file);
            Console.WriteLine($"{Path.GetFileName(file)}: analysed in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  duration {Seconds(analysis.TotalFrames)}, audible {Seconds(analysis.LeadingTrimFrame)} - {Seconds(analysis.TrailingTrimFrame)}, " +
                              $"{analysis.Gaps.Count} gap(s), tempo {analysis.TempoBpm:F1} BPM");
            var loop = analysis.Loop;
            Console.WriteLine($"  loop {Seconds(loop.StartFrame)} - {Seconds(loop.EndFrame)} (length {Seconds(loop.LengthFrames)}), " +
                              $"crossfade {loop.CrossfadeFrames * 1000.0 / AudioFormat.SampleRate:F0} ms, confidence {loop.Confidence:F2}" +
                              (loop.IsFallback ? " [fallback: whole track]" : string.Empty));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Path.GetFileName(file)}: analysis failed: {ex.Message}");
        }
    }

    var id = resolver.Add(file, analysis);
    names[id] = Path.GetFileName(file);
}

using var output = new WasapiOutput(latency);
output.DeviceLost += (_, e) => Console.WriteLine($"[device] LOST: {e.Message}");
output.DeviceRestored += (_, e) => Console.WriteLine($"[device] restored: {e.Message}");
using var engine = new PlaybackEngine(output, decoders, resolver, new PlaybackEngineOptions { DefaultCrossfadeSeconds = crossfade });
Console.WriteLine($"Output device: {output.DeviceName ?? "(none)"}");

PlaybackStatus lastStatus = PlaybackStatus.Idle;
Guid? lastTrack = null;
var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
engine.StateChanged += (_, s) =>
{
    if (s.Status != lastStatus || s.CurrentTrackId != lastTrack)
    {
        string name = s.CurrentTrackId is { } t && names.TryGetValue(t, out var n) ? n : "-";
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s.Status,-11} {name} at {s.PositionSeconds:F2}/{s.DurationSeconds:F2} s (rev {s.Revision})");
        lastStatus = s.Status;
        lastTrack = s.CurrentTrackId;
    }

    if (s.ErrorMessage is not null)
    {
        Console.WriteLine($"  error: {s.ErrorMessage}");
    }

    if (s.Status is PlaybackStatus.Stopped or PlaybackStatus.Error)
    {
        done.TrySetResult();
    }
};
engine.PlaybackError += (_, e) => Console.WriteLine($"[error] {e.Message} ({e.Exception?.GetType().Name})");

await engine.SetLoopModeAsync(smartLoop ? LoopMode.SmartLoop : repeat ? LoopMode.RepeatQueue : LoopMode.Off);
var queue = await engine.EnqueueAsync(resolver.Ids, playNext: false);
await engine.PlayQueueItemAsync(queue.Items[0].QueueItemId);

Console.WriteLine("Keys: Space pause/resume, N next, P previous, L smart loop, R repeat queue, Left/Right seek 10 s, +/- volume, M mute, S stop, Q quit");
bool interactive = !Console.IsInputRedirected;
float volume = 1f;
bool muted = false;
while (!done.Task.IsCompleted || interactive)
{
    if (!interactive)
    {
        await Task.WhenAny(done.Task, Task.Delay(250));
        continue;
    }

    if (!Console.KeyAvailable)
    {
        await Task.Delay(50);
        continue;
    }

    var key = Console.ReadKey(intercept: true).Key;
    var state = engine.GetState();
    switch (key)
    {
        case ConsoleKey.Spacebar:
            await (state.Status == PlaybackStatus.Paused || state.Status == PlaybackStatus.Stopped ? engine.ResumeAsync() : engine.PauseAsync());
            break;
        case ConsoleKey.N:
            await engine.NextAsync();
            break;
        case ConsoleKey.P:
            await engine.PreviousAsync();
            break;
        case ConsoleKey.L:
            await engine.SetLoopModeAsync(state.LoopMode == LoopMode.SmartLoop ? LoopMode.Off : LoopMode.SmartLoop);
            Console.WriteLine($"Loop mode: {engine.GetState().LoopMode}");
            break;
        case ConsoleKey.R:
            await engine.SetLoopModeAsync(state.LoopMode == LoopMode.RepeatQueue ? LoopMode.Off : LoopMode.RepeatQueue);
            Console.WriteLine($"Loop mode: {engine.GetState().LoopMode}");
            break;
        case ConsoleKey.LeftArrow:
            await engine.SeekAsync(Math.Max(0, state.PositionSeconds - 10));
            break;
        case ConsoleKey.RightArrow:
            await engine.SeekAsync(state.PositionSeconds + 10);
            break;
        case ConsoleKey.OemPlus or ConsoleKey.Add:
            volume = Math.Min(1f, volume + 0.1f);
            await engine.SetVolumeAsync(volume);
            Console.WriteLine($"Volume {volume:F1}");
            break;
        case ConsoleKey.OemMinus or ConsoleKey.Subtract:
            volume = Math.Max(0f, volume - 0.1f);
            await engine.SetVolumeAsync(volume);
            Console.WriteLine($"Volume {volume:F1}");
            break;
        case ConsoleKey.M:
            muted = !muted;
            await engine.SetMutedAsync(muted);
            Console.WriteLine(muted ? "Muted" : "Unmuted");
            break;
        case ConsoleKey.S:
            await engine.StopAsync();
            break;
        case ConsoleKey.Q:
            await engine.StopAsync();
            return 0;
        case ConsoleKey.I:
            Console.WriteLine($"{state.Status} at {state.PositionSeconds:F2} s, next {state.NextTrackId}, xfade {state.CrossfadeSeconds} s, loop {state.LoopMode}");
            break;
    }
}

return 0;

static string Seconds(long frames) => (frames / (double)AudioFormat.SampleRate).ToString("F3", CultureInfo.InvariantCulture) + " s";

static void PrintUsage() =>
    Console.WriteLine("AudioSpike <file1> <file2> [more files] [--crossfade 6] [--smartloop] [--repeat] [--no-analysis] [--latency 100]");

internal sealed class SpikeResolver : ITrackResolver
{
    private readonly ConcurrentDictionary<Guid, ResolvedTrack> _tracks = new();
    private readonly List<Guid> _order = [];

    public IReadOnlyList<Guid> Ids => _order;

    public Guid Add(string path, TrackAnalysis? analysis)
    {
        var id = Guid.NewGuid();
        _tracks[id] = new ResolvedTrack(id, path, analysis);
        _order.Add(id);
        return id;
    }

    public Task<ResolvedTrack?> ResolveAsync(Guid trackId, CancellationToken ct = default) =>
        Task.FromResult(_tracks.TryGetValue(trackId, out var track) ? track : null);
}
