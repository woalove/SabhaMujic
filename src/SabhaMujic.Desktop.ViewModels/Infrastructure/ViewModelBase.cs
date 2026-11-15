using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>
/// Base for screen view models: an error/status line and a safe wrapper for service calls,
/// so that no exception from the client ever escapes into the UI thread.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Informational (non-error) message, e.g. "Imported 3 files".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    [RelayCommand]
    private void DismissMessage()
    {
        ErrorMessage = null;
        StatusMessage = null;
    }

    /// <summary>
    /// Runs a service call; maps any exception to <see cref="ErrorMessage"/>. Returns true on success.
    /// User-initiated calls clear the previous error first; background refreshes keep it.
    /// </summary>
    protected async Task<bool> TryAsync(Func<Task> action, bool showBusy = false, bool clearError = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (showBusy)
        {
            IsBusy = true;
        }

        try
        {
            if (clearError)
            {
                ErrorMessage = null;
            }

            await action().ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
            return false;
        }
        finally
        {
            if (showBusy)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Fire-and-forget variant for event handlers (errors still surface in <see cref="ErrorMessage"/>).</summary>
    protected void Run(Func<Task> action) => _ = TryAsync(action, clearError: false);
}
