using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Service.Logging;
using SabhaMujic.Service.Security;

namespace SabhaMujic.Service;

/// <summary>
/// Service entry point:
/// <list type="number">
/// <item>Reads the settings (<see cref="ServiceSettings"/>).</item>
/// <item>Locks down the data root with <see cref="WindowsDataRootGuard"/> before anything is written (logs included).
/// If that is not possible (a junction, or a folder owned by someone else) the error goes to the Event Log and the
/// process exits with code 2 without starting.</item>
/// <item>With <c>--prepare-data-root</c> it stops there (exit 0): the installer runs this elevated so the root is owned
/// by Administrators even if a standard user created it first.</item>
/// <item>Otherwise it runs the Windows service host.</item>
/// </list>
/// </summary>
public static class ServiceProgram
{
    /// <summary>Event Log source; the installer registers exactly this name in the Application log.</summary>
    public const string EventLogSource = "SabhaMujic.Service";

    public const string EventLogName = "Application";

    public const string PrepareSwitch = "--prepare-data-root";

    public const int ExitDataRootRefused = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        var prepareOnly = args.Any(a => string.Equals(a, PrepareSwitch, StringComparison.OrdinalIgnoreCase));
        // The switch has no value; keep it away from the command-line configuration parser.
        var hostArgs = args.Where(a => !string.Equals(a, PrepareSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        var builder = Host.CreateApplicationBuilder(hostArgs);
        var settings = ServiceSettings.From(builder.Configuration);
        var paths = new SabhaDataPaths(settings.DataRoot);

        using (var bootstrapLogging = LoggerFactory.Create(b => b.AddConsole().AddEventLog(EventLogSettings())))
        {
            var logger = bootstrapLogging.CreateLogger("SabhaMujic.Service");
            try
            {
                new WindowsDataRootGuard(logger: logger).Prepare(paths);
            }
            catch (Exception ex) when (ex is DataRootException or UnauthorizedAccessException or IOException)
            {
                logger.LogCritical(ex, "SabhaMujic cannot use its data folder and will not start: {Reason}", ex.Message);
                return ExitDataRootRefused;
            }

            if (prepareOnly)
            {
                logger.LogInformation("Prepared the SabhaMujic data folder '{Root}'.", paths.Root);
                return 0;
            }
        }

        Configure(builder, settings, paths);
        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Windows service lifetime, logging (rolling file + Event Log source) and the SabhaMujic host.</summary>
    public static void Configure(HostApplicationBuilder builder, ServiceSettings settings, SabhaDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceSid.ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        ConfigureEventLog(builder.Services);

        // Rolling file log in <root>\logs (prepared and ACL'd by the guard), next to the console/debug/Event Log providers.
        builder.Logging.AddProvider(new RollingFileLoggerProvider(paths.Logs));
        builder.Services.AddSingleton(sp => WindowsHostComposition.CreateOptions(settings, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddHostedService<SabhaServiceHost>();
    }

    /// <summary>Pins the Event Log source and log, so renaming the assembly can't silently change them.</summary>
    public static void ConfigureEventLog(IServiceCollection services) =>
        services.PostConfigure<EventLogSettings>(s =>
        {
            s.SourceName = EventLogSource;
            s.LogName = EventLogName;
        });

    private static EventLogSettings EventLogSettings() => new() { SourceName = EventLogSource, LogName = EventLogName };
}
