using System.Windows;
using System.Windows.Threading;
using SabhaMujic.Client;
using SabhaMujic.Desktop.Services;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Demo;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop;

/// <summary>
/// Composition root (manual DI). Command line:
/// <c>--demo</c> uses the in-memory <c>DemoSabhaClient</c> (dev/demo tool only); otherwise the app connects to the
/// SabhaMujic Windows service through <see cref="ServiceClientFactory"/>.
/// </summary>
public partial class App : Application
{
    private ISabhaClient? _client;
    private ShellViewModel? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last line of defence: view models already turn service errors into messages, but no bug may crash the
        // player in the middle of an event.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var isDemo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
        ISabhaClientFactory factory = isDemo ? new DemoClientFactory() : new ServiceClientFactory();
        _client = factory.Create();

        var ui = new UiServices(
            new WpfDispatcher(Dispatcher),
            new DispatcherScheduler(Dispatcher),
            new WpfDialogService(),
            new WpfFilePicker(),
            new PhysicalFileSystem(),
            new WpfAppRestarter(this));
        _shell = new ShellViewModel(_client, ui);

        var demo = _client as DemoSabhaClient;
        var window = new MainWindow(_shell, UiSettingsStore.ForCurrentUser(), demo is null ? null : demo.SimulateConnectionLoss);
        MainWindow = window;
        window.Show();
        _shell.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Fire-and-forget: blocking the dispatcher here could deadlock continuations that need it, and the
        // process is about to end anyway. Playback is owned by the service and keeps running.
        _ = _shell?.DisposeAsync().AsTask();
        _ = _client?.DisposeAsync().AsTask();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(
            ErrorMessages.For(e.Exception),
            "SabhaMujic",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
