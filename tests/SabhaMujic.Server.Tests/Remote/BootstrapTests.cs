using System.Net;
using System.Xml;
using System.Xml.Linq;
using SabhaMujic.Server.Remote;
using SabhaMujic.Server.Remote.Certificates;
using SabhaMujic.Server.Remote.Http;

namespace SabhaMujic.Server.Tests.Remote;

public sealed class BootstrapTests(RemoteFixture fixture) : IClassFixture<RemoteFixture>
{
    private RemoteTestHost Host => fixture.Host;

    [Fact]
    public async Task Serves_only_the_setup_page_profile_certificate_and_health()
    {
        var http = Host.Bootstrap();
        foreach (var path in BootstrapSite.Paths)
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"), path);
        }

        string[] refused = ["/api/me", "/api/pair", "/api/login", "/pair", "/login", "/index.html", "/js/app.js", "/hub/negotiate", "/sabhamujic-ca.mobileconfig/x",
            "/HEALTH", "/../ca.key", "/favicon.ico"];
        foreach (var path in refused)
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var post = await http.PostAsync("/", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        using var pair = await http.PostAsync("/api/pair", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, pair.StatusCode);
        using var login = await http.PostAsync("/api/login", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
    }

    [Fact]
    public async Task Setup_page_shows_the_fingerprint_and_ios_steps_under_a_strict_csp()
    {
        using var response = await Host.Bootstrap().GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(Host.Ca.Sha256Fingerprint, html);
        Assert.Contains("VPN &amp; Device Management", html);
        Assert.Contains("Certificate Trust Settings", html);
        Assert.Contains($"data-port=\"{Host.Remote.HttpsPort}\"", html);
        Assert.Contains("administrator name and password", html);
        Assert.DoesNotContain("location.hash", html); // no pairing codes any more (contract v2)

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'sha256-", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Mobileconfig_is_a_plist_with_a_root_payload_holding_the_ca()
    {
        using var response = await Host.Bootstrap().GetAsync(BootstrapSite.ProfilePath);
        Assert.Equal(AppleProfile.ContentType, response.Content.Headers.ContentType!.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
        var doc = XDocument.Load(reader);
        Assert.Equal("plist", doc.Root!.Name.LocalName);
        Assert.Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", System.Text.Encoding.UTF8.GetString(bytes));
        var top = Dict(doc.Root.Element("dict")!);
        Assert.Equal("Configuration", top["PayloadType"].Value);
        Assert.Equal("1", top["PayloadVersion"].Value);
        Assert.True(Guid.TryParse(top["PayloadUUID"].Value, out _));

        var payload = Dict(top["PayloadContent"].Elements("dict").Single());
        Assert.Equal("com.apple.security.root", payload["PayloadType"].Value);
        Assert.Equal("data", payload["PayloadContent"].Name.LocalName);
        Assert.Equal(Host.Ca.CertificateDer, Convert.FromBase64String(payload["PayloadContent"].Value));

        // Stable identifiers: re-downloading replaces the installed profile instead of duplicating it.
        Assert.Equal(bytes, AppleProfile.Build(Host.Ca));

        using var cer = await Host.Bootstrap().GetAsync(BootstrapSite.CertificatePath);
        Assert.Equal(Host.Ca.CertificateDer, await cer.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void Phone_setup_urls_use_the_primary_lan_address()
    {
        var network = new FakeNetwork { Addresses = [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.7")] };
        var urls = new RemoteUrlProvider(network);
        Assert.Equal("http://192.168.1.20:8080/", urls.GetBootstrapUrl());
        Assert.Equal("https://192.168.1.20:8443/", urls.GetRemoteUrl());
        Assert.False(urls.IsRemoteRunning);

        network.Addresses = [];
        Assert.Equal("http://sabha-pc.local:8080/", urls.GetBootstrapUrl());
        urls.BootstrapPort = 18080;
        urls.HttpsPort = 18443;
        Assert.Equal("http://sabha-pc.local:18080/", urls.GetBootstrapUrl());
        Assert.Equal("https://sabha-pc.local:18443/", urls.GetRemoteUrl());
    }

    /// <summary>Plist dict → key → value element.</summary>
    private static Dictionary<string, XElement> Dict(XElement dict)
    {
        var result = new Dictionary<string, XElement>();
        var elements = dict.Elements().ToList();
        for (var i = 0; i < elements.Count; i += 2)
        {
            Assert.Equal("key", elements[i].Name.LocalName);
            result[elements[i].Value] = elements[i + 1];
        }

        return result;
    }
}
