using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>
/// "Forgot admin password?": sets a new password for an administrator (or creates a new administrator with that name) and signs in.
/// Like first-run setup, the service only allows it for this app started with "Run as administrator"; when it refuses,
/// the view offers "Restart as administrator", which relaunches the app through UAC straight back to this screen
/// (<see cref="StartupArgument"/>). Secrets are cleared after every attempt.
/// </summary>
public sealed partial class RecoveryViewModel : ViewModelBase
{
    /// <summary>Command-line switch that opens this screen at startup (passed when restarting elevated from it).</summary>
    public const string StartupArgument = "--recover-admin";

    public const int MinSecretLength = SetupViewModel.MinSecretLength;

    public const string Title = "Reset an administrator password";

    public const string Introduction =
        "Use this when no one can sign in as an administrator. Enter the administrator's user name and choose a new password. " +
        "If no account has that name, a new administrator is created. This needs Windows administrator rights on this PC.";

    public const string RunAsAdministratorMessage =
        "Close the app, then right-click SabhaMujic and choose 'Run as administrator' to reset an administrator password.";

    public const string ElevationNeededMessage =
        "Resetting an administrator password needs Windows administrator rights. Choose ‘Restart as administrator’ and confirm the Windows prompt.";

    public const string RestartCancelledMessage =
        "The Windows prompt was cancelled, so SabhaMujic is still running without administrator rights. " +
        "Choose ‘Restart as administrator’ again, or right-click SabhaMujic and choose 'Run as administrator'.";

    public const string RestartFailedMessage =
        "SabhaMujic could not restart itself. " + RunAsAdministratorMessage;

    public const string NotAnAdministratorMessage =
        "That account is not an administrator. Enter an administrator's user name, or a new name to create a new administrator.";

    private readonly ISabhaClient _client;
    private readonly IAppRestarter? _restarter;

    public RecoveryViewModel(ISabhaClient client, IAppRestarter? restarter = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _restarter = restarter;
    }

    /// <summary>Raised after the password was reset (or the administrator created); the app is then signed in with this session.</summary>
    public event Action<SessionDto>? RecoveryCompleted;

    /// <summary>Raised by "Back to sign in".</summary>
    public event Action? Closed;

    /// <summary>Raised when the view should clear both PasswordBoxes.</summary>
    public event Action? SecretsCleared;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial string AdminName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyPropertyChangedFor(nameof(ValidationHint))]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyPropertyChangedFor(nameof(ValidationHint))]
    public partial string ConfirmSecret { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>True when the service refused because the app is not running elevated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartAsAdministrator))]
    [NotifyCanExecuteChangedFor(nameof(RestartAsAdministratorCommand))]
    public partial bool NeedsElevation { get; private set; }

    /// <summary>Shows the "Restart as administrator" button.</summary>
    public bool CanRestartAsAdministrator => NeedsElevation && _restarter is not null;

    /// <summary>True after the elevated copy was started (this window is about to close).</summary>
    [ObservableProperty]
    public partial bool IsRestarting { get; private set; }

    /// <summary>Inline guidance while typing (null when the form is fine or still empty).</summary>
    public string? ValidationHint =>
        Secret.Length == 0 ? null
        : Secret.Length < MinSecretLength ? $"The new password must have at least {MinSecretLength} characters."
        : ConfirmSecret.Length > 0 && ConfirmSecret != Secret ? "The two entries do not match."
        : null;

    private bool CanReset() =>
        IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(AdminName) && Secret.Length >= MinSecretLength && Secret == ConfirmSecret;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        IsBusy = true;
        ResetCommand.NotifyCanExecuteChanged();
        try
        {
            ErrorMessage = null;
            NeedsElevation = false;
            var session = await _client.Auth.ResetAdministratorAsync(AdminName.Trim(), Secret).ConfigureAwait(true);
            Clear();
            RecoveryCompleted?.Invoke(session);
        }
        catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Forbidden)
        {
            NeedsElevation = true;
            ErrorMessage = _restarter is null ? RunAsAdministratorMessage : ElevationNeededMessage;
        }
        catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Conflict)
        {
            ErrorMessage = NotAnAdministratorMessage;
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            Secret = "";
            ConfirmSecret = "";
            SecretsCleared?.Invoke();
            IsBusy = false;
            ResetCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartAsAdministrator))]
    private void RestartAsAdministrator()
    {
        switch (_restarter!.RestartAsAdministrator(StartupArgument))
        {
            case RestartResult.Restarting:
                IsRestarting = true;
                ErrorMessage = null;
                break;
            case RestartResult.Cancelled:
                ErrorMessage = RestartCancelledMessage;
                break;
            default:
                ErrorMessage = RestartFailedMessage;
                break;
        }
    }

    [RelayCommand]
    private void Close()
    {
        Clear();
        Closed?.Invoke();
    }

    /// <summary>Resets the form (name, messages, elevation state) for the next time it opens.</summary>
    public void Clear()
    {
        AdminName = "";
        Secret = "";
        ConfirmSecret = "";
        SecretsCleared?.Invoke();
        ErrorMessage = null;
        NeedsElevation = false;
    }
}
