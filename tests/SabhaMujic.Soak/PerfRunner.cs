using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using SabhaMujic.Audio;
using SabhaMujic.Audio.Analysis;
using SabhaMujic.Audio.Decoding;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Mixing;
using SabhaMujic.Audio.Output;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Data;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;
using SabhaMujic.Reliability;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Soak;

public sealed class PerfOptions
{
    /// <summary>Real-time playback measured for engine CPU.</summary>
    public double CpuSeconds { get; init; } = 60;

    public int LibraryTracks { get; init; } = 1000;

    public string? WorkDirectory { get; init; }

    /// <summary>Only the large-library start-up (run in a fresh process to include JIT/cold-start costs).</summary>
    public bool StartupOnly { get; init; }

    public Action<string>? Log { get; init; }
}

public sealed record LatencyStats(int Samples, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

public sealed record AnalysisTiming(string Material, double AudioMinutes, double MedianSeconds, double SecondsPerAudioMinute);

public sealed record CpuMeasurement(double WallSeconds, double CpuSecondsIdle, double CpuSecondsPlaying, double IdleCorePercent, double PlayingCorePercent,
    double PlayingMachinePercent, double EngineCorePercentAboveIdle, string Scenario);

public sealed record VoiceMemory(int Voices, int BufferFrames, double ManagedBytesPerVoice, double ExpectedRingBytes);

public sealed record StartupTiming(int Tracks, bool AnalysesReady, double FirstStartSeconds, double WarmStartMedianSeconds, double HeapAfterStartMb,
    double FirstPageMs, double FullLibraryPagingMs, int AnalysisQueuedAtStart);

public sealed record PerfReport
{
    public required DateTimeOffset At { get; init; }

    public required string Machine { get; init; }

    public required LatencyStats GetStateLatency { get; init; }

    public required LatencyStats SetVolumeLatency { get; init; }

    public required LatencyStats ConcurrentGetStateLatency { get; init; }

    public required LatencyStats FanOutToTenClients { get; init; }

    public required LatencyStats FanOutSlowestOfTen { get; init; }

    public required IReadOnlyList<AnalysisTiming> Analysis { get; init; }

    public required IReadOnlyList<CpuMeasurement> Cpu { get; init; }

    public required VoiceMemory Voice { get; init; }

    public required IReadOnlyList<StartupTiming> Startup { get; init; }
}

/// <summary>
/// Performance measurements for the QA report (numbers, not pass/fail): pipe command latency, event fan-out, analysis
/// speed, engine CPU at real time on the null output, memory per voice, and host start-up with a large library.
/// </summary>
public sealed class PerfRunner(PerfOptions options)
{
    private const string AdminUser = "admin";
    private const string AdminSecret = "admin-pin-1";
    private readonly string _root = Path.Combine(options.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "sabhamujic-perf"), Guid.NewGuid().ToString("N"));

    public async Task<PerfReport> RunAsync()
    {
        Directory.CreateDirectory(_root);
        try
        {
            if (options.StartupOnly)
            {
                return new PerfReport
                {
                    At = DateTimeOffset.UtcNow,
                    Machine = $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}",
                    GetStateLatency = Latency([]),
                    SetVolumeLatency = Latency([]),
                    ConcurrentGetStateLatency = Latency([]),
                    FanOutToTenClients = Latency([]),
                    FanOutSlowestOfTen = Latency([]),
                    Analysis = [],
                    Cpu = [],
                    Voice = new VoiceMemory(0, 0, 0, 0),
                    Startup = [await MeasureStartupAsync(analysesReady: true), await MeasureStartupAsync(analysesReady: false)],
                };
            }

            var analysis = MeasureAnalysis();
            var voice = MeasureVoiceMemory();
            var (getState, setVolume, concurrent, fanOut, slowest) = await MeasureLatencyAsync();
            var cpu = await MeasureCpuAsync();
            var startup = new List<StartupTiming>
            {
                await MeasureStartupAsync(analysesReady: true),
                await MeasureStartupAsync(analysesReady: false),
            };
            return new PerfReport
            {
                At = DateTimeOffset.UtcNow,
                Machine = $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}",
                GetStateLatency = getState,
                SetVolumeLatency = setVolume,
                ConcurrentGetStateLatency = concurrent,
                FanOutToTenClients = fanOut,
                FanOutSlowestOfTen = slowest,
                Analysis = analysis,
                Cpu = cpu,
                Voice = voice,
                Startup = startup,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ analysis

    private List<AnalysisTiming> MeasureAnalysis()
    {
        var decoders = new WaveFileDecoderFactory();
        var results = new List<AnalysisTiming>();
        foreach (var (kind, minutes) in new[] { (SoakTrackKind.Loopable, 1.0), (SoakTrackKind.Loopable, 3.0), (SoakTrackKind.Loopable, 5.0), (SoakTrackKind.Through, 3.0) })
        {
            var path = Path.Combine(_root, $"analysis-{kind}-{minutes}.wav");
            File.WriteAllBytes(path, SoakWav.Create(kind, minutes * 60, 7));
            LoopAnalyzer.Analyze(decoders, path); // warm-up (JIT)
            var times = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                LoopAnalyzer.Analyze(decoders, path);
                times.Add(sw.Elapsed.TotalSeconds);
            }

            var median = times.Order().ElementAt(1);
            results.Add(new AnalysisTiming(kind.ToString(), minutes, Stats.Round(median), Stats.Round(median / minutes)));
            Say($"Analysis {kind} {minutes} min: {median:F2} s ({median / minutes:F3} s per audio minute)");
            File.Delete(path);
        }

        return results;
    }

    // ------------------------------------------------------------------ voice memory

    private VoiceMemory MeasureVoiceMemory()
    {
        var path = Path.Combine(_root, "voice.wav");
        File.WriteAllBytes(path, SoakWav.Through(60, 3));
        var decoders = new WaveFileDecoderFactory();
        const int count = 20;
        var bufferFrames = new PlaybackEngineOptions().VoiceBufferFrames;
        var voices = new List<PlaybackVoice>();
        ProcessMetrics.FullCollect();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < count; i++)
        {
            var source = decoders.Open(path);
            var voice = new PlaybackVoice(source, 0, source.TotalFrames, bufferFrames);
            voice.Prefill();
            voices.Add(voice);
        }

        ProcessMetrics.FullCollect();
        var after = GC.GetTotalMemory(true);
        GC.KeepAlive(voices);
        foreach (var voice in voices)
        {
            voice.Dispose();
        }

        var perVoice = (after - before) / (double)count;
        Say($"Memory per voice: {perVoice / 1024:F0} KiB managed (ring {bufferFrames * 2 * 4 / 1024} KiB)");
        return new VoiceMemory(count, bufferFrames, Math.Round(perVoice), bufferFrames * 2 * 4);
    }

    // ------------------------------------------------------------------ latency and fan-out

    private async Task<(LatencyStats, LatencyStats, LatencyStats, LatencyStats, LatencyStats)> MeasureLatencyAsync()
    {
        await using var host = await StartHostAsync(Path.Combine(_root, "latency"), speed: 1, out var pipeName);
        await using var admin = await ConnectAsync(pipeName, null, null);
        await admin.Auth.CompleteInitialSetupAsync(AdminUser, AdminSecret);
        var track = await ImportAsync(admin, SoakWav.Loopable(120, 5), "Latency");
        await admin.Playback.EnqueueAsync([track], false);
        await admin.Playback.SetLoopModeAsync(LoopMode.SmartLoop);
        await admin.Playback.ResumeAsync();

        for (var i = 0; i < 200; i++)
        {
            await admin.Playback.GetStateAsync();
        }

        var getState = new List<double>();
        for (var i = 0; i < 2000; i++)
        {
            var sw = Stopwatch.StartNew();
            await admin.Playback.GetStateAsync();
            getState.Add(sw.Elapsed.TotalMilliseconds);
        }

        var setVolume = new List<double>();
        for (var i = 0; i < 1000; i++)
        {
            var sw = Stopwatch.StartNew();
            await admin.Playback.SetVolumeAsync(i % 2 == 0 ? 0.8f : 0.7f);
            setVolume.Add(sw.Elapsed.TotalMilliseconds);
        }

        Say($"GetState p50 {Stats.Percentile(getState, 50):F3} ms p95 {Stats.Percentile(getState, 95):F3} ms; SetVolume p50 {Stats.Percentile(setVolume, 50):F3} ms p95 {Stats.Percentile(setVolume, 95):F3} ms");

        // Ten listeners (volunteers) on the same service.
        for (var i = 0; i < 10; i++)
        {
            await admin.Admin.CreateUserAsync($"listener{i}", Role.Volunteer, $"listener-pin-{i}");
        }

        var listeners = new List<PipeSabhaClient>();
        var seen = new ConcurrentDictionary<(int Client, float Volume), long>();
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            var client = await ConnectAsync(pipeName, $"listener{i}", $"listener-pin-{i}");
            client.Events.PlaybackStateReceived += s => seen.TryAdd((index, s.Volume), Stopwatch.GetTimestamp());
            listeners.Add(client);
        }

        // Concurrent request latency: the ten clients call GetState in parallel.
        var concurrent = new ConcurrentBag<double>();
        await Task.WhenAll(listeners.Select(async c =>
        {
            for (var i = 0; i < 300; i++)
            {
                var sw = Stopwatch.StartNew();
                await c.Playback.GetStateAsync();
                concurrent.Add(sw.Elapsed.TotalMilliseconds);
            }
        }));

        var deliveries = new List<double>();
        var slowest = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var volume = 0.2f + i * 0.002f;
            var start = Stopwatch.GetTimestamp();
            await admin.Playback.SetVolumeAsync(volume);
            var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            while (Enumerable.Range(0, 10).Any(c => !seen.ContainsKey((c, volume))) && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(1);
            }

            var times = Enumerable.Range(0, 10).Select(c => seen.TryGetValue((c, volume), out var at) ? Stopwatch.GetElapsedTime(start, at).TotalMilliseconds : double.NaN).ToList();
            deliveries.AddRange(times);
            slowest.Add(times.Max());
        }

        foreach (var client in listeners)
        {
            await client.DisposeAsync();
        }

        Say($"Fan-out to 10: p50 {Stats.Percentile(deliveries, 50):F2} ms p95 {Stats.Percentile(deliveries, 95):F2} ms; slowest-of-10 p95 {Stats.Percentile(slowest, 95):F2} ms");
        return (Latency(getState), Latency(setVolume), Latency(concurrent), Latency(deliveries), Latency(slowest));
    }

    // ------------------------------------------------------------------ CPU at 1x

    private async Task<List<CpuMeasurement>> MeasureCpuAsync()
    {
        var results = new List<CpuMeasurement>();
        await using var host = await StartHostAsync(Path.Combine(_root, "cpu"), speed: 1, out var pipeName);
        await using var admin = await ConnectAsync(pipeName, null, null);
        await admin.Auth.CompleteInitialSetupAsync(AdminUser, AdminSecret);
        var loop = await ImportAsync(admin, SoakWav.Loopable(180, 11), "Cpu loop");
        var other = await ImportAsync(admin, SoakWav.Through(40, 12), "Cpu other");
        await WaitAsync(() => host.Analysis.PendingCount == 0, 120);
        await admin.Playback.EnqueueAsync([loop, other], false);

        var playing = new List<(string Scenario, double Wall, double Cpu)>();
        foreach (var (scenario, mode, crossfade) in new[] { ("Smart Loop on a 3 min track", LoopMode.SmartLoop, 6.0), ("Repeat queue, 40 s + 3 min tracks, 12 s crossfades", LoopMode.RepeatQueue, 12.0) })
        {
            await admin.Playback.SetLoopModeAsync(mode);
            await admin.Playback.SetCrossfadeAsync(crossfade);
            var first = (await admin.Playback.GetQueueAsync()).Items[mode == LoopMode.SmartLoop ? 0 : 1].QueueItemId;
            await admin.Playback.PlayQueueItemAsync(first);
            await Task.Delay(1000);
            var cpu0 = ProcessMetrics.CpuTime();
            var sw = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(options.CpuSeconds));
            playing.Add((scenario, sw.Elapsed.TotalSeconds, (ProcessMetrics.CpuTime() - cpu0).TotalSeconds));
            await admin.Playback.StopAsync();
        }

        // Idle baseline, measured last (JIT tiering and start-up work are over): the engine is stopped but the null
        // output keeps pulling silence, as WASAPI would.
        await Task.Delay(5000);
        var idleSeconds = Math.Min(20, options.CpuSeconds);
        var idle0 = ProcessMetrics.CpuTime();
        await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
        var idlePerSecond = (ProcessMetrics.CpuTime() - idle0).TotalSeconds / idleSeconds;
        foreach (var (scenario, wall, cpu) in playing)
        {
            results.Add(new CpuMeasurement(Stats.Round(wall, 1), Stats.Round(idlePerSecond * wall), Stats.Round(cpu),
                Stats.Round(100 * idlePerSecond), Stats.Round(100 * cpu / wall), Stats.Round(100 * cpu / wall / Environment.ProcessorCount),
                Stats.Round(100 * (cpu / wall - idlePerSecond)), scenario));
            Say($"CPU ({scenario}): {100 * cpu / wall:F2} % of one core ({100 * cpu / wall / Environment.ProcessorCount:F2} % of the machine); idle {100 * idlePerSecond:F2} %");
        }

        return results;
    }

    // ------------------------------------------------------------------ startup with a large library

    private async Task<StartupTiming> MeasureStartupAsync(bool analysesReady)
    {
        var root = Path.Combine(_root, analysesReady ? "startup-ready" : "startup-pending");
        var tracks = options.LibraryTracks;
        await SeedLibraryAsync(root, tracks, analysesReady);

        var sw = Stopwatch.StartNew();
        var host = await StartHostAsync(root, speed: 1, out var pipeName);
        var first = sw.Elapsed.TotalSeconds;
        var queued = host.Analysis.PendingCount;
        ProcessMetrics.FullCollect();
        var heap = GC.GetTotalMemory(true) / (1024.0 * 1024.0);

        double firstPage;
        double fullPaging;
        await using (var admin = await ConnectAsync(pipeName, null, null))
        {
            await admin.Auth.CompleteInitialSetupAsync(AdminUser, AdminSecret);
            sw.Restart();
            await admin.Library.GetTracksAsync(new TrackQuery());
            firstPage = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            for (var skip = 0; skip < tracks; skip += 200)
            {
                await admin.Library.GetTracksAsync(new TrackQuery(Skip: skip, Take: 200));
            }

            fullPaging = sw.Elapsed.TotalMilliseconds;
        }

        await host.DisposeAsync();

        var warm = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            sw.Restart();
            var again = await StartHostAsync(root, speed: 1, out _);
            warm.Add(sw.Elapsed.TotalSeconds);
            await again.DisposeAsync();
        }

        var median = warm.Order().ElementAt(1);
        Say($"Startup with {tracks} tracks (analyses {(analysesReady ? "ready" : "pending")}): first {first:F2} s, warm median {median:F2} s, " +
            $"{queued} queued for analysis, first page {firstPage:F1} ms, all pages {fullPaging:F1} ms");
        return new StartupTiming(tracks, analysesReady, Stats.Round(first), Stats.Round(median), Stats.Round(heap, 1), Stats.Round(firstPage, 1),
            Stats.Round(fullPaging, 1), queued);
    }

    /// <summary>Seeds the database directly through the repositories, with small real WAV files in the store.</summary>
    private static async Task SeedLibraryAsync(string root, int count, bool analysesReady)
    {
        var paths = new SabhaDataPaths(root);
        Directory.CreateDirectory(paths.Data);
        var database = new SqliteDatabase(paths.DatabaseFile);
        await database.MigrateAsync();
        var store = new ProtectedStore(paths.Library, NoOpStoreAcl.Instance);
        var tracks = new SqliteTrackRepository(database);
        var analysis = new SqliteTrackAnalysisRepository(database);
        var categories = new SqliteCategoryRepository(database);
        var category = await categories.CreateAsync("Seeded");
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var bytes = SoakWav.SilencePadded(0.3, 50_000 + i);
            StoredFile stored;
            using (var content = new MemoryStream(bytes))
            {
                stored = await store.StoreAsync(content, ".wav");
            }

            var added = await tracks.AddAsync(new NewTrack(stored.ContentHash, stored.FileKey, $"Seeded track {i:0000}", "Seed", category.Id,
                bytes.Length / 96_000.0, stored.SizeBytes, $"seeded-{i}.wav"));
            ids.Add(added.Track.Id);
            if (analysesReady)
            {
                await analysis.UpsertAsync(new SabhaMujic.Data.Repositories.TrackAnalysis(added.Track.Id, AnalysisStatus.Ready, LoopAnalyzer.AnalysisVersion, 0, 0.3, 0, 0.3, 0.05, 0.2));
            }
        }

        var queue = new SqlitePlaybackStateRepository(database);
        await queue.SaveQueueAsync(ids.Take(50).Select(id => new QueueItemDto(Guid.NewGuid(), id)).ToList(), 10);
        database.ClearPool();
    }

    // ------------------------------------------------------------------ helpers

    private Task<SabhaHost> StartHostAsync(string root, double speed, out string pipeName)
    {
        var name = "sm-perf-" + Guid.NewGuid().ToString("N")[..10];
        pipeName = name;
        return SabhaHost.StartAsync(new SabhaHostOptions
        {
            DataRoot = root,
            PipeName = name,
            OutputFactory = () => new NullRealtimeOutput(speed),
            CallerIdentity = new ElevatedIdentity(),
            Server = new ServerOptions { SecretHashIterations = 1_000 },
        });
    }

    private static async Task<PipeSabhaClient> ConnectAsync(string pipeName, string? user, string? secret)
    {
        var client = new PipeSabhaClient(new PipeClientOptions { PipeName = pipeName, ConnectTimeout = TimeSpan.FromSeconds(5) });
        await client.ConnectAsync();
        if (user is not null)
        {
            await client.Auth.LoginAsync(user, secret!);
        }

        return client;
    }

    private static async Task<Guid> ImportAsync(PipeSabhaClient admin, byte[] bytes, string title)
    {
        var id = await admin.Admin.BeginImportAsync(title.Replace(' ', '_') + ".wav", bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
        {
            await admin.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)]);
        }

        return (await admin.Admin.CompleteImportAsync(id, new ImportMetadata(title, null, null))).TrackId;
    }

    private static async Task WaitAsync(Func<bool> condition, double seconds)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(50);
        }
    }

    private static LatencyStats Latency(IEnumerable<double> values)
    {
        var list = values.ToList();
        return new LatencyStats(list.Count, Stats.Round(Stats.Percentile(list, 50)), Stats.Round(Stats.Percentile(list, 95)),
            Stats.Round(Stats.Percentile(list, 99)), Stats.Round(list.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max()));
    }

    private void Say(string message) => options.Log?.Invoke(message);

    public static string Markdown(PerfReport r)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# SabhaMujic performance measurements").AppendLine();
        sb.AppendLine(c, $"{r.At:u}, {r.Machine}").AppendLine();
        sb.AppendLine("| Measurement | Samples | p50 ms | p95 ms | p99 ms | max ms |");
        sb.AppendLine("|---|---|---|---|---|---|");
        void Row(string name, LatencyStats s) => sb.AppendLine(c, $"| {name} | {s.Samples} | {s.P50Ms:F3} | {s.P95Ms:F3} | {s.P99Ms:F3} | {s.MaxMs:F2} |");
        Row("Pipe GetState (1 client)", r.GetStateLatency);
        Row("Pipe SetVolume (1 client)", r.SetVolumeLatency);
        Row("Pipe GetState (10 clients in parallel)", r.ConcurrentGetStateLatency);
        Row("Event fan-out, per delivery (10 clients)", r.FanOutToTenClients);
        Row("Event fan-out, slowest of 10", r.FanOutSlowestOfTen);
        sb.AppendLine().AppendLine("| Analysis material | Audio min | Median s | s per audio minute |").AppendLine("|---|---|---|---|");
        foreach (var a in r.Analysis)
        {
            sb.AppendLine(c, $"| {a.Material} | {a.AudioMinutes} | {a.MedianSeconds:F2} | {a.SecondsPerAudioMinute:F3} |");
        }

        sb.AppendLine().AppendLine("| CPU scenario (1x real time, null output) | Wall s | % of one core | % of machine | idle % of one core | above idle |").AppendLine("|---|---|---|---|---|---|");
        foreach (var x in r.Cpu)
        {
            sb.AppendLine(c, $"| {x.Scenario} | {x.WallSeconds:F0} | {x.PlayingCorePercent:F2} | {x.PlayingMachinePercent:F2} | {x.IdleCorePercent:F2} | {x.EngineCorePercentAboveIdle:F2} |");
        }

        sb.AppendLine().AppendLine(c, $"Memory per voice: {r.Voice.ManagedBytesPerVoice / 1024:F0} KiB managed ({r.Voice.Voices} voices measured; ring buffer alone {r.Voice.ExpectedRingBytes / 1024:F0} KiB).");
        sb.AppendLine().AppendLine("| Startup | Tracks | Analyses | First start s | Warm start median s | Heap after start MB | First page ms | All pages ms | Queued for analysis |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var s in r.Startup)
        {
            sb.AppendLine(c, $"| SabhaHost.StartAsync | {s.Tracks} | {(s.AnalysesReady ? "ready" : "pending")} | {s.FirstStartSeconds:F2} | {s.WarmStartMedianSeconds:F2} | {s.HeapAfterStartMb:F1} | {s.FirstPageMs:F1} | {s.FullLibraryPagingMs:F1} | {s.AnalysisQueuedAtStart} |");
        }

        return sb.ToString();
    }

    private sealed class ElevatedIdentity : ICallerIdentity
    {
        public PipeCallerInfo Describe(NamedPipeServerStream pipe) => new("PERF\\operator", true);
    }
}
