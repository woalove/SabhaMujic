using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Tests;

/// <summary>Decision #30: on Forbidden the setup screen offers "Restart as administrator".</summary>
public class SetupRestartTests
{
    private sealed class FakeRestarter : IAppRestarter
    {
        public RestartResult Next { get; set; } = RestartResult.Restarting;

        public int Calls { get; private set; }

        public string? LastArgument { get; private set; }

        public RestartResult RestartAsAdministrator(string? startupArgument = null)
        {
            Calls++;
            LastArgument = startupArgument;
            return Next;
        }
    }

    private static (ShellViewModel Shell, FakeSabhaClient Client, FakeRestarter Restarter) Refused()
    {
        var client = new FakeSabhaClient();
        client.Auth.SetupRequired = true;
        client.Auth.OnCompleteSetup = (_, _) => throw new SabhaMujicException(ErrorCode.Forbidden, "not elevated");
        var restarter = new FakeRestarter();
        var shell = new ShellViewModel(client, new TestUi().Services with { Restarter = restarter });
        shell.Start();
        client.SetConnectionState(ConnectionState.Connected);
        return (shell, client, restarter);
    }

    private static async Task SubmitAsync(SetupViewModel setup)
    {
        setup.AdminName = "admin";
        setup.Secret = "pass-4321";
        setup.ConfirmSecret = "pass-4321";
        await setup.CompleteSetupCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Forbidden_offers_restart_as_administrator()
    {
        var (shell, _, restarter) = Refused();
        Assert.False(shell.Setup.CanRestartAsAdministrator);
        Assert.False(shell.Setup.RestartAsAdministratorCommand.CanExecute(null));

        await SubmitAsync(shell.Setup);

        Assert.True(shell.Setup.NeedsElevation);
        Assert.True(shell.Setup.CanRestartAsAdministrator);
        Assert.True(shell.Setup.RestartAsAdministratorCommand.CanExecute(null));
        Assert.Equal(SetupViewModel.ElevationNeededMessage, shell.Setup.ErrorMessage);
        Assert.Equal(0, restarter.Calls);
    }

    [Fact]
    public async Task Restart_hands_over_to_the_elevated_copy()
    {
        var (shell, _, restarter) = Refused();
        await SubmitAsync(shell.Setup);

        shell.Setup.RestartAsAdministratorCommand.Execute(null);

        Assert.Equal(1, restarter.Calls);
        Assert.True(shell.Setup.IsRestarting);
        Assert.Null(shell.Setup.ErrorMessage);
    }

    [Fact]
    public async Task Cancelled_uac_prompt_keeps_the_app_open_with_a_message()
    {
        var (shell, _, restarter) = Refused();
        restarter.Next = RestartResult.Cancelled;
        await SubmitAsync(shell.Setup);

        shell.Setup.RestartAsAdministratorCommand.Execute(null);

        Assert.False(shell.Setup.IsRestarting);
        Assert.Equal(SetupViewModel.RestartCancelledMessage, shell.Setup.ErrorMessage);
        Assert.True(shell.Setup.CanRestartAsAdministrator); // may try again
        Assert.True(shell.IsSetupVisible);
    }

    [Fact]
    public async Task A_failed_restart_explains_the_manual_way()
    {
        var (shell, _, restarter) = Refused();
        restarter.Next = RestartResult.Failed;
        await SubmitAsync(shell.Setup);

        shell.Setup.RestartAsAdministratorCommand.Execute(null);

        Assert.Contains("Run as administrator", shell.Setup.ErrorMessage);
        Assert.False(shell.Setup.IsRestarting);
    }

    [Fact]
    public async Task Without_a_restarter_only_the_manual_instructions_are_shown()
    {
        var client = new FakeSabhaClient();
        client.Auth.OnCompleteSetup = (_, _) => throw new SabhaMujicException(ErrorCode.Forbidden, "not elevated");
        var setup = new SetupViewModel(client) { IsConnected = true };
        await SubmitAsync(setup);
        Assert.Equal(SetupViewModel.RunAsAdministratorMessage, setup.ErrorMessage);
        Assert.False(setup.CanRestartAsAdministrator);
    }

    [Fact]
    public async Task Other_errors_do_not_offer_a_restart()
    {
        var (shell, client, _) = Refused();
        client.Auth.OnCompleteSetup = (_, _) => throw new SabhaMujicException(ErrorCode.InvalidArgument, "bad name");
        await SubmitAsync(shell.Setup);
        Assert.False(shell.Setup.CanRestartAsAdministrator);
    }
}
