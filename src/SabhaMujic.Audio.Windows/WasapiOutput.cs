using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SabhaMujic.Audio.Output;

namespace SabhaMujic.Audio.Windows;

/// <summary>
/// WASAPI shared-mode, event-driven output on the default render device (~100 ms latency), using NAudio 3's
/// <see cref="WasapiPlayer"/> (<c>WasapiOut</c> is obsolete in NAudio 3). The Windows audio engine converts the
/// 48 kHz float stream to the device mix format (AUTOCONVERTPCM).
/// <para>
/// Device handling: when playback stops with an error (device unplugged, disabled, driver reset) or there is no
/// device, <see cref="DeviceLost"/> is raised and a background loop keeps trying to reopen the current default device
/// (0.5 s, backing off to 5 s). On success <see cref="DeviceRestored"/> is raised and playback resumes if it was
/// playing. A change of the default device moves the stream to the new default.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiOutput : IAudioOutput
{
    private readonly object _gate = new();
    private readonly int _latencyMilliseconds;
    private readonly CancellationTokenSource _lifetime = new();
    private IWaveProvider? _waveProvider;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private volatile WasapiPlayer? _player;
    private MMDevice? _device;
    private volatile bool _wantPlaying;
    private volatile bool _disposed;
    private bool _lost;
    private bool _reconnecting;

    public WasapiOutput(int latencyMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latencyMilliseconds, 10);
        _latencyMilliseconds = latencyMilliseconds;
    }

    public event EventHandler<AudioDeviceEventArgs>? DeviceLost;

    public event EventHandler<AudioDeviceEventArgs>? DeviceRestored;

    /// <summary>Friendly name of the device in use, or null while no device is open.</summary>
    public string? DeviceName { get; private set; }

    public bool IsDeviceLost
    {
        get
        {
            lock (_gate)
            {
                return _lost;
            }
        }
    }

    public void Init(ISampleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            throw new ArgumentException("The provider must produce IEEE float samples.", nameof(provider));
        }

        string? error;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_waveProvider is not null)
            {
                throw new InvalidOperationException("Init may only be called once.");
            }

            _waveProvider = new SampleToWaveProvider(provider);
            error = TryOpenLocked();
        }

        if (error is not null)
        {
            MarkLost(error, null);
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _wantPlaying = true;
            _player?.Play();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _wantPlaying = false;
            _player?.Pause();
        }
    }

    public void Stop()
    {
        WasapiPlayer? player;
        lock (_gate)
        {
            _wantPlaying = false;
            player = _player;
        }

        player?.Stop();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wantPlaying = false;
        }

        _lifetime.Cancel();
        if (_notifications is not null)
        {
            _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _notifications.Dispose();
        }

        lock (_gate)
        {
            CloseLocked();
        }

        _enumerator?.Dispose();
    }

    /// <summary>Opens the current default device. Returns null on success or a description of the failure.</summary>
    private string? TryOpenLocked()
    {
        CloseLocked();
        if (_waveProvider is null)
        {
            return "The output is not initialized.";
        }

        MMDevice? device;
        try
        {
            // Created lazily so a machine whose audio service is unavailable still gets DeviceLost + retries.
            if (_enumerator is null)
            {
                _enumerator = new MMDeviceEnumerator();
                _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
                _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            }

            if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device) || device is null)
            {
                return "No default audio output device is available.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Windows audio is unavailable ({ex.GetType().Name}: {ex.Message}).";
        }

        WasapiPlayer? player = null;
        try
        {
            player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(_latencyMilliseconds)
                .Build();
            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(_waveProvider);
            _player = player;
            _device = device;
            DeviceName = device.FriendlyName;
            if (_wantPlaying)
            {
                player.Play();
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (player is not null)
            {
                player.PlaybackStopped -= OnPlaybackStopped;
                TryDispose(player);
            }

            device.Dispose();
            return $"The audio device could not be opened ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    /// <summary>
    /// Disposes the player (which joins its thread). Safe under <see cref="_gate"/> because
    /// <see cref="OnPlaybackStopped"/>, which runs on that thread, never takes the lock.
    /// </summary>
    private void CloseLocked()
    {
        if (_player is { } player)
        {
            _player = null;
            player.PlaybackStopped -= OnPlaybackStopped;
            TryDispose(player);
        }

        _device?.Dispose();
        _device = null;
        DeviceName = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Raised on the WASAPI thread as it exits. Lock-free on purpose (see CloseLocked); recovery runs on the pool.
        bool unexpected = !_disposed && ReferenceEquals(sender, _player) && (e.Exception is not null || _wantPlaying);
        if (!unexpected)
        {
            return;
        }

        var message = e.Exception is null
            ? "Audio playback stopped unexpectedly."
            : $"The audio device stopped working ({e.Exception.GetType().Name}: {e.Exception.Message}).";
        _ = Task.Run(() => MarkLost(message, e.Exception));
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render || e.Role != Role.Multimedia)
        {
            return;
        }

        // No COM work on the notification callback thread.
        _ = Task.Run(() =>
        {
            string? error;
            bool wasLost;
            string? name;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                error = TryOpenLocked();
                wasLost = _lost;
                name = DeviceName;
                if (error is null)
                {
                    _lost = false;
                }
            }

            if (error is not null)
            {
                MarkLost(error, null);
            }
            else if (wasLost)
            {
                Raise(DeviceRestored, new AudioDeviceEventArgs($"Switched to {name ?? "the default device"}."));
            }
        });
    }

    private void MarkLost(string message, Exception? exception)
    {
        bool raise;
        bool startLoop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CloseLocked();
            raise = !_lost;
            _lost = true;
            startLoop = !_reconnecting;
            _reconnecting = true;
        }

        if (raise)
        {
            Raise(DeviceLost, new AudioDeviceEventArgs(message, exception));
        }

        if (startLoop)
        {
            _ = Task.Run(ReconnectLoop);
        }
    }

    private async Task ReconnectLoop()
    {
        var token = _lifetime.Token;
        int delay = 500;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                string? error;
                string? name;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (!_lost)
                    {
                        _reconnecting = false;
                        return;
                    }

                    error = TryOpenLocked();
                    name = DeviceName;
                    if (error is null)
                    {
                        _lost = false;
                        _reconnecting = false;
                    }
                }

                if (error is null)
                {
                    Raise(DeviceRestored, new AudioDeviceEventArgs($"Audio output restored on {name ?? "the default device"}."));
                    return;
                }

                delay = Math.Min(5000, delay * 2);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Raise(EventHandler<AudioDeviceEventArgs>? handler, AudioDeviceEventArgs args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Subscriber errors must not break device recovery.
        }
    }

    private static void TryDispose(WasapiPlayer player)
    {
        try
        {
            player.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The device may already be gone.
        }
    }
}
