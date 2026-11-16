using System.Collections.Frozen;
using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Security;

/// <summary>
/// The single role matrix for SabhaMujic. Every pipe RPC method and HTTP endpoint must call
/// <see cref="Demand"/> with its <see cref="Operation"/> before doing any work.
/// </summary>
public static class Authorizer
{
    private static readonly FrozenDictionary<Operation, AccessLevel> Matrix = new Dictionary<Operation, AccessLevel>
    {
        [Operation.Hello] = AccessLevel.Anonymous,
        [Operation.Login] = AccessLevel.Anonymous,
        [Operation.ResumeSession] = AccessLevel.Anonymous,
        [Operation.Pair] = AccessLevel.Anonymous,
        [Operation.InitialSetup] = AccessLevel.Anonymous,
        [Operation.LocalRecovery] = AccessLevel.Anonymous,

        [Operation.Logout] = AccessLevel.Authenticated,
        [Operation.BrowseLibrary] = AccessLevel.Authenticated,
        [Operation.ReadPlayback] = AccessLevel.Authenticated,
        [Operation.ControlPlayback] = AccessLevel.Authenticated,
        [Operation.EditQueue] = AccessLevel.Authenticated,
        [Operation.ReadPlaylists] = AccessLevel.Authenticated,
        [Operation.EditPlaylists] = AccessLevel.Authenticated,

        [Operation.ImportTracks] = AccessLevel.Admin,
        [Operation.ManageCategories] = AccessLevel.Admin,
        [Operation.ManageTracks] = AccessLevel.Admin,
        [Operation.ManageLoopPoints] = AccessLevel.Admin,
        [Operation.ManageUsers] = AccessLevel.Admin,
        [Operation.ManageDevices] = AccessLevel.Admin,
    }.ToFrozenDictionary();

    public static AccessLevel RequiredLevel(Operation operation) =>
        Matrix.TryGetValue(operation, out var level)
            ? level
            : throw new InvalidOperationException($"Operation {operation} has no entry in the authorization matrix.");

    public static bool IsAllowed(CallerContext caller, Operation operation) => Check(caller, operation) is null;

    /// <summary>Throws <see cref="SabhaMujicException"/> (Unauthenticated / Forbidden) when not permitted.</summary>
    public static void Demand(CallerContext caller, Operation operation)
    {
        if (Check(caller, operation) is { } error)
        {
            throw new SabhaMujicException(error, error == ErrorCode.Unauthenticated
                ? "Sign in is required."
                : "This action requires an administrator.");
        }
    }

    private static ErrorCode? Check(CallerContext caller, Operation operation) => RequiredLevel(operation) switch
    {
        AccessLevel.Anonymous => null,
        AccessLevel.Authenticated => caller.IsAuthenticated ? null : ErrorCode.Unauthenticated,
        AccessLevel.Admin => !caller.IsAuthenticated ? ErrorCode.Unauthenticated
            : caller.Role == Role.Admin ? null : ErrorCode.Forbidden,
        _ => ErrorCode.Forbidden, // fail closed
    };
}
