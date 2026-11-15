using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SabhaMujic.Server.Remote.Certificates;

namespace SabhaMujic.Server.Tests.Remote;

public sealed class CertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sabhamujic-cert-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeNetwork _network = new() { Addresses = [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.7")] };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Ca_is_a_P256_root_with_path_length_zero_valid_for_ten_years()
    {
        using var ca = LocalCertificateAuthority.LoadOrCreate(_dir, UnprotectedSecretProtector.Instance, "SABHA-PC", _clock);
        var cert = ca.Certificate;

        Assert.Contains("CN=SabhaMujic Local CA (SABHA-PC)", cert.Subject, StringComparison.Ordinal);
        Assert.Equal(cert.Subject, cert.Issuer);
        using var key = cert.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, key.ExportParameters(false).Curve.Oid.Value);

        var basic = cert.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.Critical);
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.HasPathLengthConstraint);
        Assert.Equal(0, basic.PathLengthConstraint);
        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);

        var validity = cert.NotAfter - cert.NotBefore;
        Assert.InRange(validity.TotalDays, 3650, 3653);
        Assert.False(cert.HasPrivateKey);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)), ca.Sha256Fingerprint.Replace(":", ""));
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", ca.Sha256Fingerprint);
    }

    [Fact]
    public void Leaf_has_the_lan_ips_mdns_name_and_localhost_and_chains_to_the_ca()
    {
        using var ca = LocalCertificateAuthority.LoadOrCreate(_dir, UnprotectedSecretProtector.Instance, "SABHA-PC", _clock);
        using var manager = new ServerCertificateManager(ca, _network, UnprotectedSecretProtector.Instance, _dir, _clock);
        var leaf = manager.Current;

        Assert.True(leaf.HasPrivateKey);
        Assert.True(leaf.NotAfter - leaf.NotBefore <= TimeSpan.FromDays(397));
        Assert.True(leaf.NotBefore <= _clock.GetUtcNow().UtcDateTime && leaf.NotAfter > _clock.GetUtcNow().UtcDateTime.AddDays(390));

        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["10.0.0.7", "192.168.1.20"], san.EnumerateIPAddresses().Select(a => a.ToString()).Order());
        Assert.Equal(["localhost", "sabha-pc.local"], san.EnumerateDnsNames().Order());

        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, leaf.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        using var leafKey = leaf.GetECDsaPublicKey();
        Assert.Equal(256, leafKey!.KeySize);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca.Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _clock.GetUtcNow().UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        Assert.True(chain.Build(leaf), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        Assert.Equal(ca.Certificate.Thumbprint, chain.ChainElements[^1].Certificate.Thumbprint);
    }

    [Fact]
    public void Leaf_is_reissued_when_the_ip_set_changes_or_expiry_nears_and_kept_otherwise()
    {
        using var ca = LocalCertificateAuthority.LoadOrCreate(_dir, UnprotectedSecretProtector.Instance, "SABHA-PC", _clock);
        using var manager = new ServerCertificateManager(ca, _network, UnprotectedSecretProtector.Instance, _dir, _clock);
        var first = manager.Current.Thumbprint;
        var rotations = 0;
        manager.Rotated += _ => rotations++;

        Assert.False(manager.EnsureCurrent());
        _network.Addresses = [IPAddress.Parse("10.0.0.7"), IPAddress.Parse("192.168.1.20")]; // same set, other order
        Assert.False(manager.EnsureCurrent());
        Assert.Equal(first, manager.Current.Thumbprint);

        _network.Addresses = [IPAddress.Parse("192.168.50.9")];
        Assert.True(manager.EnsureCurrent());
        var second = manager.Current;
        Assert.NotEqual(first, second.Thumbprint);
        Assert.Equal(["192.168.50.9"], second.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateIPAddresses().Select(a => a.ToString()));

        _network.HostName = "new-name";
        Assert.True(manager.EnsureCurrent());

        _clock.Advance(TimeSpan.FromDays(300));
        Assert.False(manager.EnsureCurrent());
        _clock.Advance(TimeSpan.FromDays(70)); // < 30 days left
        Assert.True(manager.EnsureCurrent());
        Assert.Equal(3, rotations);
    }

    [Fact]
    public void Ca_key_and_leaf_are_persisted_only_through_the_protector()
    {
        var protector = new RecordingProtector();
        string fingerprint, leafThumbprint;
        byte[] pkcs8;
        using (var ca = LocalCertificateAuthority.LoadOrCreate(_dir, protector, "SABHA-PC", _clock))
        using (var manager = new ServerCertificateManager(ca, _network, protector, _dir, _clock))
        {
            fingerprint = ca.Sha256Fingerprint;
            leafThumbprint = manager.Current.Thumbprint;
            Assert.Equal(2, protector.ProtectCalls); // CA key + leaf PFX
            pkcs8 = protector.Unprotect(File.ReadAllBytes(Path.Combine(_dir, LocalCertificateAuthority.KeyFileName)));
        }

        var stored = File.ReadAllBytes(Path.Combine(_dir, LocalCertificateAuthority.KeyFileName));
        Assert.False(stored.AsSpan().IndexOf(pkcs8.AsSpan(8, 32)) >= 0, "the raw key must not be on disk");

        var unprotectBefore = protector.UnprotectCalls;
        using (var ca = LocalCertificateAuthority.LoadOrCreate(_dir, protector, "SABHA-PC", _clock))
        using (var manager = new ServerCertificateManager(ca, _network, protector, _dir, _clock))
        {
            Assert.Equal(fingerprint, ca.Sha256Fingerprint);
            Assert.Equal(leafThumbprint, manager.Current.Thumbprint); // reused, not re-issued
            Assert.Equal(unprotectBefore + 2, protector.UnprotectCalls);
            Assert.Equal(2, protector.ProtectCalls);
        }

        // A key the protector cannot read (e.g. DPAPI key lost) yields a new CA rather than a crash.
        using (var ca = LocalCertificateAuthority.LoadOrCreate(_dir, UnprotectedSecretProtector.Instance, "SABHA-PC", _clock))
        {
            Assert.NotEqual(fingerprint, ca.Sha256Fingerprint);
        }
    }

    [Fact]
    public async Task Kestrel_serves_a_rotated_leaf_without_a_restart()
    {
        await using var host = await RemoteTestHost.StartAsync();
        var before = await PresentedCertificateAsync(host);
        Assert.Equal(host.Certificates.Current.Thumbprint, before.Thumbprint);

        host.Network.Addresses = [IPAddress.Parse("172.16.4.4")];
        Assert.True(host.Certificates.EnsureCurrent());

        var after = await PresentedCertificateAsync(host);
        Assert.NotEqual(before.Thumbprint, after.Thumbprint);
        Assert.Contains("172.16.4.4", after.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateIPAddresses().Select(a => a.ToString()));
        Assert.True(host.Remote.IsRunning);
    }

    private static async Task<X509Certificate2> PresentedCertificateAsync(RemoteTestHost host)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Remote.HttpsPort);
        X509Certificate2? presented = null;
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, errors) =>
        {
            presented = X509CertificateLoader.LoadCertificate(cert!.GetRawCertData());
            return host.Validate(presented, errors);
        });
        await ssl.AuthenticateAsClientAsync("localhost");
        return presented!;
    }
}
