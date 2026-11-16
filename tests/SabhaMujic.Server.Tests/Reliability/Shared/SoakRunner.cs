using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SabhaMujic.Audio;
using SabhaMujic.Audio.Engine;
using SabhaMujic.Audio.Mixing;
using SabhaMujic.Audio.Output;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Server.Pipe;

namespace SabhaMujic.Reliability;

/// <summary>
/// Accelerated soak of the whole service: a real <see cref="SabhaHost"/> (real engine, WAV decoder, <see cref="NullRealtimeOutput"/>
/// at <see cref="SoakOptions.Speed"/>×, SQLite, protected store, pipe server and optionally the HTTPS/SignalR remote) driven by
/// a seeded random workload through real <see cref="PipeSabhaClient"/>s and hub connections.
/// <para>
/// Simulated time is the null output's rendered frames, so it is exact even when the machine is too loaded to keep up.
/// Every simulated minute the process is sampled (heap after a full GC, working set, threads, handles, engine state, event
/// lag, estimated render underruns). Invariants are checked continuously by a 20 ms monitor and by periodic client
/// convergence checks; violations are collected rather than thrown, and the report says what broke and when.
/// </para>
/// </summary>
public sealed class SoakRunner : IAsyncDisposable
{
    public const string AdminUser = "admin";
    public const string AdminSecret = "admin-pin-1";
    private const double MonitorIntervalMs = 20;

    private readonly SoakOptions _o;
    private readonly Random _random;
    private readonly CapturingLoggerFactory _logs;
    private readonly string _root;
    private readonly bool _ownsRoot;
    private readonly string _pipeName;
    private readonly Stopwatch _wall = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<SoakViolation> _violations = new();
    private readonly ConcurrentDictionary<string, int> _events = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _actions = new();
    private readonly ConcurrentQueue<double> _windowLag = new();
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly List<double> _allLag = [];
    private readonly Lock _lagGate = new();
    private readonly List<SoakSample> _samples = [];
    private readonly List<RestartResult> _restarts = [];
    private readonly List<LibraryTrack> _library = [];
    private readonly HashSet<Guid> _badTracks = [];
    private readonly HashSet<Guid> _deleted = [];
    private readonly List<Guid> _playlists = [];
    private readonly List<PipeSoakClient> _volunteers = [];
    private readonly List<RemoteSoakClient> _phones = [];
    private readonly List<string> _deviceTokens = [];
    private readonly SoakRemoteComponent? _remote;
    private readonly CancellationTokenSource _stop = new();

    private volatile SabhaHost? _host;
    private volatile NullRealtimeOutput? _output;
    private volatile bool _restarting;
    private double _simBase;
    private PipeSoakClient? _admin;
    private int _volunteerCounter;
    private int _trackSeed = 10_000;
    private long _commandEpoch;
    private volatile int _intent = (int)Intent.Stopped;
    private double? _resumeAtSim;
    private bool _unmuteDue;
    private volatile bool _needsPlaybackRepair;
    private double _lastZeroClientWindowSim = double.NegativeInfinity;
    private int _actionsSinceSample;
    private long _underrunFrames;
    private long _measuredFrames;
    private int _stateTransitions;
    private long _playingMonitorTicks;
    private long _monitorTicks;
    private int _convergenceChecks;
    private int _convergenceFailures;
    private long _lastStateRevision;
    private long _lastQueueRevision;
    private double _setupSeconds;
    private int _libraryAtStart;
    private int _readyAtStart;
    private bool _endedEarly;
    private Task _convergence = Task.CompletedTask;
    private long _lastPlayingEpoch = -1;

