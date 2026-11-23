using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Tests;

/// <summary>"Forgot admin PIN?": the recovery screen, its elevation restart, and the shell wiring.</summary>
public class RecoveryViewModelTests
{
    private sealed class FakeRestarter : IAppRestarter
    {
        public RestartResult Next { get; set; } = RestartResult.Restarting;

        public List<string?> Arguments { get; } = [];

        public RestartResult RestartAsAdministrator(string? startupArgument = null)
        {
            Arguments.Add(startupArgument);
            return Next;
        }
    }

    private static (ShellViewModel Shell, FakeSabhaClient Client, FakeRestarter Restarter) SignedOut()
    {
        var client = new FakeSabhaClient();
        var restarter = new FakeRestarter();
        var shell = new ShellViewModel(client, new TestUi().Services with { Restarter = restarter });
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);
        return (shell, client, restarter);
    }

    private static async Task SubmitAsync(RecoveryViewModel recovery, string name = "admin", string pin = "9876")
    {
        recovery.AdminName = name;
        recovery.Secret = pin;
        recovery.ConfirmSecret = pin;
        await recovery.ResetCommand.ExecuteAsync(null);
    }

    [Fact]
    public void Forgot_admin_pin_link_opens_recovery_and_back_returns_to_sign_in()
    {
        var (shell, _, _) = SignedOut();
        Assert.False(shell.IsLoginVisible); // open use: the sign-in form only shows on request
        shell.OpenAdminSignInCommand.Execute(null);
        Assert.True(shell.IsLoginVisible);
        Assert.False(shell.IsRecoveryVisible);

        shell.Login.Secret = "half-typed";
        shell.Login.ForgotAdminPinCommand.Execute(null);

        Assert.True(shell.IsRecoveryVisible);
        Assert.False(shell.IsLoginVisible);
        Assert.Equal("", shell.Login.Secret);

        shell.Recovery.AdminName = "someone";
        shell.Recovery.CloseCommand.Execute(null);
        Assert.True(shell.IsLoginVisible);
        Assert.False(shell.IsRecoveryVisible);
        Assert.Equal("", shell.Recovery.AdminName);
    }

    [Fact]
    public void The_form_needs_a_name_and_matching_passwords_of_eight_characters()
    {
        var recovery = new RecoveryViewModel(new FakeSabhaClient()) { IsConnected = true };
        Assert.False(recovery.ResetCommand.CanExecute(null));
        recovery.AdminName = "admin";
        recovery.Secret = "1234567";
        Assert.Contains("at least 8", recovery.ValidationHint);
        recovery.Secret = "12345678";
        recovery.ConfirmSecret = "12345679";
        Assert.Equal("The two entries do not match.", recovery.ValidationHint);
        Assert.False(recovery.ResetCommand.CanExecute(null));
        recovery.ConfirmSecret = "12345678";
        Assert.Null(recovery.ValidationHint);
        Assert.True(recovery.ResetCommand.CanExecute(null));
        recovery.IsConnected = false;
        Assert.False(recovery.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task Elevated_reset_signs_in_as_that_administrator()
    {
        var (shell, client, restarter) = SignedOut();
        shell.Login.ForgotAdminPinCommand.Execute(null);
        var cleared = 0;
        shell.Recovery.SecretsCleared += () => cleared++;

        await SubmitAsync(shell.Recovery, " Ramesh ", "9876");

        var call = Assert.Single(client.CallsTo(nameof(IAuthApi.ResetAdministratorAsync)));
        Assert.Equal(["Ramesh", "9876"], call.Args);
        Assert.True(shell.IsSignedIn);
        Assert.True(shell.IsAdmin);
        Assert.False(shell.IsRecoveryVisible);
        Assert.False(shell.IsRecoveryOpen);
        Assert.Equal("", shell.Recovery.Secret);
        Assert.Equal("", shell.Recovery.ConfirmSecret);
        Assert.True(cleared > 0);
        Assert.Empty(restarter.Arguments);
    }

    [Fact]
    public async Task Not_elevated_offers_restart_as_administrator_back_into_recovery()
    {
        var (shell, client, restarter) = SignedOut();
        client.Auth.OnResetAdministrator = (_, _) => throw new SabhaMujicException(ErrorCode.Forbidden, "not elevated");
        shell.OpenRecovery();
        Assert.False(shell.Recovery.CanRestartAsAdministrator);

        await SubmitAsync(shell.Recovery);

        Assert.False(shell.IsSignedIn);
        Assert.True(shell.Recovery.NeedsElevation);
        Assert.True(shell.Recovery.CanRestartAsAdministrator);
        Assert.Equal(RecoveryViewModel.ElevationNeededMessage, shell.Recovery.ErrorMessage);
        Assert.Equal("", shell.Recovery.Secret);

        shell.Recovery.RestartAsAdministratorCommand.Execute(null);
        Assert.Equal([RecoveryViewModel.StartupArgument], restarter.Arguments);
        Assert.Equal("--recover-admin", RecoveryViewModel.StartupArgument);
        Assert.True(shell.Recovery.IsRestarting);
        Assert.Null(shell.Recovery.ErrorMessage);
    }

    [Fact]
    public async Task Cancelled_or_failed_restarts_explain_what_to_do()
    {
        var (shell, client, restarter) = SignedOut();
        client.Auth.OnResetAdministrator = (_, _) => throw new SabhaMujicException(ErrorCode.Forbidden, "not elevated");
        shell.OpenRecovery();
        await SubmitAsync(shell.Recovery);

        restarter.Next = RestartResult.Cancelled;
        shell.Recovery.RestartAsAdministratorCommand.Execute(null);
        Assert.Equal(RecoveryViewModel.RestartCancelledMessage, shell.Recovery.ErrorMessage);
        Assert.False(shell.Recovery.IsRestarting);
        Assert.True(shell.Recovery.CanRestartAsAdministrator);

        restarter.Next = RestartResult.Failed;
        shell.Recovery.RestartAsAdministratorCommand.Execute(null);
        Assert.Equal(RecoveryViewModel.RestartFailedMessage, shell.Recovery.ErrorMessage);
        Assert.True(shell.IsRecoveryVisible);
    }

    [Fact]
    public async Task Without_a_restarter_only_the_manual_instructions_are_shown()
    {
        var client = new FakeSabhaClient();
        client.Auth.OnResetAdministrator = (_, _) => throw new SabhaMujicException(ErrorCode.Forbidden, "not elevated");
        var recovery = new RecoveryViewModel(client) { IsConnected = true };
        await SubmitAsync(recovery);
        Assert.Equal(RecoveryViewModel.RunAsAdministratorMessage, recovery.ErrorMessage);
        Assert.False(recovery.CanRestartAsAdministrator);
    }

    [Fact]
    public async Task A_volunteer_name_explains_and_other_errors_do_not_offer_a_restart()
    {
        var (shell, client, _) = SignedOut();
        shell.OpenRecovery();
        client.Auth.OnResetAdministrator = (_, _) => throw new SabhaMujicException(ErrorCode.Conflict, "'vol' is not an administrator.");
        await SubmitAsync(shell.Recovery, "vol");
        Assert.Equal(RecoveryViewModel.NotAnAdministratorMessage, shell.Recovery.ErrorMessage);
        Assert.False(shell.Recovery.CanRestartAsAdministrator);

        client.Auth.OnResetAdministrator = (_, _) => throw new SabhaMujicException(ErrorCode.RateLimited, "slow down");
        await SubmitAsync(shell.Recovery);
        Assert.Equal(ErrorMessages.For(ErrorCode.RateLimited), shell.Recovery.ErrorMessage);
        Assert.False(shell.Recovery.CanRestartAsAdministrator);
        Assert.False(shell.IsSignedIn);
    }

    [Fact]
    public void First_run_setup_takes_precedence_over_recovery()
    {
        var client = new FakeSabhaClient();
        client.Auth.SetupRequired = true;
        var shell = new ShellViewModel(client, new TestUi().Services);
        shell.OpenRecovery(); // e.g. started with --recover-admin on a fresh install
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);

        Assert.True(shell.IsSetupVisible);
        Assert.False(shell.IsRecoveryVisible);
        Assert.False(shell.IsLoginVisible);
    }

    [Fact]
    public async Task The_demo_client_resets_and_creates_administrators()
    {
        await using var demo = new ViewModels.Demo.DemoSabhaClient();
        await demo.ConnectAsync();

        var reset = await demo.Auth.ResetAdministratorAsync(ViewModels.Demo.DemoSabhaClient.AdminUser.ToUpperInvariant(), "new-password");
        Assert.Equal((ViewModels.Demo.DemoSabhaClient.AdminUser, Role.Admin), (reset.UserName, reset.Role));
        await demo.Auth.LogoutAsync();
        await demo.Auth.LoginAsync(ViewModels.Demo.DemoSabhaClient.AdminUser, "new-password");

        var created = await demo.Auth.ResetAdministratorAsync("rescue", "rescue-password");
        Assert.Equal(Role.Admin, created.Role);
        await Assert.ThrowsAsync<SabhaMujicException>(() =>
            demo.Auth.ResetAdministratorAsync(ViewModels.Demo.DemoSabhaClient.VolunteerUser, "x-password-1"));
        await Assert.ThrowsAsync<SabhaMujicException>(() => demo.Auth.ResetAdministratorAsync("short", "1234567"));
    }
}
