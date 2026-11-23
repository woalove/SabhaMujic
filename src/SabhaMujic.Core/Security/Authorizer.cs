using System.Collections.Frozen;
using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Security;

/// <summary>
/// The single access matrix for SabhaMujic. Every pipe RPC method, hub method and HTTP endpoint must call
/// <see cref="Demand"/> with its <see cref="Operation"/> before doing any work. The decision depends on the operation's
/// <see cref="AccessLevel"/>, the caller's role <b>and its transport</b> (<see cref="CallerContext.Client"/>):
/// <list type="table">
/// <listheader><term>Level</term><description>Desktop pipe / iPhone remote</description></listheader>
/// <item><term>Anonymous</term><description>anyone / anyone, but only for <see cref="RemoteAnonymousOperations"/>
/// (sign-in); any other operation needs an administrator session.</description></item>
/// <item><term>Authenticated (volunteer use)</term><description>anyone, signed in or not ("open use") / administrators
/// only.</description></item>
/// <item><term>Admin</term><description>administrators / administrators.</description></item>
/// </list>
/// Denials: no valid session where one is needed → <see cref="ErrorCode.Unauthenticated"/>; a signed-in
/// non-administrator → <see cref="ErrorCode.Forbidden"/>.
/// </summary>
public static class Authorizer
{
    private static readonly FrozenDictionary<Operation, AccessLevel> Matrix = new Dictionary<Operation, AccessLevel>
    {
        [Operation.Hello] = AccessLevel.Anonymous,
        [Operation.Login] = AccessLevel.Anonymous,
        [Operation.ResumeSession] = AccessLevel.Anonymous,
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
        [Operation.ManageCategoryMembership] = AccessLevel.Admin,
        [Operation.ManageTracks] = AccessLevel.Admin,
        [Operation.ManageLoopPoints] = AccessLevel.Admin,
        [Operation.ManageUsers] = AccessLevel.Admin,
        [Operation.ManageDevices] = AccessLevel.Admin,
    }.ToFrozenDictionary();

    /// <summary>The only operations an anonymous remote caller may use: signing in (password, or a stored session).</summary>
    public static IReadOnlySet<Operation> RemoteAnonymousOperations { get; } =
        new[] { Operation.Login, Operation.ResumeSession }.ToFrozenSet();

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
                ? "Sign in as an administrator is required."
                : "This action requires an administrator.");
        }
    }

    /// <summary>Null when allowed, otherwise the denial code.</summary>
    public static ErrorCode? Check(CallerContext caller, Operation operation)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var level = RequiredLevel(operation);
        return caller.Client switch
        {
            ClientKind.Desktop => level switch
            {
                AccessLevel.Anonymous or AccessLevel.Authenticated => null, // open use at the PC
                AccessLevel.Admin => AdminOnly(caller),
                _ => ErrorCode.Forbidden, // fail closed
            },
            ClientKind.Remote => RemoteAnonymousOperations.Contains(operation) ? null : AdminOnly(caller),
            _ => ErrorCode.Forbidden, // fail closed
        };
    }

    private static ErrorCode? AdminOnly(CallerContext caller) =>
        !caller.IsAuthenticated ? ErrorCode.Unauthenticated
        : caller.Role == Role.Admin ? null
        : ErrorCode.Forbidden;
}
