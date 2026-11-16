using System.Collections.Concurrent;
using NAudio.Wave;
using SabhaMujic.Audio.Output;

namespace SabhaMujic.Audio.Mixing;

/// <summary>
/// Sample-accurate voice mixer (the render end of the audio graph).
/// <list type="bullet">
/// <item>Plays the current <see cref="PlaybackVoice"/>; any number of previous voices fade out underneath it.</item>
/// <item>A scheduled transition starts at an exact source frame of the current voice: queue advance at
/// <c>trimmed end - crossfade</c> (so silence is never inside the fade) and Smart Loop seams at <c>loop end - fade</c>
/// into a voice positioned at <c>loop start - fade</c>, which keeps both sides aligned.</item>
/// <item>Equal-power gains (<see cref="EqualPowerCrossfade"/>), with correlation compensation for loop seams.</item>
/// <item>Pause, volume and mute ramp over 10 ms to avoid clicks.</item>
/// </list>
/// Threading: control methods may be called from any thread; they are queued and applied at the start of the next
/// render block. <see cref="Read"/> runs on the output thread, never blocks, never takes locks and does not allocate.
/// Events are read with <see cref="TryDequeueEvent"/> by a single consumer.
/// </summary>
public sealed class CrossfadeMixer : IReadAheadSampleProvider
{
    /// <summary>10 ms: pause/stop/volume ramps and the default de-click fade.</summary>
    public const int DeclickFrames = 480;

    private const int BlockFrames = 2048;
    private const int MaxFadingVoices = 6;
    private const int EventCapacity = 1024;
    private const float RampStep = 1f / DeclickFrames;

    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly MixerEvent[] _events = new MixerEvent[EventCapacity];

    // Spill-over when the ring is full (the control thread stalled): events are never lost, because a lost VoiceReleased
    // leaks a voice and a lost VoiceEnded stalls the engine. While it holds anything, new events go here too (order).
    private readonly ConcurrentQueue<MixerEvent> _overflow = new();
    private readonly float[] _scratch = new float[BlockFrames * 2];
    private readonly Slot[] _fading = new Slot[MaxFadingVoices];
    private long _eventWrite;
    private long _eventRead;
    private long _droppedEvents;

    // Render-thread state.
    private Slot _current;
    private int _fadingCount;
    private PlaybackVoice? _pendingVoice;
    private PlaybackVoice? _pendingExpected;
    private long _pendingTrigger;
    private int _pendingLength;
    private TransitionKind _pendingKind;
    private double _pendingCorrelation;
    private bool _paused;
    private float _pauseGain = 1f;
    private float _gain = 1f;
    private float _gainTarget = 1f;
    private float _gainStep;
    private int _gainRampRemaining;
    private PlaybackVoice? _publishedCurrent;

    public WaveFormat WaveFormat => AudioFormat.WaveFormat;

    /// <summary>The voice currently in front (as of the last render block).</summary>
    public PlaybackVoice? CurrentVoice => Volatile.Read(ref _publishedCurrent);

    /// <summary>
    /// Events that did not fit the 1024-event ring because the consumer fell behind. None is lost: they wait in an
    /// overflow queue (allocating on the render thread, which only happens in that abnormal case). Should stay 0.
    /// </summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    /// <summary>
    /// Makes <paramref name="voice"/> current now. The previous voice fades out over <paramref name="fadeFrames"/>
    /// (equal power) while the new one fades in; 0 is a hard cut. Any scheduled transition is cancelled, unless it
    /// was for this very voice. The mixer takes ownership of the voice until it raises <see cref="MixerEventKind.VoiceReleased"/>.
    /// </summary>
    public void Play(PlaybackVoice voice, int fadeFrames, TransitionKind kind = TransitionKind.Cut, double correlation = 0)
    {
        ArgumentNullException.ThrowIfNull(voice);
        _commands.Enqueue(new Command(CommandKind.Play) { Voice = voice, Frames = Math.Max(0, fadeFrames), Transition = kind, Correlation = correlation });
    }

