using System.Security.Cryptography;
using System.Text;

namespace SabhaMujic.Core.Security;

/// <summary>
/// PBKDF2-SHA256 password/PIN hashing. Format: <c>pbkdf2-sha256$iterations$saltB64$hashB64</c>.
/// </summary>
public static class SecretHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    public static string Hash(string secret, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string secret, string encoded)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Opaque bearer tokens. Only the SHA-256 hash of a token is ever stored.</summary>
public static class Tokens
{
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Six-digit numeric pairing code from a CSPRNG.</summary>
    public static string NewPairingCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
