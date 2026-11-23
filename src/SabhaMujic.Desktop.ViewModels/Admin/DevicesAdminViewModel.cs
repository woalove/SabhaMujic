using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>
/// Admin: phones. The list shows phones remembered at remote sign-in ("Remember this phone"; revoke signs one out).
/// "Set up a phone" shows the setup page address as text and QR code (<see cref="PhoneSetupInfoDto.BootstrapUrl"/>), the
/// remote's address and the CA fingerprint to compare. There are no codes: on the phone an administrator signs in with
/// their name and password.
/// </summary>
public sealed partial class DevicesAdminViewModel : ViewModelBase
{
    public const string FingerprintHint = "Check this matches the fingerprint on the phone's setup page";

    public const string SetupInstructions =
        "On the iPhone, open the camera and scan this code (or type the address into Safari). Follow the steps to install " +
        "the certificate, then sign in with an administrator name and password.";

    public const string RemoteOffMessage =
        "The phone remote is not running on this PC, so these addresses will not work until it is turned on.";

    private readonly ISabhaClient _client;
    private readonly UiServices _ui;

    public DevicesAdminViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public ObservableCollection<DeviceDto> Devices { get; } = [];

    [ObservableProperty]
    public partial DeviceDto? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupVisible), nameof(CaFingerprintText), nameof(IsFingerprintVisible), nameof(IsRemoteOff))]
    public partial PhoneSetupInfoDto? Setup { get; private set; }

    /// <summary>PNG bytes of the QR code for <see cref="PhoneSetupInfoDto.BootstrapUrl"/>.</summary>
    [ObservableProperty]
    public partial byte[]? SetupQrPng { get; private set; }

    public bool IsSetupVisible => Setup is not null;

    /// <summary>True when the setup panel is open but the remote is not running (the addresses will not answer).</summary>
    public bool IsRemoteOff => Setup is { IsRemoteRunning: false };

    /// <summary>The local CA fingerprint in lines of 8 bytes, for comparing with the phone's setup page; null without a remote.</summary>
    public string? CaFingerprintText => Setup?.CaFingerprint is { Length: > 0 } fingerprint ? FormatFingerprint(fingerprint) : null;

    public bool IsFingerprintVisible => CaFingerprintText is not null;

    /// <summary>
    /// "AB:CD:…" (32 bytes) → 4 lines of 8 bytes, each byte pair kept colon-separated like on the phone, so the halves are
    /// easy to compare a line at a time. Anything unexpected is returned unchanged.
    /// </summary>
    public static string FormatFingerprint(string fingerprint, int bytesPerLine = 8)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var bytes = fingerprint.Trim().Split(':');
        if (bytes.Length < 2 || bytes.Any(b => b.Length != 2 || !b.All(Uri.IsHexDigit)))
        {
            return fingerprint;
        }

        return string.Join(Environment.NewLine, bytes.Chunk(bytesPerLine).Select(line => string.Join(':', line).ToUpperInvariant()));
    }

    public Task<bool> LoadAsync() => TryAsync(ReloadAsync, clearError: false);

    [RelayCommand]
    private Task RefreshAsync() => TryAsync(ReloadAsync);

    /// <summary>Opens the "Set up a phone" panel.</summary>
    [RelayCommand]
    private Task SetUpPhoneAsync() => TryAsync(async () =>
    {
        var setup = await _client.Admin.GetPhoneSetupInfoAsync().ConfigureAwait(true);
        Setup = setup;
        SetupQrPng = QrCodeRenderer.RenderPng(setup.BootstrapUrl);
    });

    /// <summary>Hides the setup panel and refreshes the list (the phone may have signed in and been remembered).</summary>
    [RelayCommand]
    private Task CloseSetupAsync()
    {
        Setup = null;
        SetupQrPng = null;
        return TryAsync(ReloadAsync);
    }

    [RelayCommand]
    private async Task RevokeAsync(DeviceDto? device)
    {
        device ??= SelectedDevice;
        if (device is null)
        {
            return;
        }

        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Forget phone",
            $"Forget \"{device.Name}\"? It will be signed out immediately; an administrator can sign in on it again.",
            "Forget").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Admin.RevokeDeviceAsync(device.Id).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public void Reset()
    {
        Setup = null;
        SetupQrPng = null;
        Devices.Clear();
    }

    private async Task ReloadAsync()
    {
        var devices = await _client.Admin.GetDevicesAsync().ConfigureAwait(true);
        Devices.Clear();
        foreach (var d in devices.OrderByDescending(d => d.LastSeenAt ?? d.PairedAt))
        {
            Devices.Add(d);
        }
    }
}
