using System.Diagnostics;
using NAudio.Wave;

namespace SabhaMujic.Audio.Output;

/// <summary>Receives every rendered block (interleaved samples). Runs on the render thread; must not block.</summary>
public delegate void RenderedSamplesHandler(ReadOnlySpan<float> samples);

/// <summary>
/// An output without a device, for tests, Linux hosts and soak runs.
/// <list type="bullet">
/// <item>Real-time mode: a background thread pulls 10 ms blocks at real time × <see cref="SpeedFactor"/>.</item>
/// <item>Manual mode (<see cref="CreateManual"/>): nothing runs by itself; <see cref="Pump(int)"/> renders an exact number
/// of frames on the calling thread, waiting for read-ahead first so results are deterministic.</item>
/// </list>
/// </summary>
public sealed class NullRealtimeOutput : IAudioOutput
{
    public const int BlockFrames = 480;

    private readonly object _gate = new();
    private readonly float[] _block = new float[BlockFrames * AudioFormat.Channels];
    private ISampleProvider? _provider;
    private Thread? _thread;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private volatile bool _disposed;
    private long _framesRendered;
    private double _speedFactor;

    /// <param name="speedFactor">Real-time multiplier for the background thread (ignored in manual mode).</param>
    /// <param name="manual">True for <see cref="Pump(int)"/>-driven rendering without a thread.</param>
    public NullRealtimeOutput(double speedFactor = 1.0, bool manual = false)
    {
        SpeedFactor = speedFactor;
        IsManual = manual;
    }

    public event EventHandler<AudioDeviceEventArgs>? DeviceLost;

    public event EventHandler<AudioDeviceEventArgs>? DeviceRestored;

    public bool IsManual { get; }

    public double SpeedFactor
    {
        get => Volatile.Read(ref _speedFactor);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            Volatile.Write(ref _speedFactor, value);
        }
    }

    public PlaybackState State => _state;

    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Optional tap that sees every rendered block (metering, tests).</summary>
    public RenderedSamplesHandler? Tap { get; set; }

    /// <summary>Maximum time a manual pump waits for read-ahead per block.</summary>
    public TimeSpan ReadAheadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static NullRealtimeOutput CreateManual() => new(1.0, manual: true);

    public void Init(ISampleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (provider.WaveFormat.Channels != AudioFormat.Channels)
        {
            throw new ArgumentException("The provider must be stereo.", nameof(provider));
        }

        _provider = provider;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_provider is null)
        {
            throw new InvalidOperationException("Init must be called before Play.");
        }

        _state = PlaybackState.Playing;
        if (!IsManual)
        {
            lock (_gate)
            {
                if (_thread is null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "SabhaMujic null audio output", Priority = ThreadPriority.Highest };
                    _thread.Start();
                }
            }
        }
    }

    public void Pause() => _state = PlaybackState.Paused;

    public void Stop() => _state = PlaybackState.Stopped;

    /// <summary>Manual mode: renders <paramref name="frames"/> frames (discarding them) if playing. Returns frames rendered.</summary>
    public int Pump(int frames)
    {
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(BlockFrames, frames - done);
            int rendered = Pump(_block.AsSpan(0, n * AudioFormat.Channels));
            if (rendered == 0)
            {
                break;
            }

            done += rendered;
        }

        return done;
    }

    /// <summary>Manual mode: renders into <paramref name="destination"/> (interleaved stereo) in 10 ms blocks. Returns frames rendered.</summary>
    public int Pump(Span<float> destination)
    {
        if (!IsManual)
        {
            throw new InvalidOperationException("Pump is only available in manual mode.");
        }

        var provider = _provider;
        if (provider is null || _state != PlaybackState.Playing)
        {
            return 0;
        }

        int frames = destination.Length / AudioFormat.Channels;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(BlockFrames, frames - done);
            if (provider is IReadAheadSampleProvider readAhead)
            {
                readAhead.WaitForReadAhead(n, ReadAheadTimeout);
            }

            RenderBlock(provider, destination.Slice(done * AudioFormat.Channels, n * AudioFormat.Channels));
            done += n;
        }

        return done;
    }

    /// <summary>Raises <see cref="DeviceLost"/> (fault-injection for tests).</summary>
    public void SimulateDeviceLost(string message = "Simulated device loss") => DeviceLost?.Invoke(this, new AudioDeviceEventArgs(message));

    /// <summary>Raises <see cref="DeviceRestored"/> (fault-injection for tests).</summary>
    public void SimulateDeviceRestored(string message = "Simulated device restored") => DeviceRestored?.Invoke(this, new AudioDeviceEventArgs(message));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state = PlaybackState.Stopped;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void RenderBlock(ISampleProvider provider, Span<float> destination)
    {
        int read = provider.Read(destination);
        if (read < destination.Length)
        {
            destination[Math.Max(0, read)..].Clear();
        }

        Tap?.Invoke(destination);
        Interlocked.Add(ref _framesRendered, destination.Length / AudioFormat.Channels);
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double renderedAtClock = 0;
        bool wasPlaying = false;
        while (!_disposed)
        {
            var provider = _provider;
            if (_state != PlaybackState.Playing || provider is null)
            {
                wasPlaying = false;
                Thread.Sleep(5);
                continue;
            }

            if (!wasPlaying)
            {
                clock.Restart();
                renderedAtClock = 0;
                wasPlaying = true;
            }

            double due = clock.Elapsed.TotalSeconds * AudioFormat.SampleRate * SpeedFactor - renderedAtClock;
            int blocks = (int)Math.Min(due / BlockFrames, 100); // at most 1 s of catch-up per wake
            for (int i = 0; i < blocks && _state == PlaybackState.Playing; i++)
            {
                RenderBlock(provider, _block);
                renderedAtClock += BlockFrames;
            }

            if (due > 100.0 * BlockFrames * 2)
            {
                renderedAtClock = clock.Elapsed.TotalSeconds * AudioFormat.SampleRate * SpeedFactor; // fell behind; drop the backlog
            }

            Thread.Sleep(blocks > 0 ? 0 : 1);
        }
    }
}
