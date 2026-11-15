using NAudio.Wave;

namespace SabhaMujic.Audio.Output;

/// <summary>An audio sink that pulls 48 kHz stereo float samples from a provider on its own thread.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>The device stopped working (unplugged, disabled, driver reset). Raised on an arbitrary thread.</summary>
    event EventHandler<AudioDeviceEventArgs>? DeviceLost;

    /// <summary>Output works again after <see cref="DeviceLost"/> (possibly on another device). Raised on an arbitrary thread.</summary>
    event EventHandler<AudioDeviceEventArgs>? DeviceRestored;

    void Init(ISampleProvider provider);

    void Play();

    void Pause();

    void Stop();
}

public sealed class AudioDeviceEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

/// <summary>
/// Implemented by providers that read ahead on another thread. Deterministic (manual) outputs call it before each
/// block so tests never hear decode underruns; real-time outputs never wait.
/// </summary>
public interface IReadAheadSampleProvider : ISampleProvider
{
    /// <summary>Waits until every active stream has <paramref name="frames"/> buffered (or has ended). Call only on the render thread.</summary>
    bool WaitForReadAhead(int frames, TimeSpan timeout);
}
