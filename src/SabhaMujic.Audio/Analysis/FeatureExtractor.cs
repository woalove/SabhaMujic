using NAudio.Dsp;

namespace SabhaMujic.Audio.Analysis;

/// <summary>Per-frame analysis features. Frame t is centred on 48 kHz frame <c>t * HopFrames</c>.</summary>
internal sealed class FeatureSeries
{
    public const int ChromaBins = 12;

    private float[] _chroma = new float[ChromaBins * 1024];
    private float[] _onset = new float[1024];
    private float[] _rmsDb = new float[1024];

    public int Count { get; private set; }

    public ReadOnlySpan<float> Onset => _onset.AsSpan(0, Count);

    public ReadOnlySpan<float> RmsDb => _rmsDb.AsSpan(0, Count);

    public ReadOnlySpan<float> Chroma(int frame) => _chroma.AsSpan(frame * ChromaBins, ChromaBins);

    public void Add(ReadOnlySpan<float> chroma, float onset, float rmsDb)
    {
        if (Count == _onset.Length)
        {
            Array.Resize(ref _onset, Count * 2);
            Array.Resize(ref _rmsDb, Count * 2);
            Array.Resize(ref _chroma, Count * 2 * ChromaBins);
        }

        chroma.CopyTo(_chroma.AsSpan(Count * ChromaBins, ChromaBins));
        _onset[Count] = onset;
        _rmsDb[Count] = rmsDb;
        Count++;
    }
}

/// <summary>
/// Streaming feature extraction (plan §3 step 1): the mono mix is low-pass filtered and decimated 4:1 to 12 kHz,
/// then a 2048-point Hann FFT (NAudio <see cref="FftProcessor"/>) every 256 samples (21.3 ms hop) yields
/// RMS energy (dB), spectral-flux onset strength and a 12-bin chroma vector. Memory: fixed buffers plus
/// 14 floats per hop.
/// </summary>
internal sealed class FeatureExtractor
{
    public const int Decimation = 4;
    public const int FeatureRate = AudioFormat.SampleRate / Decimation;
    public const int FftSize = 2048;
    public const int Hop = 256;
    public const int HopFrames = Hop * Decimation;
    public const double FramesPerSecond = (double)AudioFormat.SampleRate / HopFrames;

    private const int FirTaps = 63;
    private const double MinChromaHz = 80;
    private const double MaxChromaHz = 4200;

    private readonly float[] _fir = DesignLowPass(FirTaps, 5000.0 / AudioFormat.SampleRate);
    private readonly float[] _history = new float[FirTaps * 2];
    private int _historyPos;
    private int _phase;

    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private long _pushed;

    private readonly FftProcessor _fft = new(FftSize, FftWindowType.Hann);
    private readonly float[] _frame = new float[FftSize];
    private readonly Complex[] _spectrum = new Complex[FftSize / 2 + 1];
    private readonly float[] _prevLog = new float[FftSize / 2 + 1];
    private readonly float[] _chroma = new float[FeatureSeries.ChromaBins];
    private readonly int[] _binPitchClass;
    private readonly int _binLo;
    private readonly int _binHi;
    private bool _havePrev;

    public FeatureExtractor()
    {
        // Pre-pad half a window of zeros so frame t is centred on decimated sample t * Hop.
        _pushed = FftSize / 2;
        _ringPos = FftSize / 2;
        double binHz = (double)FeatureRate / FftSize;
        _binLo = (int)Math.Ceiling(MinChromaHz / binHz);
        _binHi = Math.Min(FftSize / 2, (int)Math.Floor(MaxChromaHz / binHz));
        _binPitchClass = new int[FftSize / 2 + 1];
        for (int k = 0; k <= FftSize / 2; k++)
        {
            double f = Math.Max(1e-3, k * binHz);
            int midi = (int)Math.Round(69 + 12 * Math.Log2(f / 440.0));
            _binPitchClass[k] = ((midi % 12) + 12) % 12;
        }
    }

    public FeatureSeries Series { get; } = new();

    /// <summary>Feeds interleaved 48 kHz stereo samples.</summary>
    public void Process(ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            float mono = 0.5f * (stereo[2 * i] + stereo[2 * i + 1]);
            PushInput(mono);
        }
    }

    /// <summary>Flushes the tail so the last frames (centred up to the end of the audio) are emitted.</summary>
    public void Complete()
    {
        for (int i = 0; i < FftSize / 2 * Decimation; i++)
        {
            PushInput(0f);
        }
    }

    private void PushInput(float sample)
    {
        _history[_historyPos] = sample;
        _history[_historyPos + FirTaps] = sample;
        _historyPos = (_historyPos + 1) % FirTaps;
        if (++_phase < Decimation)
        {
            return;
        }

        _phase = 0;

        // Oldest sample is at _historyPos; the doubled buffer makes the window contiguous.
        var window = _history.AsSpan(_historyPos, FirTaps);
        float acc = 0;
        for (int k = 0; k < FirTaps; k++)
        {
            acc += window[k] * _fir[k];
        }

        PushDecimated(acc);
    }

    private void PushDecimated(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) % FftSize;
        _pushed++;
        if (_pushed >= FftSize && (_pushed - FftSize) % Hop == 0)
        {
            EmitFrame();
        }
    }

    private void EmitFrame()
    {
        // Linearize the ring, oldest first.
        int tail = FftSize - _ringPos;
        _ring.AsSpan(_ringPos, tail).CopyTo(_frame);
        _ring.AsSpan(0, _ringPos).CopyTo(_frame.AsSpan(tail));

        double energy = 0;
        int c0 = FftSize / 2 - Hop / 2;
        for (int i = c0; i < c0 + Hop; i++)
        {
            energy += _frame[i] * _frame[i];
        }

        float rmsDb = (float)(10 * Math.Log10(energy / Hop + 1e-12));

        _fft.RealForward(_frame, _spectrum);
        Array.Clear(_chroma);
        double flux = 0;
        for (int k = 1; k <= FftSize / 2; k++)
        {
            var c = _spectrum[k];
            float mag = MathF.Sqrt(c.X * c.X + c.Y * c.Y);
            float log = MathF.Log(1 + 100 * mag);
            if (_havePrev)
            {
                float d = log - _prevLog[k];
                if (d > 0)
                {
                    flux += d;
                }
            }

            _prevLog[k] = log;
            if (k >= _binLo && k <= _binHi)
            {
                _chroma[_binPitchClass[k]] += mag;
            }
        }

        _havePrev = true;
        Series.Add(_chroma, (float)flux, rmsDb);
    }

    private static float[] DesignLowPass(int taps, double cutoff)
    {
        var h = new float[taps];
        int m = taps - 1;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - m / 2.0;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double blackman = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m);
            h[i] = (float)(sinc * blackman);
            sum += h[i];
        }

        for (int i = 0; i < taps; i++)
        {
            h[i] = (float)(h[i] / sum);
        }

        return h;
    }
}