    public SoakRunner(SoakOptions options)
    {
        _o = options;
        _random = new Random(options.Seed);
        _logs = new CapturingLoggerFactory();
        _ownsRoot = options.WorkDirectory is null;
        _root = Path.Combine(options.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "sabhamujic-soak"), Guid.NewGuid().ToString("N"));
        _pipeName = "sm-soak-" + Guid.NewGuid().ToString("N")[..10];
        if (options.RemoteClients > 0)
        {
            _remote = new SoakRemoteComponent(Path.Combine(_root, "certs"));
        }
    }

    private enum Intent
    {
        Playing,
        Paused,
        Stopped,
    }

    /// <summary>Simulated seconds rendered so far (across restarts).</summary>
    public double SimSeconds => _simBase + (_output?.FramesRendered ?? 0) / (double)AudioFormat.SampleRate;

    public CapturingLoggerFactory Logs => _logs;

    public SabhaHost? Host => _host;

    private SabhaHost H => _host ?? throw new InvalidOperationException("The host is not running.");

    private double WallSeconds => _wall.Elapsed.TotalSeconds;

    // ================================================================== run

    public async Task<SoakReport> RunAsync()
    {
        var startedAt = DateTimeOffset.UtcNow;
        _wall.Start();
        Directory.CreateDirectory(_root);
        await StartHostAsync().ConfigureAwait(false);
        await SetupAsync().ConfigureAwait(false);
        _setupSeconds = WallSeconds;
        Say($"Setup done in {_setupSeconds:F1} s: {_libraryAtStart} tracks ({_readyAtStart} analysed), {_volunteers.Count} volunteer clients, {_phones.Count} phones.");

        // The clock of the run starts now.
        _simBase = -SimSeconds;
        var monitor = Task.Run(MonitorLoopAsync);
        var sampler = Task.Run(SamplerLoopAsync);
        try
        {
            await DriveAsync().ConfigureAwait(false);
            await CheckConvergenceAsync("final").ConfigureAwait(false);
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(monitor, sampler, _convergence).ConfigureAwait(false);
        }

        TakeSample();
        var report = BuildReport(startedAt);
        await ShutdownAsync().ConfigureAwait(false);
        if (_o.ReportPath is { } path)
        {
            SoakReportWriter.Write(report, path);
        }

        return report;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _stop.Dispose();
        if (_ownsRoot)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task ShutdownAsync()
    {
        await DisposeClientsAsync(includeAdmin: true).ConfigureAwait(false);
        if (_host is { } host)
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ================================================================== host

    private SabhaHostOptions HostOptions() => new()
    {
        DataRoot = Path.Combine(_root, "data"),
        PipeName = _pipeName,
        OutputFactory = () =>
        {
            var output = new NullRealtimeOutput(_o.Speed);
            _output = output;
            return output;
        },
        CallerIdentity = new SoakCallerIdentity(),
        Server = new ServerOptions { SecretHashIterations = 1_000 },
        Engine = new PlaybackEngineOptions(),
        StateHeartbeat = TimeSpan.FromSeconds(1),
        PersistDebounce = TimeSpan.FromMilliseconds(200),
        LoggerFactory = _logs,
        Components = _remote is null ? [] : [_remote],
    };

    private async Task StartHostAsync()
    {
        var host = await SabhaHost.StartAsync(HostOptions()).ConfigureAwait(false);
        _lastStateRevision = 0;
        _lastQueueRevision = -1;
        host.Playback.StateChanged += OnBackendState;
        host.Playback.QueueChanged += OnBackendQueue;
        _host = host;
    }

    private void OnBackendState(PlaybackStateDto state)
    {
        // Raised by the backend's single dispatcher: revisions must strictly increase.
        var last = Interlocked.Exchange(ref _lastStateRevision, state.Revision);
        if (state.Revision <= last)
        {
            Violation("ServerStateRevisionOrder", $"backend state revision {state.Revision} after {last}");
        }
    }

    private void OnBackendQueue(QueueDto queue)
    {
        var last = Interlocked.Exchange(ref _lastQueueRevision, queue.Revision);
        if (queue.Revision <= last)
        {
            Violation("ServerQueueRevisionOrder", $"backend queue revision {queue.Revision} after {last}");
        }
    }

    // ================================================================== setup

    private async Task SetupAsync()
    {
        var ct = _stop.Token;
        _admin = await PipeSoakClient.ConnectAsync(_pipeName, "admin", null, null, isAdmin: true, Violation, OnLag, ct).ConfigureAwait(false);
        await _admin.Client.Auth.CompleteInitialSetupAsync(AdminUser, AdminSecret, ct).ConfigureAwait(false);
        await _admin.ReseedAsync(ct).ConfigureAwait(false);
        for (var i = 1; i <= Math.Max(1, _o.PipeClients); i++)
        {
            await _admin.Client.Admin.CreateUserAsync(VolunteerName(i), Role.Volunteer, VolunteerSecret(i), ct).ConfigureAwait(false);
        }

        // The library: lengths spread over [min, max], kinds rotated, shuffled with the seed.
        var lengths = Enumerable.Range(0, _o.TrackCount)
            .Select(i => _o.MinTrackSeconds + (_o.MaxTrackSeconds - _o.MinTrackSeconds) * (_o.TrackCount == 1 ? 0 : i / (double)(_o.TrackCount - 1)))
            .OrderBy(_ => _random.Next()).ToList();
        var category = await _admin.Client.Admin.CreateCategoryAsync("Soak", ct).ConfigureAwait(false);
        for (var i = 0; i < lengths.Count; i++)
        {
            var kind = (SoakTrackKind)(i % 3);
            await ImportAsync(kind, lengths[i], $"Soak {i:00} {kind}", category.Id, ct).ConfigureAwait(false);
        }

        // Faults planted behind the database's back: one corrupt file, and (with enough tracks) one truncated file.
        await CorruptStoredFileAsync(_library[^1].Id, truncate: false).ConfigureAwait(false);
        if (_library.Count >= 10)
        {
            await CorruptStoredFileAsync(_library[^2].Id, truncate: true).ConfigureAwait(false);
        }

        await WaitForAnalysisAsync().ConfigureAwait(false);
        _libraryAtStart = _library.Count;

        for (var i = 0; i < 2; i++)
        {
            var playlist = await _admin.Client.Playlists.CreatePlaylistAsync($"Sabha set {i}", PickTracks(6), ct).ConfigureAwait(false);
            _playlists.Add(playlist.Id);
        }

        for (var i = 0; i < _o.PipeClients; i++)
        {
            await AddVolunteerAsync(ct).ConfigureAwait(false);
        }

        if (_remote is not null)
        {
            for (var i = 0; i < _o.RemoteClients; i++)
            {
                _deviceTokens.Add(await _remote.PairAsync(H, AdminUser, AdminSecret, Role.Volunteer, $"Soak phone {i}", ct).ConfigureAwait(false));
                await AddPhoneAsync(i, ct).ConfigureAwait(false);
            }
        }

        // Start: a mixed queue (the corrupt track included), repeat on, playing.
        var start = PickTracks(Math.Min(12, _library.Count)).Concat(_badTracks).Distinct().ToList();
        await _admin.Client.Playback.EnqueueAsync(start, playNext: false, ct).ConfigureAwait(false);
        await _admin.Client.Playback.SetLoopModeAsync(LoopMode.RepeatQueue, ct).ConfigureAwait(false);
        await _admin.Client.Playback.SetCrossfadeAsync(4, ct).ConfigureAwait(false);
        await _admin.Client.Playback.ResumeAsync(ct).ConfigureAwait(false);
        SetIntent(Intent.Playing);
    }

    private static string VolunteerName(int i) => $"sevak{i}";

    private static string VolunteerSecret(int i) => $"sevak-pin-{i}";

    private async Task<Guid> ImportAsync(SoakTrackKind kind, double seconds, string title, Guid? categoryId, CancellationToken ct)
    {
        var bytes = SoakWav.Create(kind, seconds, Interlocked.Increment(ref _trackSeed));
        var admin = _admin!.Client;
        var id = await admin.Admin.BeginImportAsync(title.Replace(' ', '_') + ".wav", bytes.Length, ct).ConfigureAwait(false);
        for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
        {
            await admin.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)], ct).ConfigureAwait(false);
        }

        var result = await admin.Admin.CompleteImportAsync(id, new ImportMetadata(title, "Soak", categoryId), ct).ConfigureAwait(false);
        lock (_library)
        {
            _library.Add(new LibraryTrack(result.TrackId, seconds, kind));
        }

        return result.TrackId;
    }

    private async Task CorruptStoredFileAsync(Guid trackId, bool truncate)
    {
        var info = await H.App.Tracks.GetFileInfoAsync(trackId).ConfigureAwait(false) ?? throw new InvalidOperationException("track vanished");
        var path = H.Store.GetPhysicalPath(info.FileKey);
        if (truncate)
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes[..(44 + (bytes.Length - 44) / 3 / 2 * 2)]).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllBytesAsync(path, SoakWav.Garbage(64_000, trackId.GetHashCode())).ConfigureAwait(false);
        }

        H.Resolver.Invalidate(trackId);
        lock (_library)
        {
            _badTracks.Add(trackId);
        }
    }

    private async Task WaitForAnalysisAsync()
    {
        var deadline = WallSeconds + _o.AnalysisWait.TotalSeconds;
        while (WallSeconds < deadline)
        {
            var pending = 0;
            foreach (var track in _library)
            {
                var analysis = await H.App.Analysis.GetAsync(track.Id).ConfigureAwait(false);
                if (analysis is null || analysis.Status is AnalysisStatus.Pending or AnalysisStatus.Analyzing)
                {
                    pending++;
                }
            }

            if (pending == 0)
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        foreach (var track in _library)
        {
            if ((await H.App.Analysis.GetAsync(track.Id).ConfigureAwait(false))?.Status == AnalysisStatus.Ready)
            {
                _readyAtStart++;
            }
        }
    }

    private async Task AddVolunteerAsync(CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _volunteerCounter);
        var user = (n - 1) % Math.Max(1, _o.PipeClients) + 1;
        var client = await PipeSoakClient.ConnectAsync(_pipeName, $"pipe-{n}", VolunteerName(user), VolunteerSecret(user), isAdmin: false, Violation, OnLag, ct)
            .ConfigureAwait(false);
        lock (_volunteers)
        {
            _volunteers.Add(client);
        }
    }

    private async Task AddPhoneAsync(int index, CancellationToken ct)
    {
        var client = await RemoteSoakClient.ConnectAsync(_remote!, _deviceTokens[index], $"phone-{index}", Violation, OnLag, ct).ConfigureAwait(false);
        lock (_phones)
        {
            _phones.Add(client);
        }
    }

    private async Task DisposeClientsAsync(bool includeAdmin)
    {
        List<IAsyncDisposable> all;
        lock (_volunteers)
        {
            all = [.. _volunteers];
            _volunteers.Clear();
        }

        lock (_phones)
        {
            all.AddRange(_phones);
            _phones.Clear();
        }

        if (includeAdmin && _admin is not null)
        {
            all.Add(_admin);
            _admin = null;
        }

        foreach (var client in all)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Count("ClientDisposeError:" + ex.GetType().Name);
            }
        }
    }

    // ================================================================== workload

    private async Task DriveAsync()
    {
        var restartAt = _o.RestartAtFraction is > 0 and < 1 ? _o.RestartAtFraction * _o.SimulatedSeconds : double.PositiveInfinity;
        var next = 0.0;
        while (SimSeconds < _o.SimulatedSeconds)
        {
            if (_wall.Elapsed > _o.MaxWallTime)
            {
                _endedEarly = true;
                Say($"Wall-clock cap reached at {SimSeconds / 3600:F2} simulated hours.");
                break;
            }

            if (SimSeconds < next)
            {
                var delayMs = Math.Clamp((next - SimSeconds) / _o.Speed * 1000, 1, 250);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ConfigureAwait(false);
                continue;
            }

            next = SimSeconds + -Math.Log(1 - _random.NextDouble()) * _o.MeanActionIntervalSimSeconds;

            if (SimSeconds >= restartAt)
            {
                restartAt = double.PositiveInfinity;
                await RestartAsync().ConfigureAwait(false);
                continue;
            }

            if (_needsPlaybackRepair)
            {
                _needsPlaybackRepair = false;
                await RepairPlaybackAsync().ConfigureAwait(false);
                continue;
            }

            if (_resumeAtSim is { } resumeAt && SimSeconds >= resumeAt)
            {
                _resumeAtSim = null;
                await Act("Resume", (c, ct) => c.Playback.ResumeAsync(ct), Intent.Playing).ConfigureAwait(false);
                continue;
            }

            if (_unmuteDue)
            {
                _unmuteDue = false;
                await Act("Unmute", (c, ct) => c.Playback.SetMutedAsync(false, ct)).ConfigureAwait(false);
                continue;
            }

            await RandomActionAsync().ConfigureAwait(false);
        }
    }

    private async Task RandomActionAsync()
    {
        var host = H;
        var queue = host.Engine.GetQueue();
        var state = host.Engine.GetState();
        var playing = state.Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading;
        var roll = _random.Next(100);
        switch (roll)
        {
            case < 7 when queue.Items.Count > 0:
                var item = queue.Items[_random.Next(queue.Items.Count)].QueueItemId;
                await Act("PlayQueueItem", (c, ct) => c.Playback.PlayQueueItemAsync(item, ct), Intent.Playing).ConfigureAwait(false);
                break;
            case < 10:
                var track = PickTracks(1)[0];
                await Act("PlayTrack", (c, ct) => c.Playback.PlayTrackAsync(track, ct), Intent.Playing).ConfigureAwait(false);
                break;
            case < 15 when playing && _resumeAtSim is null:
                _resumeAtSim = SimSeconds + 5 + _random.NextDouble() * 55;
                await Act("Pause", (c, ct) => c.Playback.PauseAsync(ct), Intent.Paused).ConfigureAwait(false);
                break;
            case < 16 when _resumeAtSim is null:
                _resumeAtSim = SimSeconds + 10 + _random.NextDouble() * 80;
                await Act("Stop", (c, ct) => c.Playback.StopAsync(ct), Intent.Stopped).ConfigureAwait(false);
                break;
            case < 23:
                await Act("Next", (c, ct) => c.Playback.NextAsync(ct)).ConfigureAwait(false);
                break;
            case < 26:
                await Act("Previous", (c, ct) => c.Playback.PreviousAsync(ct)).ConfigureAwait(false);
                break;
            case < 32 when playing && state.DurationSeconds > 1:
                var position = _random.NextDouble() * state.DurationSeconds;
                await Act("Seek", (c, ct) => c.Playback.SeekAsync(position, ct)).ConfigureAwait(false);
                break;
            case < 36:
                var mode = _random.Next(100) switch
                {
                    < 25 => LoopMode.Off,
                    < 60 => LoopMode.SmartLoop,
                    _ => LoopMode.RepeatQueue,
                };
                await Act("SetLoopMode", (c, ct) => c.Playback.SetLoopModeAsync(mode, ct)).ConfigureAwait(false);
                break;
            case < 39:
                double[] fades = [0, 1, 2, 4, 6, 8, 12];
                var fade = fades[_random.Next(fades.Length)];
                await Act("SetCrossfade", (c, ct) => c.Playback.SetCrossfadeAsync(fade, ct)).ConfigureAwait(false);
                break;
            case < 41:
                var volume = (float)Math.Round(0.2 + _random.NextDouble() * 0.8, 2);
                await Act("SetVolume", (c, ct) => c.Playback.SetVolumeAsync(volume, ct)).ConfigureAwait(false);
                break;
            case < 42:
                _unmuteDue = true;
                await Act("Mute", (c, ct) => c.Playback.SetMutedAsync(true, ct)).ConfigureAwait(false);
                break;
            case < 48:
                var add = PickTracks(1 + _random.Next(4));
                if (_random.Next(6) == 0)
                {
                    add.Add(PickBadTrack()); // the planted corrupt/truncated files keep coming back into the queue
                    Count("EnqueuedBadTrack");
                }

                var playNext = _random.Next(3) == 0;
                await Act("Enqueue", (c, ct) => c.Playback.EnqueueAsync(add, playNext, ct)).ConfigureAwait(false);
                break;
            case < 51 when queue.Items.Count > 1:
                var moved = queue.Items[_random.Next(queue.Items.Count)].QueueItemId;
                var index = _random.Next(queue.Items.Count);
                await Act("MoveQueueItem", (c, ct) => c.Playback.MoveQueueItemAsync(moved, index, ct)).ConfigureAwait(false);
                break;
            case < 55 when queue.Items.Count > 3 || (queue.Items.Count > 1 && _random.Next(4) == 0):
                var removed = _random.Next(10) == 0 && state.CurrentQueueItemId is { } current
                    ? current
                    : queue.Items[_random.Next(queue.Items.Count)].QueueItemId;
                await Act("RemoveFromQueue", (c, ct) => c.Playback.RemoveFromQueueAsync(removed, ct)).ConfigureAwait(false);
                break;
            case < 56 when queue.Items.Count > 15:
                await Act("ClearQueue", (c, ct) => c.Playback.ClearQueueAsync(ct)).ConfigureAwait(false);
                var refill = PickTracks(6);
                await Act("Enqueue", (c, ct) => c.Playback.EnqueueAsync(refill, false, ct)).ConfigureAwait(false);
                break;
            case < 59 when _playlists.Count > 0:
                var playlist = _playlists[_random.Next(_playlists.Count)];
                var replace = _random.Next(5) == 0;
                await Act("EnqueuePlaylist", (c, ct) => c.Playback.EnqueuePlaylistAsync(playlist, replace, ct)).ConfigureAwait(false);
                break;
            case < 63:
                await PlaylistActionAsync().ConfigureAwait(false);
                break;
            case < 65:
                await AdminImportAsync().ConfigureAwait(false);
                break;
            case < 67:
                await AdminDeleteAsync(state).ConfigureAwait(false);
                break;
            case < 70:
                await ChurnPipeClientAsync().ConfigureAwait(false);
                break;
            case < 71:
                await GarbageConnectionAsync().ConfigureAwait(false);
                break;
            case < 78 when _phones.Count > 0:
                await PhoneActionAsync(playing).ConfigureAwait(false);
                break;
            case < 79 when _remote is not null:
                await ChurnPhoneAsync().ConfigureAwait(false);
                break;
            case < 80 when SimSeconds - _lastZeroClientWindowSim > 3600 && _resumeAtSim is null && playing:
                await ZeroClientWindowAsync().ConfigureAwait(false);
                break;
            case < 84 when queue.Items.Count < 6:
                var more = PickTracks(4);
                await Act("Enqueue", (c, ct) => c.Playback.EnqueueAsync(more, false, ct)).ConfigureAwait(false);
                break;
            default:
                await Act("GetState", (c, ct) => c.Playback.GetStateAsync(ct)).ConfigureAwait(false);
                break;
        }
    }

    private async Task PlaylistActionAsync()
    {
        var choice = _random.Next(4);
        if (choice == 0 || _playlists.Count < 2)
        {
            var tracks = PickTracks(3 + _random.Next(6));
            var name = $"Set {_random.Next(100_000)}";
            await Act("CreatePlaylist", async (c, ct) =>
            {
                var created = await c.Playlists.CreatePlaylistAsync(name, tracks, ct).ConfigureAwait(false);
                lock (_playlists)
                {
                    _playlists.Add(created.Id);
                }
            }).ConfigureAwait(false);
            return;
        }

        var id = _playlists[_random.Next(_playlists.Count)];
        switch (choice)
        {
            case 1:
                var name = $"Renamed {_random.Next(100_000)}";
                await Act("RenamePlaylist", (c, ct) => c.Playlists.RenamePlaylistAsync(id, name, ct)).ConfigureAwait(false);
                break;
            case 2:
                var tracks = PickTracks(2 + _random.Next(8));
                await Act("SetPlaylistTracks", (c, ct) => c.Playlists.SetPlaylistTracksAsync(id, tracks, ct)).ConfigureAwait(false);
                break;
            default:
                lock (_playlists)
                {
                    _playlists.Remove(id);
                }

                await Act("DeletePlaylist", (c, ct) => c.Playlists.DeletePlaylistAsync(id, ct)).ConfigureAwait(false);
                break;
        }
    }

    private async Task AdminImportAsync()
    {
        var kind = (SoakTrackKind)_random.Next(3);
        var seconds = 20 + _random.NextDouble() * 25;
        var title = $"Imported {Interlocked.Increment(ref _trackSeed)}";
        await Act("AdminImport", async (_, ct) =>
        {
            var admin = await AdminAsync(ct).ConfigureAwait(false);
            var bytes = SoakWav.Create(kind, seconds, _trackSeed);
            var id = await admin.Client.Admin.BeginImportAsync(title + ".wav", bytes.Length, ct).ConfigureAwait(false);
            for (var offset = 0; offset < bytes.Length; offset += AdminLimits.MaxChunkBytes)
            {
                await admin.Client.Admin.UploadChunkAsync(id, offset, bytes[offset..Math.Min(bytes.Length, offset + AdminLimits.MaxChunkBytes)], ct)
                    .ConfigureAwait(false);
            }

            var result = await admin.Client.Admin.CompleteImportAsync(id, new ImportMetadata(title, null, null), ct).ConfigureAwait(false);
            lock (_library)
            {
                _library.Add(new LibraryTrack(result.TrackId, seconds, kind));
            }
        }, admin: true).ConfigureAwait(false);
    }

    private async Task AdminDeleteAsync(PlaybackStateDto state)
    {
        List<LibraryTrack> candidates;
        lock (_library)
        {
            candidates = _library.Where(t => !_badTracks.Contains(t.Id)).ToList();
        }

        if (candidates.Count <= Math.Max(4, _o.TrackCount * 0.6))
        {
            return;
        }

        // A third of the deletes hit whatever is playing right now.
        var victim = _random.Next(3) == 0 && state.CurrentTrackId is { } playing && candidates.Any(t => t.Id == playing)
            ? playing
            : candidates[_random.Next(candidates.Count)].Id;
        Count(victim == state.CurrentTrackId ? "DeletePlayingTrack" : "DeleteOtherTrack");
        lock (_library)
        {
            _library.RemoveAll(t => t.Id == victim);
            _deleted.Add(victim);
        }

        await Act("AdminDelete", async (_, ct) =>
        {
            var admin = await AdminAsync(ct).ConfigureAwait(false);
            await admin.Client.Admin.DeleteTrackAsync(victim, ct).ConfigureAwait(false);
        }, admin: true).ConfigureAwait(false);
    }

    private async Task ChurnPipeClientAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            PipeSoakClient? victim = null;
            lock (_volunteers)
            {
                if (_volunteers.Count > 0)
                {
                    victim = _volunteers[_random.Next(_volunteers.Count)];
                    _volunteers.Remove(victim);
                }
            }

            if (victim is not null)
            {
                await victim.DisposeAsync().ConfigureAwait(false); // no logout: the connection just goes away
            }

            using var timeout = new CancellationTokenSource(_o.CallTimeout);
            await AddVolunteerAsync(timeout.Token).ConfigureAwait(false);
            Record("ChurnPipeClient", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record("ChurnPipeClient", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A raw connection that sends half a frame of junk and vanishes.</summary>
    private async Task GarbageConnectionAsync()
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var junk = Encoding.ASCII.GetBytes("Content-Length: 4096\r\n\r\n{\"jsonrpc\":\"2.0\",\"method\":\"IPlaybackApi.Pau");
            await pipe.WriteAsync(junk, timeout.Token).ConfigureAwait(false);
            Record("GarbageConnection", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record("GarbageConnection", ex);
        }
    }

    private async Task PhoneActionAsync(bool playing)
    {
        RemoteSoakClient phone;
        lock (_phones)
        {
            if (_phones.Count == 0)
            {
                return;
            }

            phone = _phones[_random.Next(_phones.Count)];
        }

        switch (_random.Next(6))
        {
            case 0 when playing && _resumeAtSim is null:
                _resumeAtSim = SimSeconds + 5 + _random.NextDouble() * 40;
                await ActHub("Hub.Pause", phone, ct => phone.Hub.InvokeAsync("Pause", ct), Intent.Paused).ConfigureAwait(false);
                break;
            case 1:
                await ActHub("Hub.Next", phone, ct => phone.Hub.InvokeAsync("Next", ct)).ConfigureAwait(false);
                break;
            case 2:
                var tracks = PickTracks(2);
                await ActHub("Hub.Enqueue", phone, ct => phone.Hub.InvokeAsync<QueueDto>("Enqueue", tracks, false, ct)).ConfigureAwait(false);
                break;
            case 3:
                var volume = (float)Math.Round(0.3 + _random.NextDouble() * 0.7, 2);
                await ActHub("Hub.SetVolume", phone, ct => phone.Hub.InvokeAsync("SetVolume", volume, ct)).ConfigureAwait(false);
                break;
            default:
                await ActHub("Hub.GetState", phone, ct => phone.Hub.InvokeAsync<PlaybackStateDto>("GetState", ct)).ConfigureAwait(false);
                break;
        }
    }

    private async Task ChurnPhoneAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RemoteSoakClient? victim = null;
            lock (_phones)
            {
                if (_phones.Count > 0)
                {
                    victim = _phones[_random.Next(_phones.Count)];
                    _phones.Remove(victim);
                }
            }

            var index = victim is null ? 0 : _deviceTokens.IndexOf(victim.Token);
            if (victim is not null)
            {
                await victim.DisposeAsync().ConfigureAwait(false);
            }

            using var timeout = new CancellationTokenSource(_o.CallTimeout);
            await AddPhoneAsync(Math.Max(0, index), timeout.Token).ConfigureAwait(false);
            Record("ChurnPhone", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record("ChurnPhone", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every client (admin included) disconnects for a few simulated minutes; playback must carry on.</summary>
    private async Task ZeroClientWindowAsync()
    {
        _lastZeroClientWindowSim = SimSeconds;
        Count("ZeroClientWindow");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeClientsAsync(includeAdmin: true).ConfigureAwait(false);
            await WaitUntilAsync(5, () => H.Pipe!.ConnectionCount == 0).ConfigureAwait(false);
            var until = SimSeconds + 120 + _random.NextDouble() * 360;
            while (SimSeconds < until && !_stop.IsCancellationRequested && _wall.Elapsed < _o.MaxWallTime)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            await ReconnectAllAsync().ConfigureAwait(false);
            Record("ZeroClientWindow", null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record("ZeroClientWindow", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconnectAllAsync()
    {
        using var timeout = new CancellationTokenSource(_o.CallTimeout);
        _admin ??= await PipeSoakClient.ConnectAsync(_pipeName, "admin", AdminUser, AdminSecret, isAdmin: true, Violation, OnLag, timeout.Token)
            .ConfigureAwait(false);
        while (_volunteers.Count < _o.PipeClients)
        {
            await AddVolunteerAsync(timeout.Token).ConfigureAwait(false);
        }

        if (_remote is not null)
        {
            for (var i = _phones.Count; i < _deviceTokens.Count; i++)
            {
                await AddPhoneAsync(i, timeout.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task RepairPlaybackAsync()
    {
        var queue = H.Engine.GetQueue();
        if (queue.Items.Count(i => !IsBad(i.TrackId)) < 3)
        {
            var add = PickTracks(6);
            await Act("Enqueue", (c, ct) => c.Playback.EnqueueAsync(add, false, ct)).ConfigureAwait(false);
        }

        if (_random.Next(2) == 0)
        {
            await Act("SetLoopMode", (c, ct) => c.Playback.SetLoopModeAsync(LoopMode.RepeatQueue, ct)).ConfigureAwait(false);
        }

        var first = H.Engine.GetQueue().Items.FirstOrDefault(i => !IsBad(i.TrackId));
        if (first is not null)
        {
            await Act("PlayQueueItem", (c, ct) => c.Playback.PlayQueueItemAsync(first.QueueItemId, ct), Intent.Playing).ConfigureAwait(false);
        }
    }

    private async Task RestartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var host = H;
            var expectedQueue = host.Engine.GetQueue();
            var expectedState = host.Engine.GetState();
            var sim = SimSeconds;
            Say($"Restarting the host at {sim / 3600:F2} simulated hours (status {expectedState.Status}, {expectedQueue.Items.Count} queued).");

            // Phones point at the old HTTPS port; pipe clients are left alone and must reconnect by themselves.
            List<RemoteSoakClient> phones;
            lock (_phones)
            {
                phones = [.. _phones];
                _phones.Clear();
            }

            foreach (var phone in phones)
            {
                await phone.DisposeAsync().ConfigureAwait(false);
            }

            var connectionsBefore = new Dictionary<PipeSoakClient, int>();
            lock (_volunteers)
            {
                foreach (var client in _volunteers)
                {
                    connectionsBefore[client] = client.Connections;
                }
            }

            if (_admin is not null)
            {
                connectionsBefore[_admin] = _admin.Connections;
            }

            _restarting = true;
            var outage = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();
            await host.StopAsync().ConfigureAwait(false);
            var stopSeconds = sw.Elapsed.TotalSeconds;
            // What the final save actually wrote. The output keeps rendering at Speed× in wall-clock time while the host
            // stops (closing the remote and the pipe, then the fade), so with RepeatQueue the engine can legitimately move
            // on several items after the pre-stop snapshot above (1.3 s of stop = 156 s of audio at 120×). The restore must
            // match the save; the snapshot only fixes which tracks are queued.
            var persisted = await new Data.Repositories.SqlitePlaybackStateRepository(host.Database).LoadQueueAsync().ConfigureAwait(false);
            _simBase += (_output?.FramesRendered ?? 0) / (double)AudioFormat.SampleRate;
            _output = null;
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
            sw.Restart();
            await StartHostAsync().ConfigureAwait(false);
            var startSeconds = sw.Elapsed.TotalSeconds;
            SetIntent(Intent.Stopped);
            _resumeAtSim = null;
            _restarting = false;

            var restoredQueue = H.Engine.GetQueue();
            var restoredState = H.Engine.GetState();
            var queueOk = restoredQueue.Items.Select(i => i.TrackId).SequenceEqual(expectedQueue.Items.Select(i => i.TrackId).Where(id => !_deleted.Contains(id)))
                          && restoredQueue.Items.Select(i => i.QueueItemId).SequenceEqual(persisted.Items.Select(i => i.QueueItemId))
                          && (persisted.CurrentIndex < 0 || restoredQueue.CurrentIndex == persisted.CurrentIndex)
                          && (expectedQueue.CurrentIndex < 0 || persisted.CurrentIndex >= 0);
            var settingsOk = restoredState.Volume == expectedState.Volume && restoredState.Muted == expectedState.Muted
                             && restoredState.LoopMode == expectedState.LoopMode && restoredState.CrossfadeSeconds == expectedState.CrossfadeSeconds;
            var detail = $"queue {expectedQueue.Items.Count}@{expectedQueue.CurrentIndex} (saved {persisted.Items.Count}@{persisted.CurrentIndex}) -> " +
                         $"{restoredQueue.Items.Count}@{restoredQueue.CurrentIndex}; " +
                         $"settings {expectedState.Volume}/{expectedState.Muted}/{expectedState.LoopMode}/{expectedState.CrossfadeSeconds} -> " +
                         $"{restoredState.Volume}/{restoredState.Muted}/{restoredState.LoopMode}/{restoredState.CrossfadeSeconds}";
            if (!queueOk)
            {
                Violation("RestoreQueueMismatch", detail);
            }

            if (!settingsOk)
            {
                Violation("RestoreSettingsMismatch", detail);
            }

            // Pipe clients reconnect and resume their sessions on their own (measured from the start of the stop).
            var clients = connectionsBefore.Keys.ToList();
            bool Back(PipeSoakClient c) => c.Connections > connectionsBefore[c] && c.IsConnected;
            await WaitUntilAsync(30, () => clients.All(Back)).ConfigureAwait(false);
            var reconnectSeconds = outage.Elapsed.TotalSeconds;
            if (!clients.All(Back))
            {
                Violation("ClientDidNotReconnect", $"{clients.Count(c => !Back(c))} of {clients.Count} pipe clients not reconnected 30 s after restart");
            }

            foreach (var client in clients.Where(c => c.IsConnected))
            {
                try
                {
                    await client.ReseedAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (SabhaMujicException ex)
                {
                    Count("ReseedFailed:" + ex.Code);
                }
            }

            if (_remote is not null)
            {
                using var timeout = new CancellationTokenSource(_o.CallTimeout);
                for (var i = 0; i < _deviceTokens.Count; i++)
                {
                    await AddPhoneAsync(i, timeout.Token).ConfigureAwait(false);
                }
            }

            _restarts.Add(new RestartResult(Stats.Round(sim), Stats.Round(stopSeconds), Stats.Round(startSeconds), queueOk, settingsOk,
                restoredState.Status, Stats.Round(reconnectSeconds), detail));
            Say($"Restarted: stop {stopSeconds:F2} s, start {startSeconds:F2} s, pipe clients reconnected {reconnectSeconds:F2} s after the stop began; {detail}");
        }
        finally
        {
            _restarting = false;
            _gate.Release();
        }

        await Act("Resume", (c, ct) => c.Playback.ResumeAsync(ct), Intent.Playing).ConfigureAwait(false);
        await WaitUntilAsync(2, () => H.Engine.GetState().Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading).ConfigureAwait(false);
        if (H.Engine.GetState().Status is not (PlaybackStatus.Playing or PlaybackStatus.Crossfading))
        {
            await RepairPlaybackAsync().ConfigureAwait(false);
        }
    }

    // ================================================================== action plumbing

    private async Task<PipeSoakClient> AdminAsync(CancellationToken ct)
    {
        if (_admin is { } admin)
        {
            return admin;
        }

        _admin = await PipeSoakClient.ConnectAsync(_pipeName, "admin", AdminUser, AdminSecret, isAdmin: true, Violation, OnLag, ct).ConfigureAwait(false);
        return _admin;
    }

    private PipeSoakClient? PickClient()
    {
        lock (_volunteers)
        {
            var connected = _volunteers.Where(c => c.IsConnected).ToList();
            if (connected.Count > 0)
            {
                return connected[_random.Next(connected.Count)];
            }
        }

        return _admin;
    }

    /// <summary>Runs one client command under the workload gate with a watchdog, and records its outcome.</summary>
    private async Task Act(string name, Func<PipeSabhaClient, CancellationToken, Task> body, Intent? intent = null, bool admin = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _commandEpoch);
        try
        {
            var client = admin ? await AdminAsync(_stop.Token).ConfigureAwait(false) : PickClient();
            if (client is null)
            {
                Record(name, new InvalidOperationException("no client"));
                return;
            }

            using var timeout = new CancellationTokenSource(_o.CallTimeout);
            var call = body(client.Client, timeout.Token);
            if (await Task.WhenAny(call, Task.Delay(_o.CallTimeout + TimeSpan.FromSeconds(5))).ConfigureAwait(false) != call)
            {
                Violation("HungCall", $"{name} did not complete within {_o.CallTimeout.TotalSeconds:F0} s");
                Record(name, new TimeoutException());
                return;
            }

            await call.ConfigureAwait(false);
            if (intent is { } i)
            {
                SetIntent(i);
            }

            Record(name, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record(name, ex);
        }
        finally
        {
            Interlocked.Increment(ref _commandEpoch);
            _gate.Release();
        }
    }

    private Task ActHub(string name, RemoteSoakClient phone, Func<CancellationToken, Task> body, Intent? intent = null) =>
        Act(name, (_, ct) => phone.IsConnected ? body(ct) : throw new InvalidOperationException("hub not connected"), intent);

    private void Record(string action, Exception? ex)
    {
        Interlocked.Increment(ref _actionsSinceSample);
        var outcome = ex switch
        {
            null => "ok",
            SabhaMujicException s => s.Code.ToString(),
            HubException h => "Hub:" + (HubErrors.CodeOf(h)?.ToString() ?? "unknown"),
            OperationCanceledException => "Canceled",
            _ => ex.GetType().Name,
        };
        _actions.GetOrAdd(action, _ => new ConcurrentDictionary<string, int>()).AddOrUpdate(outcome, 1, (_, n) => n + 1);
        _recent.Enqueue($"{SimSeconds:F0}s {action}:{outcome}");
        while (_recent.Count > 8)
        {
            _recent.TryDequeue(out _);
        }

        if (ex is SabhaMujicException { Code: ErrorCode.Internal } || (ex is HubException hub && HubErrors.CodeOf(hub) == ErrorCode.Internal))
        {
            Violation("InternalError", $"{action} failed with Internal: {ex.Message}");
        }
    }

    private void SetIntent(Intent intent)
    {
        _intent = (int)intent;
        if (intent == Intent.Playing)
        {
            _needsPlaybackRepair = false;
        }
    }

    private List<Guid> PickTracks(int count)
    {
        lock (_library)
        {
            var good = _library.Where(t => !_badTracks.Contains(t.Id)).ToList();
            return Enumerable.Range(0, count).Select(_ => good[_random.Next(good.Count)].Id).ToList();
        }
    }

    private Guid PickBadTrack()
    {
        lock (_library)
        {
            var bad = _badTracks.ToList();
            return bad[_random.Next(bad.Count)];
        }
    }

    private bool IsBad(Guid trackId)
    {
        lock (_library)
        {
            return _badTracks.Contains(trackId) || _deleted.Contains(trackId);
        }
    }

    // ================================================================== monitor (invariants)

    private async Task MonitorLoopAsync()
    {
        PlaybackStateDto? previous = null;
        PlaybackVoice? previousVoice = null;
        long previousFrames = 0;
        long previousEpoch = -1;
        long windowFrames = 0;
        long windowPosition = 0;
        var statusSince = WallSeconds;
        var lastProgress = WallSeconds;
        var notPlayingSince = double.NaN;
        long stopEpoch = -1;
        var stopExhausted = false;
        var stopAllBad = false;
        var stopDetail = string.Empty;
        var flaggedEpisode = false;
        var stuck = _o.StuckThreshold.TotalSeconds;
        var crossfadeLimit = stuck + 12 / _o.Speed;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(MonitorIntervalMs), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var host = _host;
            var output = _output;
            if (_restarting || host is null || output is null)
            {
                previous = null;
                previousVoice = null;
                continue;
            }

            PlaybackStateDto state;
            QueueDto queue;
            PlaybackVoice? voice;
            try
            {
                state = host.Engine.GetState();
                queue = host.Engine.GetQueue();
                voice = host.Engine.Mixer.CurrentVoice;
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            var now = WallSeconds;
            var frames = output.FramesRendered;
            var epoch = Interlocked.Read(ref _commandEpoch);
            var intent = (Intent)_intent;
            _monitorTicks++;
            if (state.Status is PlaybackStatus.Playing or PlaybackStatus.Crossfading or PlaybackStatus.Loading)
            {
                _playingMonitorTicks++;
                _lastPlayingEpoch = epoch;
            }

            // Status changes; Loading must resolve, Crossfading must resolve.
            if (previous is null || state.Status != previous.Status)
            {
                if (previous is not null)
                {
                    _stateTransitions++;
                }

                statusSince = now;
                flaggedEpisode = false;
            }
            else if (!flaggedEpisode && state.Status == PlaybackStatus.Loading && now - statusSince > stuck)
            {
                flaggedEpisode = true;
                Violation("StuckLoading", $"Loading for {now - statusSince:F1} s");
            }
            else if (!flaggedEpisode && state.Status == PlaybackStatus.Crossfading && now - statusSince > crossfadeLimit)
            {
                flaggedEpisode = true;
                Violation("StuckCrossfading", $"Crossfading for {now - statusSince:F1} s (crossfade {state.CrossfadeSeconds} s)");
            }

            // Position: monotonic within an item unless a command, a Smart Loop seam or a one-item repeat explains a jump back.
            if (previous is { Status: PlaybackStatus.Playing } && state.Status == PlaybackStatus.Playing
                && previous.CurrentQueueItemId == state.CurrentQueueItemId && previousEpoch == epoch
                && state.PositionSeconds < previous.PositionSeconds - 0.05
                && state.LoopMode != LoopMode.SmartLoop && previous.LoopMode != LoopMode.SmartLoop
                && !(state.LoopMode == LoopMode.RepeatQueue && queue.Items.Count == 1))
            {
                Violation("PositionWentBackwards", $"{previous.PositionSeconds:F2} -> {state.PositionSeconds:F2} s in item {state.CurrentQueueItemId} ({state.LoopMode})");
            }

            // Progress: while playing, the position (or the item) must keep moving.
            if (state.Status != PlaybackStatus.Playing || previous is null || previous.Status != PlaybackStatus.Playing
                || state.PositionSeconds != previous.PositionSeconds || state.CurrentQueueItemId != previous.CurrentQueueItemId)
            {
                lastProgress = now;
            }
            else if (now - lastProgress > stuck)
            {
                Violation("PlaybackStalled", $"Playing but the position stayed at {state.PositionSeconds:F2} s for {now - lastProgress:F1} s");
                lastProgress = now;
            }

            // Playback continues unless a client paused/stopped it (or the queue legitimately ran out).
            if (intent == Intent.Playing && state.Status is PlaybackStatus.Stopped or PlaybackStatus.Idle or PlaybackStatus.Error or PlaybackStatus.Paused)
            {
                if (double.IsNaN(notPlayingSince))
                {
                    // Classify when the stop is first seen: later commands (an Enqueue a moment after the queue ran out)
                    // must not turn a legitimate end of queue into a violation.
                    notPlayingSince = now;
                    stopEpoch = epoch;
                    stopExhausted = state.LoopMode == LoopMode.Off
                                    && (queue.Items.Count == 0 || queue.Items.Skip(Math.Max(0, queue.CurrentIndex + 1)).All(i => IsBad(i.TrackId)));
                    stopAllBad = queue.Items.Count > 0 && queue.Items.All(i => IsBad(i.TrackId));
                    stopDetail = $"{state.Status} with nobody pausing/stopping it: item {queue.CurrentIndex + 1}/{queue.Items.Count}, loop {state.LoopMode}, error '{state.ErrorMessage}'";
                }
                else if (now - notPlayingSince > 1.0 && !_needsPlaybackRepair)
                {
                    if (stopEpoch != _lastPlayingEpoch || (stopEpoch & 1) == 1)
                    {
                        // A command ran since playback was last seen (Next past the end, removing/deleting the last item…).
                        Count("StoppedByCommand");
                    }
                    else if (stopExhausted)
                    {
                        Count("QueueRanOut");
                    }
                    else if (stopAllBad)
                    {
                        Count("NothingPlayableQueued");
                    }
                    else
                    {
                        Violation("UnexpectedStop", stopDetail);
                    }

                    _needsPlaybackRepair = true;
                    notPlayingSince = double.NaN;
                }
            }
            else
            {
                notPlayingSince = double.NaN;
            }

            // Underrun estimate: over a stretch where the same voice plays with no command, the voice must advance by
            // as many frames as the output rendered. Stretches near the voice's end are excluded.
            if (voice is not null && voice == previousVoice && previousEpoch == epoch && previous is { Status: PlaybackStatus.Playing }
                && state.Status == PlaybackStatus.Playing && voice.RemainingFrames > frames - previousFrames + AudioFormat.SampleRate)
            {
                windowFrames += frames - previousFrames;
                windowPosition = voice.Position;
            }
            else
            {
                CloseUnderrunWindow(ref windowFrames, ref windowPosition);
                windowPosition = voice?.Position ?? 0;
                _windowStartPosition = windowPosition;
            }

            previous = state;
            previousVoice = voice;
            previousFrames = frames;
            previousEpoch = epoch;
        }
    }

    private long _windowStartPosition;

    private void CloseUnderrunWindow(ref long windowFrames, ref long windowPosition)
    {
        if (windowFrames > 0)
        {
            var advanced = windowPosition - _windowStartPosition;
            var deficit = windowFrames - advanced;
            // One render block of slack for the non-atomic reads at both ends.
            if (deficit > 2 * NullRealtimeOutput.BlockFrames)
            {
                Interlocked.Add(ref _underrunFrames, deficit);
            }

            Interlocked.Add(ref _measuredFrames, windowFrames);
        }

        windowFrames = 0;
    }

    // ================================================================== sampling and convergence

    private async Task SamplerLoopAsync()
    {
        var nextSample = 0.0;
        var samples = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(25, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_restarting || _host is null || SimSeconds < nextSample)
            {
                continue;
            }

            nextSample = Math.Max(nextSample + _o.SampleIntervalSimSeconds, SimSeconds);
            TakeSample();
            samples++;
            if (_o.ConvergenceEverySamples > 0 && samples % _o.ConvergenceEverySamples == 0 && _convergence.IsCompleted)
            {
                // Off the sampling loop: the check waits for the workload gate, which a restart or a zero-client window holds.
                _convergence = Task.Run(() => CheckConvergenceAsync("periodic"));
            }

            if (samples % 30 == 0)
            {
                var last = _samples[^1];
                Say($"  sim {SimSeconds / 3600:F2} h, wall {WallSeconds / 60:F1} min: heap {last.ManagedHeapMb:F1} MB, ws {last.WorkingSetMb:F0} MB, " +
                    $"threads {last.Threads}, fds {last.Handles}, {last.Status}, violations {_violations.Count}");
            }
        }
    }

    private void TakeSample()
    {
        var host = _host;
        if (host is null)
        {
            return;
        }

        var reading = ProcessMetrics.Read();
        var lags = new List<double>();
        while (_windowLag.TryDequeue(out var lag))
        {
            lags.Add(lag);
        }

        PlaybackStatus status;
        int queueLength;
        try
        {
            status = host.Engine.GetState().Status;
            queueLength = host.Engine.GetQueue().Items.Count;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        int libraryCount;
        lock (_library)
        {
            libraryCount = _library.Count;
        }

        var sample = new SoakSample(
            Stats.Round(SimSeconds, 1),
            Stats.Round(WallSeconds, 2),
            Stats.Round(reading.ManagedHeapMb),
            Stats.Round(reading.WorkingSetMb),
            reading.Threads,
            reading.Handles,
            reading.ThreadPoolThreads,
            status,
            queueLength,
            libraryCount,
            host.Pipe?.ConnectionCount ?? 0,
            _remote?.Remote?.ConnectionCount ?? 0,
            host.App.Events.SubscriberCount,
            host.Analysis.PendingCount,
            Stats.Round(Stats.Percentile(lags, 50)),
            Stats.Round(Stats.Percentile(lags, 95)),
            Stats.Round(lags.Count == 0 ? double.NaN : lags.Max()),
            Interlocked.Read(ref _underrunFrames),
            host.Engine.Mixer.DroppedEvents,
            Interlocked.Exchange(ref _actionsSinceSample, 0));
        lock (_samples)
        {
            _samples.Add(sample);
        }
    }

    /// <summary>With the workload paused, every connected client's pushed view must match the server within the timeout.</summary>
    private async Task CheckConvergenceAsync(string why)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var host = _host;
            if (host is null)
            {
                return;
            }

            _convergenceChecks++;
            var views = new List<(ClientView View, Func<bool> Connected)>();
            lock (_volunteers)
            {
                views.AddRange(_volunteers.Select(c => (c.View, (Func<bool>)(() => c.IsConnected))));
            }

            lock (_phones)
            {
                views.AddRange(_phones.Select(c => (c.View, (Func<bool>)(() => c.IsConnected))));
            }

            if (_admin is { } admin)
            {
                views.Add((admin.View, () => admin.IsConnected));
            }

            var deadline = WallSeconds + _o.ConvergenceTimeout.TotalSeconds;
            var difference = string.Empty;
            while (true)
            {
                var server = host.Engine.GetState();
                var queue = host.Engine.GetQueue();
                var lagging = views.Where(v => v.Connected()).FirstOrDefault(v => !v.View.Matches(host.App.ServerEpoch, server, queue, out difference));
                if (lagging.View is null)
                {
                    return;
                }

                if (WallSeconds > deadline)
                {
                    _convergenceFailures++;
                    Violation("ClientDiverged", $"{why}: {lagging.View.Name} still differs after {_o.ConvergenceTimeout.TotalSeconds:F0} s: {difference}");
                    return;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ================================================================== bookkeeping

    private void OnLag(double milliseconds)
    {
        _windowLag.Enqueue(milliseconds);
        lock (_lagGate)
        {
            _allLag.Add(milliseconds);
        }
    }

    private void Violation(string kind, string detail)
    {
        var violation = new SoakViolation(kind, Stats.Round(SimSeconds, 1), $"{detail} [recent actions: {string.Join(", ", _recent)}]");
        _violations.Enqueue(violation);
        if (_violations.Count <= 50)
        {
            Say($"  VIOLATION {kind} at sim {violation.SimSeconds:F0} s: {violation.Detail}");
        }
    }

    private void Count(string name) => _events.AddOrUpdate(name, 1, (_, n) => n + 1);

    private void Say(string message) => _o.Log?.Invoke(message);

    private static async Task WaitUntilAsync(double seconds, Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    // ================================================================== report

    private SoakReport BuildReport(DateTimeOffset startedAt)
    {
        List<SoakSample> samples;
        lock (_samples)
        {
            samples = [.. _samples];
        }

        var warm = samples.Where(s => s.SimSeconds >= _o.WarmupFraction * _o.SimulatedSeconds).ToList();
        if (warm.Count < 2)
        {
            warm = samples;
        }

        GrowthSummary Growth(string metric, string unit, Func<SoakSample, double> value) => new(
            metric,
            unit,
            Stats.Round(warm.Count > 0 ? value(warm[0]) : double.NaN),
            Stats.Round(warm.Count > 0 ? value(warm[^1]) : double.NaN),
            Stats.Round(warm.Count > 0 ? warm.Max(value) : double.NaN),
            Stats.Round(Stats.Slope(warm.Select(s => (s.SimSeconds / 3600, value(s))).ToList())));

        double[] lags;
        lock (_lagGate)
        {
            lags = [.. _allLag];
        }

        var logs = _logs.Entries
            .GroupBy(l => (l.Level, l.Category, Message: Normalize(l.Message), l.ExceptionType))
            .Select(g => new LogGroup(g.Key.Level.ToString(), g.Key.Category, g.Key.Message, g.Key.ExceptionType, g.Count(), IsExpected(g.First())))
            .OrderBy(g => g.Expected).ThenByDescending(g => g.Count)
            .ToList();
        var violations = _violations.ToList();
        var measured = Interlocked.Read(ref _measuredFrames);
        return new SoakReport
        {
            Version = "1",
            StartedAt = startedAt,
            Machine = $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}",
            Options = new
            {
                _o.SimulatedHours,
                _o.Speed,
                _o.Seed,
                _o.TrackCount,
                _o.MinTrackSeconds,
                _o.MaxTrackSeconds,
                _o.PipeClients,
                _o.RemoteClients,
                _o.RestartAtFraction,
                _o.SampleIntervalSimSeconds,
                _o.MeanActionIntervalSimSeconds,
                _o.WarmupFraction,
            },
            TargetSimHours = _o.SimulatedHours,
            AchievedSimHours = Stats.Round(SimSeconds / 3600),
            WallMinutes = Stats.Round(WallSeconds / 60, 2),
            SetupSeconds = Stats.Round(_setupSeconds, 1),
            EndedEarly = _endedEarly,
            LibraryTracksAtStart = _libraryAtStart,
            AnalysesReadyAtStart = _readyAtStart,
            Growth =
            [
                Growth("Managed heap (after full GC)", "MB", s => s.ManagedHeapMb),
                Growth("Working set", "MB", s => s.WorkingSetMb),
                Growth("OS threads", "threads", s => s.Threads),
                Growth("Open handles / FDs", "handles", s => s.Handles),
                Growth("Thread-pool threads", "threads", s => s.ThreadPoolThreads),
                Growth("Event subscribers", "subscribers", s => s.EventSubscribers),
            ],
            Violations = violations,
            ViolationCounts = violations.GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.Count()),
            Actions = _actions.OrderBy(a => a.Key)
                .Select(a => new ActionStat(a.Key, a.Value.GetValueOrDefault("ok"),
                    a.Value.Where(kv => kv.Key != "ok").ToDictionary(kv => kv.Key, kv => kv.Value)))
                .ToList(),
            Events = new SortedDictionary<string, int>(_events),
            Restarts = _restarts,
            ConvergenceChecks = _convergenceChecks,
            ConvergenceFailures = _convergenceFailures,
            EventLagP50Ms = Stats.Round(Stats.Percentile(lags, 50)),
            EventLagP95Ms = Stats.Round(Stats.Percentile(lags, 95)),
            EventLagP99Ms = Stats.Round(Stats.Percentile(lags, 99)),
            EventLagMaxMs = Stats.Round(lags.Length == 0 ? double.NaN : lags.Max()),
            EventsReceived = lags.Length,
            EstimatedUnderrunFrames = Interlocked.Read(ref _underrunFrames),
            EstimatedUnderrunPercent = Stats.Round(measured == 0 ? 0 : 100.0 * Interlocked.Read(ref _underrunFrames) / measured, 4),
            MixerDroppedEvents = samples.Count == 0 ? 0 : samples.Max(s => s.MixerDroppedEvents),
            StateTransitions = _stateTransitions,
            PlayingFraction = Stats.Round(_monitorTicks == 0 ? 0 : _playingMonitorTicks / (double)_monitorTicks),
            Logs = logs,
            Samples = samples,
        };
    }

    /// <summary>Warnings the workload provokes on purpose (planted corrupt/truncated files, deleted tracks, junk connections).</summary>
    private static bool IsExpected(CapturedLog log) =>
        log.Level < LogLevel.Warning
        || (log.Category.EndsWith("EnginePlaybackBackend", StringComparison.Ordinal) && log.Message.StartsWith("Playback problem for track", StringComparison.Ordinal))
        || (log.Category.EndsWith("AnalysisWorker", StringComparison.Ordinal) && log.Message.StartsWith("Analysis of track", StringComparison.Ordinal));

    /// <summary>Groups log messages that differ only in ids and numbers.</summary>
    private static string Normalize(string message)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(message, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<id>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\d+(\.\d+)?", "<n>");
        return text.Length > 240 ? text[..240] : text;
    }

    private sealed record LibraryTrack(Guid Id, double Seconds, SoakTrackKind Kind);

    private sealed class SoakCallerIdentity : ICallerIdentity
    {
        public PipeCallerInfo Describe(NamedPipeServerStream pipe) => new("SOAK\\operator", true);
    }
}
