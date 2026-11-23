using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Dialogs;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Services;

/// <summary>Marshals to the WPF dispatcher. Always queues (never inline) so event handlers can't re-enter a running command.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary><see cref="IScheduler"/> on DispatcherTimers, so callbacks run on the UI thread.</summary>
public sealed class DispatcherScheduler(Dispatcher dispatcher) : IScheduler
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public IDisposable Schedule(TimeSpan dueTime, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Max(dueTime) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    public IDisposable SchedulePeriodic(TimeSpan period, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Max(period) };
        timer.Tick += (_, _) => action();
        timer.Start();
        return new TimerHandle(timer);
    }

    private static TimeSpan Max(TimeSpan t) => t < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : t;

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

/// <summary>All dialogs are the app's own flat, themed windows (no Windows message boxes).</summary>
public sealed class WpfDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        var dialog = new ConfirmWindow(title, message, confirmText);
        SetOwner(dialog);
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task<string?> PromptTextAsync(string title, string label, string initialValue = "")
    {
        var dialog = new PromptWindow(title, label, initialValue, isSecret: false);
        SetOwner(dialog);
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value : null);
    }

    public Task<string?> PromptSecretAsync(string title, string label)
    {
        var dialog = new PromptWindow(title, label, "", isSecret: true);
        SetOwner(dialog);
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value : null);
    }

    public Task<bool> ShowSheetAsync(SheetViewModel sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var dialog = new SheetWindow(sheet);
        SetOwner(dialog);
        dialog.ShowDialog();
        return Task.FromResult(dialog.Saved);
    }

    /// <summary>A plain message with an OK button (used for unexpected errors).</summary>
    public static void ShowMessage(string title, string message)
    {
        var dialog = new ConfirmWindow(title, message, "OK", cancelText: null);
        SetOwner(dialog);
        dialog.ShowDialog();
    }

    internal static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is MainWindow or MiniPlayerWindow);

    private static void SetOwner(Window dialog)
    {
        var owner = ActiveWindow();
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}

public sealed class WpfFilePicker : IFilePicker
{
    public Task<IReadOnlyList<string>> PickAudioFilesAsync()
    {
        var patterns = string.Join(";", AdminLimits.AllowedExtensions.Order().Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Choose music to import",
            Multiselect = true,
            CheckFileExists = true,
            Filter = $"Audio files ({patterns})|{patterns}|All files (*.*)|*.*",
        };
        var owner = WpfDialogService.ActiveWindow();
        var ok = (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
        return Task.FromResult<IReadOnlyList<string>>(ok ? dialog.FileNames : []);
    }
}
