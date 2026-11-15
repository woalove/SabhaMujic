using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>
/// Admin: paired remote devices (list, revoke) and pairing a new device. Pairing shows the one-time code,
/// a QR code of <see cref="PairingCodeDto.PairingUrl"/> and a countdown to <see cref="PairingCodeDto.ExpiresAt"/>.
/// </summary>
public sealed partial class DevicesAdminViewModel : ViewModelBase
{
    public static readonly TimeSpan CountdownInterval = TimeSpan.FromSeconds(1);

    public const string FingerprintHint = "Check this matches the fingerprint on the phone's setup page";

    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private IDisposable? _countdown;

    public DevicesAdminViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public ObservableCollection<DeviceDto> Devices { get; } = [];

    public IReadOnlyList<Role> Roles { get; } = [Role.Volunteer, Role.Admin];

    [ObservableProperty]
    public partial DeviceDto? SelectedDevice { get; set; }

    /// <summary>Role the next paired device will get.</summary>
    [ObservableProperty]
    public partial Role PairingRole { get; set; } = Role.Volunteer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairingVisible), nameof(CaFingerprintText), nameof(IsFingerprintVisible))]
    public partial PairingCodeDto? Pairing { get; private set; }

    /// <summary>The local CA fingerprint in lines of 8 bytes, for comparing with the phone's setup page; null without a remote.</summary>
    public string? CaFingerprintText => Pairing?.CaFingerprint is { Length: > 0 } fingerprint ? FormatFingerprint(fingerprint) : null;

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

    /// <summary>PNG bytes of the QR code for <see cref="PairingCodeDto.PairingUrl"/>.</summary>
    [ObservableProperty]
    public partial byte[]? PairingQrPng { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownText))]
    public partial TimeSpan Remaining { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownText))]
    public partial bool IsPairingExpired { get; private set; }

    public bool IsPairingVisible => Pairing is not null;

    public string CountdownText => Pairing is null
        ? ""
        : IsPairingExpired
            ? "This code has expired. Create a new one."
            : $"Expires in {(int)Remaining.TotalMinutes}:{Remaining.Seconds:00}";

    public Task<bool> LoadAsync() => TryAsync(ReloadAsync, clearError: false);

    [RelayCommand]
    private Task RefreshAsync() => TryAsync(ReloadAsync);

    [RelayCommand]
    private Task PairNewDeviceAsync() => TryAsync(async () =>
    {
        var code = await _client.Admin.CreatePairingCodeAsync(PairingRole).ConfigureAwait(true);
        StopCountdown();
        Pairing = code;
        PairingQrPng = QrCodeRenderer.RenderPng(code.PairingUrl);
        IsPairingExpired = false;
        UpdateCountdown();
        if (!IsPairingExpired)
        {
            _countdown = _ui.Scheduler.SchedulePeriodic(CountdownInterval, UpdateCountdown);
        }
    });

    /// <summary>Hides the pairing panel (e.g. the phone is paired) and refreshes the device list.</summary>
    [RelayCommand]
    private Task ClosePairingAsync()
    {
        StopCountdown();
        Pairing = null;
        PairingQrPng = null;
        IsPairingExpired = false;
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
            "Revoke device",
            $"Revoke \"{device.Name}\"? It will be signed out immediately and must be paired again.",
            "Revoke").ConfigureAwait(true);
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
        StopCountdown();
        Pairing = null;
        PairingQrPng = null;
        Devices.Clear();
    }

    private void UpdateCountdown()
    {
        if (Pairing is null)
        {
            StopCountdown();
            return;
        }

        var remaining = Pairing.ExpiresAt - _ui.Scheduler.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Remaining = TimeSpan.Zero;
            IsPairingExpired = true;
            PairingQrPng = null; // never show a QR code that no longer works
            StopCountdown();
            Run(ReloadAsync);    // the device may have paired just before expiry
            return;
        }

        // Round up so the display reaches 0:00 exactly at expiry, never early.
        Remaining = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
    }

    private void StopCountdown()
    {
        _countdown?.Dispose();
        _countdown = null;
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
