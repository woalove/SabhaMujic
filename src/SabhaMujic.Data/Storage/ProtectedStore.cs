using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SabhaMujic.Contracts;

namespace SabhaMujic.Data.Storage;

/// <summary>A file in the <see cref="ProtectedStore"/>. <see cref="AlreadyExisted"/> is true when identical content was already stored.</summary>
public sealed record StoredFile(string FileKey, string ContentHash, long SizeBytes, bool AlreadyExisted);

/// <summary>
/// Content-addressed file store for protected audio. A file key is <c>&lt;sha256 lowercase hex&gt;.&lt;ext&gt;</c> and lives at
/// <c>&lt;root&gt;/&lt;first two hex chars&gt;/&lt;key&gt;</c>. Writes stream into <c>&lt;root&gt;/.staging</c> while hashing, then are
/// moved into place atomically (same volume), so a partially written file is never visible under a key.
/// The store's directory is secured through <see cref="IStoreAcl"/> when the store is created.
/// One instance per root per process: construction deletes leftover staging files from an earlier run.
/// </summary>
public sealed partial class ProtectedStore
{
    public const string StagingFolderName = ".staging";
    private const int BufferSize = 81920;

    public ProtectedStore(string rootDirectory, IStoreAcl acl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(acl);
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        CreateRealDirectory(RootDirectory); // checked before securing: applying an ACL to a link would change its target
        acl.Secure(RootDirectory);
        StagingDirectory = Path.Combine(RootDirectory, StagingFolderName);
        CreateRealDirectory(StagingDirectory);
        CleanupStaging();
    }

    public string RootDirectory { get; }

    public string StagingDirectory { get; }

    /// <summary>Strict file-key check: exactly 64 lowercase hex chars, a dot, and a 1–8 char lowercase alphanumeric extension.</summary>
    public static bool IsValidFileKey(string? fileKey) => fileKey is not null && FileKeyPattern().IsMatch(fileKey);

    /// <summary>
    /// Lowercases an extension (with or without the leading dot) and checks it against
    /// <see cref="AdminLimits.AllowedExtensions"/>. Throws UnsupportedFormat otherwise.
    /// </summary>
    public static string NormalizeExtension(string? extension)
    {
        var ext = (extension ?? "").Trim().ToLowerInvariant();
        if (ext.Length > 0 && ext[0] != '.')
        {
            ext = "." + ext;
        }

        if (!ExtensionPattern().IsMatch(ext) || !AdminLimits.AllowedExtensions.Contains(ext))
        {
            throw new SabhaMujicException(ErrorCode.UnsupportedFormat, $"Files of type '{extension}' are not supported.");
        }

        return ext;
    }

    /// <summary>
    /// Streams <paramref name="content"/> into the store. If identical content (same hash and extension) is already
    /// stored, the new copy is discarded and the existing key is returned. On any failure nothing is left in place.
    /// </summary>
    public async Task<StoredFile> StoreAsync(Stream content, string extension, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var ext = NormalizeExtension(extension);
        var tempPath = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash;
            long size = 0;
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        size += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }

            var key = hash + ext;
            var finalPath = PathForKey(key);
            CreateRealDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(finalPath))
            {
                File.Delete(tempPath);
                return new StoredFile(key, hash, size, AlreadyExisted: true);
            }

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Lost a race with a concurrent store of the same content.
                File.Delete(tempPath);
                return new StoredFile(key, hash, size, AlreadyExisted: true);
            }

            return new StoredFile(key, hash, size, AlreadyExisted: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Physical path for a key. For use inside the service only (decoding); never send it to clients.
    /// Throws InvalidArgument for anything that is not a well-formed key, so no path traversal is possible.
    /// </summary>
    public string GetPhysicalPath(string fileKey) => PathForKey(fileKey);

    public bool Exists(string fileKey) => File.Exists(PathForKey(fileKey));

    /// <summary>Deletes the file. Returns false when it did not exist.</summary>
    public Task<bool> DeleteAsync(string fileKey, CancellationToken ct = default)
    {
        var path = PathForKey(fileKey);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>Deletes everything in the staging folder (left behind by a crash). Returns the number of files removed.</summary>
    public int CleanupStaging()
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(StagingDirectory))
        {
            if (TryDelete(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private string PathForKey(string fileKey)
    {
        if (!IsValidFileKey(fileKey))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Invalid file key.");
        }

        var path = Path.GetFullPath(Path.Combine(RootDirectory, fileKey[..2], fileKey));
        // Defence in depth: the regex already rules out separators and dots, but never leave the root.
        if (!path.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SabhaMujicException(ErrorCode.InvalidArgument, "Invalid file key.");
        }

        return path;
    }

    /// <summary>
    /// Creates a folder of the store, refusing a junction/symlink in its place (checked without following it), so a link
    /// planted inside the store can never redirect where protected audio is written.
    /// </summary>
    private static void CreateRealDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The protected store folder '{Path.GetFileName(path)}' is a junction or symbolic link; refusing to use it.");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // \z (not $) so a trailing newline can never match.
    [GeneratedRegex(@"^[0-9a-f]{64}\.[a-z0-9]{1,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileKeyPattern();

    [GeneratedRegex(@"^\.[a-z0-9]{1,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();
}
