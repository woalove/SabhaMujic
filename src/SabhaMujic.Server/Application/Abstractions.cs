using System.Reflection;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;

namespace SabhaMujic.Server.Application;

/// <summary>
/// The playback engine as seen by the application layer. M3b adapts the Audio engine to this interface.
/// Commands mirror <see cref="IPlaybackApi"/>; arguments are already validated (track ids exist, ranges checked).
/// The engine owns queue/settings persistence and must keep playing when clients disconnect.
/// Events may be raised on any thread and must not block.
/// </summary>
public interface IPlaybackBackend
{
    PlaybackStateDto GetState();

    QueueDto GetQueue();

    event Action<PlaybackStateDto>? StateChanged;

    event Action<QueueDto>? QueueChanged;

    Task PlayTrackAsync(Guid trackId, CancellationToken ct);

    /// <summary>NotFound when the queue item does not exist.</summary>
    Task PlayQueueItemAsync(Guid queueItemId, CancellationToken ct);

    Task PauseAsync(CancellationToken ct);

    Task ResumeAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    Task NextAsync(CancellationToken ct);

    Task PreviousAsync(CancellationToken ct);

    Task SeekAsync(double positionSeconds, CancellationToken ct);

    Task SetVolumeAsync(float volume, CancellationToken ct);

    Task SetMutedAsync(bool muted, CancellationToken ct);

    Task SetLoopModeAsync(LoopMode mode, CancellationToken ct);

    Task SetCrossfadeAsync(double seconds, CancellationToken ct);

    Task<QueueDto> EnqueueAsync(IReadOnlyList<Guid> trackIds, bool playNext, CancellationToken ct);

    Task<QueueDto> ReplaceQueueAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct);

    Task<QueueDto> RemoveFromQueueAsync(Guid queueItemId, CancellationToken ct);

    Task<QueueDto> MoveQueueItemAsync(Guid queueItemId, int newIndex, CancellationToken ct);

    Task<QueueDto> ClearQueueAsync(CancellationToken ct);

    /// <summary>
    /// A track was updated (metadata, loop override) or removed from the library. On removal the engine drops it from the
    /// queue (stopping it if it is playing) and releases the file before the caller deletes it.
    /// </summary>
    Task OnTrackChangedAsync(Guid trackId, LibraryChangeKind kind, CancellationToken ct);
}

/// <summary>Result of probing an uploaded file by actually decoding it.</summary>
public sealed record AudioProbeResult(bool IsSupported, double DurationSeconds, string? Error)
{
    public static AudioProbeResult Supported(double durationSeconds) => new(true, durationSeconds, null);

    public static AudioProbeResult Unsupported(string error) => new(false, 0, error);
}

/// <summary>Validates an audio file by decoding it (M3b: MediaFoundation on Windows). Must not modify the file.</summary>
public interface IAudioProbe
{
    Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct = default);
}

/// <summary>Background analysis (silence trim + loop detection). M3b supplies the real analyzer.</summary>
public interface IAnalysisQueue
{
    /// <summary>Version of the analysis algorithm; tracks analysed with an older version are re-queued at startup.</summary>
    int AnalysisVersion { get; }

    /// <summary>
    /// Queues a track for analysis. Must not block; duplicates may be ignored. With <paramref name="progress"/> the
    /// analyzer reports <see cref="ImportStages.Analyzing"/> / <see cref="ImportStages.Done"/> (or Failed) to the
    /// importing session.
    /// </summary>
    void Enqueue(Guid trackId, ImportProgressTarget? progress = null);
}

/// <summary>Where to send import progress for an analysis that belongs to an upload.</summary>
public sealed record ImportProgressTarget(string SessionId, Guid UploadId, string FileName, long TotalBytes);

/// <summary>The addresses the desktop's "Set up a phone" panel shows (see <see cref="PhoneSetupInfoDto"/>).</summary>
public interface IRemoteUrlProvider
{
    /// <summary>The plain-HTTP setup page (certificate profile and instructions).</summary>
    string GetBootstrapUrl();

    /// <summary>The remote's HTTPS address (sign-in page).</summary>
    string GetRemoteUrl();

    /// <summary>Fingerprint of the local CA phones must trust (see <see cref="PhoneSetupInfoDto.CaFingerprint"/>); null without a remote.</summary>
    string? CaFingerprint => null;

    /// <summary>True while the remote is listening.</summary>
    bool IsRemoteRunning => false;
}

/// <summary>Tunables for the application layer. Defaults are the production values.</summary>
public sealed class ServerOptions
{
    /// <summary>
    /// Desktop sign-ins (administrators): an idle timeout. Sliding: each recorded activity (at most one write per
    /// <see cref="SessionTouchInterval"/>) moves the end to now + this. The pipe ends the session when it passes.
    /// </summary>
    public TimeSpan DesktopSessionLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Remote sign-ins without "Remember this phone": fixed, counted from the sign-in.</summary>
    public TimeSpan RemoteSessionLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Remembered phones: sliding, until revoked or unused this long.</summary>
    public TimeSpan DeviceSessionLifetime { get; init; } = TimeSpan.FromDays(180);

    /// <summary>Minimum time between last-seen/expiry updates of a session (limits DB writes).</summary>
    public TimeSpan SessionTouchInterval { get; init; } = TimeSpan.FromMinutes(1);

    public int LoginMaxFailuresPerUser { get; init; } = 5;

    public int LoginMaxFailuresGlobal { get; init; } = 50;

    public TimeSpan LoginFailureWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>PBKDF2 iterations for new secrets (tests lower it).</summary>
    public int SecretHashIterations { get; init; } = SecretHasher.DefaultIterations;

    /// <summary>Minimum password length (administrator passwords).</summary>
    public int MinSecretLength { get; init; } = 8;

    public int MaxSecretLength { get; init; } = 256;

    public string ServerVersion { get; init; } =
        typeof(ServerOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public string MachineName { get; init; } = Environment.MachineName;
}
