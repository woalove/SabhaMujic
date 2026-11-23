namespace SabhaMujic.Contracts;

/// <summary>
/// Version of the client/service contract. Bump on any breaking change to DTOs or RPC signatures
/// and record the change in docs/ARCHITECTURE.md.
/// </summary>
public static class ContractVersion
{
    /// <summary>
    /// 2: open desktop use (anonymous desktop = volunteer), admin-only remote with password sign-in (pairing codes
    /// removed: <c>GetPhoneSetupInfoAsync</c> replaces <c>CreatePairingCodeAsync</c>), passwords instead of PINs, and
    /// tags (several categories per track: <c>CategoryIds</c> on tracks, imports and updates).
    /// </summary>
    public const int Current = 2;
}

/// <summary>Well-known transport constants shared by the service and clients.</summary>
public static class Endpoints
{
    /// <summary>Named pipe used by the desktop app (on Linux test hosts this maps to a Unix domain socket).</summary>
    public const string PipeName = "SabhaMujic.v1";

    /// <summary>HTTPS port for the remote API, SignalR hub and web app.</summary>
    public const int RemoteHttpsPort = 8443;

    /// <summary>Plain-HTTP port that only serves the local CA profile and setup instructions.</summary>
    public const int BootstrapHttpPort = 8080;

    /// <summary>SignalR hub path on the remote HTTPS endpoint.</summary>
    public const string RemoteHubPath = "/hub";

    /// <summary>HTTP header carrying the remote device/session token.</summary>
    public const string AuthHeader = "Authorization";
}
