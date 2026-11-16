using Microsoft.Extensions.Logging;
using SabhaMujic.Contracts;
using SabhaMujic.Core.Security;
using SabhaMujic.Data.Repositories;
using SabhaMujic.Data.Storage;

namespace SabhaMujic.Server.Application;

/// <summary>
/// Chunked admin import: upload into <see cref="UploadStaging"/>, then on completion decode-validate with
/// <see cref="IAudioProbe"/>, move into the <see cref="ProtectedStore"/>, add the track (duplicates by content hash are
/// reported, not re-added) and queue it for analysis. Progress uses <see cref="ImportStages"/>; a duplicate ends in Done. Uploads are owned by the session that began them.
/// </summary>
public sealed class ImportService
{
    private readonly UploadStaging _staging;
    private readonly ProtectedStore _store;
    private readonly ITrackRepository _tracks;
    private readonly ICategoryRepository _categories;
    private readonly ITrackAnalysisRepository _analysis;
    private readonly IAudioProbe _probe;
    private readonly IAnalysisQueue _analysisQueue;
    private readonly EventBroadcaster _events;
    private readonly AuditWriter _audit;
    private readonly ILogger _logger;

    public ImportService(UploadStaging staging, ProtectedStore store, ITrackRepository tracks, ICategoryRepository categories,
        ITrackAnalysisRepository analysis, IAudioProbe probe, IAnalysisQueue analysisQueue, EventBroadcaster events, AuditWriter audit,
        ILogger<ImportService> logger)
    {
        _staging = staging;
        _store = store;
        _tracks = tracks;
        _categories = categories;
        _analysis = analysis;
        _probe = probe;
        _analysisQueue = analysisQueue;
        _events = events;
        _audit = audit;
        _logger = logger;
    }

    public Task<Guid> BeginImportAsync(CallerContext caller, string fileName, long totalBytes, CancellationToken ct)
    {
        var id = _staging.Begin(fileName, totalBytes, Owner(caller));
        var status = _staging.GetStatus(id, Owner(caller));
        Progress(caller, status, ImportStages.Uploading);
        return Task.FromResult(id);
    }

    public async Task UploadChunkAsync(CallerContext caller, Guid uploadId, long offset, byte[] data, CancellationToken ct)
    {
        Errors.RequireNotNull(data, "data");
        var status = await _staging.AppendChunkAsync(uploadId, Owner(caller), offset, data, ct).ConfigureAwait(false);
        Progress(caller, status, ImportStages.Uploading);
    }

    public Task CancelImportAsync(CallerContext caller, Guid uploadId, CancellationToken ct)
    {
        _staging.Cancel(uploadId, Owner(caller));
        return Task.CompletedTask;
    }

    public async Task<ImportResultDto> CompleteImportAsync(CallerContext caller, Guid uploadId, ImportMetadata metadata, CancellationToken ct)
    {
        Errors.RequireNotNull(metadata, "metadata");
        // Check the category before taking the upload, so a typo does not throw the uploaded bytes away.
        if (metadata.CategoryId is { } categoryId && await _categories.GetAsync(categoryId, ct).ConfigureAwait(false) is null)
        {
            throw Errors.CategoryNotFound();
        }

        using var upload = _staging.Complete(uploadId, Owner(caller));
        var status = new UploadStatus(uploadId, upload.FileName, upload.Length, upload.Length);
        try
        {
            Progress(caller, status, ImportStages.Validating);
            var probe = await _probe.ProbeAsync(upload.TempPath, ct).ConfigureAwait(false);
            if (!probe.IsSupported || !double.IsFinite(probe.DurationSeconds) || probe.DurationSeconds <= 0)
            {
                throw new SabhaMujicException(ErrorCode.UnsupportedFormat,
                    $"'{upload.FileName}' could not be read as audio{(probe.Error is null ? "." : ": " + probe.Error)}");
            }

            Progress(caller, status, ImportStages.Storing);
            StoredFile stored;
            await using (var content = upload.OpenRead())
            {
                stored = await _store.StoreAsync(content, upload.Extension, ct).ConfigureAwait(false);
            }

            var title = string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileNameWithoutExtension(upload.FileName) : metadata.Title;
            TrackAddResult added;
            try
            {
                added = await _tracks.AddAsync(new NewTrack(stored.ContentHash, stored.FileKey, title, metadata.Artist, metadata.CategoryId,
                    probe.DurationSeconds, stored.SizeBytes, upload.FileName, caller.UserId), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await DeleteIfNewAsync(stored).ConfigureAwait(false);
                throw;
            }

            if (added.WasDuplicate)
            {
                if (added.FileKey != stored.FileKey)
                {
                    // Same audio already in the library under another extension: drop the extra copy.
                    await DeleteIfNewAsync(stored).ConfigureAwait(false);
                }

                Progress(caller, status, ImportStages.Done);
                await _audit.WriteAsync(caller, "import.duplicate", "track:" + added.Track.Id, upload.FileName).ConfigureAwait(false);
                return new ImportResultDto(added.Track.Id, WasDuplicate: true);
            }

            try
            {
                await _analysis.UpsertAsync(new TrackAnalysis(added.Track.Id, AnalysisStatus.Pending, 0), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: the track and its file are in the library now, so the import must not report Failed (a
                // retry would only say "duplicate"). The worker below stores the analysis status itself, and the next
                // service start re-queues any track without a Ready analysis.
                _logger.LogWarning(ex, "Could not mark the analysis of imported track {TrackId} as pending; it is analysed anyway.", added.Track.Id);
            }

            // The track is in the library now; the analyzer reports "done" (or "failed") when its analysis finishes.
            Progress(caller, status, ImportStages.Analyzing);
            _analysisQueue.Enqueue(added.Track.Id, new ImportProgressTarget(Owner(caller), uploadId, upload.FileName, upload.Length));
            _events.PublishLibraryChanged(new LibraryChangedDto(LibraryChangeKind.TrackAdded, added.Track.Id));
            await _audit.WriteAsync(caller, "import.complete", "track:" + added.Track.Id, upload.FileName).ConfigureAwait(false);
            return new ImportResultDto(added.Track.Id, WasDuplicate: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Progress(caller, status, ImportStages.Failed);
            await _audit.WriteAsync(caller, "import.failed", null, upload.FileName).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DeleteIfNewAsync(StoredFile stored)
    {
        if (stored.AlreadyExisted)
        {
            return;
        }

        try
        {
            await _store.DeleteAsync(stored.FileKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove unused stored file {FileKey}.", stored.FileKey);
        }
    }

    private void Progress(CallerContext caller, UploadStatus status, string stage) =>
        _events.PublishImportProgress(Owner(caller),
            new ImportProgressDto(status.UploadId, status.FileName, status.BytesReceived, status.TotalBytes, stage));

    private static string Owner(CallerContext caller) =>
        caller.SessionId ?? throw new SabhaMujicException(ErrorCode.Unauthenticated, "Sign in is required.");
}
