using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Network;
using SabhaMujic.Service;
using SabhaMujic.Service.Security;
using Xunit.Abstractions;

namespace SabhaMujic.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class RemoteOnWindowsTests(ITestOutputHelper output)
{
    private sealed class LoopbackNetwork : INetworkAddressProvider
    {
        public IReadOnlyList<IPAddress> GetPrivateIPv4Addresses() => [IPAddress.Parse("192.168.1.20")];

        public IPAddress? GetPrimaryIPv4() => IPAddress.Parse("192.168.1.20");

        public string GetHostName() => "sabha-ci";
    }

    [Fact]
    public void Dpapi_round_trips_and_is_bound_to_this_machine_scope_and_entropy()
    {
        var protector = new DpapiSecretProtector();
        var secret = RandomNumberGenerator.GetBytes(2048);
        var sealedBytes = protector.Protect(secret);

        Assert.NotEqual(secret, sealedBytes);
        Assert.Equal(secret, protector.Unprotect(sealedBytes));
        Assert.Equal(secret, new DpapiSecretProtector().Unprotect(sealedBytes)); // any instance on this machine
        Assert.Throws<CryptographicException>(() => new DpapiSecretProtector("other entropy"u8.ToArray()).Unprotect(sealedBytes));

        var tampered = sealedBytes.ToArray();
        tampered[^5] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered));
        Assert.Empty(protector.Unprotect(protector.Protect([])));
    }

    /// <summary>
    /// The CA and leaf are created, stored DPAPI-protected, loaded back from disk, and the leaf then serves a real TLS
    /// handshake on loopback under SChannel, validated against exactly that CA.
    /// </summary>
    [Fact]
    public async Task A_certificate_loaded_through_dpapi_works_for_tls_under_schannel()
    {
        using var dir = WindowsTestSupport.NewProgramDataDir();
        var dpapi = new DpapiSecretProtector();
        var network = new LoopbackNetwork();
        string fingerprint;
        using (var created = LocalCertificateAuthority.LoadOrCreate(dir.Path, dpapi, "CI-PC"))
        using (new ServerCertificateManager(created, network, dpapi, dir.Path))
        {
            fingerprint = created.Sha256Fingerprint;
        }

        // On disk the keys are DPAPI blobs, not readable as plain data.
        Assert.Throws<CryptographicException>(() =>
            UnprotectedSecretProtector.Instance.Unprotect(File.ReadAllBytes(Path.Combine(dir.Path, LocalCertificateAuthority.KeyFileName))));

        using var ca = LocalCertificateAuthority.LoadOrCreate(dir.Path, dpapi, "CI-PC");
        using var certificates = new ServerCertificateManager(ca, network, dpapi, dir.Path);
        Assert.Equal(fingerprint, ca.Sha256Fingerprint); // loaded, not re-created
        Assert.True(certificates.Current.HasPrivateKey);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverSide = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var tls = new SslStream(accepted.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificates.Current });
                await tls.WriteAsync("ok"u8.ToArray());
                await tls.FlushAsync();
            });

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var clientTls = new SslStream(client.GetStream(), false, (_, cert, _, errors) =>
            {
                if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca.Certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
            });
            await clientTls.AuthenticateAsClientAsync("localhost");
            var buffer = new byte[2];
            await clientTls.ReadExactlyAsync(buffer);
            await serverSide.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(clientTls.IsAuthenticated);
            Assert.Equal("ok"u8.ToArray(), buffer);
            output.WriteLine($"TLS {clientTls.SslProtocol}, {clientTls.NegotiatedCipherSuite}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Service_settings_have_safe_defaults_and_validate()
    {
        static ServiceSettings Parse(params (string Key, string Value)[] values) => ServiceSettings.From(
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

        var defaults = Parse();
        Assert.Equal(WindowsHostComposition.DefaultDataRoot, defaults.DataRoot);
        Assert.Null(defaults.PipeName);
        Assert.True(defaults.RemoteEnabled);
        Assert.Equal((8443, (int?)8080), (defaults.HttpsPort, defaults.BootstrapPort));

        var custom = Parse(("SabhaMujic:Remote:Enabled", "false"), ("SabhaMujic:Remote:HttpsPort", "9443"),
            ("SabhaMujic:Remote:BootstrapEnabled", "false"), ("SabhaMujic:PipeName", "custom"));
        Assert.Equal((false, 9443, (int?)null, "custom"), (custom.RemoteEnabled, custom.HttpsPort, custom.BootstrapPort, custom.PipeName));

        Assert.Throws<InvalidOperationException>(() => Parse(("SabhaMujic:Remote:HttpsPort", "0")));
        Assert.Throws<InvalidOperationException>(() => Parse(("SabhaMujic:Remote:HttpsPort", "70000")));
        Assert.Throws<InvalidOperationException>(() => Parse(("SabhaMujic:Remote:Enabled", "maybe")));
        Assert.Throws<InvalidOperationException>(() => Parse(("SabhaMujic:Remote:HttpsPort", "8080")));
        Assert.Null(WindowsHostComposition.CreateOptions(custom, NullLoggerFactory.Instance).Remote);
        Assert.IsType<DpapiSecretProtector>(WindowsHostComposition.CreateOptions(defaults, NullLoggerFactory.Instance).Remote!.SecretProtector);
    }

    /// <summary>The Windows composition with the remote on: DPAPI keys in an ACL-protected certs folder, HTTPS answering.</summary>
    [Fact]
    public async Task Service_runs_the_remote_with_dpapi_keys_in_a_protected_folder()
    {
        if (!WindowsTestSupport.IsElevatedAdministrator())
        {
            output.WriteLine("SKIPPED: needs an elevated administrator (the service's folders are ACL-protected).");
            return;
        }

        using var root = WindowsTestSupport.NewProgramDataDir();
        var options = WindowsHostComposition.CreateOptions(root.Path, NullLoggerFactory.Instance, "sm-svc-remote-" + Guid.NewGuid().ToString("N")[..8],
            new SabhaRemoteOptions
            {
                SecretProtector = new DpapiSecretProtector(),
                HttpsPort = 0,
                BootstrapPort = 0,
                BindAddress = IPAddress.Loopback,
                Network = new LoopbackNetwork(),
            });
        var host = await SabhaHost.StartAsync(options);
        try
        {
            var remote = host.Remote!.Host!;
            Assert.True(new DirectoryInfo(host.Paths.Certs).GetAccessControl().AreAccessRulesProtected);
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, chain, errors) => cert is not null &&
                    cert.Issuer == host.Remote.Authority.Certificate.Subject,
            });
            using var response = await http.GetAsync($"https://localhost:{remote.HttpsPort}/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var bootstrap = await http.GetAsync($"http://127.0.0.1:{remote.BootstrapPort}/");
            Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