    /// <summary>
    /// Schedules a transition to <paramref name="incoming"/> when the current voice (which must be
    /// <paramref name="expectedCurrent"/>) reaches source frame <paramref name="triggerFrame"/>. A loop seam starts only
    /// exactly at the trigger; other kinds start late if the trigger has already passed (with the fade shortened to end
    /// at the current voice's end). Replaces any earlier schedule.
    /// </summary>
    public void Schedule(PlaybackVoice expectedCurrent, PlaybackVoice incoming, long triggerFrame, int fadeFrames, TransitionKind kind, double correlation = 0)
    {
        ArgumentNullException.ThrowIfNull(expectedCurrent);
        ArgumentNullException.ThrowIfNull(incoming);
        _commands.Enqueue(new Command(CommandKind.Schedule)
        {
            Voice = incoming,
            Expected = expectedCurrent,
            Position = triggerFrame,
            Frames = Math.Max(0, fadeFrames),
            Transition = kind,
            Correlation = correlation,
        });
    }

    /// <summary>Cancels the scheduled transition if it is still for <paramref name="incoming"/> (the voice is then released).</summary>
    public void CancelScheduled(PlaybackVoice incoming) => _commands.Enqueue(new Command(CommandKind.Cancel) { Voice = incoming });

    /// <summary>Fades everything out over <paramref name="fadeFrames"/> and releases all voices.</summary>
    public void Stop(int fadeFrames = DeclickFrames) => _commands.Enqueue(new Command(CommandKind.Stop) { Frames = Math.Max(0, fadeFrames) });

    /// <summary>Pausing ramps the output down over 10 ms and then freezes every voice where it is.</summary>
    public void SetPaused(bool paused) => _commands.Enqueue(new Command(CommandKind.Pause) { Flag = paused });

    /// <summary>Master gain (linear), ramped over 10 ms.</summary>
    public void SetGain(float gain) => _commands.Enqueue(new Command(CommandKind.Gain) { Gain = Math.Max(0, gain) });

    /// <summary>Single consumer: next event from the render thread.</summary>
    public bool TryDequeueEvent(out MixerEvent mixerEvent)
    {
        long read = _eventRead;
        if (read >= Volatile.Read(ref _eventWrite))
        {
            // The ring is drained; anything spilled over is newer than all of it.
            return _overflow.TryDequeue(out mixerEvent);
        }

        int index = (int)(read % EventCapacity);
        mixerEvent = _events[index];
        _events[index] = default;
        Volatile.Write(ref _eventRead, read + 1);
        return true;
    }

    public int Read(Span<float> buffer)
    {
        ProcessCommands();
        buffer.Clear();
        int samples = buffer.Length & ~1;
        int offset = 0;
        while (offset < samples)
        {
            int frames = Math.Min((samples - offset) / 2, BlockFrames);
            RenderBlock(buffer.Slice(offset, frames * 2));
            offset += frames * 2;
        }

        return buffer.Length;
    }

