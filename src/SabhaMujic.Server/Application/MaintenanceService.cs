using Microsoft.Extensions.Logging;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;

namespace SabhaMujic.Server.Application;

public sealed record MaintenanceReport(int SessionsPurged, int UploadsExpired);

/// <summary>Startup recovery and periodic clean-up (expired sessions, abandoned uploads).</summary>
public sealed class MaintenanceService(
    ISessionRepository sessions,
    ITrackAnalysisRepository analysis,
    UploadStaging uploads,
    IAnalysisQueue analysisQueue,
    TimeProvider clock,
    ILogger<MaintenanceService> logger)
{
    private const int MaxRequeueAtStartup = 100_000;

    /// <summary>
    /// Call once at service start: analysis interrupted by a crash goes back to Pending, and every track that needs
    /// (re)analysis for the current analyzer version is queued. Then runs one clean-up pass.
    /// </summary>
    public async Task StartupAsync(CancellationToken ct = default)
    {
        var reset = await analysis.ResetInterruptedAsync(ct).ConfigureAwait(false);
        var pending = await analysis.ListTracksNeedingAnalysisAsync(analysisQueue.AnalysisVersion, MaxRequeueAtStartup, ct).ConfigureAwait(false);
        foreach (var trackId in pending)
        {
            analysisQueue.Enqueue(trackId);
        }

        logger.LogInformation("Startup: {Reset} interrupted analyses reset, {Queued} tracks queued for analysis.", reset, pending.Count);
        await RunOnceAsync(ct).ConfigureAwait(false);
    }

    public async Task<MaintenanceReport> RunOnceAsync(CancellationToken ct = default)
    {
        var report = new MaintenanceReport(
            await sessions.PurgeExpiredAsync(ct).ConfigureAwait(false),
            uploads.ExpireAbandoned());
        if (report != new MaintenanceReport(0, 0))
        {
            logger.LogInformation("Maintenance: {Report}.", report);
        }

        return report;
    }

    /// <summary>Runs <see cref="RunOnceAsync"/> every <paramref name="interval"/> until cancelled. Failures are logged, not fatal.</summary>
    public async Task RunPeriodicallyAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Maintenance pass failed.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
