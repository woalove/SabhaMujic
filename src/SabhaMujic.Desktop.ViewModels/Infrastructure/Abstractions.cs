using SabhaMujic.Client;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>Marshals work onto the UI thread. The WPF implementation wraps the Dispatcher; tests run inline.</summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already on it).</summary>
    void Post(Action action);
}

/// <summary>
/// Clock + timers for debouncing, position interpolation and countdowns.
/// Callbacks always run on the UI thread (WPF: DispatcherTimer; tests: <c>ManualScheduler.Advance</c>).
/// </summary>
public interface IScheduler
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Runs <paramref name="action"/> once after <paramref name="dueTime"/>. Dispose to cancel.</summary>
    IDisposable Schedule(TimeSpan dueTime, Action action);

    /// <summary>Runs <paramref name="action"/> every <paramref name="period"/>. Dispose to stop.</summary>
    IDisposable SchedulePeriodic(TimeSpan period, Action action);
}

/// <summary>Modal UI interactions the view models need from the WPF layer.</summary>
public interface IDialogService
{
    /// <summary>Asks a yes/no question. Returns true when the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK");

    /// <summary>Asks for a single line of text. Returns null when cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string label, string initialValue = "");

    /// <summary>Asks for a secret (PIN/password) with confirmation. Returns null when cancelled.</summary>
    Task<string?> PromptSecretAsync(string title, string label);
}

/// <summary>Native file-open dialog.</summary>
public interface IFilePicker
{
    /// <summary>Lets the user pick audio files. Returns an empty list when cancelled.</summary>
    Task<IReadOnlyList<string>> PickAudioFilesAsync();
}

/// <summary>Read-only file access for the import pipeline (tests use an in-memory implementation).</summary>
public interface IFileSystem
{
    long GetLength(string path);

    Stream OpenRead(string path);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public long GetLength(string path) => new FileInfo(path).Length;

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
}

/// <summary>Creates the <see cref="ISabhaClient"/> the app talks to (demo client or the real service client).</summary>
public interface ISabhaClientFactory
{
    ISabhaClient Create();
}

/// <summary>Outcome of asking Windows to restart the app elevated.</summary>
public enum RestartResult
{
    /// <summary>The elevated copy was started; this instance is shutting down.</summary>
    Restarting,

    /// <summary>The user declined the UAC prompt; the app stays open.</summary>
    Cancelled,

    /// <summary>The restart could not be started (e.g. the exe path is unknown); the app stays open.</summary>
    Failed,
}

/// <summary>Relaunches the app with administrator rights (UAC prompt) and exits on success. Implemented by the WPF app.</summary>
public interface IAppRestarter
{
    RestartResult RestartAsAdministrator();
}

/// <summary>The UI services a view model may use. One instance is shared by the whole app.</summary>
public sealed record UiServices(
    IUiDispatcher Dispatcher,
    IScheduler Scheduler,
    IDialogService Dialogs,
    IFilePicker FilePicker,
    IFileSystem FileSystem,
    IAppRestarter? Restarter = null);
