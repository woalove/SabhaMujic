using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Services;

/// <summary>
/// Relaunches this exe through UAC (<c>Verb = "runas"</c>, shell execute) with the same arguments (plus an optional
/// startup argument such as <c>--recover-admin</c>), then shuts this instance down. If the user declines the UAC prompt (Win32 error 1223) or the launch fails, the app stays open.
/// </summary>
public sealed class WpfAppRestarter(Application app) : IAppRestarter
{
    private const int ErrorCancelled = 1223;

    public RestartResult RestartAsAdministrator(string? startupArgument = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return RestartResult.Failed;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (!string.IsNullOrEmpty(startupArgument) && !arguments.Contains(startupArgument, StringComparer.OrdinalIgnoreCase))
        {
            arguments.Add(startupArgument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var _ = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return RestartResult.Cancelled;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return RestartResult.Failed;
        }

        // Let the command finish (the view shows "Restarting…") before the window closes.
        app.Dispatcher.BeginInvoke(() => app.Shutdown());
        return RestartResult.Restarting;
    }
}
