using Microsoft.Data.Sqlite;

namespace SabhaMujic.Data;

/// <summary>Recognises transient, environmental storage failures: the database busy or locked, or the disk full.</summary>
public static class StorageErrors
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteFull = 13;

    private const int UnixNoSpace = 28; // ENOSPC
    private const int WindowsDiskFull = unchecked((int)0x80070070); // ERROR_DISK_FULL
    private const int WindowsHandleDiskFull = unchecked((int)0x80070027); // ERROR_HANDLE_DISK_FULL

    /// <summary>True for SQLITE_BUSY (5), SQLITE_LOCKED (6), SQLITE_FULL (13) and "no space left" I/O errors, also when wrapped.</summary>
    public static bool IsBusyOrFull(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case SqliteException sqlite when sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked or SqliteFull:
                    return true;
                case IOException io when IsDiskFull(io):
                    return true;
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    return IsBusyOrFull(aggregate.InnerExceptions[0]);
            }
        }

        return false;
    }

    private static bool IsDiskFull(IOException io) =>
        OperatingSystem.IsWindows() ? io.HResult is WindowsDiskFull or WindowsHandleDiskFull : io.HResult == UnixNoSpace;
}
