using System.Globalization;
using System.Text;
using SabhaMujic.Contracts;

namespace SabhaMujic.Data.Storage;

public sealed record UploadStatus(Guid UploadId, string FileName, long BytesReceived, long TotalBytes);

/// <summary>
/// A finished upload handed to the importer. Owns its temp file: disposing deletes it.
/// <see cref="FileName"/> is sanitized metadata only and must never be used as a path.
/// </summary>
public sealed class CompletedUpload : IDisposable
{
    internal CompletedUpload(Guid uploadId, string fileName, string extension, long length, string tempPath)
    {
        UploadId = uploadId;
        FileName = fileName;
        Extension = extension;
        Length = length;
        TempPath = tempPath;
    }

    public Guid UploadId { get; }

    public string FileName { get; }

    /// <summary>Lowercase extension with the dot, already checked against <see cref="AdminLimits.AllowedExtensions"/>.</summary>
    public string Extension { get; }

    public long Length { get; }

    /// <summary>Service-internal path of the staged bytes (e.g. for decode validation).</summary>
    public string TempPath { get; }

    public Stream OpenRead() =>
        new FileStream(TempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Dispose()
    {
        try
        {
            File.Delete(TempPath);
        }
        catch (IOException)
        {
            // Still open elsewhere; UploadStaging deletes leftovers at the next start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Server-side state of chunked admin uploads (see <see cref="IAdminApi.BeginImportAsync"/>). Enforces
/// <see cref="AdminLimits"/>: allowed extensions, total size, chunk size and concurrent uploads per session; chunks must
/// arrive in order and only from the session that began the upload. Abandoned uploads expire after an idle timeout.
/// Thread-safe. Construction deletes leftover files from an earlier run, so use one instance per directory.
/// </summary>
public sealed class UploadStaging
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);
    public const int DefaultMaxConcurrentUploads = 8;
    public const int MaxFileNameLength = 200;
    private const string PartSuffix = ".part";

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Upload> _uploads = [];
    private readonly TimeProvider _clock;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxConcurrentUploads;

    public UploadStaging(string stagingDirectory, IStoreAcl acl, TimeProvider? clock = null, TimeSpan? idleTimeout = null,
        int maxConcurrentUploads = DefaultMaxConcurrentUploads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentUploads, 1);
        StagingDirectory = Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(StagingDirectory);
        acl.Secure(StagingDirectory);
        _clock = clock ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _maxConcurrentUploads = maxConcurrentUploads;

        foreach (var leftover in Directory.EnumerateFiles(StagingDirectory))
        {
            TryDelete(leftover);
        }
    }

    public string StagingDirectory { get; }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _uploads.Count;
            }
        }
    }

    /// <summary>
    /// Starts an upload. UnsupportedFormat for a disallowed extension, InvalidArgument for a bad name/size/owner,
    /// TooLarge above <see cref="AdminLimits.MaxUploadBytes"/>, RateLimited when too many uploads are open.
    /// </summary>
    public Guid Begin(string fileName, long totalBytes, string ownerSessionId)
    {
        RequireOwner(ownerSessionId);
        var (name, extension) = SanitizeFileName(fileName);
        if (totalBytes <= 0)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "The upload size must be positive.");
        }

        if (totalBytes > AdminLimits.MaxUploadBytes)
        {
            throw new SabhaMujicException(ErrorCode.TooLarge, $"Uploads are limited to {AdminLimits.MaxUploadBytes} bytes.");
        }

        ExpireAbandoned();
        lock (_gate)
        {
            var mine = _uploads.Values.Count(u => string.Equals(u.Owner, ownerSessionId, StringComparison.Ordinal));
            if (mine >= AdminLimits.MaxConcurrentUploadsPerSession)
            {
                throw new SabhaMujicException(ErrorCode.RateLimited,
                    $"At most {AdminLimits.MaxConcurrentUploadsPerSession} uploads can be in progress per session.");
            }

            if (_uploads.Count >= _maxConcurrentUploads)
            {
                throw new SabhaMujicException(ErrorCode.RateLimited, "Too many uploads are in progress; try again later.");
            }

            var id = Guid.NewGuid();
            var path = Path.Combine(StagingDirectory, id.ToString("N") + PartSuffix);
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            _uploads.Add(id, new Upload(id, ownerSessionId, name, extension, totalBytes, path, _clock.GetUtcNow()));
            return id;
        }
    }

    /// <summary>
    /// Appends a chunk. NotFound for an unknown/expired upload, Forbidden for another session's upload, InvalidArgument
    /// when <paramref name="offset"/> is not exactly the bytes received so far or the chunk is empty, TooLarge when the
    /// chunk exceeds <see cref="AdminLimits.MaxChunkBytes"/> or would pass the declared total.
    /// </summary>
    public async Task<UploadStatus> AppendChunkAsync(Guid uploadId, string ownerSessionId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var upload = GetOwned(uploadId, ownerSessionId);
        if (data.Length == 0)
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "A chunk must not be empty.");
        }

        if (data.Length > AdminLimits.MaxChunkBytes)
        {
            throw new SabhaMujicException(ErrorCode.TooLarge, $"Chunks are limited to {AdminLimits.MaxChunkBytes} bytes.");
        }

        await upload.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (upload.Closed)
            {
                throw UnknownUpload();
            }

            if (offset != upload.Received)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument,
                    string.Create(CultureInfo.InvariantCulture, $"Expected a chunk at offset {upload.Received}, got {offset}."));
            }

            if (data.Length > upload.Total - upload.Received)
            {
                throw new SabhaMujicException(ErrorCode.TooLarge, "The chunk would exceed the declared upload size.");
            }

            await using (var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                // Drop anything a previously failed write may have left past the accepted bytes.
                file.SetLength(upload.Received);
                file.Seek(upload.Received, SeekOrigin.Begin);
                await file.WriteAsync(data, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            upload.Received += data.Length;
            upload.LastActivity = _clock.GetUtcNow();
            return upload.Status();
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    public UploadStatus GetStatus(Guid uploadId, string ownerSessionId) => GetOwned(uploadId, ownerSessionId).Status();

    /// <summary>
    /// Finishes the upload once every declared byte has arrived (InvalidArgument otherwise; the upload stays open).
    /// The caller owns the returned <see cref="CompletedUpload"/> and must dispose it.
    /// </summary>
    public CompletedUpload Complete(Guid uploadId, string ownerSessionId)
    {
        var upload = GetOwned(uploadId, ownerSessionId);
        upload.Gate.Wait();
        try
        {
            if (upload.Closed)
            {
                throw UnknownUpload();
            }

            if (upload.Received != upload.Total)
            {
                throw new SabhaMujicException(ErrorCode.InvalidArgument,
                    string.Create(CultureInfo.InvariantCulture, $"The upload is incomplete: {upload.Received} of {upload.Total} bytes received."));
            }

            var length = new FileInfo(upload.Path).Length;
            if (length != upload.Total)
            {
                throw new SabhaMujicException(ErrorCode.Internal, "The staged upload does not have the expected size.");
            }

            Close(upload, deleteFile: false);
            return new CompletedUpload(upload.Id, upload.FileName, upload.Extension, length, upload.Path);
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    /// <summary>Cancels the upload and deletes its bytes. NotFound / Forbidden as for <see cref="AppendChunkAsync"/>.</summary>
    public void Cancel(Guid uploadId, string ownerSessionId)
    {
        var upload = GetOwned(uploadId, ownerSessionId);
        upload.Gate.Wait();
        try
        {
            if (!upload.Closed)
            {
                Close(upload, deleteFile: true);
            }
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    /// <summary>Cancels every upload of a session (e.g. on logout). Returns the number cancelled.</summary>
    public int CancelAllForOwner(string ownerSessionId)
    {
        List<Upload> owned;
        lock (_gate)
        {
            owned = _uploads.Values.Where(u => string.Equals(u.Owner, ownerSessionId, StringComparison.Ordinal)).ToList();
        }

        var cancelled = 0;
        foreach (var upload in owned)
        {
            upload.Gate.Wait();
            try
            {
                if (!upload.Closed)
                {
                    Close(upload, deleteFile: true);
                    cancelled++;
                }
            }
            finally
            {
                upload.Gate.Release();
            }
        }

        return cancelled;
    }

    /// <summary>Removes uploads idle for longer than the timeout. Also runs on every <see cref="Begin"/>. Returns the number removed.</summary>
    public int ExpireAbandoned()
    {
        var cutoff = _clock.GetUtcNow() - _idleTimeout;
        List<Upload> stale;
        lock (_gate)
        {
            stale = _uploads.Values.Where(u => u.LastActivity < cutoff).ToList();
        }

        var removed = 0;
        foreach (var upload in stale)
        {
            // Skip uploads with a chunk in flight; they are clearly not abandoned.
            if (!upload.Gate.Wait(0))
            {
                continue;
            }

            try
            {
                if (!upload.Closed && upload.LastActivity < cutoff)
                {
                    Close(upload, deleteFile: true);
                    removed++;
                }
            }
            finally
            {
                upload.Gate.Release();
            }
        }

        return removed;
    }

    /// <summary>
    /// Reduces a client-supplied file name to a safe display name: last path segment only, no control/format/reserved
    /// characters, bounded length. Throws UnsupportedFormat when the extension is not allowed.
    /// </summary>
    internal static (string Name, string Extension) SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "A file name is required.");
        }

        var lastSegment = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        var builder = new StringBuilder(lastSegment.Length);
        foreach (var c in lastSegment)
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) || category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                || "<>:\"|?*".Contains(c))
            {
                continue;
            }

            builder.Append(c);
        }

        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
        {
            throw new SabhaMujicException(ErrorCode.UnsupportedFormat, "The file has no extension.");
        }

        extension = ProtectedStore.NormalizeExtension(extension);
        var stem = name[..^extension.Length].Trim();
        if (stem.Length == 0)
        {
            stem = "upload";
        }

        if (stem.Length + extension.Length > MaxFileNameLength)
        {
            stem = stem[..(MaxFileNameLength - extension.Length)];
            if (char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }
        }

        return (stem + extension, extension);
    }

    private Upload GetOwned(Guid uploadId, string ownerSessionId)
    {
        RequireOwner(ownerSessionId);
        Upload? upload;
        lock (_gate)
        {
            _uploads.TryGetValue(uploadId, out upload);
        }

        if (upload is null)
        {
            throw UnknownUpload();
        }

        if (!string.Equals(upload.Owner, ownerSessionId, StringComparison.Ordinal))
        {
            throw new SabhaMujicException(ErrorCode.Forbidden, "This upload belongs to another session.");
        }

        if (upload.LastActivity < _clock.GetUtcNow() - _idleTimeout)
        {
            ExpireAbandoned();
            throw UnknownUpload();
        }

        return upload;
    }

    /// <summary>Caller holds <c>upload.Gate</c>.</summary>
    private void Close(Upload upload, bool deleteFile)
    {
        upload.Closed = true;
        lock (_gate)
        {
            _uploads.Remove(upload.Id);
        }

        if (deleteFile)
        {
            TryDelete(upload.Path);
        }
    }

    private static void RequireOwner(string ownerSessionId)
    {
        if (string.IsNullOrEmpty(ownerSessionId))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "An owning session is required.");
        }
    }

    private static SabhaMujicException UnknownUpload() => new(ErrorCode.NotFound, "The upload does not exist or has expired.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Upload(Guid id, string owner, string fileName, string extension, long total, string path, DateTimeOffset now)
    {
        public Guid Id { get; } = id;

        public string Owner { get; } = owner;

        public string FileName { get; } = fileName;

        public string Extension { get; } = extension;

        public long Total { get; } = total;

        public string Path { get; } = path;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long Received { get; set; }

        private long _lastActivityTicks = now.UtcTicks;

        /// <summary>Read without the gate by expiry checks, so stored atomically.</summary>
        public DateTimeOffset LastActivity
        {
            get => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastActivityTicks, value.UtcTicks);
        }

        public bool Closed { get; set; }

        public UploadStatus Status() => new(Id, FileName, Received, Total);
    }
}
