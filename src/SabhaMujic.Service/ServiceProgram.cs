using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Win32;
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
/// process exits with <see cref="ExitDataRootRefused"/> without starting.</item>
/// <item>With <c>--prepare-data-root</c> it stops there (exit 0): the installer runs this elevated so the root is owned
/// by Administrators even if a standard user created it first.</item>
/// <item>Otherwise it runs the Windows service host. If the host fails to start or stops with an error, the error is
/// logged and the process exits with <see cref="ExitStartFailed"/>.</item>
/// </list>
/// The Event Log is used only when the installer registered the <see cref="EventLogSource"/> source (a dev build run
/// without the MSI logs to the console and the rolling file only: an unregistered source would make the Event Log
/// logger throw while the host is built).
/// </summary>
public static class ServiceProgram
{
    /// <summary>Event Log source; the installer registers exactly this name in the Application log.</summary>
    public const string EventLogSource = "SabhaMujic.Service";

    public const string EventLogName = "Application";

    public const string PrepareSwitch = "--prepare-data-root";

    public const int ExitStartFailed = 1;

    public const int ExitDataRootRefused = 2;

    /// <summary>Runs the service (or the prepare step) until <paramref name="ct"/> or the service stop; returns the exit code.</summary>
    /// <param name="logging">Extra logging setup (tests add a provider that captures the service's log).</param>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default, Action<ILoggingBuilder>? logging = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var prepareOnly = args.Any(a => string.Equals(a, PrepareSwitch, StringComparison.OrdinalIgnoreCase));
        // The switch has no value; keep it away from the command-line configuration parser.
        var hostArgs = args.Where(a => !string.Equals(a, PrepareSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        var builder = Host.CreateApplicationBuilder(hostArgs);
        var settings = ServiceSettings.From(builder.Configuration);
        var paths = new SabhaDataPaths(settings.DataRoot);
        var eventLog = IsEventLogSourceRegistered();

        using (var bootstrapLogging = LoggerFactory.Create(b =>
               {
                   b.AddConsole();
                   if (eventLog)
                   {
                       b.AddEventLog(EventLogSettings());
                   }

                   logging?.Invoke(b);
               }))
        {
            var logger = bootstrapLogging.CreateLogger("SabhaMujic.Service");
            if (!eventLog)
            {
                logger.LogWarning("The Event Log source '{Source}' is not registered (SabhaMujic was not installed with its installer); " +
                    "logging to the console and the log folder only.", EventLogSource);
            }

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

        Configure(builder, settings, paths, eventLog);
        logging?.Invoke(builder.Logging);
        using var host = builder.Build();
        try
        {
            await host.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SabhaMujic.Service")
                .LogCritical(ex, "SabhaMujic stopped because of an error: {Reason}", ex.Message);
            return ExitStartFailed;
        }
    }

    /// <summary>
    /// Windows service lifetime, logging (rolling file, plus the Event Log under <see cref="EventLogSource"/> when that
    /// source is registered) and the SabhaMujic host.
    /// </summary>
    /// <param name="eventLogSourceRegistered">Null: look it up (<see cref="IsEventLogSourceRegistered"/>).</param>
    public static void Configure(HostApplicationBuilder builder, ServiceSettings settings, SabhaDataPaths paths, bool? eventLogSourceRegistered = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceSid.ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        if (eventLogSourceRegistered ?? IsEventLogSourceRegistered())
        {
            ConfigureEventLog(builder.Services);
        }
        else
        {
            RemoveEventLog(builder.Services);
        }

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

    /// <summary>Removes the Event Log logger the default host adds (used when our source is not registered).</summary>
    public static void RemoveEventLog(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(EventLogLoggerProvider)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>
    /// True when the installer registered <see cref="EventLogSource"/> in the <see cref="EventLogName"/> log. Reads the
    /// registry key directly (readable by every account, unlike <c>EventLog.SourceExists</c>, which needs to scan logs a
    /// service account may not read). False on any error.
    /// </summary>
    public static bool IsEventLogSourceRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\EventLog\{EventLogName}\{EventLogSource}");
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static EventLogSettings EventLogSettings() => new() { SourceName = EventLogSource, LogName = EventLogName };
}
