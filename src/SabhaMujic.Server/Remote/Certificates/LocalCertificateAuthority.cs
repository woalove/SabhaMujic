using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SabhaMujic.Server.Remote.Certificates;

/// <summary>
/// The app's own root CA: ECDSA P-256, 10 years, <c>CN=SabhaMujic Local CA (&lt;machine&gt;)</c>, BasicConstraints CA with
/// path length 0, KeyUsage keyCertSign + cRLSign. Each iPhone trusts it once (profile + full trust); it signs the
/// short-lived HTTPS leaf. The certificate is stored as <c>ca.cer</c> (DER) and the private key as <c>ca.key</c>,
/// PKCS#8 wrapped by <see cref="ISecretProtector"/>.
/// </summary>
public sealed class LocalCertificateAuthority : IDisposable
{
    public const string CertificateFileName = "ca.cer";
    public const string KeyFileName = "ca.key";
    public static readonly TimeSpan Validity = TimeSpan.FromDays(3652);

    private readonly ECDsa _key;

    private LocalCertificateAuthority(X509Certificate2 certificate, ECDsa key)
    {
        Certificate = certificate;
        _key = key;
        CertificateDer = certificate.RawData;
        Sha256Fingerprint = FormatFingerprint(SHA256.HashData(CertificateDer));
    }

    /// <summary>The CA certificate (public part only).</summary>
    public X509Certificate2 Certificate { get; }

    public byte[] CertificateDer { get; }

    /// <summary>SHA-256 of the DER certificate as colon-separated upper-case hex (what iOS shows in the profile details).</summary>
    public string Sha256Fingerprint { get; }

    /// <summary>
    /// Loads the CA from <paramref name="directory"/>, or creates and saves a new one when it is missing, unreadable,
    /// mismatched or expired (phones must then trust the new CA again; a warning is logged).
    /// </summary>
    public static LocalCertificateAuthority LoadOrCreate(string directory, ISecretProtector protector, string machineName,
        TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(protector);
        clock ??= TimeProvider.System;
        logger ??= NullLogger.Instance;
        Directory.CreateDirectory(directory);
        var certPath = Path.Combine(directory, CertificateFileName);
        var keyPath = Path.Combine(directory, KeyFileName);

        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            try
            {
                var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(certPath));
                var key = ECDsa.Create();
                try
                {
                    var pkcs8 = protector.Unprotect(File.ReadAllBytes(keyPath));
                    try
                    {
                        key.ImportPkcs8PrivateKey(pkcs8, out _);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(pkcs8);
                    }

                    using var certKey = cert.GetECDsaPublicKey();
                    var matches = certKey is not null &&
                        certKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo());
                    if (matches && cert.NotAfter.ToUniversalTime() > clock.GetUtcNow().UtcDateTime.AddDays(1))
                    {
                        return new LocalCertificateAuthority(cert, key);
                    }

                    logger.LogWarning("The stored local CA is expired or does not match its key; creating a new one. Phones must trust it again.");
                }
                catch
                {
                    key.Dispose();
                    throw;
                }

                key.Dispose();
                cert.Dispose();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The stored local CA could not be loaded; creating a new one. Phones must trust it again.");
            }
        }

        var created = Create(machineName, clock.GetUtcNow());
        WriteAtomically(keyPath, protector.Protect(created._key.ExportPkcs8PrivateKey()));
        WriteAtomically(certPath, created.CertificateDer);
        logger.LogInformation("Created local CA {Subject} ({Fingerprint}).", created.Certificate.Subject, created.Sha256Fingerprint);
        return created;
    }

    /// <summary>Creates a new CA in memory (not persisted).</summary>
    public static LocalCertificateAuthority Create(string machineName, DateTimeOffset now)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var subject = new X500DistinguishedNameBuilder();
            subject.AddOrganizationName("SabhaMujic");
            subject.AddCommonName($"SabhaMujic Local CA ({SanitizeName(machineName)})");
            var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var notBefore = now.AddDays(-1);
            using var selfSigned = request.CreateSelfSigned(notBefore, notBefore + Validity);
            // Keep only the public certificate; the key is held (and persisted) separately.
            var publicOnly = X509CertificateLoader.LoadCertificate(selfSigned.RawData);
            return new LocalCertificateAuthority(publicOnly, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Issues a TLS server certificate (with its private key): ECDSA P-256, EKU serverAuth, KeyUsage digitalSignature,
    /// SAN = <paramref name="addresses"/> + <c>&lt;hostLabel&gt;.local</c> + <c>localhost</c>, validity at most
    /// <paramref name="validity"/> (≤ 397 days) and never beyond the CA.
    /// </summary>
    public X509Certificate2 IssueServerCertificate(IEnumerable<IPAddress> addresses, string hostLabel, DateTimeOffset now, TimeSpan validity)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (validity <= TimeSpan.Zero || validity > ServerCertificateManager.MaxLeafValidity)
        {
            throw new ArgumentOutOfRangeException(nameof(validity));
        }

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddOrganizationName("SabhaMujic");
        subject.AddCommonName($"{hostLabel}.local");
        var request = new CertificateRequest(subject.Build(), leafKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var address in addresses.Distinct())
        {
            san.AddIpAddress(address);
        }

        san.AddDnsName($"{hostLabel}.local");
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Certificate, true, false));

        var notBefore = now.AddHours(-1);
        var notAfter = notBefore + validity;
        var caNotAfter = new DateTimeOffset(Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (notAfter > caNotAfter)
        {
            notAfter = caNotAfter;
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        using var signed = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(leafKey);
        // Round-trip through PKCS#12 so SChannel (Windows) gets a usable key container, not an ephemeral one.
        return LoadPfx(withKey.Export(X509ContentType.Pfx));
    }

    // Windows: a real key container (SChannel can't use ephemeral keys) that stays exportable, because the leaf is
    // re-exported as PFX to persist it (without Exportable, Export throws "Key not valid for use in specified state").
    // The key isn't persisted in the store (no PersistKeySet), so it's removed when the certificate is disposed.
    internal static X509Certificate2 LoadPfx(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null,
            OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
                : X509KeyStorageFlags.EphemeralKeySet);

    public void Dispose()
    {
        _key.Dispose();
        Certificate.Dispose();
    }

    internal static void WriteAtomically(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string FormatFingerprint(byte[] hash) => string.Join(':', hash.Select(b => b.ToString("X2")));

    private static string SanitizeName(string name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c) && c is not ',' and not '=' and not '+' and not '"' and not '\\' and not '<' and not '>' and not ';').ToArray()).Trim();
        if (cleaned.Length > 40)
        {
            cleaned = cleaned[..40];
        }

        return cleaned.Length == 0 ? "PC" : cleaned;
    }
}
