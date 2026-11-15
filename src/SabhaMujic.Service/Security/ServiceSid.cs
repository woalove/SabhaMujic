using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SabhaMujic.Service.Security;

/// <summary>
/// Well-known identities used in the service's ACLs.
/// The per-service SID (<c>NT SERVICE\&lt;name&gt;</c>) is computed the way Windows does it (RtlCreateServiceSid:
/// <c>S-1-5-80-</c> followed by the SHA-1 of the upper-cased UTF-16 service name as five 32-bit sub-authorities), so it is
/// available even before the service is installed.
/// </summary>
public static class ServiceSid
{
    public const string ServiceName = "SabhaMujic";

    public static SecurityIdentifier For(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var sb = new StringBuilder("S-1-5-80");
        for (var i = 0; i < 5; i++)
        {
            sb.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)));
        }

        return new SecurityIdentifier(sb.ToString());
    }

    public static SecurityIdentifier LocalSystem { get; } = new(WellKnownSidType.LocalSystemSid, null);

    public static SecurityIdentifier Administrators { get; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static SecurityIdentifier AuthenticatedUsers { get; } = new(WellKnownSidType.AuthenticatedUserSid, null);
}
