using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>
/// First-run setup (decision #24): shown instead of the sign-in form while the service has no accounts.
/// Creates the first administrator; the service only allows it for this app started with "Run as administrator".
/// Secrets are cleared after every attempt.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    public const int MinSecretLength = 4;

    public const string RunAsAdministratorMessage =
        "Close the app, then right-click SabhaMujic and choose 'Run as administrator' to complete first-time setup.";

    private readonly ISabhaClient _client;

    public SetupViewModel(ISabhaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Raised after the administrator was created; the app is then signed in with this session.</summary>
    public event Action<SessionDto>? SetupCompleted;

    /// <summary>Raised when the service reports it is already set up (another admin finished first).</summary>
    public event Action? SetupNoLongerRequired;

    /// <summary>Raised when the view should clear both PasswordBoxes.</summary>
    public event Action? SecretsCleared;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteSetupCommand))]
    public partial string AdminName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteSetupCommand))]
    [NotifyPropertyChangedFor(nameof(ValidationHint))]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteSetupCommand))]
    [NotifyPropertyChangedFor(nameof(ValidationHint))]
    public partial string ConfirmSecret { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteSetupCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>True when the service refused because the app is not running elevated.</summary>
    [ObservableProperty]
    public partial bool NeedsElevation { get; private set; }

    /// <summary>Inline guidance while typing (null when the form is fine or still empty).</summary>
    public string? ValidationHint =>
        Secret.Length == 0 ? null
        : Secret.Length < MinSecretLength ? $"The PIN or password must have at least {MinSecretLength} characters."
        : ConfirmSecret.Length > 0 && ConfirmSecret != Secret ? "The two entries do not match."
        : null;

    private bool CanCompleteSetup() =>
        IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(AdminName) && Secret.Length >= MinSecretLength && Secret == ConfirmSecret;

    [RelayCommand(CanExecute = nameof(CanCompleteSetup))]
    private async Task CompleteSetupAsync()
    {
        IsBusy = true;
        CompleteSetupCommand.NotifyCanExecuteChanged();
        try
        {
            ErrorMessage = null;
            NeedsElevation = false;
            var session = await _client.Auth.CompleteInitialSetupAsync(AdminName.Trim(), Secret).ConfigureAwait(true);
            SetupCompleted?.Invoke(session);
        }
        catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Forbidden)
        {
            NeedsElevation = true;
            ErrorMessage = RunAsAdministratorMessage;
        }
        catch (SabhaMujicException ex) when (ex.Code == ErrorCode.Conflict)
        {
            ErrorMessage = "SabhaMujic is already set up. Sign in with an existing account.";
            SetupNoLongerRequired?.Invoke();
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
            CompleteSetupCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Asks the service whether setup is still needed. Returns false when it cannot tell.</summary>
    public async Task<bool> IsSetupRequiredAsync()
    {
        try
        {
            return await _client.Auth.IsSetupRequiredAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
