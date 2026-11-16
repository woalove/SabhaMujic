using System.Globalization;
using Microsoft.Extensions.Configuration;
using SabhaMujic.Contracts;

namespace SabhaMujic.Service;

/// <summary>
/// Service configuration (command line <c>--SabhaMujic:Key=value</c>, environment <c>SabhaMujic__Key</c> or
/// <c>appsettings.json</c> next to the exe):
/// <list type="table">
/// <item><term>SabhaMujic:DataRoot</term><description>Data root (default <c>%ProgramData%\SabhaMujic</c>).</description></item>
/// <item><term>SabhaMujic:PipeName</term><description>Desktop pipe name (default <c>SabhaMujic.v1</c>).</description></item>
/// <item><term>SabhaMujic:Remote:Enabled</term><description>Run the iPhone remote (default true).</description></item>
/// <item><term>SabhaMujic:Remote:HttpsPort</term><description>HTTPS port (default 8443).</description></item>
/// <item><term>SabhaMujic:Remote:BootstrapEnabled</term><description>Serve the plain-HTTP setup page (default true).</description></item>
/// <item><term>SabhaMujic:Remote:BootstrapPort</term><description>Setup page port (default 8080).</description></item>
/// </list>
/// </summary>
public sealed record ServiceSettings(string DataRoot, string? PipeName, bool RemoteEnabled, int HttpsPort, int? BootstrapPort)
{
    public const string Section = "SabhaMujic";

    public static ServiceSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = configuration[$"{Section}:DataRoot"];
        var https = Port(configuration, $"{Section}:Remote:HttpsPort", Endpoints.RemoteHttpsPort);
        var bootstrap = Port(configuration, $"{Section}:Remote:BootstrapPort", Endpoints.BootstrapHttpPort);
        var bootstrapEnabled = Flag(configuration, $"{Section}:Remote:BootstrapEnabled", true);
        if (bootstrapEnabled && bootstrap == https)
        {
            throw new InvalidOperationException("SabhaMujic:Remote:HttpsPort and SabhaMujic:Remote:BootstrapPort must differ.");
        }

        return new ServiceSettings(
            Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? WindowsHostComposition.DefaultDataRoot : root),
            string.IsNullOrWhiteSpace(configuration[$"{Section}:PipeName"]) ? null : configuration[$"{Section}:PipeName"],
            Flag(configuration, $"{Section}:Remote:Enabled", true),
            https,
            bootstrapEnabled ? bootstrap : null);
    }

    private static int Port(IConfiguration configuration, string key, int fallback)
    {
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException($"{key} must be a port number between 1 and 65535 (got '{text}').");
    }

    private static bool Flag(IConfiguration configuration, string key, bool fallback)
    {
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return bool.TryParse(text, out var value) ? value : throw new InvalidOperationException($"{key} must be true or false (got '{text}').");
    }
}
