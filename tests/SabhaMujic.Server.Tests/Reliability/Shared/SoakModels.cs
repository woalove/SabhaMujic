using SabhaMujic.Contracts;

namespace SabhaMujic.Reliability;

/// <summary>Configuration of one soak run. Times named "Sim" are simulated (audio) time; the rest is wall-clock time.</summary>
public sealed class SoakOptions
{
    public double SimulatedHours { get; init; } = 8;

    /// <summary>Real-time multiplier of the null output (simulated seconds per wall second).</summary>
    public double Speed { get; init; } = 50;

    public int Seed { get; init; } = 1;

    /// <summary>Where the JSON report goes (a <c>.md</c> summary is written next to it). Null: no files.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Data root parent; default: a fresh folder under the temp directory (deleted afterwards).</summary>
    public string? WorkDirectory { get; init; }

    public int TrackCount { get; init; } = 30;

    public double MinTrackSeconds { get; init; } = 20;

    public double MaxTrackSeconds { get; init; } = 240;

    /// <summary>Volunteer pipe clients kept connected (churned during the run); the admin client is extra.</summary>
    public int PipeClients { get; init; } = 4;

    /// <summary>Paired SignalR clients (0 disables the remote).</summary>
    public int RemoteClients { get; init; } = 2;

    /// <summary>Fraction of the run at which the host is restarted (outside (0,1): no restart).</summary>
    public double RestartAtFraction { get; init; } = 0.5;

    public double SampleIntervalSimSeconds { get; init; } = 60;

    /// <summary>Mean simulated time between workload actions.</summary>
    public double MeanActionIntervalSimSeconds { get; init; } = 20;

    /// <summary>Samples before this fraction of the run are excluded from growth slopes.</summary>
    public double WarmupFraction { get; init; } = 0.15;

    /// <summary>Client convergence is checked every this many samples.</summary>
    public int ConvergenceEverySamples { get; init; } = 10;

    public TimeSpan ConvergenceTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Loading/stall/stop detection thresholds (wall time).</summary>
    public TimeSpan StuckThreshold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A single client call that takes longer than this is a hang (watchdog).</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait for imported tracks to be analysed before the clock starts (bounded).</summary>
    public TimeSpan AnalysisWait { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Hard wall-clock cap; the run ends early (and says so) when it is reached.</summary>
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromMinutes(30);

    public Action<string>? Log { get; init; }

    public double SimulatedSeconds => SimulatedHours * 3600;
}

/// <summary>A broken invariant, at a simulated time.</summary>
public sealed record SoakViolation(string Kind, double SimSeconds, string Detail);

/// <summary>One per simulated minute.</summary>
public sealed record SoakSample(
    double SimSeconds,
    double WallSeconds,
    double ManagedHeapMb,
    double WorkingSetMb,
    int Threads,
    int Handles,
    int ThreadPoolThreads,
    PlaybackStatus Status,
    int QueueLength,
    int LibraryTracks,
    int PipeConnections,
    int RemoteConnections,
    int EventSubscribers,
    int AnalysisPending,
    double EventLagP50Ms,
    double EventLagP95Ms,
    double EventLagMaxMs,
    long EstimatedUnderrunFrames,
    long MixerDroppedEvents,
    int ActionsSinceLastSample);

public sealed record GrowthSummary(string Metric, string Unit, double First, double Last, double Max, double SlopePerSimHour);

public sealed record LogGroup(string Level, string Category, string Message, string? ExceptionType, int Count, bool Expected);

public sealed record ActionStat(string Action, int Ok, IReadOnlyDictionary<string, int> Failures);

public sealed record RestartResult(double SimSeconds, double StopSeconds, double StartSeconds, bool QueueRestored, bool SettingsRestored,
    PlaybackStatus StatusAfterRestore, double ClientsReconnectedSeconds, string Detail);

public sealed record SoakReport
{
    public required string Version { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string Machine { get; init; }

    public required object Options { get; init; }

    public required double TargetSimHours { get; init; }

    public required double AchievedSimHours { get; init; }

    public required double WallMinutes { get; init; }

    public required double SetupSeconds { get; init; }

    public required bool EndedEarly { get; init; }

    public required int LibraryTracksAtStart { get; init; }

    public required int AnalysesReadyAtStart { get; init; }

    public required IReadOnlyList<GrowthSummary> Growth { get; init; }

    public required IReadOnlyList<SoakViolation> Violations { get; init; }

    public required IReadOnlyDictionary<string, int> ViolationCounts { get; init; }

    public required IReadOnlyList<ActionStat> Actions { get; init; }

    public required IReadOnlyDictionary<string, int> Events { get; init; }

    public required IReadOnlyList<RestartResult> Restarts { get; init; }

    public required int ConvergenceChecks { get; init; }

    public required int ConvergenceFailures { get; init; }

    public required double EventLagP50Ms { get; init; }

    public required double EventLagP95Ms { get; init; }

    public required double EventLagP99Ms { get; init; }

    public required double EventLagMaxMs { get; init; }

    public required long EventsReceived { get; init; }

    public required long EstimatedUnderrunFrames { get; init; }

    public required double EstimatedUnderrunPercent { get; init; }

    public required long MixerDroppedEvents { get; init; }

    public required int StateTransitions { get; init; }

    public required double PlayingFraction { get; init; }

    public required IReadOnlyList<LogGroup> Logs { get; init; }

    public required IReadOnlyList<SoakSample> Samples { get; init; }

    public int UnexpectedLogCount => Logs.Where(l => !l.Expected).Sum(l => l.Count);
}
