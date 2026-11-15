using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels;

/// <summary>Sign-in form: user name + PIN/password. The secret is cleared after every attempt.</summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;

    public LoginViewModel(ISabhaClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Raised after a successful sign-in with the new session.</summary>
    public event Action<SessionDto>? LoggedIn;

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
