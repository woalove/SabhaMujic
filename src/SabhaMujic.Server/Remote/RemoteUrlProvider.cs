using System.Globalization;
using SabhaMujic.Contracts;
using SabhaMujic.Server.Application;
using SabhaMujic.Server.Remote.Network;

namespace SabhaMujic.Server.Remote;

/// <summary>
/// The addresses for the desktop's "Set up a phone" panel, on the primary LAN address: the plain-HTTP setup page
/// (<c>http://&lt;ip&gt;:8080/</c>) and the HTTPS remote (<c>https://&lt;ip&gt;:8443/</c>).
/// </summary>
public sealed class RemoteUrlProvider : IRemoteUrlProvider
{
    private readonly INetworkAddressProvider _network;
    private readonly string? _hostOverride;
    private int _bootstrapPort;
    private int _httpsPort = Endpoints.RemoteHttpsPort;
    private int _running;

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

    /// <summary>The HTTPS port actually listened on (updated by <see cref="RemoteHost"/>).</summary>
    public int HttpsPort
    {
        get => Volatile.Read(ref _httpsPort);
        set => Volatile.Write(ref _httpsPort, value);
    }

    /// <summary>The local CA's SHA-256 fingerprint, set by the host once the CA is loaded.</summary>
    public string? CaFingerprint { get; set; }

    /// <summary>Set by <see cref="RemoteHost"/> while it listens.</summary>
    public bool IsRemoteRunning
    {
        get => Volatile.Read(ref _running) != 0;
        set => Volatile.Write(ref _running, value ? 1 : 0);
    }

    public string GetBootstrapUrl() => $"http://{Host()}:{BootstrapPort.ToString(CultureInfo.InvariantCulture)}/";

    public string GetRemoteUrl() => $"https://{Host()}:{HttpsPort.ToString(CultureInfo.InvariantCulture)}/";

    private string Host() =>
        _hostOverride ?? _network.GetPrimaryIPv4()?.ToString() ?? _network.GetHostName() + ".local";
}
