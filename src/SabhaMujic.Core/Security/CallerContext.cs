using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Security;

/// <summary>
/// Who is calling. Built by the transport adapter (pipe connection or HTTP request) from a validated session.
/// <see cref="Role"/> is null for unauthenticated callers.
/// </summary>
public sealed record CallerContext(
    ClientKind Client,
    Role? Role,
    Guid? UserId,
    Guid? DeviceId,
    string? SessionId,
    string Principal)
{
    public bool IsAuthenticated => Role is not null;

    public static CallerContext Anonymous(ClientKind client, string principal) =>
        new(client, null, null, null, null, principal);
}
