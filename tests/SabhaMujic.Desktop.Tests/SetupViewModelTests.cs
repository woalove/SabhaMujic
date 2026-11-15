using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Tests;

public class SetupViewModelTests
{
    private static (ShellViewModel Shell, FakeSabhaClient Client) FreshInstall(bool setupRequired = true)
    {
        var client = new FakeSabhaClient();
        client.Auth.SetupRequired = setupRequired;
        var shell = new ShellViewModel(client, new TestUi().Services);
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);
        return (shell, client);
    }

    private static void Fill(SetupViewModel setup, string name = " Ramesh ", string secret = "4321", string? confirm = null)
    {
        setup.AdminName = name;
        setup.Secret = secret;
        setup.ConfirmSecret = confirm ?? secret;
    }

    [Fact]
    public void Fresh_install_shows_setup_instead_of_login()
    {
        var (shell, client) = FreshInstall();
        Assert.Single(client.CallsTo("IsSetupRequiredAsync"));
        Assert.True(shell.IsSetupRequired);
        Assert.True(shell.IsSetupVisible);
        Assert.False(shell.IsLoginVisible);
    }

    [Fact]
    public void Configured_service_shows_login()
    {
        var (shell, _) = FreshInstall(setupRequired: false);
        Assert.False(shell.IsSetupVisible);
        Assert.True(shell.IsLoginVisible);
    }

    [Fact]
    public void Setup_is_not_checked_before_connecting()
    {
        var client = new FakeSabhaClient();
        client.Auth.SetupRequired = true;
        var shell = new ShellViewModel(client, new TestUi().Services);
        shell.Start();
        Assert.Empty(client.CallsTo("IsSetupRequiredAsync"));
        Assert.False(shell.IsSetupVisible);
    }

    [Theory]
    [InlineData("", "1234", "1234")]
    [InlineData("admin", "123", "123")]
    [InlineData("admin", "1234", "1235")]
    [InlineData("admin", "1234", "")]
    public void Create_is_disabled_until_the_form_is_valid(string name, string secret, string confirm)
    {
        var (shell, _) = FreshInstall();
        Fill(shell.Setup, name, secret, confirm);
        Assert.False(shell.Setup.CompleteSetupCommand.CanExecute(null));
        Fill(shell.Setup);
        Assert.True(shell.Setup.CompleteSetupCommand.CanExecute(null));
    }

    [Fact]
    public void Validation_hint_explains_short_or_mismatched_secrets()
    {
        var (shell, _) = FreshInstall();
        Assert.Null(shell.Setup.ValidationHint);
        shell.Setup.Secret = "12";
        Assert.Contains("at least 4", shell.Setup.ValidationHint);
        shell.Setup.Secret = "1234";
        shell.Setup.ConfirmSecret = "9";
        Assert.Contains("do not match", shell.Setup.ValidationHint);
        shell.Setup.ConfirmSecret = "1234";
        Assert.Null(shell.Setup.ValidationHint);
    }

    [Fact]
    public async Task Successful_setup_signs_in_as_the_new_admin()
    {
        var (shell, client) = FreshInstall();
        var cleared = false;
        shell.Setup.SecretsCleared += () => cleared = true;
        Fill(shell.Setup);

        await shell.Setup.CompleteSetupCommand.ExecuteAsync(null);

        var call = Assert.Single(client.CallsTo("CompleteInitialSetupAsync"));
        Assert.Equal(["Ramesh", "4321"], call.Args);
        Assert.True(shell.IsSignedIn);
        Assert.True(shell.IsAdmin);
        Assert.False(shell.IsSetupVisible);
        Assert.False(shell.IsLoginVisible);
        Assert.Equal("", shell.Setup.Secret);
        Assert.Equal("", shell.Setup.ConfirmSecret);
        Assert.True(cleared);
        Assert.Null(shell.Setup.ErrorMessage);
    }

    [Fact]
    public async Task Not_elevated_explains_run_as_administrator()
    {
        var (shell, client) = FreshInstall();
        client.Auth.OnCompleteSetup = (_, _) => throw new SabhaMujicException(ErrorCode.Forbidden, "server text");
        Fill(shell.Setup);

        await shell.Setup.CompleteSetupCommand.ExecuteAsync(null);

        Assert.Equal(SetupViewModel.RunAsAdministratorMessage, shell.Setup.ErrorMessage);
        Assert.Contains("Run as administrator", shell.Setup.ErrorMessage);
        Assert.True(shell.Setup.NeedsElevation);
        Assert.False(shell.IsSignedIn);
        Assert.True(shell.IsSetupVisible);
        Assert.Equal("", shell.Setup.Secret);
    }

    [Fact]
    public async Task Already_set_up_switches_to_login()
    {
        var (shell, client) = FreshInstall();
        client.Auth.OnCompleteSetup = (_, _) => throw new SabhaMujicException(ErrorCode.Conflict, "already");
        Fill(shell.Setup);

        await shell.Setup.CompleteSetupCommand.ExecuteAsync(null);

        Assert.False(shell.IsSetupRequired);
        Assert.True(shell.IsLoginVisible);
        Assert.NotNull(shell.Setup.ErrorMessage);
    }

    [Fact]
    public async Task Other_errors_are_shown_and_the_form_stays()
    {
        var (shell, client) = FreshInstall();
        client.Auth.OnCompleteSetup = (_, _) => throw new SabhaMujicException(ErrorCode.InvalidArgument, "User name is too long.");
        Fill(shell.Setup);

        await shell.Setup.CompleteSetupCommand.ExecuteAsync(null);

        Assert.Contains("User name is too long.", shell.Setup.ErrorMessage);
        Assert.False(shell.Setup.NeedsElevation);
        Assert.True(shell.IsSetupVisible);
    }

    [Fact]
    public async Task Demo_client_reports_already_set_up()
    {
        await using var demo = new ViewModels.Demo.DemoSabhaClient(autoTick: false);
        await demo.ConnectAsync();
        Assert.False(await demo.Auth.IsSetupRequiredAsync());
        var ex = await Assert.ThrowsAsync<SabhaMujicException>(() => demo.Auth.CompleteInitialSetupAsync("x", "1234"));
        Assert.Equal(ErrorCode.Conflict, ex.Code);
    }
}
