namespace SabhaMujic.Server.Hosting;

/// <summary>The data root (or one of its folders) is unsafe to use; the service must not start.</summary>
public sealed class DataRootException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Which folder of the data root is being prepared.</summary>
public enum DataFolder
{
    Data,
    Library,
    Uploads,
    Certs,
    Logs,
}

/// <summary>
/// Prepares the data root before the service writes anything: every folder is created as a real directory and a
/// junction/symlink (reparse point) in place of the root or a folder is refused, never followed, because a standard
/// user who could create it would redirect protected data. The base class only does that (Linux, dev hosts);
/// the Windows service subclass also takes ownership and applies the DACLs.
/// </summary>
public class DataRootGuard
{
    /// <summary>The folders under the root, in preparation order.</summary>
    public static IReadOnlyList<(DataFolder Kind, Func<SabhaDataPaths, string> Path)> Folders { get; } =
    [
        (DataFolder.Data, p => p.Data),
        (DataFolder.Library, p => p.Library),
        (DataFolder.Uploads, p => p.Uploads),
        (DataFolder.Certs, p => p.Certs),
        (DataFolder.Logs, p => p.Logs),
    ];

    /// <summary>Creates and secures the root first (so nobody else can add or swap folders in it), then each folder.</summary>
    public void Prepare(SabhaDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnsureRealDirectory(paths.Root, "data root");
        SecureRoot(paths.Root);
        EnsureNotReparsePoint(paths.Root, "data root"); // re-check: nothing may have been swapped before the root was locked
        foreach (var (kind, path) in Folders)
        {
            var folder = path(paths);
            EnsureRealDirectory(folder, kind.ToString().ToLowerInvariant() + " folder");
            SecureFolder(folder, kind);
            EnsureNotReparsePoint(folder, kind.ToString().ToLowerInvariant() + " folder");
        }
    }

    /// <summary>Platform hook: owner + DACL of the root. The base does nothing.</summary>
    protected virtual void SecureRoot(string root)
    {
    }

    /// <summary>Platform hook: owner + DACL of a folder. The base does nothing.</summary>
    protected virtual void SecureFolder(string folder, DataFolder kind)
    {
    }

    /// <summary>Creates <paramref name="path"/> if needed; throws <see cref="DataRootException"/> if it is a reparse point or a file.</summary>
    public static void EnsureRealDirectory(string path, string what)
    {
        EnsureNotReparsePoint(path, what);
        if (File.Exists(path))
        {
            throw new DataRootException($"The SabhaMujic {what} '{path}' is a file, not a folder.");
        }

        Directory.CreateDirectory(path);
        EnsureNotReparsePoint(path, what);
    }

    /// <summary>Throws if <paramref name="path"/> exists as a junction or symbolic link (checked without following it).</summary>
    public static void EnsureNotReparsePoint(string path, string what)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path); // attributes of the link itself, not its target
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new DataRootException(
                $"The SabhaMujic {what} '{path}' is a junction or symbolic link. SabhaMujic refuses to use it because it could redirect " +
                "protected data elsewhere. An administrator must remove the link (not its target) and restart the service.");
        }
    }
}
