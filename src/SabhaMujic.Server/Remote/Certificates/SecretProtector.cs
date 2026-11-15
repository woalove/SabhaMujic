using System.Security.Cryptography;

namespace SabhaMujic.Server.Remote.Certificates;

/// <summary>
/// Protects private key material at rest (the local CA key and the TLS leaf). The Windows service supplies a DPAPI
/// (machine scope) implementation; the folder is additionally ACL'd to the service account.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Throws <see cref="CryptographicException"/> when the data cannot be unprotected.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// <b>Development/test only.</b> Stores secrets unencrypted (with a format marker). Used on Linux test hosts and by the
/// e2e host, where DPAPI does not exist. The Windows service must never use it.
/// </summary>
public sealed class UnprotectedSecretProtector : ISecretProtector
{
    private static readonly byte[] Marker = "SM-UNPROTECTED-DEV-ONLY\n"u8.ToArray();

    public static UnprotectedSecretProtector Instance { get; } = new();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return [.. Marker, .. plaintext];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        if (protectedData.Length < Marker.Length || !protectedData.AsSpan(0, Marker.Length).SequenceEqual(Marker))
        {
            throw new CryptographicException("The data was not written by UnprotectedSecretProtector.");
        }

        return protectedData[Marker.Length..];
    }
}
