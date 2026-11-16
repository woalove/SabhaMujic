namespace SabhaMujic.Contracts;

public enum ErrorCode
{
    Internal = 0,
    /// <summary>No valid session (not logged in, expired, revoked device).</summary>
    Unauthenticated = 1,
    /// <summary>Authenticated but the role does not permit the operation.</summary>
    Forbidden = 2,
    NotFound = 3,
    InvalidArgument = 4,
    Conflict = 5,
    UnsupportedFormat = 6,
    TooLarge = 7,
    RateLimited = 8,
    IncompatibleClient = 9,
    /// <summary>Client-side only: the service is not reachable or the connection was lost.</summary>
    Unavailable = 10,
}

/// <summary>Fixed, safe messages the service sends with some errors (clients may show them as they are).</summary>
public static class ServiceMessages
{
    /// <summary>
    /// With <see cref="ErrorCode.Internal"/> when the database or disk was busy or full (SQLITE_BUSY, SQLITE_LOCKED,
    /// SQLITE_FULL, or no space left for an upload). Transient: the same request may succeed shortly.
    /// </summary>
    public const string StorageUnavailable = "The SabhaMujic service could not save data (storage busy or full). Try again shortly.";
}

/// <summary>
/// Error payload carried by RPC errors (JSON-RPC error.data) and HTTP problem responses.
/// </summary>
public sealed record ErrorInfo(ErrorCode Code, string Message);

/// <summary>Exception type used on both sides of the contract to represent an <see cref="ErrorInfo"/>.</summary>
public sealed class SabhaMujicException : Exception
{
    public SabhaMujicException(ErrorCode code, string message) : base(message) => Code = code;

    public ErrorCode Code { get; }

    public ErrorInfo ToErrorInfo() => new(Code, Message);
}
