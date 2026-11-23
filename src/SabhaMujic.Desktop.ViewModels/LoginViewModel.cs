using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>
/// Administrator sign-in: user name + password. Opened from "Admin sign-in" (the player itself needs no sign-in); the
/// password is cleared after every attempt.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;

    public LoginViewModel(ISabhaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Raised after a successful sign-in with the new session.</summary>
    public event Action<SessionDto>? LoggedIn;

    /// <summary>Raised by the "Forgot admin password?" link.</summary>
    public event Action? RecoveryRequested;

    /// <summary>Raised by "Back" (close the sign-in form, keep using the player).</summary>
    public event Action? Closed;

    [RelayCommand]
    private void Close()
    {
        ErrorMessage = null;
        Secret = "";
        SecretCleared?.Invoke();
        Closed?.Invoke();
    }

    /// <summary>"Forgot admin password?": opens the administrator password recovery screen.</summary>
    [RelayCommand]
    private void ForgotAdminPin()
    {
        ErrorMessage = null;
        Secret = "";
        SecretCleared?.Invoke();
        RecoveryRequested?.Invoke();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string UserName { get; set; } = "";

    /// <summary>Set from the PasswordBox (WPF cannot bind it directly). Never persisted.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>Why the user was signed out (e.g. session expired), shown above the form.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>Raised when the view should clear the PasswordBox.</summary>
    public event Action? SecretCleared;

    private bool CanLogin() =>
        IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrEmpty(Secret);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        IsBusy = true;
        LoginCommand.NotifyCanExecuteChanged();
        try
        {
            ErrorMessage = null;
            var session = await _client.Auth.LoginAsync(UserName.Trim(), Secret).ConfigureAwait(true);
            Notice = null;
            LoggedIn?.Invoke(session);
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.ForLogin(ex);
        }
        finally
        {
            Secret = "";
            SecretCleared?.Invoke();
            IsBusy = false;
            LoginCommand.NotifyCanExecuteChanged();
        }
    }
}
