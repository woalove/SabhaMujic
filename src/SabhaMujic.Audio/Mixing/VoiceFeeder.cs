namespace SabhaMujic.Audio.Mixing;

/// <summary>
/// Background decode thread that keeps every registered <see cref="PlaybackVoice"/> buffered ahead of the render
/// thread. Decoder I/O happens only here (and when voices are created on the engine's control thread).
/// </summary>
public sealed class VoiceFeeder : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private PlaybackVoice[] _voices = [];
    private volatile bool _stopping;

    public VoiceFeeder()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SabhaMujic audio decode",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public int VoiceCount => Volatile.Read(ref _voices).Length;

    public void Register(PlaybackVoice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        lock (_gate)
        {
            if (Array.IndexOf(_voices, voice) < 0)
            {
                _voices = [.. _voices, voice];
            }
        }

        voice.Feeder = this;
        Signal();
    }

    public void Unregister(PlaybackVoice voice)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_voices, voice);
            if (index >= 0)
            {
                var list = _voices.ToList();
                list.RemoveAt(index);
                _voices = list.ToArray();
            }
        }
    }

    /// <summary>Wakes the decode thread. Safe from the render thread (no allocation, no lock).</summary>
    public void Signal() => _wake.Set();

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _wake.Dispose();
    }

    private void Run()
    {
        var order = new PlaybackVoice[8];
        var keys = new int[8];
        while (!_stopping)
        {
            bool worked = false;
            var voices = Volatile.Read(ref _voices);
            if (order.Length < voices.Length)
            {
                order = new PlaybackVoice[voices.Length * 2];
                keys = new int[voices.Length * 2];
            }

            // Neediest voice first (fewest buffered frames), sorted on a snapshot because the render thread
            // keeps draining the buffers while we sort.
            int count = voices.Length;
            for (int i = 0; i < count; i++)
            {
                order[i] = voices[i];
                keys[i] = voices[i].BufferedFrames;
            }

            Array.Sort(keys, order, 0, count);
            for (int i = 0; i < count; i++)
            {
                if (_stopping)
                {
                    return;
                }

                worked |= order[i].FillChunk();
                order[i] = null!;
            }

            if (!worked)
            {
                _wake.WaitOne(10);
            }
        }
    }
}
