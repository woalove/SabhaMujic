using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels.Admin;

namespace SabhaMujic.Desktop.Tests;

public class PairingFingerprintTests
{
    private static readonly string Fingerprint = string.Join(':', Enumerable.Range(0, 32).Select(i => i.ToString("X2")));

    private static (DevicesAdminViewModel Vm, FakeSabhaClient Client) Create(string? fingerprint)
    {
        var client = new FakeSabhaClient();
        var ui = new TestUi();
        client.Admin.OnPairing = role => new PairingCodeDto("482913", "http://192.168.1.20:8080/#code=482913", "http://192.168.1.20:8080/", role,
            ui.Scheduler.UtcNow.AddMinutes(5)) { CaFingerprint = fingerprint };
        return (new DevicesAdminViewModel(client, ui.Services), client);
    }

    [Fact]
    public async Task Pairing_panel_shows_the_ca_fingerprint_in_groups()
    {
        var (vm, _) = Create(Fingerprint);
        Assert.False(vm.IsFingerprintVisible);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.PairNewDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsFingerprintVisible);
        Assert.Contains(nameof(vm.CaFingerprintText), changed);
        var lines = vm.CaFingerprintText!.Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.Equal("00:01:02:03:04:05:06:07", lines[0]);
        Assert.Equal("18:19:1A:1B:1C:1D:1E:1F", lines[3]);
        Assert.Equal(Fingerprint, string.Join(':', lines)); // nothing lost or reordered
        Assert.Equal("Check this matches the fingerprint on the phone's setup page", DevicesAdminViewModel.FingerprintHint);

        await vm.ClosePairingCommand.ExecuteAsync(null);
        Assert.False(vm.IsFingerprintVisible);
        Assert.Null(vm.CaFingerprintText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_fingerprint_without_a_remote(string? fingerprint)
    {
        var (vm, _) = Create(fingerprint);
        await vm.PairNewDeviceCommand.ExecuteAsync(null);
        Assert.True(vm.IsPairingVisible);
        Assert.False(vm.IsFingerprintVisible);
    }

    [Theory]
    [InlineData("not a fingerprint")]
    [InlineData("AB")]
    [InlineData("ZZ:YY")]
    public void Unexpected_formats_are_shown_unchanged(string value) =>
        Assert.Equal(value, DevicesAdminViewModel.FormatFingerprint(value));

    [Fact]
    public void Lower_case_is_normalised_and_short_lines_are_kept()
    {
        Assert.Equal($"AB:CD:EF{Environment.NewLine}01", DevicesAdminViewModel.FormatFingerprint("ab:cd:ef:01", bytesPerLine: 3));
    }

    [Fact]
    public async Task Demo_client_shows_a_fingerprint()
    {
        await using var demo = new ViewModels.Demo.DemoSabhaClient(autoTick: false);
        await demo.ConnectAsync();
        await demo.Auth.LoginAsync(ViewModels.Demo.DemoSabhaClient.AdminUser, ViewModels.Demo.DemoSabhaClient.AdminSecret);
        var code = await demo.Admin.CreatePairingCodeAsync(Role.Volunteer);
        Assert.Equal(32, code.CaFingerprint!.Split(':').Length);
    }
}
