using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Remote.Certificates;

/// <summary>
/// Owns the current HTTPS leaf certificate. <see cref="EnsureCurrent"/> re-issues it when it is within
/// <see cref="RenewBefore"/> of expiry, was not issued by the current CA, or the machine's set of private IPv4 addresses
/// (or host name) changed. Kestrel reads <see cref="Current"/> per TLS handshake, so a new leaf takes effect without a
/// restart. The leaf is persisted as <c>leaf.pfx</c> through <see cref="ISecretProtector"/>.
/// </summary>
public sealed class ServerCertificateManager : IDisposable
{
    public const string LeafFileName = "leaf.pfx";
    public static readonly TimeSpan MaxLeafValidity = TimeSpan.FromDays(397);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly LocalCertificateAuthority _ca;
    private readonly INetworkAddressProvider _network;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly string? _leafPath;
    private readonly Lock _gate = new();
    private X509Certificate2? _current;
    private X509Certificate2? _previous;

    /// <param name="directory">Where the leaf is persisted; null keeps it in memory only.</param>
    public ServerCertificateManager(LocalCertificateAuthority ca, INetworkAddressProvider network, ISecretProtector protector, string? directory,
        TimeProvider? clock = null, ILogger? logger = null)
    {
        _ca = ca;
        _network = network;
        _protector = protector;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _leafPath = directory is null ? null : Path.Combine(directory, LeafFileName);
        if (_leafPath is not null && File.Exists(_leafPath))
        {
            try
            {
                _current = LocalCertificateAuthority.LoadPfx(_protector.Unprotect(File.ReadAllBytes(_leafPath)));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The stored HTTPS certificate could not be loaded; a new one will be issued.");
            }
        }

        EnsureCurrent();
    }

    public LocalCertificateAuthority Authority => _ca;

    /// <summary>The leaf to present in TLS handshakes (with private key).</summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                return _current!;
            }
        }
    }

    /// <summary>Raised after a new leaf was issued.</summary>
    public event Action<X509Certificate2>? Rotated;

    /// <summary>Checks the leaf and re-issues it when needed. Returns true when a new leaf was issued.</summary>
    public bool EnsureCurrent()
    {
        var addresses = _network.GetPrivateIPv4Addresses();
        var host = _network.GetHostName();
        var now = _clock.GetUtcNow();
        X509Certificate2 issued;
        lock (_gate)
        {
            var reason = _current is null ? "no certificate" : WhyRenew(_current, addresses, host, now);
            if (reason is null)
            {
                return false;
            }

            issued = _ca.IssueServerCertificate(addresses, host, now, MaxLeafValidity);
            if (_leafPath is not null)
            {
                try
                {
                    LocalCertificateAuthority.WriteAtomically(_leafPath, _protector.Protect(issued.Export(X509ContentType.Pfx)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not persist the HTTPS certificate; it will be re-issued at the next start.");
                }
            }

            // Keep the previous leaf alive briefly: a handshake may still be using it.
            _previous?.Dispose();
            _previous = _current;
            _current = issued;
            _logger.LogInformation("Issued HTTPS certificate ({Reason}) for {Addresses} and {Host}.local, valid until {NotAfter:u}.",
                reason, string.Join(", ", addresses), host, issued.NotAfter.ToUniversalTime());
        }

        Rotated?.Invoke(issued);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _previous?.Dispose();
            _current?.Dispose();
            _previous = null;
            _current = null;
        }
    }

    private string? WhyRenew(X509Certificate2 leaf, IReadOnlyList<IPAddress> addresses, string host, DateTimeOffset now)
    {
        if (!leaf.HasPrivateKey)
        {
            return "missing private key";
        }

        if (leaf.NotAfter.ToUniversalTime() - now.UtcDateTime < RenewBefore || leaf.NotBefore.ToUniversalTime() > now.UtcDateTime)
        {
            return "expiring";
        }

        if (!IssuedByCurrentCa(leaf))
        {
            return "issued by another CA";
        }

        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return "no subject alternative names";
        }

        var ips = san.EnumerateIPAddresses().Select(a => a.ToString()).Order(StringComparer.Ordinal);
        if (!ips.SequenceEqual(addresses.Select(a => a.ToString()).Distinct().Order(StringComparer.Ordinal)))
        {
            return "IP addresses changed";
        }

        return san.EnumerateDnsNames().Contains($"{host}.local", StringComparer.OrdinalIgnoreCase) ? null : "host name changed";
    }

    private bool IssuedByCurrentCa(X509Certificate2 leaf)
    {
        if (!leaf.IssuerName.RawData.AsSpan().SequenceEqual(_ca.Certificate.SubjectName.RawData))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca.Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = leaf.NotBefore.AddMinutes(1);
        return chain.Build(leaf);
    }
}