    public bool WaitForReadAhead(int frames, TimeSpan timeout)
    {
        ProcessCommands();
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            bool ready = IsReady(_current.Voice, frames) && IsReady(_pendingVoice, frames);
            for (int i = 0; i < _fadingCount && ready; i++)
            {
                ready = IsReady(_fading[i].Voice, frames);
            }

            if (ready)
            {
                return true;
            }

            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }
    }

    private static bool IsReady(PlaybackVoice? voice, int frames)
    {
        if (voice is null || voice.IsDecodeComplete || voice.BufferedFrames >= Math.Min(frames, voice.BufferCapacityFrames / 2))
        {
            return true;
        }

        voice.Feeder?.Signal();
        return false;
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Kind)
            {
                case CommandKind.Play:
                    if (_pendingVoice == command.Voice)
                    {
                        ClearPending();
                    }
                    else if (_pendingVoice is not null)
                    {
                        Release(_pendingVoice);
                        ClearPending();
                    }

                    BeginTransition(command.Voice!, command.Frames, command.Transition, command.Correlation);
                    break;

                case CommandKind.Schedule:
                    if (_pendingVoice is not null && _pendingVoice != command.Voice)
                    {
                        Release(_pendingVoice);
                    }

                    ClearPending();
                    if (_current.Voice != command.Expected)
                    {
                        Emit(MixerEventKind.TransitionMissed, command.Transition, command.Voice!, command.Expected);
                        Release(command.Voice!);
                        break;
                    }

                    _pendingVoice = command.Voice;
                    _pendingExpected = command.Expected;
                    _pendingTrigger = command.Position;
                    _pendingLength = command.Frames;
                    _pendingKind = command.Transition;
                    _pendingCorrelation = command.Correlation;
                    break;

                case CommandKind.Cancel:
                    if (_pendingVoice is not null && _pendingVoice == command.Voice)
                    {
                        Release(_pendingVoice);
                        ClearPending();
                    }

                    break;

                case CommandKind.Stop:
                    if (_pendingVoice is not null)
                    {
                        Release(_pendingVoice);
                        ClearPending();
                    }

                    if (_current.Voice is not null)
                    {
                        if (command.Frames > 0 && !(_paused && _pauseGain <= 0f))
                        {
                            AddFading(_current.Voice, CurrentGain(), command.Frames, 0);
                        }
                        else
                        {
                            Release(_current.Voice);
                        }

                        _current = default;
                        Volatile.Write(ref _publishedCurrent, null);
                    }

                    break;

                case CommandKind.Pause:
                    _paused = command.Flag;
                    break;

                case CommandKind.Gain:
                    _gainTarget = command.Gain;
                    _gainRampRemaining = DeclickFrames;
                    _gainStep = (_gainTarget - _gain) / DeclickFrames;
                    break;
            }
        }
    }

    private void RenderBlock(Span<float> destination)
    {
        int frames = destination.Length / 2;
        int done = 0;
        while (done < frames)
        {
            if (_paused && _pauseGain <= 0f)
            {
                break; // frozen: the rest stays silent and no voice advances
            }

            int n = frames - done;
            if (_paused)
            {
                n = Math.Min(n, Math.Max(1, (int)Math.Ceiling(_pauseGain / RampStep)));
            }

            if (_pendingVoice is not null)
            {
                var current = _current.Voice;
                if (current is null || current != _pendingExpected)
                {
                    MissPending();
                    continue;
                }

                long distance = _pendingTrigger - current.Position;
                if (distance <= 0)
                {
                    if (distance < 0 && _pendingKind == TransitionKind.LoopSeam)
                    {
                        MissPending();
                    }
                    else
                    {
                        StartPending(current);
                    }

                    continue;
                }

                n = (int)Math.Min(n, distance);
            }

            if (_current.Voice is not null && _current.FadePos < _current.FadeLen)
            {
                n = (int)Math.Min(n, _current.FadeLen - _current.FadePos);
            }

            for (int i = 0; i < _fadingCount; i++)
            {
                n = (int)Math.Min(n, _fading[i].FadeLen - _fading[i].FadePos);
            }

            var span = destination.Slice(done * 2, n * 2);
            MixFading(span, n);
            MixCurrent(span, n);
            ApplyGains(span, n);
            done += n;
        }
    }

    private void StartPending(PlaybackVoice current)
    {
        var incoming = _pendingVoice!;
        var kind = _pendingKind;
        long length = _pendingLength;
        double correlation = _pendingCorrelation;
        ClearPending();
        if (kind != TransitionKind.LoopSeam)
        {
            // Late start: finish the fade-out exactly at the outgoing voice's end.
            length = Math.Min(length, Math.Max(0, current.RemainingFrames));
        }

        BeginTransition(incoming, (int)length, kind, correlation);
    }

    private void MissPending()
    {
        var voice = _pendingVoice!;
        var expected = _pendingExpected;
        var kind = _pendingKind;
        ClearPending();
        Emit(MixerEventKind.TransitionMissed, kind, voice, expected);
        Release(voice);
    }

    private void ClearPending()
    {
        _pendingVoice = null;
        _pendingExpected = null;
        _pendingTrigger = 0;
        _pendingLength = 0;
        _pendingCorrelation = 0;
    }

    private void BeginTransition(PlaybackVoice incoming, int fadeFrames, TransitionKind kind, double correlation)
    {
        var from = _current.Voice;
        if (from == incoming)
        {
            return;
        }

        if (_paused && _pauseGain <= 0f)
        {
            fadeFrames = 0; // nothing is audible; swap instantly
        }

        if (from is not null)
        {
            if (fadeFrames > 0)
            {
                AddFading(from, CurrentGain(), fadeFrames, correlation);
            }
            else
            {
                Release(from);
            }
        }

        _current = new Slot
        {
            Voice = incoming,
            FadeLen = fadeFrames,
            FadePos = 0,
            StartGain = 1f,
            Correlation = correlation,
            Kind = kind,
            From = from,
        };
        Volatile.Write(ref _publishedCurrent, incoming);
        Emit(MixerEventKind.TransitionStarted, kind, incoming, from);
        if (fadeFrames == 0)
        {
            Emit(MixerEventKind.TransitionCompleted, kind, incoming, from);
            _current.From = null;
        }
    }

    private float CurrentGain()
    {
        if (_current.FadePos >= _current.FadeLen)
        {
            return 1f;
        }

        double t = (double)_current.FadePos / _current.FadeLen;
        return EqualPowerCrossfade.FadeInGain(t) * EqualPowerCrossfade.CorrelationCompensation(t, _current.Correlation);
    }

    private void AddFading(PlaybackVoice voice, float startGain, int frames, double correlation)
    {
        if (_fadingCount == MaxFadingVoices)
        {
            Release(_fading[0].Voice!);
            Array.Copy(_fading, 1, _fading, 0, MaxFadingVoices - 1);
            _fadingCount--;
        }

        _fading[_fadingCount++] = new Slot
        {
            Voice = voice,
            FadeLen = Math.Max(1, frames),
            FadePos = 0,
            StartGain = startGain,
            Correlation = correlation,
        };
    }

    private void MixCurrent(Span<float> destination, int frames)
    {
        var voice = _current.Voice;
        if (voice is null)
        {
            return;
        }

        var samples = _scratch.AsSpan(0, frames * 2);
        int got = voice.Read(samples);
        if (got < frames)
        {
            samples[(got * 2)..].Clear();
        }

        if (_current.FadePos < _current.FadeLen)
        {
            double length = _current.FadeLen;
            long position = _current.FadePos;
            double correlation = _current.Correlation;
            for (int i = 0; i < frames; i++)
            {
                double t = (position + i) / length;
                float g = EqualPowerCrossfade.FadeInGain(t) * EqualPowerCrossfade.CorrelationCompensation(t, correlation);
                destination[2 * i] += samples[2 * i] * g;
                destination[2 * i + 1] += samples[2 * i + 1] * g;
            }

            _current.FadePos += frames;
            if (_current.FadePos >= _current.FadeLen)
            {
                Emit(MixerEventKind.TransitionCompleted, _current.Kind, voice, _current.From);
                _current.From = null;
            }
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
            {
                destination[i] += samples[i];
            }
        }

        if (voice.IsDrained)
        {
            HandleCurrentDrained(voice);
        }
    }

    private void HandleCurrentDrained(PlaybackVoice voice)
    {
        if (_pendingVoice is not null && _pendingExpected == voice)
        {
            // The outgoing track ended before the trigger (short or truncated file): start the next one now.
            var incoming = _pendingVoice;
            var kind = _pendingKind;
            ClearPending();
            BeginTransition(incoming, 0, kind, 0);
            return;
        }

        Emit(MixerEventKind.VoiceEnded, TransitionKind.Cut, voice, null);
        _current = default;
        Volatile.Write(ref _publishedCurrent, null);
        Release(voice);
    }

    private void MixFading(Span<float> destination, int frames)
    {
        var samples = _scratch.AsSpan(0, frames * 2);
        for (int j = _fadingCount - 1; j >= 0; j--)
        {
            ref var slot = ref _fading[j];
            var voice = slot.Voice!;
            int got = voice.Read(samples);
            if (got < frames)
            {
                samples[(got * 2)..].Clear();
            }

            double length = slot.FadeLen;
            long position = slot.FadePos;
            for (int i = 0; i < frames; i++)
            {
                double t = (position + i) / length;
                float g = slot.StartGain * EqualPowerCrossfade.FadeOutGain(t) * EqualPowerCrossfade.CorrelationCompensation(t, slot.Correlation);
                destination[2 * i] += samples[2 * i] * g;
                destination[2 * i + 1] += samples[2 * i + 1] * g;
            }

            slot.FadePos += frames;
            if (slot.FadePos >= slot.FadeLen || voice.IsDrained)
            {
                Release(voice);
                for (int k = j; k < _fadingCount - 1; k++)
                {
                    _fading[k] = _fading[k + 1];
                }

                _fading[--_fadingCount] = default;
            }
        }
    }

    private void ApplyGains(Span<float> destination, int frames)
    {
        if (_gainRampRemaining == 0 && _gain == 1f && !_paused && _pauseGain >= 1f)
        {
            return;
        }

        for (int i = 0; i < frames; i++)
        {
            if (_gainRampRemaining > 0)
            {
                _gain += _gainStep;
                if (--_gainRampRemaining == 0)
                {
                    _gain = _gainTarget;
                }
            }

            _pauseGain = _paused ? Math.Max(0f, _pauseGain - RampStep) : Math.Min(1f, _pauseGain + RampStep);
            float g = _gain * _pauseGain;
            destination[2 * i] *= g;
            destination[2 * i + 1] *= g;
        }
    }

    private void Release(PlaybackVoice voice) => Emit(MixerEventKind.VoiceReleased, TransitionKind.Cut, voice, null);

    private void Emit(MixerEventKind kind, TransitionKind transition, PlaybackVoice voice, PlaybackVoice? other)
    {
        long write = _eventWrite;
        if (!_overflow.IsEmpty || write - Volatile.Read(ref _eventRead) >= EventCapacity)
        {
            Interlocked.Increment(ref _droppedEvents);
            _overflow.Enqueue(new MixerEvent(kind, transition, voice, other));
            return;
        }

        _events[(int)(write % EventCapacity)] = new MixerEvent(kind, transition, voice, other);
        Volatile.Write(ref _eventWrite, write + 1);
    }

    private enum CommandKind
    {
        Play,
        Schedule,
        Cancel,
        Stop,
        Pause,
        Gain,
    }

    private sealed class Command(CommandKind kind)
    {
        public CommandKind Kind { get; } = kind;

        public PlaybackVoice? Voice { get; init; }

        public PlaybackVoice? Expected { get; init; }

        public long Position { get; init; }

        public int Frames { get; init; }

        public TransitionKind Transition { get; init; }

        public double Correlation { get; init; }

        public float Gain { get; init; }

        public bool Flag { get; init; }
    }

    private struct Slot
    {
        public PlaybackVoice? Voice;
        public long FadeLen;
        public long FadePos;
        public float StartGain;
        public double Correlation;
        public TransitionKind Kind;
        public PlaybackVoice? From;
    }
}
