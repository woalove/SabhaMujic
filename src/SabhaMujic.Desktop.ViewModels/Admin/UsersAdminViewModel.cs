using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>Admin: list, create, reset password, delete administrator accounts (volunteers need no account).</summary>
public sealed partial class UsersAdminViewModel : ViewModelBase
{
    /// <summary>Client-side minimum; the service applies its own policy too.</summary>
    public const int MinSecretLength = 8;

    private readonly ISabhaClient _client;
    private readonly UiServices _ui;

    public UsersAdminViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public ObservableCollection<UserDto> Users { get; } = [];

    /// <summary>Only administrators get accounts (contract v2); volunteers use the app without signing in.</summary>
    public IReadOnlyList<Role> Roles { get; } = [Role.Admin];

    [ObservableProperty]
    public partial UserDto? SelectedUser { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewUserName { get; set; } = "";

    [ObservableProperty]
    public partial Role NewUserRole { get; set; } = Role.Admin;

    /// <summary>Set from a PasswordBox by the view.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewUserSecret { get; set; } = "";

    /// <summary>Raised when the view should clear its PasswordBox.</summary>
    public event Action? SecretCleared;

    public Task<bool> LoadAsync() => TryAsync(ReloadAsync, clearError: false);

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewUserName) && NewUserSecret.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync()
    {
        if (NewUserSecret.Length < MinSecretLength)
        {
            ErrorMessage = $"The password must be at least {MinSecretLength} characters.";
            return Task.CompletedTask;
        }

        var name = NewUserName.Trim();
        var role = NewUserRole;
        var secret = NewUserSecret;
        return TryAsync(async () =>
        {
            try
            {
                await _client.Admin.CreateUserAsync(name, role, secret).ConfigureAwait(true);
                NewUserName = "";
                NewUserRole = Role.Admin;
                StatusMessage = $"Created {name} ({role}).";
                await ReloadAsync().ConfigureAwait(true);
            }
            finally
            {
                NewUserSecret = "";
                SecretCleared?.Invoke();
            }
        });
    }

    [RelayCommand]
    private async Task ResetSecretAsync(UserDto? user)
    {
        user ??= SelectedUser;
        if (user is null)
        {
            return;
        }

        var secret = await _ui.Dialogs.PromptSecretAsync("Reset password", $"New password for {user.UserName}:").ConfigureAwait(true);
        if (secret is null)
        {
            return;
        }

        if (secret.Length < MinSecretLength)
        {
            ErrorMessage = $"The password must be at least {MinSecretLength} characters.";
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Admin.SetUserSecretAsync(user.Id, secret).ConfigureAwait(true);
            StatusMessage = $"The password for {user.UserName} was changed.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(UserDto? user)
    {
        user ??= SelectedUser;
        if (user is null)
        {
            return;
        }

        var self = string.Equals(_client.Session?.UserName, user.UserName, StringComparison.OrdinalIgnoreCase);
        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Delete user",
            self
                ? $"\"{user.UserName}\" is the account you are signed in with. Delete it anyway?"
                : $"Delete the user \"{user.UserName}\"?",
            "Delete").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Admin.DeleteUserAsync(user.Id).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        var users = await _client.Admin.GetUsersAsync().ConfigureAwait(true);
        Users.Clear();
        foreach (var u in users.OrderBy(u => u.UserName, StringComparer.CurrentCultureIgnoreCase))
        {
            Users.Add(u);
        }
    }
}
