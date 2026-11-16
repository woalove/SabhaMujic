using System.Globalization;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Remote;

/// <summary>
/// URLs shown with a pairing code. Both point at the plain-HTTP bootstrap page on the primary LAN address
/// (<c>http://&lt;ip&gt;:8080/</c>). The pairing code travels only in the URL fragment (<c>#code=123456</c>), which
/// browsers never send to a server; the bootstrap page's script carries it into the HTTPS <c>/pair#code=</c> link.
/// </summary>
public sealed class RemoteUrlProvider : IRemoteUrlProvider
{
    private readonly INetworkAddressProvider _network;
    private readonly string? _hostOverride;
    private int _bootstrapPort;

    /// <param name="hostOverride">Fixed host for the URLs (tests/e2e); null uses the primary private IPv4.</param>
    public RemoteUrlProvider(INetworkAddressProvider network, int bootstrapPort = Endpoints.BootstrapHttpPort, string? hostOverride = null)
    {
        _network = network;
        _bootstrapPort = bootstrapPort;
        _hostOverride = hostOverride;
    }

    /// <summary>The bootstrap port actually listened on (updated by <see cref="RemoteHost"/> when it binds port 0).</summary>
    public int BootstrapPort
    {
        get => Volatile.Read(ref _bootstrapPort);
        set => Volatile.Write(ref _bootstrapPort, value);
    }

    /// <summary>The local CA's SHA-256 fingerprint, set by the host once the CA is loaded.</summary>
    public string? CaFingerprint { get; set; }

    public string GetBootstrapUrl() => $"http://{Host()}:{BootstrapPort.ToString(CultureInfo.InvariantCulture)}/";

    public string GetPairingUrl(string code) => GetBootstrapUrl() + "#code=" + Uri.EscapeDataString(code ?? "");

    private string Host() =>
        _hostOverride ?? _network.GetPrimaryIPv4()?.ToString() ?? _network.GetHostName() + ".local";
}
